using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace VMonitor.Streamer;

/// <summary>
/// H.264 エンコーダー。実処理はネイティブの VMonitor.Encoder.dll
/// (Windows Media Foundation MFT) に委譲する。
/// </summary>
/// <remarks>
/// <para>
/// Media Foundation の初期化・終了 (MFStartup / MFShutdown) と MFT の生成は
/// すべてネイティブ側が持つ。マネージド側からも MFStartup / MFShutdown を
/// 呼ぶと参照カウントが釣り合わなくなり、余分な MFShutdown で
/// Media Foundation が壊れてプロセスごと落ちる。ここでは一切触らない。
/// </para>
/// <para>
/// ネイティブのエンコーダーはプロセス内に 1 つだけ存在する。
/// 解像度が変わった場合はネイティブ側が自動で作り直す。
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class MfEncoder : IDisposable
{
    private bool _disposed;
    private bool _initialized;

    private int _width;
    private int _height;
    private int _targetBitrateBps;
    private int _maxFps;

    /// <summary>エンコーダーのパラメーターを設定する。</summary>
    /// <remarks>
    /// ネイティブのエンコーダーは最初のフレームが来た時点で、
    /// ここで渡された設定を使って初期化される。
    /// </remarks>
    public void Initialize(int width, int height, int targetBitrateBps, int maxFps)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _width            = width;
        _height           = height;
        _targetBitrateBps = targetBitrateBps;
        _maxFps           = maxFps;

        _initialized = NativeEncoder.IsAvailable;
    }

    /// <summary>
    /// BGRA32 のフレームを H.264 の NAL ユニット列 (Annex-B) にエンコードする。
    /// </summary>
    /// <returns>
    /// エンコード結果。エンコーダーがまだ出力を返せない場合や
    /// 利用できない場合は null。
    /// </returns>
    public byte[]? EncodeFrame(ReadOnlySpan<byte> bgra32Data, long timestampUs)
    {
        if (!_initialized) return null;

        return NativeEncoder.Encode(
            bgra32Data, _width, _height, _targetBitrateBps, _maxFps, timestampUs);
    }

    /// <summary>ビットレートを変更する。次のフレームから反映される。</summary>
    public void SetBitrate(int bitrateBps) => _targetBitrateBps = bitrateBps;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_initialized)
            NativeEncoder.Release();
    }
}

/// <summary>
/// VMonitor.Encoder.dll (C++ ネイティブ) へのブリッジ。
/// </summary>
internal static class NativeEncoder
{
    private const string DllName = "VMonitor.Encoder";

    private static readonly bool _isAvailable;

    static NativeEncoder()
    {
        _isAvailable = CheckAvailable();
    }

    public static bool IsAvailable => _isAvailable;

