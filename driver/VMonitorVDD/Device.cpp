#include "Device.h"
#include "SwapChainProcessor.h"
#include "Trace.h"

// ---------------------------------------------------------------
// Monitor EDID and mode definitions
// ---------------------------------------------------------------

static const BYTE s_SampleMonitorEdid[] =
{
    0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00,
    0x31, 0xD8, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    0x05, 0x16, 0x01, 0x03, 0x6D, 0x32, 0x1C, 0x78,
    0xEA, 0x5E, 0xC0, 0xA4, 0x59, 0x4A, 0x98, 0x25,
    0x20, 0x50, 0x54, 0x00, 0x00, 0x00, 0xD1, 0xC0,
    0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01,
    0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x02, 0x3A,
    0x80, 0x18, 0x71, 0x38, 0x2D, 0x40, 0x58, 0x2C,
    0x45, 0x00, 0xF4, 0x19, 0x11, 0x00, 0x00, 0x1E,
    0x00, 0x00, 0x00, 0xFF, 0x00, 0x76, 0x6D, 0x6F,
    0x6E, 0x69, 0x74, 0x6F, 0x72, 0x0A, 0x20, 0x20,
    0x20, 0x20, 0x00, 0x00, 0x00, 0xFC, 0x00, 0x76,
    0x6D, 0x6F, 0x6E, 0x69, 0x74, 0x6F, 0x72, 0x0A,
    0x20, 0x20, 0x20, 0x20, 0x00, 0x00, 0x00, 0xFD,
    0x00, 0x18, 0x55, 0x18, 0x5E, 0x11, 0x00, 0x0A,
    0x20, 0x20, 0x20, 0x20, 0x20, 0x20, 0x01, 0xC2
};

struct MonitorMode { UINT Width; UINT Height; UINT RefreshRate; };

// 汎用のモード一覧。スマホ固有の解像度が分かる前の既定値として使う。
static const MonitorMode s_DefaultModes[] =
{
    { 1920, 1080, 60 },
    { 1280,  720, 60 },
    { 1080, 1920, 60 },
    {  720, 1280, 60 },
    { 2560, 1440, 60 },
    { 3840, 2160, 60 },
};

// 実際に OS へ提示するモード一覧。
//
// 接続してきたスマホの解像度をそのまま 1 番目に載せる。
// スマホの画面比率に合わないモードしか無いと、映像に帯が出たり
// 引き伸ばされたりするため、端末に合わせた 1 モードを優先する。
static MonitorMode s_SupportedModes[1 + ARRAYSIZE(s_DefaultModes)];
static UINT        s_SupportedModeCount = 0;

/// <summary>
/// OS へ提示するモード一覧を組み立てる。
/// 幅か高さが 0 の場合は既定の一覧を使う。
/// </summary>
/// <remarks>
/// <para>
/// 端末の解像度が分かっているときは、それ 1 つだけを提示する。
/// </para>
/// <para>
/// 一覧に複数のモードを載せて「0 番が推奨」と伝えても、
/// Windows はその通りに選んでくれない。実測では一覧の中で
/// 最も大きいモード (3840x2160) が選ばれ、1080x1920 を要求しても
/// 4K の仮想ディスプレイができてしまった。
/// 推奨インデックスはあくまで手がかりであって、指示ではない。
/// </para>
/// <para>
/// スマホに合わせた 1 枚を作るのが目的なので、選択の余地を残さない。
/// 帯が出たり引き伸ばされたりするより、モードが 1 つしかないほうがよい。
/// </para>
/// </remarks>
void VMonitorVDD_SetPreferredMode(UINT width, UINT height, UINT refreshRate)
{
    if (width >= 640 && height >= 480)
    {
        s_SupportedModes[0].Width       = width;
        s_SupportedModes[0].Height      = height;
        s_SupportedModes[0].RefreshRate = (refreshRate > 0 ? refreshRate : 60);
        s_SupportedModeCount            = 1;
        return;
    }

    // 端末の解像度が分からないとき用の控え
    for (UINT i = 0; i < ARRAYSIZE(s_DefaultModes); ++i)
        s_SupportedModes[i] = s_DefaultModes[i];

    s_SupportedModeCount = ARRAYSIZE(s_DefaultModes);
}

// ─────────────────────────────────────────────────────────────────────────
// EDID の生成
// ─────────────────────────────────────────────────────────────────────────

