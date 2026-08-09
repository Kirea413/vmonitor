#include "Driver.h"
#include "Device.h"
#include "ControlServer.h"
#include "Trace.h"

#include <sddl.h>

#pragma comment(lib, "advapi32.lib")

namespace VMonitorControl
{
    static HANDLE    g_thread     = nullptr;
    static HANDLE    g_stopEvent  = nullptr;
    static WDFDEVICE g_device     = nullptr;
    static volatile LONG g_running = 0;

    // ─────────────────────────────────────────────────────────────────────
    // コマンドの処理
    // ─────────────────────────────────────────────────────────────────────

    static void HandleCommand(const Command& cmd, Response& res)
    {
        ZeroMemory(&res, sizeof(res));

        auto* Ctx = WdfObjectGet_IndirectDeviceContextWrapper(g_device);

        switch (cmd.Operation)
        {
        case OpConnect:
        {
            VMTRACE("Control: CONNECT");

            if (Ctx->AdapterObject == nullptr)
            {
                VMTRACE("Control: adapter not ready");
                break;
            }

            if (Ctx->MonitorObject != nullptr)
            {
                // 既に接続中。二重に作らない。
                res.Succeeded = 1;
                break;
            }

            // スマホの解像度を最優先モードとして提示する。
            // モニターを作る前に設定しないと、OS は古い一覧を読んでしまう。
            VMonitorVDD_SetPreferredMode(cmd.Width, cmd.Height, cmd.RefreshRate);

            // アダプターの初期化は非同期で進む。
            //
            // IddCxAdapterInitAsync が成功しても、その時点ではまだ準備中で、
            // すぐにモニターを作ろうとすると
            // STATUS_OPERATION_IN_PROGRESS (0xC0000476) で拒否される。
            // 準備が整うまで少し待ってから作る。
            constexpr int MaxAttempts = 50;   // 100ms × 50 = 最大 5 秒
            constexpr NTSTATUS StatusOperationInProgress = (NTSTATUS)0xC0000476L;

            IDDCX_MONITOR monitor = nullptr;
            NTSTATUS status = StatusOperationInProgress;

            for (int attempt = 0; attempt < MaxAttempts; ++attempt)
            {
                status = VMonitorVDD_CreateMonitorEx(Ctx->AdapterObject, 0, &monitor);

                if (status != StatusOperationInProgress)
                    break;

                Sleep(100);
            }

            VMTRACE_STATUS("Control: CreateMonitor", status);

            if (NT_SUCCESS(status))
            {
                Ctx->MonitorObject = monitor;
                Ctx->Width         = cmd.Width;
                Ctx->Height        = cmd.Height;
                res.Succeeded      = 1;
            }

            break;
        }

        case OpDisconnect:
        {
            VMTRACE("Control: DISCONNECT");

            if (Ctx->MonitorObject == nullptr)
            {
                res.Succeeded = 1;   // 既に未接続
                break;
            }

            NTSTATUS status = IddCxMonitorDeparture(Ctx->MonitorObject);
            VMTRACE_STATUS("Control: MonitorDeparture", status);

            // 失敗しても状態は未接続に戻す。
            // 掴んだままにすると次の接続要求が「既に接続中」と誤判定され、
            // 二度とモニターを出せなくなる。
            Ctx->MonitorObject = nullptr;
            Ctx->Width         = 0;
            Ctx->Height        = 0;
            res.Succeeded      = 1;

            break;
        }

        case OpGetState:
            res.Succeeded = 1;
            break;

        default:
            VMTRACE("Control: unknown operation");
            break;
        }

        res.Connected = (Ctx->MonitorObject != nullptr) ? 1u : 0u;
        res.Width     = Ctx->Width;
        res.Height    = Ctx->Height;
    }

    // ─────────────────────────────────────────────────────────────────────
    // パイプサーバー本体
    // ─────────────────────────────────────────────────────────────────────

