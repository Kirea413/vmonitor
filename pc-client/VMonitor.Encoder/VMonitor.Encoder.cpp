/**
 * VMonitor.Encoder.dll
 *
 * Windows Media Foundation MFT H.264 ハードウェアエンコーダー
 * C# の Streamer から P/Invoke で呼び出される。
 *
 * 依存: mf.lib, mfplat.lib, mfuuid.lib, mfreadwrite.lib
 */

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <d3d11.h>
#include <dxgi1_2.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mftransform.h>
#include <mferror.h>
#include <mfreadwrite.h>
#include <icodecapi.h>   // ICodecAPI 本体（codecapi.h は GUID 定義のみ）
#include <codecapi.h>
#include <wrl/client.h>
#include <stdint.h>
#include <vector>
#include <thread>

#pragma comment(lib, "mf.lib")
#pragma comment(lib, "mfplat.lib")
#pragma comment(lib, "mfuuid.lib")
#pragma comment(lib, "mfreadwrite.lib")
#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "dxgi.lib")

using Microsoft::WRL::ComPtr;

// ─────────────────────────────────────────────────────────────────────────────
// グローバル状態
// ─────────────────────────────────────────────────────────────────────────────

static ComPtr<IMFTransform>          g_encoder;
static ComPtr<IMFMediaType>          g_inputType;
static ComPtr<IMFMediaType>          g_outputType;
static ComPtr<IMFMediaEventGenerator> g_eventGen;   // 非同期 MFT のイベント源
static bool                   g_initialized = false;
static bool                   g_isAsync     = false; // ハードウェア MFT は非同期
static int                    g_pendingNeedInput = 0; // 受信済みで未消化の NeedInput 数
static int                    g_width       = 0;
static int                    g_height      = 0;
static int                    g_bitrateBps  = 0;
static int                    g_fps         = 0;
static int64_t                g_sampleCount = 0;
static CRITICAL_SECTION       g_cs;

// ─────────────────────────────────────────────────────────────────────────────
// クリティカルセクションは DLL ロード時に用意する。
//
// 遅延初期化にすると、初期化そのものが競合しうる。エンコーダーは
// ストリーマーのバックグラウンドスレッドから呼ばれるため、
// ロックが使える状態であることを最初から保証しておく。
// ─────────────────────────────────────────────────────────────────────────────
BOOL WINAPI DllMain(HINSTANCE /*hinstDLL*/, DWORD fdwReason, LPVOID /*lpvReserved*/)
{
    switch (fdwReason)
    {
    case DLL_PROCESS_ATTACH:
        InitializeCriticalSection(&g_cs);
        break;

    case DLL_PROCESS_DETACH:
        DeleteCriticalSection(&g_cs);
        break;
    }

    return TRUE;
}

// ─────────────────────────────────────────────────────────────────────────────
// ヘルパー: H.264 エンコーダー MFT を探して作成する
// GPU ハードウェアエンコーダーを優先し、失敗時はソフトウェアにフォールバック
// ─────────────────────────────────────────────────────────────────────────────

static HRESULT CreateH264Encoder(IMFTransform** ppTransform)
{
    // H.264 エンコーダー MFT を列挙する（ハードウェア優先）
    MFT_REGISTER_TYPE_INFO outputTypeInfo = {};
    outputTypeInfo.guidMajorType = MFMediaType_Video;
    outputTypeInfo.guidSubtype   = MFVideoFormat_H264;

    UINT32 count = 0;
    IMFActivate** ppActivate = nullptr;

    // 環境変数 VMONITOR_ENCODER で選び方を変えられるようにしておく。
    //   hw : ハードウェア（非同期）を優先する（既定）
    //   sw : ソフトウェア（同期）を優先する
    // 切り分けのために、推測ではなく実際に切り替えて測れるようにしてある。
    bool preferSoftware = false;
    {
        char mode[16] = {};
        size_t len = 0;
        if (getenv_s(&len, mode, sizeof(mode), "VMONITOR_ENCODER") == 0 && len > 0)
            preferSoftware = (_stricmp(mode, "sw") == 0);
    }

    // 既定ではハードウェア (非同期) の MFT を優先する。
    //
    // 以前は非同期の扱いを誤っていて動かず、ソフトウェアを既定にしていた。
    // 原因は 2 つあり、どちらも解消済み。
    //   - 1 枚入れるたびに出力を待っていた（ハードウェアは数枚溜めてから出す）
    //   - MF_E_TRANSFORM_STREAM_CHANGE（出力形式の再交渉）を処理していなかった
    //
    // 実測では、符号化そのものの時間が 1920x1080 で
    // ソフトウェア 12.7ms に対しハードウェア 1.1ms と一桁違う。
    // 画面を見ながら操作する用途では、この差がそのまま遅れの差になる。
    HRESULT hr = preferSoftware ? E_FAIL : MFTEnumEx(
        MFT_CATEGORY_VIDEO_ENCODER,
        MFT_ENUM_FLAG_HARDWARE | MFT_ENUM_FLAG_ASYNCMFT | MFT_ENUM_FLAG_SORTANDFILTER,
        nullptr,        // 入力タイプ: 任意
        &outputTypeInfo,
        &ppActivate,
        &count);

    if (FAILED(hr) || count == 0)
    {
        // ハードウェアが無い環境ではソフトウェア (同期) を使う
        if (ppActivate) { CoTaskMemFree(ppActivate); ppActivate = nullptr; }
        hr = MFTEnumEx(
            MFT_CATEGORY_VIDEO_ENCODER,
            MFT_ENUM_FLAG_SYNCMFT | MFT_ENUM_FLAG_SORTANDFILTER,
            nullptr,
            &outputTypeInfo,
            &ppActivate,
            &count);
    }

    if (FAILED(hr) || count == 0)
    {
        // 最後の手段: 種別を問わず列挙する
        if (ppActivate) { CoTaskMemFree(ppActivate); ppActivate = nullptr; }
        hr = MFTEnumEx(
            MFT_CATEGORY_VIDEO_ENCODER,
            MFT_ENUM_FLAG_SORTANDFILTER,
            nullptr,
            &outputTypeInfo,
            &ppActivate,
            &count);
    }

    if (FAILED(hr) || count == 0)
    {
        if (ppActivate) CoTaskMemFree(ppActivate);
        return MF_E_TOPO_CODEC_NOT_FOUND;
    }

    // 最初のエンコーダーをアクティブ化する
    hr = ppActivate[0]->ActivateObject(IID_PPV_ARGS(ppTransform));

    for (UINT32 i = 0; i < count; ++i)
        ppActivate[i]->Release();
    CoTaskMemFree(ppActivate);

    if (FAILED(hr)) return hr;

    // ── 非同期 MFT のアンロック ─────────────────────────────────────────
    //
    // GPU の H.264 エンコーダーは非同期 MFT として実装されている。
    // 非同期 MFT は MF_TRANSFORM_ASYNC_UNLOCK を立てるまでロックされており、
    // その前に SetOutputType を呼ぶと MF_E_TRANSFORM_ASYNC_LOCKED (0xC00D6D77)
    // で失敗する。ここでアンロックし、あわせて低遅延モードを要求する。
    {
        ComPtr<IMFAttributes> attrs;
        if (SUCCEEDED((*ppTransform)->GetAttributes(attrs.GetAddressOf())) && attrs)
        {
            UINT32 isAsync = 0;
            attrs->GetUINT32(MF_TRANSFORM_ASYNC, &isAsync);

            if (isAsync)
            {
                HRESULT unlockHr = attrs->SetUINT32(MF_TRANSFORM_ASYNC_UNLOCK, TRUE);
                if (FAILED(unlockHr)) return unlockHr;
                g_isAsync = true;
            }
            else
            {
                g_isAsync = false;
            }

            // 画面転送用途なので、スループットより遅延を優先する
            attrs->SetUINT32(MF_LOW_LATENCY, TRUE);
        }
    }

    return S_OK;
}

