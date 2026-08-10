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

        //
        // 接続を頼んできたプロセスの ID。
        //
        // このパイプは 1 コマンドごとに繋いで切るため、繋ぎっぱなしの
        // 接続が無い。つまりアプリが落ちても、こちらには何も伝わらない。
        // 強制終了された場合はアプリ側のコードが一切動かないので、
        // 後始末を頼むこともできない。
        //
        // 結果として、モニターが出たまま残る。ウィンドウがそちらへ飛び、
        // マウスが画面外へ抜けるようになる。
        //
        // そこで持ち主の ID を受け取り、こちらでその終了を待つ。
        // 0 なら見張らない（古いアプリとの組み合わせ）。
        //
        unsigned int OwnerProcessId;
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