    static DWORD WINAPI ServerThread(LPVOID)
    {
        VMTRACE("Control: server thread start");

        // ドライバは LocalService として動き、アプリは利用者の権限で動く。
        // 双方が繋がれるよう、明示的にアクセスを許可する記述子を付ける。
        SECURITY_ATTRIBUTES sa;
        ZeroMemory(&sa, sizeof(sa));
        sa.nLength        = sizeof(sa);
        sa.bInheritHandle = FALSE;

        PSECURITY_DESCRIPTOR pSd = nullptr;

        // D:(A;;GA;;;WD)  = Everyone に全権限
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(
                L"D:(A;;GA;;;WD)", SDDL_REVISION_1, &pSd, nullptr))
        {
            VMTRACE("Control: security descriptor failed");
            return 0;
        }

        sa.lpSecurityDescriptor = pSd;

        while (InterlockedCompareExchange(&g_running, 1, 1) == 1)
        {
            HANDLE pipe = CreateNamedPipeW(
                PipeName,
                PIPE_ACCESS_DUPLEX,
                PIPE_TYPE_MESSAGE | PIPE_READMODE_MESSAGE | PIPE_WAIT,
                PIPE_UNLIMITED_INSTANCES,
                sizeof(Response),
                sizeof(Command),
                0,
                &sa);

            if (pipe == INVALID_HANDLE_VALUE)
            {
                VMTRACE("Control: CreateNamedPipe failed");
                Sleep(1000);
                continue;
            }

            // 接続待ち。停止要求が来たらパイプを閉じて抜ける。
            OVERLAPPED ov;
            ZeroMemory(&ov, sizeof(ov));
            ov.hEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);

            BOOL connected = ConnectNamedPipe(pipe, &ov);
            DWORD err = GetLastError();

            if (!connected && err == ERROR_IO_PENDING)
            {
                HANDLE waits[2] = { ov.hEvent, g_stopEvent };
                DWORD  which = WaitForMultipleObjects(2, waits, FALSE, INFINITE);

                if (which != WAIT_OBJECT_0)
                {
                    // 停止要求
                    CancelIo(pipe);
                    CloseHandle(ov.hEvent);
                    CloseHandle(pipe);
                    break;
                }
            }
            else if (!connected && err != ERROR_PIPE_CONNECTED)
            {
                CloseHandle(ov.hEvent);
                CloseHandle(pipe);
                continue;
            }

            CloseHandle(ov.hEvent);

            // 1 コマンド処理して切る（接続はその都度張り直す）
            Command  cmd;
            Response res;
            DWORD    read = 0, written = 0;

            ZeroMemory(&cmd, sizeof(cmd));

            if (ReadFile(pipe, &cmd, sizeof(cmd), &read, nullptr) && read == sizeof(cmd))
            {
                HandleCommand(cmd, res);
                WriteFile(pipe, &res, sizeof(res), &written, nullptr);
                FlushFileBuffers(pipe);
            }

            DisconnectNamedPipe(pipe);
            CloseHandle(pipe);
        }

        if (pSd) LocalFree(pSd);

        VMTRACE("Control: server thread exit");
        return 0;
    }

    // ─────────────────────────────────────────────────────────────────────

    void Start(WDFDEVICE Device)
    {
        if (InterlockedCompareExchange(&g_running, 1, 0) != 0)
            return;   // 既に起動済み

        g_device    = Device;
        g_stopEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);

        if (g_stopEvent == nullptr)
        {
            InterlockedExchange(&g_running, 0);
            VMTRACE("Control: CreateEvent failed");
            return;
        }

        g_thread = CreateThread(nullptr, 0, ServerThread, nullptr, 0, nullptr);

        if (g_thread == nullptr)
        {
            CloseHandle(g_stopEvent);
            g_stopEvent = nullptr;
            InterlockedExchange(&g_running, 0);
            VMTRACE("Control: CreateThread failed");
            return;
        }

        VMTRACE("Control: started");
    }

    void Stop()
    {
        if (InterlockedCompareExchange(&g_running, 0, 1) != 1)
            return;   // 起動していない

        if (g_stopEvent) SetEvent(g_stopEvent);

        if (g_thread)
        {
            WaitForSingleObject(g_thread, 5000);
            CloseHandle(g_thread);
            g_thread = nullptr;
        }

        if (g_stopEvent)
        {
            CloseHandle(g_stopEvent);
            g_stopEvent = nullptr;
        }

        VMTRACE("Control: stopped");
    }
}