/// <summary>OS に渡す EDID。接続のたびに組み立て直す。</summary>
static BYTE s_MonitorEdid[128];

/// <summary>
/// 要求された解像度を素直に表す EDID を組み立てる。
/// </summary>
/// <remarks>
/// <para>
/// 出来合いのサンプル EDID を使い回していたときは、そこに書かれた
/// 3840x2160 のモニターとして扱われ、それより縦に長いモードを
/// 提示しても Windows に拒否された。1080x2400 を要求しても
/// 3840x2160 の仮想ディスプレイができてしまう。
/// （高さ 2100 までは通り、2200 以上で差し替えられることを実測）
/// </para>
/// <para>
/// スマホは縦長で、しかも機種ごとに違う。固定の EDID では表現できないので、
/// 接続してきた端末の解像度からその都度作る。
/// </para>
/// </remarks>
static void VMonitorVDD_BuildEdid(UINT width, UINT height, UINT refreshRate)
{
    ZeroMemory(s_MonitorEdid, sizeof(s_MonitorEdid));

    BYTE* e = s_MonitorEdid;

    // 固定ヘッダー
    e[0] = 0x00;
    for (int i = 1; i <= 6; ++i) e[i] = 0xFF;
    e[7] = 0x00;

    // メーカー ID "VMN"（5bit × 3、A=1）
    const UINT manufacturer = ((UINT)('V' - 'A' + 1) << 10)
                            | ((UINT)('M' - 'A' + 1) <<  5)
                            | ((UINT)('N' - 'A' + 1));
    e[8] = (BYTE)(manufacturer >> 8);
    e[9] = (BYTE)(manufacturer & 0xFF);

    e[10] = 0x01;   // 製品コード
    e[11] = 0x00;

    e[12] = 0x01;   // シリアル番号
    e[13] = 0x00;
    e[14] = 0x00;
    e[15] = 0x00;

    e[16] = 0x01;   // 製造週
    e[17] = 0x24;   // 製造年 (1990 + 36 = 2026)

    e[18] = 0x01;   // EDID 1.4
    e[19] = 0x04;

    // デジタル入力・8bit・インターフェース未定義
    e[20] = 0x80 | 0x20;

    // 画面の物理サイズ (cm)。実物は無いので、比率だけ合わせておく。
    // 0 にすると「サイズ不明」となり、拡大率の判断が働かなくなる。
    UINT widthCm  = 8;
    UINT heightCm = (height > 0 && width > 0)
        ? (widthCm * height) / width
        : 15;
    if (heightCm == 0)  heightCm = 1;
    if (heightCm > 255) heightCm = 255;

    e[21] = (BYTE)widthCm;
    e[22] = (BYTE)heightCm;

    e[23] = 0x78;   // ガンマ 2.2
    e[24] = 0x06;   // 推奨タイミングは 1 番目の詳細タイミング / sRGB

    // 色度。一般的な sRGB 相当の値をそのまま使う。
    e[25] = 0xEE; e[26] = 0x91; e[27] = 0xA3; e[28] = 0x54; e[29] = 0x4C;
    e[30] = 0x99; e[31] = 0x26; e[32] = 0x0F; e[33] = 0x50; e[34] = 0x54;

    // 既定タイミング・標準タイミングは持たない。
    // 提示するモードは 1 つだけなので、余計な候補を作らない。
    e[35] = 0x00; e[36] = 0x00; e[37] = 0x00;
    for (int i = 38; i <= 53; i += 2) { e[i] = 0x01; e[i + 1] = 0x01; }

    // ── 詳細タイミング（これが「このモニターの本来の解像度」になる）──

    UINT vsync   = (refreshRate > 0 ? refreshRate : 60);
    UINT hBlank  = width / 4;
    UINT vBlank  = 45;
    UINT hTotal  = width  + hBlank;
    UINT vTotal  = height + vBlank;

    // ピクセルクロックは 10kHz 単位で書く
    UINT pixelClock10kHz = (hTotal * vTotal * vsync) / 10000;
    if (pixelClock10kHz > 0xFFFF) pixelClock10kHz = 0xFFFF;

    UINT hFront = hBlank / 4;
    UINT hSync  = hBlank / 2;
    UINT vFront = 3;
    UINT vSync  = 5;

    BYTE* d = &e[54];
    d[0] = (BYTE)(pixelClock10kHz & 0xFF);
    d[1] = (BYTE)(pixelClock10kHz >> 8);
    d[2] = (BYTE)(width  & 0xFF);
    d[3] = (BYTE)(hBlank & 0xFF);
    d[4] = (BYTE)(((width >> 8) << 4) | ((hBlank >> 8) & 0x0F));
    d[5] = (BYTE)(height & 0xFF);
    d[6] = (BYTE)(vBlank & 0xFF);
    d[7] = (BYTE)(((height >> 8) << 4) | ((vBlank >> 8) & 0x0F));
    d[8] = (BYTE)(hFront & 0xFF);
    d[9] = (BYTE)(hSync  & 0xFF);
    d[10] = (BYTE)(((vFront & 0x0F) << 4) | (vSync & 0x0F));
    d[11] = (BYTE)((((hFront >> 8) & 0x03) << 6) | (((hSync >> 8) & 0x03) << 4));
    d[12] = (BYTE)(widthCm  * 10 & 0xFF);
    d[13] = (BYTE)(heightCm * 10 & 0xFF);
    d[14] = (BYTE)(((((widthCm * 10) >> 8) & 0x0F) << 4) | ((((heightCm * 10) >> 8) & 0x0F)));
    d[15] = 0x00;   // 水平ボーダー
    d[16] = 0x00;   // 垂直ボーダー
    d[17] = 0x1E;   // デジタル・セパレートシンク、極性はどちらも正

    // ── 動作範囲。提示するモードが必ず収まるように広めにとる ──

    BYTE* r = &e[72];
    r[0] = 0x00; r[1] = 0x00; r[2] = 0x00; r[3] = 0xFD; r[4] = 0x00;
    r[5] = 24;    // 垂直の下限 (Hz)
    r[6] = 144;   // 垂直の上限 (Hz)
    r[7] = 24;    // 水平の下限 (kHz)
    r[8] = 255;   // 水平の上限 (kHz)
    r[9] = 0xFF;  // ピクセルクロックの上限 (×10 MHz)
    r[10] = 0x01; // 範囲の指定のみ（タイミング計算式は持たない）
    r[11] = 0x0A;
    for (int i = 12; i <= 17; ++i) r[i] = 0x20;

    // ── モニター名 ──

    BYTE* n = &e[90];
    n[0] = 0x00; n[1] = 0x00; n[2] = 0x00; n[3] = 0xFC; n[4] = 0x00;
    const char* name = "vmonitor";
    int written = 0;
    for (; name[written] != '\0' && written < 13; ++written)
        n[5 + written] = (BYTE)name[written];
    if (written < 13) n[5 + written++] = 0x0A;
    for (; written < 13; ++written) n[5 + written] = 0x20;

    // ── 4 つ目の記述子は使わない ──

    BYTE* u = &e[108];
    u[0] = 0x00; u[1] = 0x00; u[2] = 0x00; u[3] = 0x10; u[4] = 0x00;
    for (int i = 5; i <= 17; ++i) u[i] = 0x20;

    e[126] = 0x00;  // 拡張ブロックなし

    // チェックサム。128 バイトの合計が 0 になるようにする。
    BYTE sum = 0;
    for (int i = 0; i < 127; ++i) sum = (BYTE)(sum + e[i]);
    e[127] = (BYTE)(256 - sum);
}

