using System.Diagnostics;
using VMonitor.Core.Interfaces;
using VMonitor.Core.Models;

namespace VMonitor.Streamer;

/// <summary>
/// IStreamer の実装。仮想ディスプレイのフレームをエンコードしてスマホへ送信する。
/// Windows Media Foundation MFT によるGPUハードウェアエンコードを想定した
/// マネージドシミュレーション実装。
/// </summary>
public sealed class Streamer : IStreamer
{
    private readonly object _configLock = new();
    private StreamerConfig _config;
    private CancellationTokenSource? _cts;
    private Task? _frameLoopTask;

    private long _framesEncoded;
    private long _framesSent;
    private long _fpsFrameCount;
    private DateTimeOffset? _lastEncodedAt;
    private double _currentFps;
    private long _lastFrameEncodeMs;
    private readonly Stopwatch _fpsSw = new();

    /// <summary>帯域適応品質制御コントローラー。</summary>
    private readonly BandwidthAdaptiveController _adaptiveController;

    /// <summary>現在ストリーミング中の仮想ディスプレイハンドル。</summary>
    public VirtualDisplayHandle CurrentHandle { get; private set; }

    /// <summary>エンコーダーを作る関数。差し替えるとエンコード処理を置き換えられる。</summary>
    private readonly Func<IFrameEncoder?> _encoderFactory;

    public Streamer() : this(new BandwidthAdaptiveController()) { }

    /// <summary>カスタムの帯域適応コントローラーでストリーマーを作成する（テスト用）。</summary>
    public Streamer(BandwidthAdaptiveController adaptiveController)
        : this(adaptiveController, CreateDefaultEncoder) { }

    /// <summary>
    /// エンコーダーの生成方法を指定してストリーマーを作成する。
    /// </summary>
    /// <param name="adaptiveController">帯域適応コントローラー。</param>
    /// <param name="encoderFactory">
    /// エンコーダーを生成する関数。エンコーダーが使えない環境では null を返してよい。
    /// </param>
    public Streamer(BandwidthAdaptiveController adaptiveController, Func<IFrameEncoder?> encoderFactory)
    {
        _adaptiveController = adaptiveController ?? throw new ArgumentNullException(nameof(adaptiveController));
        _encoderFactory     = encoderFactory ?? throw new ArgumentNullException(nameof(encoderFactory));
        _config = new StreamerConfig(
            TargetBitrateBps: 10_000_000,
            MaxFps: 60,
            Codec: VideoCodec.H264,
            TargetResolution: new Resolution(1920, 1080));
        CurrentHandle = VirtualDisplayHandle.NewHandle();
    }

    /// <summary>
    /// 既定のエンコーダーを作る。使えない環境では null を返す。
    /// </summary>
    private static IFrameEncoder? CreateDefaultEncoder()
    {
        if (!OperatingSystem.IsWindows()) return null;

#pragma warning disable CA1416
        return NativeH264Encoder.IsAvailable ? new NativeH264Encoder() : null;
#pragma warning restore CA1416
    }

    /// <inheritdoc/>
    public StreamerConfig Config
    {
        get { lock (_configLock) return _config; }
        set { lock (_configLock) _config = value; }
    }

    /// <summary>現在の統計情報を返す。</summary>
    public StreamerStats Stats => new(
        FramesEncoded: Interlocked.Read(ref _framesEncoded),
        FramesSent: Interlocked.Read(ref _framesSent),
        CurrentFps: _currentFps,
        CurrentBitrateBps: Config.TargetBitrateBps,
        LastEncodedAt: _lastEncodedAt,
        LastFrameEncodeMs: Interlocked.Read(ref _lastFrameEncodeMs));

    /// <inheritdoc/>
    public Task StartAsync(IVirtualDisplayDriver source, ITransport transport, CancellationToken ct)
        => StartAsync(CurrentHandle, source, transport, ct);

