#pragma once
#include "Driver.h"

// Monitor context
struct IndirectMonitorContextWrapper
{
    IDDCX_MONITOR Monitor;
    UINT Width;
    UINT Height;
    UINT RefreshRate;
};

WDF_DECLARE_CONTEXT_TYPE(IndirectMonitorContextWrapper);

// IddCx 1.10 callback declarations
EVT_IDD_CX_ADAPTER_INIT_FINISHED                 VMonitorVDD_AdapterInitFinished;
EVT_IDD_CX_ADAPTER_COMMIT_MODES                  VMonitorVDD_AdapterCommitModes;
EVT_IDD_CX_PARSE_MONITOR_DESCRIPTION             VMonitorVDD_ParseMonitorDescription;
EVT_IDD_CX_MONITOR_GET_DEFAULT_DESCRIPTION_MODES VMonitorVDD_MonitorGetDefaultModes;
EVT_IDD_CX_MONITOR_QUERY_TARGET_MODES            VMonitorVDD_MonitorQueryTargetModes;
EVT_IDD_CX_MONITOR_ASSIGN_SWAPCHAIN              VMonitorVDD_AssignSwapChain;
EVT_IDD_CX_MONITOR_UNASSIGN_SWAPCHAIN            VMonitorVDD_UnassignSwapChain;

/// <summary>
/// OS に提示するモード一覧の先頭を、接続してきた端末の解像度にする。
/// モニターを作る前に呼ぶこと。
/// </summary>
void VMonitorVDD_SetPreferredMode(UINT width, UINT height, UINT refreshRate);

NTSTATUS VMonitorVDD_CreateMonitor(IDDCX_ADAPTER Adapter, UINT Index);

// 作成したモニターオブジェクトを受け取る版。
// 切断 (IddCxMonitorDeparture) するにはハンドルを保持しておく必要がある。
NTSTATUS VMonitorVDD_CreateMonitorEx(IDDCX_ADAPTER Adapter, UINT Index, IDDCX_MONITOR* pMonitorOut);