/// <summary>
/// モードの映像信号情報を組み立てる。
/// </summary>
/// <remarks>
/// リフレッシュレートは Hz そのまま（60）で、分母は 1 にする。
///
/// 6000/100 のような等価な表現にすると、モニターモードと
/// ターゲットモードの突き合わせに失敗し、IddCxMonitorArrival が
/// STATUS_INVALID_PARAMETER で失敗する。OS は両者の積集合を
/// 利用可能なモードとして扱うため、表現を揃える必要がある。
/// </remarks>
static void FillVideoSignalInfo(
    DISPLAYCONFIG_VIDEO_SIGNAL_INFO& si,
    UINT width, UINT height, UINT vsyncHz)
{
    ZeroMemory(&si, sizeof(si));

    si.totalSize.cx  = si.activeSize.cx = width;
    si.totalSize.cy  = si.activeSize.cy = height;

    si.AdditionalSignalInfo.vSyncFreqDivider = 1;
    si.AdditionalSignalInfo.videoStandard    = 255;

    si.vSyncFreq.Numerator   = vsyncHz;
    si.vSyncFreq.Denominator = 1;
    si.hSyncFreq.Numerator   = vsyncHz * height;
    si.hSyncFreq.Denominator = 1;

    si.scanLineOrdering = DISPLAYCONFIG_SCANLINE_ORDERING_PROGRESSIVE;
    si.pixelRate        = (UINT64)vsyncHz * width * height;
}