    /// <summary>ハンドルを指定してストリーミングを開始するオーバーロード。</summary>
    public Task StartAsync(VirtualDisplayHandle handle, IVirtualDisplayDriver source, ITransport transport, CancellationToken ct)
    {
        CurrentHandle = handle;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _cts.Token;
        _fpsSw.Restart();
        _frameLoopTask = Task.Run(async () =>
        {
            try
            {
                await FrameLoopAsync(source, transport, token);
            }
            catch (OperationCanceledException)
            {
                // 正常なキャンセル
            }
            catch (Exception ex)
            {
                // フレームループが予期しない例外で終了
                System.Diagnostics.Trace.WriteLine($"[Streamer] FrameLoop EXCEPTION: {ex}");
                // ログファイルに書き込む
                try
                {
                    var logDir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "vmonitor", "logs");
                    Directory.CreateDirectory(logDir);
                    File.AppendAllText(
                        Path.Combine(logDir, "streamer_error.log"),
                        $"{DateTimeOffset.UtcNow:O} EXCEPTION: {ex}\n");
                }
                catch { }
            }
        }, token);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task StopAsync()
    {
        _cts?.Cancel();
        if (_frameLoopTask != null)
        {
            try { await _frameLoopTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        _cts?.Dispose();
        _cts = null;
        _frameLoopTask = null;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// 帯域適応ビットレート制御の実装（Requirements 4.5）:
    ///
    /// 1. <see cref="BandwidthAdaptiveController.Update"/> を呼び出してティアを評価する。
    ///    帯域低下時は解像度・ビットレートを即座に降格し、回復時は段階的に昇格する。
    /// 2. <see cref="BandwidthAdaptiveController.CalculateCappedBitrate"/> で
    ///    ティアのビットレートと帯域推定値の小さい方に設定する（Property 8 の条件）。
    /// 3. 最低品質ティアでも MaxFps は 30fps を維持する。
    /// </remarks>
    public void OnBandwidthEstimate(long bitsPerSecond)
    {
        if (bitsPerSecond < 0)
            bitsPerSecond = 0;

        // ティア評価: 帯域低下なら即降格、回復なら連続観測後に昇格
        _adaptiveController.Update(bitsPerSecond);

        // ビットレートは帯域推定値を超えてはならない
        int effectiveBitrate = _adaptiveController.CalculateCappedBitrate(bitsPerSecond);

        lock (_configLock)
        {
            _config = _config with
            {
                TargetBitrateBps = effectiveBitrate,
                TargetResolution = _adaptiveController.CurrentResolution,
                MaxFps = _adaptiveController.CurrentMaxFps,
            };
        }
    }

    private async Task FrameLoopAsync(IVirtualDisplayDriver source, ITransport transport, CancellationToken ct)
    {
        // エンコーダーは最初のフレームが来てから、そのフレームの解像度で初期化する。
        // Config.TargetResolution は要求値でしかなく、実際に届く絵と食い違うことがある
        // （ミラー元ディスプレイの解像度が優先されるため）。
        // 食い違ったまま初期化すると、エンコーダーは入力サイズ不一致で何も出力しない。
        IFrameEncoder? encoder = _encoderFactory();
        Resolution? encoderResolution = null;
        bool encoderUnavailable = encoder is null;

        long framesSentLocal = 0;
        long framesSkippedLocal = 0;

        try
        {
            await foreach (var frame in source.GetFramesAsync(CurrentHandle, ct))
            {
                ct.ThrowIfCancellationRequested();

                byte[]? encoded;

                // フレームは BGRA32 なので、幅×高さ×4 バイト必要。
                // 足りないフレームはエンコーダーに渡さない（不正な入力になる）。
                bool frameIsComplete =
                    frame.Data.Length >= (long)frame.Resolution.Width * frame.Resolution.Height * 4;

                // 実際に届いたフレームの解像度でエンコーダーを用意する。
                // 解像度が変わった場合は作り直す。
                if (!encoderUnavailable && frameIsComplete && frame.Resolution != encoderResolution)
                {
                    try
                    {
                        var cfg = Config;
                        encoder!.Configure(frame.Resolution, cfg.TargetBitrateBps, cfg.MaxFps);
                        encoderResolution = frame.Resolution;
                    }
                    catch (Exception ex)
                    {
                        // 初期化に失敗したらエンコードなしで継続する（送信はしない）
                        System.Diagnostics.Debug.WriteLine($"[Streamer] Encoder init failed: {ex.Message}");
                        encoder?.Dispose();
                        encoder = null;
                        encoderUnavailable = true;
                    }
                }

                if (encoder != null && frameIsComplete)
                {
                    var cfg = Config;
                    encoder.SetBitrate(cfg.TargetBitrateBps);

                    var encSw = Stopwatch.StartNew();
                    encoded = encoder.Encode(frame.Data.Span, frame.TimestampUs);
                    encSw.Stop();
                    Interlocked.Exchange(ref _lastFrameEncodeMs, encSw.ElapsedMilliseconds);

                    if (encoded is null || encoded.Length == 0)
                    {
                        // H.264 エンコーダーはバッファリングのため最初の数フレームは null を返す
                        // スキップして次フレームを待つ
                        framesSkippedLocal++;
                        continue;
                    }
                }
                else
                {
                    // エンコーダーが使えない、またはフレームが空。
                    //
                    // ここでそれらしいバイト列を送ってはいけない。受信側の
                    // ハードウェアデコーダーは不正な NAL でエラー状態に落ち、
                    // 以降の正常なフレームも表示できなくなる。
                    // 送らずに落とすほうが、復帰可能なぶん正しい。
                    framesSkippedLocal++;
                    continue;
                }

                Interlocked.Increment(ref _framesEncoded);
                _lastEncodedAt = DateTimeOffset.UtcNow;

                try
                {
                    await transport.SendAsync(encoded, ChannelId.Video, ct);
                    Interlocked.Increment(ref _framesSent);
                    framesSentLocal++;
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    System.Diagnostics.Debug.WriteLine($"[Streamer] SendAsync failed: {ex.Message}");
                    // 送信エラーは無視して継続（接続が切れたら ct がキャンセルされる）
                }

                // FPS 計測: 1 秒ごとに更新
                Interlocked.Increment(ref _fpsFrameCount);
                if (_fpsSw.Elapsed.TotalSeconds >= 1.0)
                {
                    _currentFps = Interlocked.Exchange(ref _fpsFrameCount, 0) / _fpsSw.Elapsed.TotalSeconds;
                    _fpsSw.Restart();
                }
            }
        }
        finally
        {
            encoder?.Dispose();
            System.Diagnostics.Debug.WriteLine($"[Streamer] FrameLoop ended. sent={framesSentLocal}, skipped={framesSkippedLocal}");
        }
    }
}
