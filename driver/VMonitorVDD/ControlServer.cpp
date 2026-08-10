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
    // 持ち主の死活監視
    // ─────────────────────────────────────────────────────────────────────
    //
    // アプリが強制終了されると、こちらには何も伝わらないままモニターが
    // 残る。持ち主のプロセスを開いて終了を待ち、死んだら自分で外す。
    //
    static HANDLE g_ownerProcess = nullptr;   // 見張っている相手
    static HANDLE g_ownerThread  = nullptr;   // 待ち受けているスレッド
    static HANDLE g_ownerCancel  = nullptr;   // 見張りをやめるための合図

    static void DisconnectMonitor();          // 前方宣言

    /// <summary>持ち主の終了を待つ。死んだらモニターを外す。</summary>
    static DWORD WINAPI OwnerWatchThread(LPVOID)
    {
        HANDLE waits[2] = { g_ownerProcess, g_ownerCancel };

        DWORD which = WaitForMultipleObjects(2, waits, FALSE, INFINITE);

        // 合図で終わったなら、正常に切断済み。何もしない。
        if (which != WAIT_OBJECT_0) return 0;

        VMTRACE("Control: owner process exited; disconnecting monitor");

        DisconnectMonitor();
        return 0;
    }

    /// <summary>見張りをやめる。</summary>
    static void StopWatchingOwner()
    {
        if (g_ownerCancel) SetEvent(g_ownerCancel);

        if (g_ownerThread)
        {
            WaitForSingleObject(g_ownerThread, 2000);
            CloseHandle(g_ownerThread);
            g_ownerThread = nullptr;
        }

        if (g_ownerProcess) { CloseHandle(g_ownerProcess); g_ownerProcess = nullptr; }
        if (g_ownerCancel)  { CloseHandle(g_ownerCancel);  g_ownerCancel  = nullptr; }
    }

    /// <summary>指定したプロセスの終了を見張り始める。</summary>
    static void StartWatchingOwner(unsigned int processId)
    {
        StopWatchingOwner();

        if (processId == 0) return;   // 見張らない

        // 終了を待つだけなので、必要な権限は最小限にする
        g_ownerProcess = OpenProcess(SYNCHRONIZE, FALSE, processId);

        if (g_ownerProcess == nullptr)
        {
            VMTRACE("Control: could not open owner process; not watching");
            return;
        }

        g_ownerCancel = CreateEventW(nullptr, TRUE, FALSE, nullptr);

        if (g_ownerCancel == nullptr)
        {
            CloseHandle(g_ownerProcess);
            g_ownerProcess = nullptr;
            return;
        }

        g_ownerThread = CreateThread(nullptr, 0, OwnerWatchThread, nullptr, 0, nullptr);

        if (g_ownerThread == nullptr)
        {
            StopWatchingOwner();
            return;
        }

        VMTRACE("Control: watching owner process");
    }

    // ─────────────────────────────────────────────────────────────────────
    // モニターを外す
    // ─────────────────────────────────────────────────────────────────────
    //
    // 利用者が切ったときと、持ち主が死んだときの両方から呼ばれる。
    // どちらも後始末の中身は同じなので、一箇所にまとめる。
    //
    static void DisconnectMonitor()
    {
        if (g_device == nullptr) return;

        auto* Ctx = WdfObjectGet_IndirectDeviceContextWrapper(g_device);
        if (Ctx == nullptr || Ctx->MonitorObject == nullptr) return;

        NTSTATUS status = IddCxMonitorDeparture(Ctx->MonitorObject);
        VMTRACE_STATUS("Control: MonitorDeparture", status);

        // 失敗しても状態は未接続に戻す。
        // 掴んだままにすると次の接続要求が「既に接続中」と誤判定され、
        // 二度とモニターを出せなくなる。
        Ctx->MonitorObject = nullptr;
        Ctx->Width         = 0;
        Ctx->Height        = 0;
    }

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

                // 出せたら、持ち主が生きているかを見張り始める。
                // 強制終了されてもモニターが残らないようにするため。
                StartWatchingOwner(cmd.OwnerProcessId);
            }

            break;
        }

        case OpDisconnect:
        {
            VMTRACE("Control: DISCONNECT");

            // 自分から切るので、持ち主の見張りも終える
            StopWatchingOwner();

            DisconnectMonitor();
            res.Succeeded = 1;

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

        // 見張りのスレッドを先に畳む。残すと、後片付けの最中に
        // モニターを外しにきて衝突する。
        StopWatchingOwner();

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
