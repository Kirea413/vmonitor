#include "Driver.h"
#include "SwapChainProcessor.h"
#include "Trace.h"

#include <d3d11.h>
#include <dxgi1_4.h>

#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "dxgi.lib")

namespace VMonitorSwapChain
{
    // ─────────────────────────────────────────────────────────────────────
    // 状態
    //
    // 仮想モニターは同時に 1 つしか作らないので、スワップチェーンも 1 本。
    // WDF のオブジェクトコンテキストを増やすより、ここで持つほうが素直。
    // ─────────────────────────────────────────────────────────────────────

    static IDDCX_SWAPCHAIN   g_swapChain            = nullptr;
    static HANDLE            g_surfaceAvailable     = nullptr;   // OS の持ち物。閉じてはいけない
    static HANDLE            g_terminateEvent       = nullptr;
    static HANDLE            g_thread               = nullptr;

    static ID3D11Device*        g_d3dDevice   = nullptr;
    static ID3D11DeviceContext* g_d3dContext  = nullptr;
    static IDXGIDevice*         g_dxgiDevice  = nullptr;

    /// <summary>
    /// デスクトップを描いたアダプターの上に D3D デバイスを作る。
    /// </summary>
    /// <remarks>
    /// スワップチェーンの面は、そのアダプターのデバイスからしか触れない。
    /// 別のアダプターで作ると IddCxSwapChainSetDevice が失敗する。
    /// </remarks>
    static bool CreateDeviceOnAdapter(LUID adapterLuid)
    {
        IDXGIFactory4* factory = nullptr;

        HRESULT hr = CreateDXGIFactory2(0, IID_PPV_ARGS(&factory));
        if (FAILED(hr))
        {
            VMTRACE("SwapChain: CreateDXGIFactory2 failed");
            return false;
        }

        IDXGIAdapter1* adapter = nullptr;
        hr = factory->EnumAdapterByLuid(adapterLuid, IID_PPV_ARGS(&adapter));
        factory->Release();

        if (FAILED(hr) || adapter == nullptr)
        {
            VMTRACE("SwapChain: EnumAdapterByLuid failed");
            return false;
        }

        // アダプターを明示するときは、種類を Unknown にしなければならない。
        // Hardware を渡すと E_INVALIDARG になる。
        hr = D3D11CreateDevice(
            adapter,
            D3D_DRIVER_TYPE_UNKNOWN,
            nullptr,
            0,
            nullptr, 0,
            D3D11_SDK_VERSION,
            &g_d3dDevice,
            nullptr,
            &g_d3dContext);

        adapter->Release();

        if (FAILED(hr))
        {
            VMTRACE("SwapChain: D3D11CreateDevice failed");
            return false;
        }

        hr = g_d3dDevice->QueryInterface(IID_PPV_ARGS(&g_dxgiDevice));
        if (FAILED(hr))
        {
            VMTRACE("SwapChain: QueryInterface(IDXGIDevice) failed");
            return false;
        }

        return true;
    }

    static void ReleaseDevice()
    {
        if (g_dxgiDevice) { g_dxgiDevice->Release(); g_dxgiDevice = nullptr; }
        if (g_d3dContext) { g_d3dContext->Release(); g_d3dContext = nullptr; }
        if (g_d3dDevice)  { g_d3dDevice->Release();  g_d3dDevice  = nullptr; }
    }

    // ─────────────────────────────────────────────────────────────────────
    // フレームを引き取り続けるループ
    // ─────────────────────────────────────────────────────────────────────