// ─────────────────────────────────────────────────────────────────────────────
// ヘルパー: ICodecAPI 経由でエンコード設定を適用する
//
// レート制御や GOP 長は「メディアタイプの属性」ではなく
// コーデックのプロパティなので、IMFMediaType に載せても無視されるか
// SetOutputType 自体を失敗させる。ICodecAPI で設定するのが正しい。
// ─────────────────────────────────────────────────────────────────────────────

static int g_pendingHaveOutput = 0; // 受信済みで未消化の HaveOutput 数

// ── 診断カウンター（VMonitorEncoderGetDiag で取得する） ──────────────────
static int      g_diagEventsSeen        = 0;
static int      g_diagNeedInputSeen     = 0;
static int      g_diagHaveOutputSeen    = 0;
static int      g_diagOtherEventSeen    = 0;
static uint32_t g_diagLastOtherEvent    = 0;
static int      g_diagProcessInputCalls = 0;
static int      g_diagProcessInputFails = 0;
static int      g_diagProcessOutputCalls = 0;
static HRESULT  g_diagLastHr            = S_OK;
static HRESULT  g_diagLastGetEventHr    = S_OK;

// 直近 1 枚ぶんの内訳（高分解能カウンターの刻み数）
static long long g_diagConvertTicks = 0;   // BGRA → NV12 の変換
static long long g_diagMftTicks     = 0;   // MFT への投入と取り出し