    private static bool CheckAvailable()
    {
        try
        {
            // DLL が読み込めてエンコーダーを作れるかを試す。
            //
            // 確認したら必ず解放する。ネイティブ側のエンコーダーは
            // プロセス内で 1 つだけなので、ここで掴んだままにすると
            // 実際に使う解像度ではなくこの確認用の解像度で固定されてしまう。
            int hr = NativeEncoderInit(1920, 1080, 4_000_000, 30);

            if (hr != 0)
            {
                // 初期化失敗 - DLL は存在するがエンコーダーが使えない
                return false;
            }

            NativeEncoderRelease();
            return true;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
        catch
        {
            return false;
        }
    }

    public static byte[]? Encode(
        ReadOnlySpan<byte> bgraData,
        int width, int height,
        int bitrateBps, int fps,
        long timestampUs)
    {
        // 入力が 1 フレーム分に足りない場合は渡さない。
        // ネイティブ側も検証するが、ここで弾けば無駄な相互運用を避けられる。
        long required = (long)width * height * 4;
        if (bgraData.Length < required) return null;

        // H.264 の出力は入力より十分小さいので、入力サイズぶんあれば足りる
        var outputBuffer = new byte[bgraData.Length];
        int outputSize = 0;

        unsafe
        {
            fixed (byte* pInput = bgraData)
            fixed (byte* pOutput = outputBuffer)
            {
                int hr = NativeEncoderEncodeFrame(
                    pInput, bgraData.Length,
                    width, height, bitrateBps, fps, timestampUs,
                    pOutput, outputBuffer.Length, &outputSize);

                if (hr != 0 || outputSize <= 0)
                    return null;
            }
        }

        return outputBuffer[..outputSize];
    }

    public static void Release()
    {
        try { NativeEncoderRelease(); }
        catch { /* DLL が存在しない場合は無視 */ }
    }

    [DllImport(DllName, EntryPoint = "VMonitorEncoderInit")]
    private static extern int NativeEncoderInit(int width, int height, int bitrateBps, int fps);

    [DllImport(DllName, EntryPoint = "VMonitorEncoderEncodeFrame")]
    private static unsafe extern int NativeEncoderEncodeFrame(
        byte* pInputBgra, int inputSize,
        int width, int height, int bitrateBps, int fps,
        long timestampUs,
        byte* pOutputNal, int outputCapacity,
        int* pOutputSize);

    [DllImport(DllName, EntryPoint = "VMonitorEncoderRelease")]
    private static extern void NativeEncoderRelease();

    /// <summary>
    /// この PC で使える H.264 エンコーダーを一覧する（切り分け用）。
    /// </summary>
    /// <remarks>
    /// ソフトウェアエンコードは 1 枚あたり数十ミリ秒かかり、そのまま画面の
    /// 遅れになる。ハードウェアエンコーダーがあるかどうかで打ち手が変わるので、
    /// 推測せず実際に列挙して確かめられるようにしてある。
    /// </remarks>
    /// <returns>1 台につき (ハードウェアか, 非同期か, 名前)。</returns>
    public static IReadOnlyList<(bool IsHardware, bool IsAsync, string Name)> ListEncoders()
    {
        var results = new List<(bool, bool, string)>();

        var buffer = new System.Text.StringBuilder(8192);

        int count;
        try { count = NativeEncoderListEncoders(buffer, buffer.Capacity); }
        catch { return results; }

        if (count <= 0) return results;

        foreach (var line in buffer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('\t');
            if (parts.Length < 3) continue;

            results.Add((parts[0] == "HW", parts[1] == "async", parts[2]));
        }

        return results;
    }

    [DllImport(DllName, EntryPoint = "VMonitorEncoderListEncoders", CharSet = CharSet.Unicode)]
    private static extern int NativeEncoderListEncoders(
        System.Text.StringBuilder buffer, int bufferChars);

    /// <summary>直近のエンコード動作の内部状態を取得する（切り分け用）。</summary>
    public static EncoderCapabilities.EncoderDiagnostics GetDiagnostics()
    {
        try
        {
            NativeEncoderGetDiag(
                out int isAsync,
                out int eventsSeen, out int needInput, out int haveOutput,
                out _, out _,
                out int inputCalls, out int inputFails, out int outputCalls,
                out int lastHr, out _);

            return new EncoderCapabilities.EncoderDiagnostics(
                isAsync != 0, eventsSeen, needInput, haveOutput,
                inputCalls, inputFails, outputCalls, lastHr);
        }
        catch
        {
            return default;
        }
    }

    /// <summary>直近 1 枚ぶんの内訳（変換 / エンコード本体）をマイクロ秒で返す。</summary>
    public static (int ConvertUs, int MftUs) GetTiming()
    {
        try
        {
            NativeEncoderGetTiming(out int convertUs, out int mftUs);
            return (convertUs, mftUs);
        }
        catch
        {
            return (0, 0);
        }
    }

    [DllImport(DllName, EntryPoint = "VMonitorEncoderGetTiming")]
    private static extern void NativeEncoderGetTiming(out int convertUs, out int mftUs);

    [DllImport(DllName, EntryPoint = "VMonitorEncoderGetDiag")]
    private static extern void NativeEncoderGetDiag(
        out int isAsync,
        out int eventsSeen, out int needInputSeen, out int haveOutputSeen,
        out int otherEventSeen, out uint lastOtherEvent,
        out int processInputCalls, out int processInputFails, out int processOutputCalls,
        out int lastHr, out int lastGetEventHr);
}
