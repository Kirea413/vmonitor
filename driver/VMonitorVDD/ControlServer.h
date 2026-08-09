#pragma once
#include <windows.h>

//
// アプリからドライバへ「接続 / 切断」を伝えるための制御サーバー。
//
// IddCx はデバイスのリクエスト振り分けを占有するため、
// 同じデバイス上でカスタム IOCTL を受け取ることができない
// （IddCxDeviceInitialize と WdfDeviceConfigureRequestDispatching が
//   互いに STATUS_WDF_BUSY で弾き合う）。
// UMDF は制御デバイスオブジェクトにも対応していない（KMDF 専用）。
//
// 一方 UMDF ドライバはユーザーモードで動くので、普通の Win32 の
// プロセス間通信がそのまま使える。ここでは名前付きパイプを使う。
//

namespace VMonitorControl
{
    // パイプ名。アプリ側 (VirtualDisplayControl.cs) と一致させること。
    constexpr const wchar_t* PipeName = L"\\\\.\\pipe\\vmonitor-control";

    // 操作コード
    enum Op : unsigned int
    {
        OpConnect    = 1,
        OpDisconnect = 2,
        OpGetState   = 3,
    };

#pragma pack(push, 1)
    struct Command
    {
        unsigned int Operation;
        unsigned int Width;
        unsigned int Height;
        unsigned int RefreshRate;
    };

    struct Response
    {
        unsigned int Succeeded;   // 0 = 失敗, 1 = 成功
        unsigned int Connected;   // 現在モニターが接続中か
        unsigned int Width;
        unsigned int Height;
    };
#pragma pack(pop)

    /// <summary>制御サーバーを開始する。多重呼び出しは無視される。</summary>
    void Start(WDFDEVICE Device);

    /// <summary>制御サーバーを停止する。</summary>
    void Stop();
}