    static void ProcessFrames()
    {
        IDARG_IN_SWAPCHAINSETDEVICE setDevice;
        ZeroMemory(&setDevice, sizeof(setDevice));
        setDevice.pDevice = g_dxgiDevice;

        HRESULT hr = IddCxSwapChainSetDevice(g_swapChain, &setDevice);
        if (FAILED(hr))
        {
            VMTRACE("SwapChain: IddCxSwapChainSetDevice failed");
            return;
        }

        VMTRACE("SwapChain: processing started");

        for (;;)
        {
            IDARG_OUT_RELEASEANDACQUIREBUFFER buffer;
            ZeroMemory(&buffer, sizeof(buffer));

            hr = IddCxSwapChainReleaseAndAcquireBuffer(g_swapChain, &buffer);

            if (hr == E_PENDING)
            {
                // まだ新しい絵が無い。届くか、停止を指示されるまで待つ。
                //
                // 待ち時間に上限を置いているのは、イベントを取りこぼしても
                // 止まったままにならないようにするため。
                HANDLE waits[2] = { g_surfaceAvailable, g_terminateEvent };
                DWORD  result   = WaitForMultipleObjects(2, waits, FALSE, 16);

                if (result == WAIT_OBJECT_0 || result == WAIT_TIMEOUT)
                    continue;   // 新しい絵が来た、または様子を見に戻る

                if (result == WAIT_OBJECT_0 + 1)
                    break;      // 停止

                VMTRACE("SwapChain: wait failed");
                break;
            }

            if (SUCCEEDED(hr))
            {
                // 取り出した絵はここでは使わない。
                // 画面の取り込みはアプリ側の Desktop Duplication が担当する。
                // ここでの役目は、OS に「処理した」と伝えて次へ進めること。
                if (buffer.MetaData.pSurface != nullptr)
                    buffer.MetaData.pSurface->Release();

                hr = IddCxSwapChainFinishedProcessingFrame(g_swapChain);

                if (FAILED(hr))
                {
                    VMTRACE("SwapChain: FinishedProcessingFrame failed");
                    break;
                }

                // 停止を指示されていないか確認する
                if (WaitForSingleObject(g_terminateEvent, 0) == WAIT_OBJECT_0)
                    break;

                continue;
            }

            // 取得に失敗した。スワップチェーンを手放して OS に作り直させる。
            VMTRACE("SwapChain: ReleaseAndAcquireBuffer failed");
            break;
        }

        VMTRACE("SwapChain: processing stopped");
    }

    static DWORD WINAPI ThreadProc(LPVOID)
    {
        ProcessFrames();

        // ループを抜けたら必ずスワップチェーンを手放す。
        // 残したままにすると OS は新しいスワップチェーンを作れず、
        // 画面が更新されないまま固まる。
        if (g_swapChain != nullptr)
        {
            WdfObjectDelete((WDFOBJECT)g_swapChain);
            g_swapChain = nullptr;
        }

        return 0;
    }

    // ─────────────────────────────────────────────────────────────────────

    bool Start(IDDCX_SWAPCHAIN hSwapChain, LUID renderAdapterLuid, HANDLE hNextSurfaceAvailable)
    {
        // 前のものが残っていれば片付けてから
        Stop();

        g_swapChain        = hSwapChain;
        g_surfaceAvailable = hNextSurfaceAvailable;

        if (!CreateDeviceOnAdapter(renderAdapterLuid))
        {
            ReleaseDevice();
            g_swapChain = nullptr;
            return false;
        }

        g_terminateEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);

        if (g_terminateEvent == nullptr)
        {
            VMTRACE("SwapChain: CreateEvent failed");
            ReleaseDevice();
            g_swapChain = nullptr;
            return false;
        }

        g_thread = CreateThread(nullptr, 0, ThreadProc, nullptr, 0, nullptr);

        if (g_thread == nullptr)
        {
            VMTRACE("SwapChain: CreateThread failed");
            CloseHandle(g_terminateEvent);
            g_terminateEvent = nullptr;
            ReleaseDevice();
            g_swapChain = nullptr;
            return false;
        }

        return true;
    }

    void Stop()
    {
        if (g_thread != nullptr)
        {
            if (g_terminateEvent != nullptr)
                SetEvent(g_terminateEvent);

            // 待ちは最長 16ms 単位なので、すぐ戻ってくる
            WaitForSingleObject(g_thread, 5000);

            CloseHandle(g_thread);
            g_thread = nullptr;
        }

        if (g_terminateEvent != nullptr)
        {
            CloseHandle(g_terminateEvent);
            g_terminateEvent = nullptr;
        }

        // スレッドが自分で消しているはずだが、起動前に失敗した経路もある
        if (g_swapChain != nullptr)
        {
            WdfObjectDelete((WDFOBJECT)g_swapChain);
            g_swapChain = nullptr;
        }

        g_surfaceAvailable = nullptr;

        ReleaseDevice();
    }
}
