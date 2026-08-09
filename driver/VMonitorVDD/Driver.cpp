#include "Driver.h"
#include "Device.h"
#include "Trace.h"
#include "ControlServer.h"

// vmonitor Virtual Display Driver - IddCx 1.10 UMDF2

// DLL entry point required for UMDF
extern "C" BOOL WINAPI DllMain(HINSTANCE hInstance, DWORD reason, LPVOID reserved)
{
    UNREFERENCED_PARAMETER(hInstance);
    UNREFERENCED_PARAMETER(reason);
    UNREFERENCED_PARAMETER(reserved);
    return TRUE;
}

extern "C" NTSTATUS DriverEntry(
    _In_ PDRIVER_OBJECT  DriverObject,
    _In_ PUNICODE_STRING RegistryPath)
{
    VMTRACE("DriverEntry: enter");

    WDF_DRIVER_CONFIG Config;
    WDF_DRIVER_CONFIG_INIT(&Config, VMonitorVDD_AddDevice);

    NTSTATUS Status = WdfDriverCreate(DriverObject, RegistryPath,
                                      WDF_NO_OBJECT_ATTRIBUTES, &Config, WDF_NO_HANDLE);

    VMTRACE_STATUS("DriverEntry: WdfDriverCreate", Status);
    return Status;
}

// ---------------------------------------------------------------
// AddDevice
//
// ここでは「デバイスを作るところまで」を行う。
// アダプターの初期化 (IddCxAdapterInitAsync) は D0Entry で行う。
// AddDevice の時点ではデバイスはまだ電源が入っておらず、
// アダプターを初期化しても失敗する。
// ---------------------------------------------------------------

NTSTATUS VMonitorVDD_AddDevice(
    _In_    WDFDRIVER       Driver,
    _Inout_ PWDFDEVICE_INIT DeviceInit)
{
    UNREFERENCED_PARAMETER(Driver);

    VMTRACE("AddDevice: enter");

    // 電源投入 (D0Entry) を受け取れるようにする。
    // アダプターの初期化はそちらで行う。
    WDF_PNPPOWER_EVENT_CALLBACKS PnpPowerCallbacks;
    WDF_PNPPOWER_EVENT_CALLBACKS_INIT(&PnpPowerCallbacks);
    PnpPowerCallbacks.EvtDeviceD0Entry = VMonitorVDD_DeviceD0Entry;
    WdfDeviceInitSetPnpPowerEventCallbacks(DeviceInit, &PnpPowerCallbacks);

    // Register callbacks via IDD_CX_CLIENT_CONFIG (IddCx 1.10 pattern)
    // Use manual zeroing to avoid memset dependency from IDD_CX_CLIENT_CONFIG_INIT
    IDD_CX_CLIENT_CONFIG ClientConfig;
    ZeroMemory(&ClientConfig, sizeof(ClientConfig));
    ClientConfig.Size = sizeof(ClientConfig);

    ClientConfig.EvtIddCxAdapterInitFinished               = VMonitorVDD_AdapterInitFinished;
    ClientConfig.EvtIddCxAdapterCommitModes                = VMonitorVDD_AdapterCommitModes;
    ClientConfig.EvtIddCxParseMonitorDescription           = VMonitorVDD_ParseMonitorDescription;
    ClientConfig.EvtIddCxMonitorGetDefaultDescriptionModes = VMonitorVDD_MonitorGetDefaultModes;
    ClientConfig.EvtIddCxMonitorQueryTargetModes           = VMonitorVDD_MonitorQueryTargetModes;
    ClientConfig.EvtIddCxMonitorAssignSwapChain            = VMonitorVDD_AssignSwapChain;
    ClientConfig.EvtIddCxMonitorUnassignSwapChain          = VMonitorVDD_UnassignSwapChain;

    // Must call IddCxDeviceInitConfig BEFORE WdfDeviceCreate
    NTSTATUS Status = IddCxDeviceInitConfig(DeviceInit, &ClientConfig);
    VMTRACE_STATUS("AddDevice: IddCxDeviceInitConfig", Status);
    if (!NT_SUCCESS(Status))
        return Status;

    WDF_OBJECT_ATTRIBUTES DeviceAttrib;
    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&DeviceAttrib, IndirectDeviceContextWrapper);

    WDFDEVICE Device;
    Status = WdfDeviceCreate(&DeviceInit, &DeviceAttrib, &Device);
    VMTRACE_STATUS("AddDevice: WdfDeviceCreate", Status);
    if (!NT_SUCCESS(Status))
        return Status;

    // カスタム IOCTL はこのデバイスでは使えない。
    //
    // IddCx がリクエストの振り分けを占有するため、
    // IddCxDeviceInitialize と WdfDeviceConfigureRequestDispatching は
    // 呼ぶ順序を入れ替えても、後から呼んだ方が STATUS_WDF_BUSY (0xC0200204)
    // で必ず弾かれる。UMDF には制御デバイスオブジェクトも無い（KMDF 専用）。
    //
    // 代わりに、UMDF がユーザーモードで動くことを利用して
    // 名前付きパイプで制御を受け付ける（ControlServer.cpp）。
    Status = IddCxDeviceInitialize(Device);
    VMTRACE_STATUS("AddDevice: IddCxDeviceInitialize", Status);
    if (!NT_SUCCESS(Status))
        return Status;

    auto* Ctx = WdfObjectGet_IndirectDeviceContextWrapper(Device);
    Ctx->AdapterObject = nullptr;
    Ctx->MonitorObject = nullptr;
    Ctx->Width         = 0;
    Ctx->Height        = 0;

    // アプリから接続・切断を指示できるようにする。
    //
    // 仮想モニターを常設にすると、スマホを繋いでいない間も
    // Windows からはディスプレイが 1 枚多く見えたままになる。
    // 実際に映せる相手がいるときだけモニターを出したいので、
    // アプリが状態を伝えられる口を用意する。
    Status = WdfDeviceCreateDeviceInterface(Device, &GUID_DEVINTERFACE_VMONITOR, nullptr);
    VMTRACE_STATUS("AddDevice: WdfDeviceCreateDeviceInterface", Status);
    if (!NT_SUCCESS(Status))
        return Status;

    VMTRACE("AddDevice: success");
    return STATUS_SUCCESS;
}