// ─────────────────────────────────────────────────────────────────────────────
// 非同期 MFT のイベント処理
//
// 非同期 MFT は「入力をよこせ (METransformNeedInput)」「出力があるぞ
// (METransformHaveOutput)」をイベントで通知してくる。求められる前に
// ProcessInput / ProcessOutput を呼んでも失敗するため、イベントを待つ。
//
// GetEvent のブロッキング版は待ち続けてしまうので、NO_WAIT でポーリングして
// 上限時間で打ち切る。取りこぼしたイベントはカウンターに貯めて次回消化する。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// 溜まっているイベントを読み取ってカウンターに反映する。
/// </summary>
/// <param name="timeoutMs">
/// 何も来ていないときに待つ上限。0 なら待たずにその場の分だけ取る。
/// </param>
/// <remarks>
/// 「特定のイベントが来るまで待つ」という書き方はしない。
/// ハードウェアのエンコーダーは数フレーム溜めてから出力を出すので、
/// 1 枚入れるたびに出力を待つと、出るはずのないものを待って空振りする。
/// 実測では 3 枚入れたところで入力要求が止まり、以降は 200ms の
/// タイムアウトを繰り返して 1 枚も出力できなかった。
/// ここでは来ているものを溜めるだけにして、使う側が判断する。
/// </remarks>
static HRESULT AsyncPumpEvents(int timeoutMs)
{
    if (!g_eventGen) return S_OK;

    const int stepMs = 1;
    int waited = 0;

    for (;;)
    {
        ComPtr<IMFMediaEvent> ev;
        HRESULT hr = g_eventGen->GetEvent(MF_EVENT_FLAG_NO_WAIT, ev.GetAddressOf());

        g_diagLastGetEventHr = hr;

        if (hr == MF_E_NO_EVENTS_AVAILABLE)
        {
            // 何か 1 つでも溜まっていれば、それ以上は待たない
            if (waited >= timeoutMs || g_pendingNeedInput > 0 || g_pendingHaveOutput > 0)
                return S_OK;

            Sleep(stepMs);
            waited += stepMs;
            continue;
        }

        if (FAILED(hr)) return hr;

        MediaEventType type = MEUnknown;
        ev->GetType(&type);

        ++g_diagEventsSeen;

        if (type == METransformNeedInput)
        {
            ++g_pendingNeedInput;
            ++g_diagNeedInputSeen;
        }
        else if (type == METransformHaveOutput)
        {
            ++g_pendingHaveOutput;
            ++g_diagHaveOutputSeen;
        }
        else
        {
            // ドレイン完了・マーカーなどは読み捨てる
            ++g_diagOtherEventSeen;
            g_diagLastOtherEvent = (uint32_t)type;
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// エンコード済みサンプルを取り出して出力バッファへ書き込む
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// BGRA32 を NV12 へ変換する（複数スレッドで分担）。
/// </summary>
/// <remarks>
/// <para>
/// エンコーダーは NV12 しか受け取らないため、取り込んだ BGRA を毎フレーム
/// 変換する必要がある。1920x1080 なら 200 万画素を超え、1 画素ずつ
/// 単スレッドで回すと数十ミリ秒かかる。これはエンコード自体と同じ桁で、
/// ハードウェアエンコーダーに変えても縮まらない。
/// </para>
/// <para>
/// 行ごとに独立した計算なので、行を分けて並列に処理する。
/// 分ける境界は偶数行にする。NV12 の色差は 2 行 1 組で作るため、
/// 奇数行で切ると担当が重なる。
/// </para>
/// </remarks>
static void ConvertBgraToNv12(const uint8_t* pSrc, uint8_t* pDst, int width, int height)
{
    const size_t uvOffset = (size_t)width * height;

    auto convertRows = [pSrc, pDst, width, uvOffset](int yStart, int yEnd)
    {
        for (int y = yStart; y < yEnd; ++y)
        {
            const uint8_t* row  = pSrc + (size_t)y * width * 4;
            uint8_t*       yRow = pDst + (size_t)y * width;

            // 輝度
            for (int x = 0; x < width; ++x)
            {
                const uint8_t* px = row + (size_t)x * 4;
                uint8_t b = px[0], g = px[1], r = px[2];
                // BT.601 変換式
                yRow[x] = (uint8_t)((66 * r + 129 * g + 25 * b + 128) / 256 + 16);
            }

            // 色差は 2 行 1 組。偶数行のときだけ作る。
            if ((y & 1) != 0) continue;

            uint8_t* uvRow = pDst + uvOffset + (size_t)(y / 2) * width;

            for (int x = 0; x < width / 2; ++x)
            {
                const uint8_t* px = row + (size_t)(x * 2) * 4;
                uint8_t b = px[0], g = px[1], r = px[2];

                uvRow[x * 2 + 0] = (uint8_t)((-38 * r -  74 * g + 112 * b + 128) / 256 + 128);
                uvRow[x * 2 + 1] = (uint8_t)((112 * r -  94 * g -  18 * b + 128) / 256 + 128);
            }
        }
    };

    unsigned threads = std::thread::hardware_concurrency();
    if (threads == 0) threads = 2;
    if (threads > 8) threads = 8;

    // 小さい絵は分けるほうが高くつく
    if (threads <= 1 || height < 64)
    {
        convertRows(0, height);
        return;
    }

    // 偶数行で切れるように、1 スレッドあたりの行数を偶数にする
    int pairRows      = (height + 1) / 2;
    int pairsPerThread = ((int)((pairRows + threads - 1) / threads));
    int rowsPerThread  = pairsPerThread * 2;

    std::vector<std::thread> workers;
    workers.reserve(threads);

    for (unsigned i = 0; i < threads; ++i)
    {
        int start = (int)i * rowsPerThread;
        if (start >= height) break;

        int end = start + rowsPerThread;
        if (end > height) end = height;

        workers.emplace_back(convertRows, start, end);
    }

    for (auto& worker : workers)
        worker.join();
}

static HRESULT PullOutput(uint8_t* pOutputNal, int outputCapacity, int* pOutputSize)
{
    MFT_OUTPUT_STREAM_INFO streamInfo = {};
    g_encoder->GetOutputStreamInfo(0, &streamInfo);

    const bool selfAllocated = (streamInfo.dwFlags & MFT_OUTPUT_STREAM_PROVIDES_SAMPLES) != 0;

    MFT_OUTPUT_DATA_BUFFER outputBuffer = {};
    ComPtr<IMFSample> outputSample;

    if (!selfAllocated)
    {
        ComPtr<IMFMediaBuffer> outMediaBuf;
        HRESULT hr = MFCreateMemoryBuffer(outputCapacity, outMediaBuf.GetAddressOf());
        if (FAILED(hr)) return hr;

        hr = MFCreateSample(outputSample.GetAddressOf());
        if (FAILED(hr)) return hr;

        outputSample->AddBuffer(outMediaBuf.Get());
        outputBuffer.pSample = outputSample.Get();
    }

    DWORD dwStatus = 0;
    HRESULT hr = g_encoder->ProcessOutput(0, 1, &outputBuffer, &dwStatus);

    if (hr == MF_E_TRANSFORM_NEED_MORE_INPUT)
        return S_OK; // まだ出せるものがない

    // 出力形式の再交渉を求められた。
    //
    // ハードウェアの MFT は、動き始めるときに「出したい形式」を自分から
    // 提示してくる。これに応じないと ProcessOutput は延々と同じ要求を返し、
    // 出力が 1 枚も取れない。取れないと入力側も詰まり、
    // METransformNeedInput が止まってエンコードが完全に停止する。
    // （実測では 3 枚入れたところで止まった）
    if (hr == MF_E_TRANSFORM_STREAM_CHANGE)
    {
        ComPtr<IMFMediaType> proposed;
        HRESULT typeHr = g_encoder->GetOutputAvailableType(0, 0, proposed.GetAddressOf());

        if (SUCCEEDED(typeHr) && proposed)
            typeHr = g_encoder->SetOutputType(0, proposed.Get(), 0);

        if (FAILED(typeHr)) return typeHr;

        // 形式を入れ替えたので、このフレームぶんの出力は次の呼び出しで取る
        return S_OK;
    }

    if (FAILED(hr)) return hr;

    IMFSample* pOutSample = selfAllocated ? outputBuffer.pSample : outputSample.Get();
    if (!pOutSample) return S_OK;

    ComPtr<IMFMediaBuffer> outBuf;
    hr = pOutSample->ConvertToContiguousBuffer(outBuf.GetAddressOf());

    if (SUCCEEDED(hr))
    {
        BYTE* pData = nullptr;
        DWORD cbLen = 0;
        if (SUCCEEDED(outBuf->Lock(&pData, nullptr, &cbLen)))
        {
            int copySize = min((int)cbLen, outputCapacity);
            memcpy(pOutputNal, pData, copySize);
            *pOutputSize = copySize;
            outBuf->Unlock();
        }
    }

    if (selfAllocated && outputBuffer.pSample)
        outputBuffer.pSample->Release();

    if (outputBuffer.pEvents)
        outputBuffer.pEvents->Release();

    return hr;
}

static void ApplyCodecSettings(IMFTransform* pTransform, int bitrateBps, int fps)
{
    ComPtr<ICodecAPI> codec;
    if (FAILED(pTransform->QueryInterface(IID_PPV_ARGS(codec.GetAddressOf()))) || !codec)
        return;

    VARIANT v;

    // CBR: 帯域が読みやすく、ネットワーク越しの再生が安定する
    VariantInit(&v);
    v.vt = VT_UI4;
    v.ulVal = eAVEncCommonRateControlMode_CBR;
    codec->SetValue(&CODECAPI_AVEncCommonRateControlMode, &v);

    VariantInit(&v);
    v.vt = VT_UI4;
    v.ulVal = (ULONG)bitrateBps;
    codec->SetValue(&CODECAPI_AVEncCommonMeanBitRate, &v);

    // GOP 長を 2 秒ぶんにして、途中参加のクライアントが早く絵を得られるようにする
    VariantInit(&v);
    v.vt = VT_UI4;
    v.ulVal = (ULONG)(fps * 2);
    codec->SetValue(&CODECAPI_AVEncMPVGOPSize, &v);

    // B フレームを使わない: 参照の並べ替えが起きないぶん遅延が減る
    VariantInit(&v);
    v.vt = VT_UI4;
    v.ulVal = 0;
    codec->SetValue(&CODECAPI_AVEncMPVDefaultBPictureCount, &v);

    // 低遅延モード
    VariantInit(&v);
    v.vt = VT_BOOL;
    v.boolVal = VARIANT_TRUE;
    codec->SetValue(&CODECAPI_AVLowLatencyMode, &v);

    // 画質より速度を優先する。
    //
    // 0 が最速、100 が最高画質で、既定は 50。
    // ここで使っているのはソフトウェアエンコーダーなので、1 枚あたりの
    // 時間がそのまま画面の遅れになる（1920x1080 で実測 51.6ms）。
    // 画面を見て操作する用途では、多少の粗さより応答の速さが効く。
    VariantInit(&v);
    v.vt = VT_UI4;
    v.ulVal = 20;
    codec->SetValue(&CODECAPI_AVEncCommonQualityVsSpeed, &v);
}

// ─────────────────────────────────────────────────────────────────────────────
// エクスポート関数
// ─────────────────────────────────────────────────────────────────────────────

// ─────────────────────────────────────────────────────────────────────────────
// 内部実装（呼び出し側が g_cs を保持していることが前提）
//
// エンコーダーはプロセス内に 1 つしかない共有資源なので、
// 「初期化済みか」「解像度は一致しているか」の確認と、それに続く
// 初期化・解放は、まとめてロックの内側で行わなければならない。
// 確認だけロックの外に出すと、複数スレッドから同時に呼ばれたときに
// 二重初期化や解放済みエンコーダーの使用が起きてプロセスごと落ちる。
// ─────────────────────────────────────────────────────────────────────────────

static HRESULT EncoderInitNoLock(int width, int height, int bitrateBps, int fps);
static void    EncoderReleaseNoLock();

extern "C"
{

/**
 * エンコーダーを初期化する。
 * @return S_OK (0) on success, HRESULT error code on failure.
 */
__declspec(dllexport) int WINAPI VMonitorEncoderInit(
    int width, int height, int bitrateBps, int fps)
{
    EnterCriticalSection(&g_cs);
    HRESULT hr = EncoderInitNoLock(width, height, bitrateBps, fps);
    LeaveCriticalSection(&g_cs);
    return hr;
}

} // extern "C"

static HRESULT EncoderInitNoLock(int width, int height, int bitrateBps, int fps)
{
    if (g_initialized) return S_OK; // 既に初期化済み

    HRESULT hr = MFStartup(MF_VERSION, MFSTARTUP_NOSOCKET);
    if (FAILED(hr)) return hr;

    hr = CreateH264Encoder(g_encoder.ReleaseAndGetAddressOf());
    if (FAILED(hr)) { MFShutdown(); return hr; }

    // ── 出力タイプ設定 (H.264) ──────────────────────────────────────────
    //
    // エンコーダーでは出力タイプを先に決める必要がある（入力の制約が
    // 出力の設定に依存するため）。
    hr = MFCreateMediaType(g_outputType.ReleaseAndGetAddressOf());
    if (FAILED(hr)) return hr;

    g_outputType->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
    g_outputType->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_H264);
    MFSetAttributeRatio(g_outputType.Get(), MF_MT_FRAME_RATE, fps, 1);
    MFSetAttributeSize(g_outputType.Get(), MF_MT_FRAME_SIZE, width, height);
    g_outputType->SetUINT32(MF_MT_AVG_BITRATE, bitrateBps);
    g_outputType->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
    g_outputType->SetUINT32(MF_MT_ALL_SAMPLES_INDEPENDENT, FALSE);
    // Baseline プロファイル: Android / iOS のハードウェアデコーダーが確実に対応する
    g_outputType->SetUINT32(MF_MT_MPEG2_PROFILE, eAVEncH264VProfile_Base);
    MFSetAttributeRatio(g_outputType.Get(), MF_MT_PIXEL_ASPECT_RATIO, 1, 1);

    hr = g_encoder->SetOutputType(0, g_outputType.Get(), 0);
    if (FAILED(hr)) return hr;

    // ── 入力タイプ設定 (NV12) ───────────────────────────────────────────
    hr = MFCreateMediaType(g_inputType.ReleaseAndGetAddressOf());
    if (FAILED(hr)) return hr;

    g_inputType->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
    g_inputType->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_NV12);
    MFSetAttributeRatio(g_inputType.Get(), MF_MT_FRAME_RATE, fps, 1);
    MFSetAttributeSize(g_inputType.Get(), MF_MT_FRAME_SIZE, width, height);
    g_inputType->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
    MFSetAttributeRatio(g_inputType.Get(), MF_MT_PIXEL_ASPECT_RATIO, 1, 1);

    hr = g_encoder->SetInputType(0, g_inputType.Get(), 0);
    if (FAILED(hr)) return hr;

    // レート制御・GOP 長はメディアタイプではなく ICodecAPI で設定する
    ApplyCodecSettings(g_encoder.Get(), bitrateBps, fps);

    // 非同期 MFT はイベントで入出力のタイミングを通知してくる
    g_pendingNeedInput  = 0;
    g_pendingHaveOutput = 0;
    g_eventGen.Reset();
    if (g_isAsync)
    {
        hr = g_encoder.As(&g_eventGen);
        if (FAILED(hr)) return hr;
    }

    hr = g_encoder->ProcessMessage(MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, 0);
    if (FAILED(hr)) return hr;

    hr = g_encoder->ProcessMessage(MFT_MESSAGE_NOTIFY_START_OF_STREAM, 0);
    if (FAILED(hr)) return hr;

    g_width      = width;
    g_height     = height;
    g_bitrateBps = bitrateBps;
    g_fps        = fps;
    g_initialized = true;

    return S_OK;
}

extern "C"
{

/**
 * BGRA32 フレームを H.264 NAL ユニットにエンコードする。
 * BGRA → NV12 変換 → MFT エンコード → 出力バッファ書き込み の流れ。
 *
 * @param pInputBgra      入力 BGRA32 データポインター
 * @param inputSize       入力データサイズ (bytes)
 * @param width / height  フレームサイズ
 * @param bitrateBps      目標ビットレート (bps)
 * @param fps             フレームレート
 * @param timestampUs     タイムスタンプ (マイクロ秒)
 * @param pOutputNal      出力 NAL バッファポインター
 * @param outputCapacity  出力バッファサイズ (bytes)
 * @param pOutputSize     実際の出力サイズ (bytes) を受け取るポインター
 * @return S_OK on success
 */
__declspec(dllexport) int WINAPI VMonitorEncoderEncodeFrame(
    const uint8_t* pInputBgra, int inputSize,
    int width, int height,
    int bitrateBps, int fps,
    int64_t timestampUs,
    uint8_t* pOutputNal, int outputCapacity,
    int* pOutputSize)
{
    *pOutputSize = 0;

    if (!pInputBgra || !pOutputNal || !pOutputSize) return E_POINTER;
    if (width <= 0 || height <= 0)                  return E_INVALIDARG;

    // 入力バッファが 1 フレーム分に足りているか必ず確認する。
    // BGRA→NV12 変換は width*height*4 バイトを無条件に読むため、
    // ここを信じると呼び出し元の確保サイズを超えて読み出してしまう。
    const int64_t requiredBytes = (int64_t)width * height * 4;
    if ((int64_t)inputSize < requiredBytes) return E_INVALIDARG;

    // 以降はエンコーダーの状態に触れるため、最後までロックを保持する。
    // 状態の確認と、それに続く初期化・解放・エンコードを分割すると、
    // 別スレッドが間に割り込んでエンコーダーを差し替えてしまう。
    EnterCriticalSection(&g_cs);

    HRESULT hr = S_OK;

    // 解像度が変わったらエンコーダーを作り直す。
    //
    // エンコーダーはプロセス内で 1 つだけ持つ設計なので、
    // 初期化時と違うサイズのフレームが来たら、
    // NV12 の組み立てとエンコーダーの期待がずれて何も出力されなくなる。
    // 「別のディスプレイをミラーし始めた」「解像度を変えた」で普通に起きる。
    if (g_initialized && (width != g_width || height != g_height))
    {
        EncoderReleaseNoLock();
    }

    if (!g_initialized)
    {
        hr = EncoderInitNoLock(width, height, bitrateBps, fps);

        if (FAILED(hr))
        {
            // この時点ではまだ後続の変数を宣言していないため、
            // goto ではなくここで直接抜ける（宣言をまたぐ goto は書けない）。
            LeaveCriticalSection(&g_cs);
            return hr;
        }
    }

    // ── BGRA32 → NV12 変換 ─────────────────────────────────────────────────
    //
    // 変換先のバッファを先に用意して、そこへ直接書き込む。
    // 以前は毎フレーム 3MB の一時バッファを確保して埋めてから
    // memcpy していたが、確保・ゼロ初期化・複写のぶんが丸ごと無駄だった。
    int nv12Size = width * height * 3 / 2;

    ComPtr<IMFSample> inputSample;
    hr = MFCreateSample(inputSample.ReleaseAndGetAddressOf());
    if (FAILED(hr)) goto exit;

    {
        ComPtr<IMFMediaBuffer> inputBuffer;
        hr = MFCreateMemoryBuffer((DWORD)nv12Size, inputBuffer.ReleaseAndGetAddressOf());
        if (FAILED(hr)) goto exit;

        BYTE* pDst = nullptr;
        DWORD maxLen = 0, curLen = 0;
        hr = inputBuffer->Lock(&pDst, &maxLen, &curLen);
        if (FAILED(hr)) goto exit;

        {
            // 変換とエンコード本体のどちらが重いかを分けて測る。
            // 遅延を詰めるときに、推測ではなく数字で判断するため。
            LARGE_INTEGER convStart, convEnd;
            QueryPerformanceCounter(&convStart);

            ConvertBgraToNv12(pInputBgra, pDst, width, height);

            QueryPerformanceCounter(&convEnd);
            g_diagConvertTicks = convEnd.QuadPart - convStart.QuadPart;
        }

        inputBuffer->Unlock();
        inputBuffer->SetCurrentLength((DWORD)nv12Size);

        inputSample->AddBuffer(inputBuffer.Get());
    }

    // タイムスタンプを設定する (100 ns 単位)
    inputSample->SetSampleTime(timestampUs * 10LL);
    inputSample->SetSampleDuration(10000000LL / g_fps);

    // ── エンコーダーへ入力し、出力を取り出す ───────────────────────────────
    LARGE_INTEGER mftStart;
    QueryPerformanceCounter(&mftStart);

    if (g_isAsync)
    {
        // 非同期 MFT は「入力を受け取れる」「出力ができた」をイベントで知らせる。
        // この 2 つは対応していない。ハードウェアは数フレーム溜めてから
        // 出力を出すので、1 枚入れて 1 枚出るのを待ってはいけない。
        //
        // ここでは
        //   1. 受け付けてもらえるなら入れる
        //   2. 出来ているものがあれば取り出す（無ければ待たない）
        // とし、出力は数フレーム遅れて出てくるものとして扱う。
        // 呼び出し側は「出力なし」を許容する作りになっている。

        // 1. 入力
        hr = AsyncPumpEvents(g_pendingNeedInput > 0 ? 0 : 100);
        if (FAILED(hr)) { g_diagLastHr = hr; hr = S_OK; goto exit; }

        if (g_pendingNeedInput > 0)
        {
            --g_pendingNeedInput;

            ++g_diagProcessInputCalls;
            hr = g_encoder->ProcessInput(0, inputSample.Get(), 0);

            if (FAILED(hr))
            {
                ++g_diagProcessInputFails;
                g_diagLastHr = hr;
                goto exit;
            }
        }
        // 受け付けてもらえなかったフレームは捨てる。
        // 溜まっている出力を先に掃ければ、次の呼び出しで入れられる。

        // 2. 出力（待たない）
        hr = AsyncPumpEvents(0);
        if (FAILED(hr)) { g_diagLastHr = hr; hr = S_OK; goto exit; }

        if (g_pendingHaveOutput > 0)
        {
            --g_pendingHaveOutput;

            ++g_diagProcessOutputCalls;
            hr = PullOutput(pOutputNal, outputCapacity, pOutputSize);
            g_diagLastHr = hr;
        }
        else
        {
            // まだ出来ていない。次の呼び出しで回収する。
            hr = S_OK;
        }
    }
    else
    {
        // 同期 MFT: そのまま入れて、そのまま取り出す。
        ++g_diagProcessInputCalls;
        hr = g_encoder->ProcessInput(0, inputSample.Get(), 0);
        if (FAILED(hr)) { ++g_diagProcessInputFails; g_diagLastHr = hr; goto exit; }

        ++g_diagProcessOutputCalls;
        hr = PullOutput(pOutputNal, outputCapacity, pOutputSize);
        g_diagLastHr = hr;
    }

exit:
    {
        LARGE_INTEGER mftEnd;
        QueryPerformanceCounter(&mftEnd);
        g_diagMftTicks = mftEnd.QuadPart - mftStart.QuadPart;
    }

    LeaveCriticalSection(&g_cs);
    return hr;
}

/**
 * 直近 1 枚ぶんの内訳を返す（マイクロ秒）。
 *
 * 変換とエンコード本体のどちらが重いかで、遅延を詰める手立てが変わる。
 */
__declspec(dllexport) void WINAPI VMonitorEncoderGetTiming(
    int* pConvertUs, int* pMftUs)
{
    LARGE_INTEGER freq;
    QueryPerformanceFrequency(&freq);

    if (freq.QuadPart == 0)
    {
        if (pConvertUs) *pConvertUs = 0;
        if (pMftUs)     *pMftUs     = 0;
        return;
    }

    if (pConvertUs) *pConvertUs = (int)(g_diagConvertTicks * 1000000LL / freq.QuadPart);
    if (pMftUs)     *pMftUs     = (int)(g_diagMftTicks     * 1000000LL / freq.QuadPart);
}

/**
 * 内部状態を取得する（不具合切り分け用）。
 * すべての引数は出力。NULL を渡した項目は書き込まれない。
 */
__declspec(dllexport) void WINAPI VMonitorEncoderGetDiag(
    int* pIsAsync,
    int* pEventsSeen, int* pNeedInputSeen, int* pHaveOutputSeen,
    int* pOtherEventSeen, unsigned int* pLastOtherEvent,
    int* pProcessInputCalls, int* pProcessInputFails, int* pProcessOutputCalls,
    int* pLastHr, int* pLastGetEventHr)
{
    if (pIsAsync)            *pIsAsync            = g_isAsync ? 1 : 0;
    if (pEventsSeen)         *pEventsSeen         = g_diagEventsSeen;
    if (pNeedInputSeen)      *pNeedInputSeen      = g_diagNeedInputSeen;
    if (pHaveOutputSeen)     *pHaveOutputSeen     = g_diagHaveOutputSeen;
    if (pOtherEventSeen)     *pOtherEventSeen     = g_diagOtherEventSeen;
    if (pLastOtherEvent)     *pLastOtherEvent     = g_diagLastOtherEvent;
    if (pProcessInputCalls)  *pProcessInputCalls  = g_diagProcessInputCalls;
    if (pProcessInputFails)  *pProcessInputFails  = g_diagProcessInputFails;
    if (pProcessOutputCalls) *pProcessOutputCalls = g_diagProcessOutputCalls;
    if (pLastHr)             *pLastHr             = (int)g_diagLastHr;
    if (pLastGetEventHr)     *pLastGetEventHr     = (int)g_diagLastGetEventHr;
}

/**
 * この PC で使える H.264 エンコーダーを一覧する（切り分け用）。
 *
 * ソフトウェアエンコードは 1 枚あたり数十ミリ秒かかり、そのまま画面の
 * 遅れになる。ハードウェアエンコーダーが存在するのかどうかで、
 * 遅延を詰める手立てが変わるため、実際に列挙して確かめられるようにする。
 *
 * 書式は 1 行 1 台で "HW|SW<TAB>async|sync<TAB>名前"。
 *
 * @param buffer      出力先（ワイド文字）
 * @param bufferChars buffer の要素数
 * @return 見つかった台数。負なら失敗（HRESULT）。
 */
__declspec(dllexport) int WINAPI VMonitorEncoderListEncoders(
    wchar_t* buffer, int bufferChars)
{
    if (buffer == nullptr || bufferChars <= 0) return -1;
    buffer[0] = L'\0';

    HRESULT hr = MFStartup(MF_VERSION, MFSTARTUP_LITE);
    if (FAILED(hr)) return (int)hr;

    MFT_REGISTER_TYPE_INFO outputTypeInfo;
    outputTypeInfo.guidMajorType = MFMediaType_Video;
    outputTypeInfo.guidSubtype   = MFVideoFormat_H264;

    IMFActivate** ppActivate = nullptr;
    UINT32 count = 0;

    hr = MFTEnumEx(
        MFT_CATEGORY_VIDEO_ENCODER,
        MFT_ENUM_FLAG_SYNCMFT | MFT_ENUM_FLAG_ASYNCMFT |
        MFT_ENUM_FLAG_HARDWARE | MFT_ENUM_FLAG_SORTANDFILTER,
        nullptr,
        &outputTypeInfo,
        &ppActivate,
        &count);

    if (FAILED(hr))
    {
        MFShutdown();
        return (int)hr;
    }

    int written = 0;

    for (UINT32 i = 0; i < count; i++)
    {
        // ハードウェアかどうかは、この属性の有無で分かる
        UINT32 urlLength = 0;
        bool isHardware =
            SUCCEEDED(ppActivate[i]->GetStringLength(MFT_ENUM_HARDWARE_URL_Attribute, &urlLength));

        // 非同期かどうか
        UINT32 asyncFlag = 0;
        ppActivate[i]->GetUINT32(MF_TRANSFORM_ASYNC, &asyncFlag);

        wchar_t* pName = nullptr;
        UINT32 nameLength = 0;
        if (FAILED(ppActivate[i]->GetAllocatedString(
                MFT_FRIENDLY_NAME_Attribute, &pName, &nameLength)))
        {
            pName = nullptr;
        }

        wchar_t line[512];
        _snwprintf_s(line, _TRUNCATE, L"%s\t%s\t%s\n",
                     isHardware ? L"HW" : L"SW",
                     asyncFlag  ? L"async" : L"sync",
                     pName ? pName : L"(名前不明)");

        if (pName) CoTaskMemFree(pName);

        int remaining = bufferChars - written - 1;
        if (remaining > 0)
        {
            wcsncat_s(buffer, bufferChars, line, remaining);
            written = (int)wcslen(buffer);
        }

        ppActivate[i]->Release();
    }

    if (ppActivate) CoTaskMemFree(ppActivate);

    MFShutdown();
    return (int)count;
}

/**
 * エンコーダーリソースを解放する。
 */
__declspec(dllexport) void WINAPI VMonitorEncoderRelease()
{
    EnterCriticalSection(&g_cs);
    EncoderReleaseNoLock();
    LeaveCriticalSection(&g_cs);
}

} // extern "C"

static void EncoderReleaseNoLock()
{
    if (!g_initialized) return;

    if (g_encoder)
    {
        g_encoder->ProcessMessage(MFT_MESSAGE_NOTIFY_END_OF_STREAM, 0);
        g_encoder->ProcessMessage(MFT_MESSAGE_COMMAND_DRAIN, 0);
    }

    g_eventGen.Reset();
    g_encoder.Reset();
    g_inputType.Reset();
    g_outputType.Reset();
    g_initialized      = false;
    g_isAsync          = false;
    g_pendingNeedInput  = 0;
    g_pendingHaveOutput = 0;
    g_sampleCount      = 0;

    MFShutdown();
}

// =============================================================================
// SwapChain ブリッジ
// IddCx の SwapChain からフレームを取得して C# コールバックへ渡す
// =============================================================================

#include <map>
#include <thread>
#include <atomic>

typedef void(__cdecl* FrameReadyCallback)(
    int64_t sequenceNumber,
    int64_t timestampUs,
    int width, int height,
    const uint8_t* pBgra32,
    int dataSize);

struct SwapChainSession
{
    GUID handleId;
    int width;
    int height;
    FrameReadyCallback callback;
    std::atomic<bool> active;
    std::thread captureThread;
};

static std::map<GUID, SwapChainSession*> g_swapChains;
static CRITICAL_SECTION g_swapChainCs;
static bool g_swapChainCsInit = false;

// GUID 比較演算子
static bool operator<(const GUID& a, const GUID& b)
{
    return memcmp(&a, &b, sizeof(GUID)) < 0;
}

extern "C"
{

/**
 * SwapChain ブリッジを初期化する。
 * C# の SwapChainBridge コンストラクターから呼び出される。
 */
__declspec(dllexport) int WINAPI VMonitorSwapChainInit(
    GUID handleId,
    int width, int height,
    FrameReadyCallback callback)
{
    if (!g_swapChainCsInit)
    {
        InitializeCriticalSection(&g_swapChainCs);
        g_swapChainCsInit = true;
    }

    EnterCriticalSection(&g_swapChainCs);

    auto* session = new SwapChainSession();
    session->handleId = handleId;
    session->width    = width;
    session->height   = height;
    session->callback = callback;
    session->active   = true;

    // フレームキャプチャスレッドを起動する
    // DXGI Desktop Duplication でメインディスプレイをキャプチャする
    session->captureThread = std::thread([session]()
    {
        int64_t seq = 0;
        const int fps = 30;
        const int intervalMs = 1000 / fps;
        const int W = session->width;
        const int H = session->height;
        const int frameSize = W * H * 4; // BGRA32

        std::vector<uint8_t> frameData(frameSize, 0);

        // ── DXGI Desktop Duplication 初期化 ──────────────────────────────
        IDXGIFactory1* pFactory = nullptr;
        IDXGIAdapter1* pAdapter = nullptr;
        ID3D11Device* pDevice   = nullptr;
        ID3D11DeviceContext* pCtx = nullptr;
        IDXGIOutput* pOutput    = nullptr;
        IDXGIOutput1* pOutput1  = nullptr;
        IDXGIOutputDuplication* pDupl = nullptr;
        ID3D11Texture2D* pStagingTex  = nullptr;

        bool dxgiOk = false;

        do {
            HRESULT hr = CreateDXGIFactory1(__uuidof(IDXGIFactory1), (void**)&pFactory);
            if (FAILED(hr)) break;

            hr = pFactory->EnumAdapters1(0, &pAdapter);
            if (FAILED(hr)) break;

            D3D_FEATURE_LEVEL featureLevel;
            hr = D3D11CreateDevice(pAdapter, D3D_DRIVER_TYPE_UNKNOWN, nullptr,
                0, nullptr, 0, D3D11_SDK_VERSION, &pDevice, &featureLevel, &pCtx);
            if (FAILED(hr)) break;

            hr = pAdapter->EnumOutputs(0, &pOutput);
            if (FAILED(hr)) break;

            hr = pOutput->QueryInterface(__uuidof(IDXGIOutput1), (void**)&pOutput1);
            if (FAILED(hr)) break;

            hr = pOutput1->DuplicateOutput(pDevice, &pDupl);
            if (FAILED(hr)) break;

            // スタジングテクスチャ作成（CPU 読み取り用）
            DXGI_OUTDUPL_DESC duplDesc = {};
            pDupl->GetDesc(&duplDesc);
            D3D11_TEXTURE2D_DESC stagingDesc = {};
            stagingDesc.Width  = duplDesc.ModeDesc.Width;
            stagingDesc.Height = duplDesc.ModeDesc.Height;
            stagingDesc.MipLevels = 1;
            stagingDesc.ArraySize = 1;
            stagingDesc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
            stagingDesc.SampleDesc.Count = 1;
            stagingDesc.Usage = D3D11_USAGE_STAGING;
            stagingDesc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
            hr = pDevice->CreateTexture2D(&stagingDesc, nullptr, &pStagingTex);
            if (FAILED(hr)) break;

            dxgiOk = true;
        } while (false);

        while (session->active.load())
        {
            LARGE_INTEGER counter, freq;
            QueryPerformanceCounter(&counter);
            QueryPerformanceFrequency(&freq);
            int64_t timestampUs = counter.QuadPart * 1000000LL / freq.QuadPart;

            bool frameReady = false;

            if (dxgiOk && pDupl)
            {
                DXGI_OUTDUPL_FRAME_INFO frameInfo = {};
                IDXGIResource* pDesktopResource = nullptr;

                HRESULT hr = pDupl->AcquireNextFrame(0, &frameInfo, &pDesktopResource);
                if (hr == S_OK && pDesktopResource)
                {
                    ID3D11Texture2D* pDesktopTex = nullptr;
                    if (SUCCEEDED(pDesktopResource->QueryInterface(__uuidof(ID3D11Texture2D),
                        (void**)&pDesktopTex)))
                    {
                        pCtx->CopyResource(pStagingTex, pDesktopTex);
                        pDesktopTex->Release();

                        D3D11_MAPPED_SUBRESOURCE mapped = {};
                        if (SUCCEEDED(pCtx->Map(pStagingTex, 0, D3D11_MAP_READ, 0, &mapped)))
                        {
                            // スケールダウンしてフレームバッファに書き込む
                            D3D11_TEXTURE2D_DESC texDesc = {};
                            pStagingTex->GetDesc(&texDesc);
                            int srcW = (int)texDesc.Width;
                            int srcH = (int)texDesc.Height;
                            int srcPitch = (int)mapped.RowPitch;
                            const uint8_t* pSrc = (const uint8_t*)mapped.pData;

                            for (int y = 0; y < H; ++y)
                            {
                                int srcY = y * srcH / H;
                                for (int x = 0; x < W; ++x)
                                {
                                    int srcX = x * srcW / W;
                                    const uint8_t* s = pSrc + srcY * srcPitch + srcX * 4;
                                    uint8_t* d = frameData.data() + (y * W + x) * 4;
                                    d[0] = s[0]; // B
                                    d[1] = s[1]; // G
                                    d[2] = s[2]; // R
                                    d[3] = 255;  // A
                                }
                            }

                            pCtx->Unmap(pStagingTex, 0);
                            frameReady = true;
                        }
                    }
                    pDesktopResource->Release();
                    pDupl->ReleaseFrame();
                }
                else if (hr == DXGI_ERROR_ACCESS_LOST)
                {
                    // アダプタがリセットされた: 再初期化
                    pDupl->Release(); pDupl = nullptr;
                    dxgiOk = false;
                }
                else
                {
                    // フレームなし（変化なし）: 前フレームをそのまま使う
                    if (frameData[0] || frameData[1] || frameData[2])
                        frameReady = true; // 前フレームが有効
                }
            }

            if (!frameReady)
            {
                // フォールバック: 色付きカラーバーを生成する
                for (int y = 0; y < H; ++y)
                {
                    for (int x = 0; x < W; ++x)
                    {
                        int bar = x * 8 / W;
                        uint8_t r = 0, g = 0, b = 0;
                        switch (bar) {
                            case 0: r=192;g=192;b=192; break; // 白
                            case 1: r=192;g=192;b=0;   break; // 黄
                            case 2: r=0;  g=192;b=192; break; // シアン
                            case 3: r=0;  g=192;b=0;   break; // 緑
                            case 4: r=192;g=0;  b=192; break; // マゼンタ
                            case 5: r=192;g=0;  b=0;   break; // 赤
                            case 6: r=0;  g=0;  b=192; break; // 青
                            default:r=0;  g=0;  b=0;   break; // 黒
                        }
                        uint8_t* d = frameData.data() + (y * W + x) * 4;
                        d[0] = b; d[1] = g; d[2] = r; d[3] = 255;
                    }
                }
            }

            if (session->callback)
            {
                session->callback(seq++, timestampUs, W, H,
                    frameData.data(), frameSize);
            }

            Sleep(intervalMs);
        }

        // クリーンアップ
        if (pStagingTex) pStagingTex->Release();
        if (pDupl)    pDupl->Release();
        if (pOutput1) pOutput1->Release();
        if (pOutput)  pOutput->Release();
        if (pCtx)     pCtx->Release();
        if (pDevice)  pDevice->Release();
        if (pAdapter) pAdapter->Release();
        if (pFactory) pFactory->Release();
    });

    g_swapChains[handleId] = session;
    LeaveCriticalSection(&g_swapChainCs);

    return S_OK;
}

/**
 * SwapChain ブリッジを解放する。
 */
__declspec(dllexport) void WINAPI VMonitorSwapChainRelease(GUID handleId)
{
    if (!g_swapChainCsInit) return;

    EnterCriticalSection(&g_swapChainCs);

    auto it = g_swapChains.find(handleId);
    if (it != g_swapChains.end())
    {
        auto* session = it->second;
        session->active = false;
        if (session->captureThread.joinable())
            session->captureThread.join();
        delete session;
        g_swapChains.erase(it);
    }

    LeaveCriticalSection(&g_swapChainCs);
}

} // extern "C"