/// <summary>
/// モニター記述子（EDID）由来のモードとして映像信号情報を組み立てる。
/// </summary>
/// <remarks>
/// ターゲットモードとは表現の仕方が違う点に注意。
///
/// モニターモードは実際のディスプレイのタイミングを表すので、
/// 走査の帰線期間（ブランキング）を含む総ピクセル数を totalSize に入れ、
/// 同期周波数はピクセルクロックとの比で表す。
/// vSyncFreqDivider も 0 にする。
///
/// ターゲットモードと同じ簡易表現で埋めると、OS 側の突き合わせに失敗し、
/// IddCxMonitorArrival が STATUS_INVALID_PARAMETER で失敗する。
/// </remarks>
static void FillMonitorModeSignalInfo(
    DISPLAYCONFIG_VIDEO_SIGNAL_INFO& si,
    UINT width, UINT height, UINT vsyncHz)
{
    ZeroMemory(&si, sizeof(si));

    // 一般的な帰線期間を仮定して総ピクセル数を求める。
    // 実物のモニターではないので、内部で辻褄が合っていればよい。
    const UINT hTotal = width  + width / 4;   // 水平は 25% 程度
    const UINT vTotal = height + 45;          // 垂直は 45 ライン程度

    const UINT64 pixelClock = (UINT64)hTotal * vTotal * vsyncHz;

    si.pixelRate = pixelClock;

    si.hSyncFreq.Numerator   = (UINT)pixelClock;
    si.hSyncFreq.Denominator = hTotal;

    si.vSyncFreq.Numerator   = (UINT)pixelClock;
    si.vSyncFreq.Denominator = hTotal * vTotal;

    si.activeSize.cx = width;
    si.activeSize.cy = height;
    si.totalSize.cx  = hTotal;
    si.totalSize.cy  = vTotal;

    si.AdditionalSignalInfo.videoStandard    = 255;
    si.AdditionalSignalInfo.vSyncFreqDivider = 0;

    si.scanLineOrdering = DISPLAYCONFIG_SCANLINE_ORDERING_PROGRESSIVE;
}

// ---------------------------------------------------------------
// IddCx callbacks
// ---------------------------------------------------------------

NTSTATUS VMonitorVDD_AdapterInitFinished(
    IDDCX_ADAPTER AdapterObject,
    const IDARG_IN_ADAPTER_INIT_FINISHED* pInArgs)
{
    VMTRACE("CB: AdapterInitFinished");
    UNREFERENCED_PARAMETER(AdapterObject);

    VMTRACE_STATUS("AdapterInitFinished", pInArgs->AdapterInitStatus);

    // ここではモニターを作らない。
    //
    // 仮想モニターを常設にすると、スマホを繋いでいない間も Windows からは
    // ディスプレイが 1 枚多く見えたままになり、ウィンドウがそちらへ飛んだり
    // マウスが画面外へ抜けたりする。
    // モニターの接続はアプリからの IOCTL_VMONITOR_CONNECT で行う。
    return STATUS_SUCCESS;
}

NTSTATUS VMonitorVDD_AdapterCommitModes(
    IDDCX_ADAPTER AdapterObject,
    const IDARG_IN_COMMITMODES* pInArgs)
{
    VMTRACE("CB: AdapterCommitModes");
    UNREFERENCED_PARAMETER(AdapterObject);
    UNREFERENCED_PARAMETER(pInArgs);
    return STATUS_SUCCESS;
}