// ---------------------------------------------------------------
// D0Entry — デバイスに電源が入ったところでアダプターを初期化する
// ---------------------------------------------------------------

NTSTATUS VMonitorVDD_DeviceD0Entry(
    _In_ WDFDEVICE Device,
    _In_ WDF_POWER_DEVICE_STATE PreviousState)
{
    UNREFERENCED_PARAMETER(PreviousState);

    VMTRACE("D0Entry: enter");

    auto* Ctx = WdfObjectGet_IndirectDeviceContextWrapper(Device);

    // 一度初期化したら作り直さない（スリープ復帰でも再入する）
    if (Ctx->AdapterObject != nullptr)
        return STATUS_SUCCESS;

    // エンドポイント診断情報。
    // IddCx はこの構造体を検証するため、Size を含めて必ず埋める必要がある。
    // 未設定のまま渡すとアダプターの初期化が STATUS_UNSUCCESSFUL で失敗する。
    // IddCx 側が非 const ポインターを要求するため const は付けない
    static IDDCX_ENDPOINT_VERSION EndpointVersion =
    {
        sizeof(IDDCX_ENDPOINT_VERSION),
        1,  // MajorVer
        0,  // MinorVer
        0   // Build
    };

    IDDCX_ADAPTER_CAPS AdapterCaps;
    ZeroMemory(&AdapterCaps, sizeof(AdapterCaps));
    AdapterCaps.Size                 = sizeof(AdapterCaps);
    AdapterCaps.MaxMonitorsSupported = 1;

    // 表示帯域の上限。OS はアクティブな全モードの合計がこの値を超えないようにする。
    // 0 のままだとどのモードも有効にできない。
    // 単位はドライバの裁量なので、実質無制限として大きな値を入れる。
    AdapterCaps.MaxDisplayPipelineRate = 0xFFFFFFFFFFFFFFFFull;

    AdapterCaps.EndPointDiagnostics.Size             = sizeof(AdapterCaps.EndPointDiagnostics);
    AdapterCaps.EndPointDiagnostics.GammaSupport     = IDDCX_FEATURE_IMPLEMENTATION_NONE;
    AdapterCaps.EndPointDiagnostics.TransmissionType = IDDCX_TRANSMISSION_TYPE_WIRED_OTHER;

    AdapterCaps.EndPointDiagnostics.pEndPointFriendlyName     = L"vmonitor Virtual Display";
    AdapterCaps.EndPointDiagnostics.pEndPointManufacturerName = L"vmonitor";
    AdapterCaps.EndPointDiagnostics.pEndPointModelName        = L"vmonitor Virtual Display";
    AdapterCaps.EndPointDiagnostics.pFirmwareVersion          = &EndpointVersion;
    AdapterCaps.EndPointDiagnostics.pHardwareVersion          = &EndpointVersion;

    IDARG_IN_ADAPTER_INIT AdapterInit;
    ZeroMemory(&AdapterInit, sizeof(AdapterInit));
    AdapterInit.WdfDevice        = Device;
    AdapterInit.pCaps            = &AdapterCaps;
    AdapterInit.ObjectAttributes = WDF_NO_OBJECT_ATTRIBUTES;

    IDARG_OUT_ADAPTER_INIT AdapterInitOut;
    ZeroMemory(&AdapterInitOut, sizeof(AdapterInitOut));

    NTSTATUS Status = IddCxAdapterInitAsync(&AdapterInit, &AdapterInitOut);
    VMTRACE_STATUS("D0Entry: IddCxAdapterInitAsync", Status);
    if (!NT_SUCCESS(Status))
        return Status;

    Ctx->AdapterObject = AdapterInitOut.AdapterObject;

    // アプリからの接続 / 切断を受け付ける制御サーバーを立ち上げる
    VMonitorControl::Start(Device);

    VMTRACE("D0Entry: success");
    return STATUS_SUCCESS;
}
