#pragma once

//
// vmonitor 仮想ディスプレイドライバの制御プロトコル。
//
// PC アプリ (VMonitor.UI) がこのインターフェースを開き、
// スマホの接続状態に合わせて仮想モニターを出したり消したりする。
//
// 仮想モニターを常設にすると、スマホを繋いでいない間も Windows からは
// ディスプレイが 1 枚多く見えたままになり、ウィンドウがそちらへ飛んだり
// マウスが画面外へ抜けたりする。実際に映せる相手がいるときだけ
// モニターを「接続」状態にする。
//
// この定義は C# 側 (VMonitor.Driver/VirtualDisplayControl.cs) と対になっている。
// 変更するときは両方を揃えること。
//

// 制御用デバイスインターフェースの GUID
// {B5B0A4F1-8E4C-4E7B-9A2D-6F3C1D8E5A70}
DEFINE_GUID(GUID_DEVINTERFACE_VMONITOR,
    0xb5b0a4f1, 0x8e4c, 0x4e7b, 0x9a, 0x2d, 0x6f, 0x3c, 0x1d, 0x8e, 0x5a, 0x70);

//
// IOCTL コード
//
// FILE_DEVICE_UNKNOWN (0x22) + 独自機能番号 (0x800 以降) + METHOD_BUFFERED
//

#define IOCTL_VMONITOR_CONNECT \
    CTL_CODE(FILE_DEVICE_UNKNOWN, 0x800, METHOD_BUFFERED, FILE_ANY_ACCESS)

#define IOCTL_VMONITOR_DISCONNECT \
    CTL_CODE(FILE_DEVICE_UNKNOWN, 0x801, METHOD_BUFFERED, FILE_ANY_ACCESS)

#define IOCTL_VMONITOR_GET_STATE \
    CTL_CODE(FILE_DEVICE_UNKNOWN, 0x802, METHOD_BUFFERED, FILE_ANY_ACCESS)

//
// IOCTL_VMONITOR_CONNECT の入力
//
// スマホ側の表示解像度を渡す。ドライバはこの解像度で仮想モニターを
// 「接続」させる。
//
#pragma pack(push, 1)
typedef struct _VMONITOR_CONNECT_INFO
{
    unsigned int Width;        // 幅（ピクセル）
    unsigned int Height;       // 高さ（ピクセル）
    unsigned int RefreshRate;  // リフレッシュレート（Hz）。0 なら 60 を使う
} VMONITOR_CONNECT_INFO;

//
// IOCTL_VMONITOR_GET_STATE の出力
//
typedef struct _VMONITOR_STATE
{
    unsigned int Connected;    // 0 = 未接続, 1 = 接続中
    unsigned int Width;
    unsigned int Height;
} VMONITOR_STATE;
#pragma pack(pop)