NTSTATUS VMonitorVDD_ParseMonitorDescription(
    const IDARG_IN_PARSEMONITORDESCRIPTION* pInArgs,
    IDARG_OUT_PARSEMONITORDESCRIPTION* pOutArgs)
{
    VMTRACE("CB: ParseMonitorDescription");

    // 報告する件数は常に「全モード数」。
    //
    // OS はまずバッファ無しで呼んで件数を聞き、その件数ぶんの
    // バッファを用意して呼び直す。ここで実際に書けた数に
    // 上書きしてしまうと、OS の把握するモード数と食い違い、
    // IddCxMonitorArrival が STATUS_INVALID_PARAMETER で失敗する。
    pOutArgs->MonitorModeBufferOutputCount = s_SupportedModeCount;

    if (pInArgs->MonitorModeBufferInputCount < s_SupportedModeCount)
    {
        // 件数を聞かれただけなら成功。バッファがあるのに足りない場合は
        // 「小さすぎる」と伝える（黙って切り詰めない）。
        return (pInArgs->MonitorModeBufferInputCount > 0)
            ? STATUS_BUFFER_TOO_SMALL
            : STATUS_SUCCESS;
    }

    for (UINT i = 0; i < s_SupportedModeCount; i++)
    {
        IDDCX_MONITOR_MODE& mode = pInArgs->pMonitorModes[i];
        mode.Size   = sizeof(IDDCX_MONITOR_MODE);
        mode.Origin = IDDCX_MONITOR_MODE_ORIGIN_MONITORDESCRIPTOR;
        FillMonitorModeSignalInfo(
            mode.MonitorVideoSignalInfo,
            s_SupportedModes[i].Width,
            s_SupportedModes[i].Height,
            s_SupportedModes[i].RefreshRate);
    }

    // 先頭（＝接続してきた端末の解像度）を推奨モードにする
    pOutArgs->PreferredMonitorModeIdx = 0;
    return STATUS_SUCCESS;
}

NTSTATUS VMonitorVDD_MonitorGetDefaultModes(
    IDDCX_MONITOR MonitorObject,
    const IDARG_IN_GETDEFAULTDESCRIPTIONMODES* pInArgs,
    IDARG_OUT_GETDEFAULTDESCRIPTIONMODES* pOutArgs)
{
    VMTRACE("CB: MonitorGetDefaultModes");
    UNREFERENCED_PARAMETER(MonitorObject);
    UNREFERENCED_PARAMETER(pInArgs);
    UNREFERENCED_PARAMETER(pOutArgs);
    return STATUS_SUCCESS;
}

NTSTATUS VMonitorVDD_MonitorQueryTargetModes(
    IDDCX_MONITOR MonitorObject,
    const IDARG_IN_QUERYTARGETMODES* pInArgs,
    IDARG_OUT_QUERYTARGETMODES* pOutArgs)
{
    VMTRACE("CB: MonitorQueryTargetModes");
    UNREFERENCED_PARAMETER(MonitorObject);

    // モニターモードと同じ扱い。件数は常に全モード数を返す。
    pOutArgs->TargetModeBufferOutputCount = s_SupportedModeCount;

    if (pInArgs->TargetModeBufferInputCount < s_SupportedModeCount)
    {
        return (pInArgs->TargetModeBufferInputCount > 0)
            ? STATUS_BUFFER_TOO_SMALL
            : STATUS_SUCCESS;
    }

    for (UINT i = 0; i < s_SupportedModeCount; i++)
    {
        IDDCX_TARGET_MODE& mode = pInArgs->pTargetModes[i];
        mode.Size = sizeof(IDDCX_TARGET_MODE);
        FillVideoSignalInfo(
            mode.TargetVideoSignalInfo.targetVideoSignalInfo,
            s_SupportedModes[i].Width,
            s_SupportedModes[i].Height,
            s_SupportedModes[i].RefreshRate);
    }

    return STATUS_SUCCESS;
}

// EVT_IDD_CX_MONITOR_ASSIGN_SWAPCHAIN: called with (IDDCX_MONITOR, IDARG_IN_SETSWAPCHAIN*)
// EVT_IDD_CX_MONITOR_ASSIGN_SWAPCHAIN
//
// OS がスワップチェーンを渡してくる。ここからフレームを引き取り続けるのは
// ドライバの義務で、放置すると IddCx はしばらくしてドライバを終了させる。
// 実際の引き取りは専用スレッドで行い、この関数はすぐ返す。
NTSTATUS VMonitorVDD_AssignSwapChain(
    IDDCX_MONITOR MonitorObject,
    const IDARG_IN_SETSWAPCHAIN* pInArgs)
{
    UNREFERENCED_PARAMETER(MonitorObject);

    if (pInArgs == nullptr)
        return STATUS_INVALID_PARAMETER;

    bool started = VMonitorSwapChain::Start(
        pInArgs->hSwapChain,
        pInArgs->RenderAdapterLuid,
        pInArgs->hNextSurfaceAvailable);

    if (!started)
    {
        // 作り直せば直る見込みがあるときの返し方。
        // これ以外のエラーを返すと、OS はドライバをバグチェックする。
        VMTRACE("AssignSwapChain: start failed; abandoning swapchain");
        return STATUS_GRAPHICS_INDIRECT_DISPLAY_ABANDON_SWAPCHAIN;
    }

    return STATUS_SUCCESS;
}

