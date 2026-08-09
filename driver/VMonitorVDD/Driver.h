#pragma once

// UMDF_USING_NTSTATUS is defined by the WDK toolset on the command line.
// Guard against redefinition.
#ifndef UMDF_USING_NTSTATUS
#define UMDF_USING_NTSTATUS
#endif

// IddCx のバージョン指定 - IddCx.h より前に置く必要がある
//
// INF の UmdfExtensions = IddCx0102 と、UMDF 2.15 に合わせて 1.2 を使う。
// IddCx 1.10 のヘッダは新しい WDF を前提にしており、UMDF 2.15 とは組み合わせられない。
#define IDDCX_VERSION_MAJOR 1
#define IDDCX_VERSION_MINOR 2
#define IDDCX_MINIMUM_VERSION_REQUIRED 2

#include <windows.h>
#include <wdf.h>
#include <IddCx.h>

#include <initguid.h>
#include "VMonitorProtocol.h"

// Device context
struct IndirectDeviceContextWrapper
{
    IDDCX_ADAPTER AdapterObject;

    // 現在「接続中」の仮想モニター。未接続なら nullptr。
    // スマホが繋がっている間だけ有効になる。
    IDDCX_MONITOR MonitorObject;

    // 接続中の解像度（状態問い合わせ用）
    UINT Width;
    UINT Height;
};

WDF_DECLARE_CONTEXT_TYPE(IndirectDeviceContextWrapper);

// Entry points
extern "C" DRIVER_INITIALIZE DriverEntry;
EVT_WDF_DRIVER_DEVICE_ADD VMonitorVDD_AddDevice;
EVT_WDF_DEVICE_D0_ENTRY   VMonitorVDD_DeviceD0Entry;