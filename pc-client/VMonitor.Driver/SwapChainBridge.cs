using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading.Channels;
using VMonitor.Core.Models;

namespace VMonitor.Driver;

/// <summary>
/// IddCx ドライバの SwapChain コールバックと C# の IAsyncEnumerable を繋ぐブリッジ。
///
/// ドライバの VMonitorVDD_AssignSwapChain が呼び出されると、
/// このブリッジが有効化され、GetFramesAsync が実際のフレームを返すようになる。
///
/// フレームの取得には VMonitor.Encoder.dll の SwapChain API を経由する。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SwapChainBridge : IDisposable
{
    // 各ハンドルに対応するフレームチャンネル
    private readonly Channel<VideoFrame> _frameChannel;
    private readonly VirtualDisplayHandle _handle;
    private bool _disposed;

    // ネイティブコールバック デリゲート（GC に回収されないよう保持）
    private readonly FrameReadyCallback _callbackDelegate;
    private GCHandle _callbackHandle;

    public SwapChainBridge(VirtualDisplayHandle handle, int width, int height)
    {
        _handle = handle;

        _frameChannel = Channel.CreateBounded<VideoFrame>(
            new BoundedChannelOptions(4)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = true
            });

        _callbackDelegate = OnFrameReady;
        _callbackHandle = GCHandle.Alloc(_callbackDelegate);

        // ネイティブブリッジを初期化する
        int hr = NativeSwapChain.Initialize(
            handle.Value,
            width, height,
            Marshal.GetFunctionPointerForDelegate(_callbackDelegate));

        if (hr != 0)
        {
            // ネイティブ DLL が利用できない場合は、シミュレーションモードで動作する
            _ = Task.Run(SimulateFrames);
        }
    }

    /// <summary>フレームを非同期ストリームとして返す。</summary>
    public async IAsyncEnumerable<VideoFrame> GetFramesAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var frame in _frameChannel.Reader.ReadAllAsync(ct))
        {
            yield return frame;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        NativeSwapChain.Release(_handle.Value);
        _callbackHandle.Free();
        _frameChannel.Writer.Complete();
    }

    // ── ネイティブコールバック ──────────────────────────────────────────────

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void FrameReadyCallback(
        long sequenceNumber,
        long timestampUs,
        int width, int height,
        IntPtr pBgra32Data,
        int dataSize);

    private void OnFrameReady(
        long sequenceNumber,
        long timestampUs,
        int width, int height,
        IntPtr pBgra32Data,
        int dataSize)
    {
        // ネイティブバッファをマネージド配列にコピーする
        var data = new byte[dataSize];
        if (pBgra32Data != IntPtr.Zero && dataSize > 0)
            Marshal.Copy(pBgra32Data, data, 0, dataSize);

        var frame = new VideoFrame
        {
            SequenceNumber = sequenceNumber,
            TimestampUs    = timestampUs,
            Resolution     = new Resolution(width, height),
            Data           = data.AsMemory()
        };

        // チャンネルへ書き込む（非ブロッキング）
        _frameChannel.Writer.TryWrite(frame);
    }

    // ── シミュレーションモード（ネイティブ DLL 不在時）─────────────────────

    private async Task SimulateFrames()
    {
        long seq = 0;
        const int fps = 30;
        const int intervalMs = 1000 / fps;
        const int W = 1920;
        const int H = 1080;

        // カラーバーパターンを事前生成（BGRA32）
        var frameData = new byte[W * H * 4];
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                int bar = x * 8 / W;
                byte r = 0, g = 0, b = 0;
                switch (bar)
                {
                    case 0: r = 192; g = 192; b = 192; break; // 白
                    case 1: r = 192; g = 192; b = 0;   break; // 黄
                    case 2: r = 0;   g = 192; b = 192; break; // シアン
                    case 3: r = 0;   g = 192; b = 0;   break; // 緑
                    case 4: r = 192; g = 0;   b = 192; break; // マゼンタ
                    case 5: r = 192; g = 0;   b = 0;   break; // 赤
                    case 6: r = 0;   g = 0;   b = 192; break; // 青
                    default: r = 0;  g = 0;   b = 0;   break; // 黒
                }
                int idx = (y * W + x) * 4;
                frameData[idx + 0] = b; // BGRA
                frameData[idx + 1] = g;
                frameData[idx + 2] = r;
                frameData[idx + 3] = 255;
            }
        }

        while (!_disposed)
        {
            var resolution = new Resolution(W, H);
            var frame = new VideoFrame
            {
                SequenceNumber = seq++,
                TimestampUs    = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000L,
                Resolution     = resolution,
                Data           = frameData.AsMemory()
            };

            _frameChannel.Writer.TryWrite(frame);

            try { await Task.Delay(intervalMs); }
            catch (TaskCanceledException) { break; }
        }
    }
}

/// <summary>
/// VMonitor.Encoder.dll の SwapChain ブリッジ P/Invoke。
/// </summary>
[SupportedOSPlatform("windows")]
internal static class NativeSwapChain
{
    private const string DllName = "VMonitor.Encoder";

    [DllImport(DllName, EntryPoint = "VMonitorSwapChainInit")]
    public static extern int Initialize(
        Guid handleId,
        int width, int height,
        IntPtr frameReadyCallback);

    [DllImport(DllName, EntryPoint = "VMonitorSwapChainRelease")]
    public static extern void Release(Guid handleId);
}