// EVT_IDD_CX_MONITOR_UNASSIGN_SWAPCHAIN: called with (IDDCX_MONITOR)
NTSTATUS VMonitorVDD_UnassignSwapChain(
    IDDCX_MONITOR MonitorObject)
{
    UNREFERENCED_PARAMETER(MonitorObject);

    VMonitorSwapChain::Stop();

    return STATUS_SUCCESS;
}

NTSTATUS VMonitorVDD_CreateMonitor(IDDCX_ADAPTER Adapter, UINT Index)
{
    return VMonitorVDD_CreateMonitorEx(Adapter, Index, nullptr);
}

NTSTATUS VMonitorVDD_CreateMonitorEx(IDDCX_ADAPTER Adapter, UINT Index, IDDCX_MONITOR* pMonitorOut)
{
    UNREFERENCED_PARAMETER(Index);

    // このモニターのコンテナ ID。
    //
    // ゼロのままだと IddCxMonitorCreate が STATUS_INVALID_PARAMETER で失敗する。
    // 仮想モニターは 1 つだけで、着脱しても同じディスプレイとして
    // 扱ってほしいので、固定の GUID を使う。
    // {7F3E9C21-4A85-4D6B-9E2F-1C8D5A7B3E40}
    static const GUID s_MonitorContainerId =
        { 0x7f3e9c21, 0x4a85, 0x4d6b, { 0x9e, 0x2f, 0x1c, 0x8d, 0x5a, 0x7b, 0x3e, 0x40 } };

    IDDCX_MONITOR_INFO monitorInfo;
    ZeroMemory(&monitorInfo, sizeof(monitorInfo));
    monitorInfo.Size               = sizeof(monitorInfo);
    // 動作実績のあるサンプルに合わせる。INDIRECT_WIRED では受け付けられない。
    monitorInfo.MonitorType        = DISPLAYCONFIG_OUTPUT_TECHNOLOGY_HDMI;
    monitorInfo.ConnectorIndex     = 0;
    monitorInfo.MonitorContainerId = s_MonitorContainerId;

    monitorInfo.MonitorDescription.Size     = sizeof(monitorInfo.MonitorDescription);
    monitorInfo.MonitorDescription.Type     = IDDCX_MONITOR_DESCRIPTION_TYPE_EDID;
    // 提示するモードに合わせた EDID を作る。
    // モニターを作る直前に組み立てないと、前回の解像度のまま渡してしまう。
    VMonitorVDD_BuildEdid(
        s_SupportedModes[0].Width,
        s_SupportedModes[0].Height,
        s_SupportedModes[0].RefreshRate);

    monitorInfo.MonitorDescription.DataSize = sizeof(s_MonitorEdid);
    monitorInfo.MonitorDescription.pData    = s_MonitorEdid;

    IDARG_IN_MONITORCREATE monitorCreate;
    ZeroMemory(&monitorCreate, sizeof(monitorCreate));
    monitorCreate.ObjectAttributes = WDF_NO_OBJECT_ATTRIBUTES;
    monitorCreate.pMonitorInfo     = &monitorInfo;

    IDARG_OUT_MONITORCREATE monitorCreateOut;
    ZeroMemory(&monitorCreateOut, sizeof(monitorCreateOut));
    NTSTATUS status = IddCxMonitorCreate(Adapter, &monitorCreate, &monitorCreateOut);
    VMTRACE_STATUS("CreateMonitor: IddCxMonitorCreate", status);
    if (!NT_SUCCESS(status))
        return status;

    IDARG_OUT_MONITORARRIVAL arrivalOut;
    ZeroMemory(&arrivalOut, sizeof(arrivalOut));

    status = IddCxMonitorArrival(monitorCreateOut.MonitorObject, &arrivalOut);
    VMTRACE_STATUS("CreateMonitor: IddCxMonitorArrival", status);
    if (!NT_SUCCESS(status))
        return status;

    if (pMonitorOut != nullptr)
        *pMonitorOut = monitorCreateOut.MonitorObject;

    return STATUS_SUCCESS;
}
