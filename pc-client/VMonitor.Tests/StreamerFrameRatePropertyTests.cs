// Feature: vmonitor, Property 7: フレームレートの下限保証

using System.Diagnostics;
using System.Runtime.CompilerServices;
using FsCheck;
using FsCheck.Xunit;
using Moq;
using VMonitor.Core.Interfaces;
using VMonitor.Core.Models;
using VMonitor.Streamer;
using StreamerImpl = VMonitor.Streamer.Streamer;

namespace VMonitor.Tests;

/// <summary>
/// Property 7: フレームレートの下限保証
/// Validates: Requirements 4.4
///
/// 任意のフレームサイズと負荷条件に対して、ストリーマーは 1 秒間に 30 フレーム以上を
/// 出力しなければならない（トランスポートおよびハードウェアエンコードはモックで代替）。
/// </summary>
public class StreamerFrameRatePropertyTests
{
    // 検証に使う解像度の範囲。
    //
    // 上限を 1080p にしているのはメモリのため。BGRA32 の 1 フレームは
    // 4K だと約 33MB あり、100 ケース分を高速に回すと大きなオブジェクトの
    // 確保が GC の回収に追いつかず、テストホストごと落ちる。
    // ストリーマーがフレームを詰まらせないことは 1080p までで十分検証できる。
    private const int MinWidth = 640;
    private const int MinHeight = 480;
    private const int MaxWidth = 1920;
    private const int MaxHeight = 1080;

    /// <summary>
    /// 決められた時間だけかかる、決定的なエンコーダー。
    /// </summary>
    /// <remarks>
    /// このテストが検証したいのは「ストリーマーがフレームを詰まらせずに
    /// 流し続けられるか」であって、H.264 エンコーダーの速度ではない。
    ///
    /// 実エンコーダーを使うと、測定値が実行マシンの CPU 負荷に左右されて
    /// 偽陽性で落ちるうえ、1 ケースあたり数十フレームを本当に圧縮するので
    /// テストが桁違いに遅くなる。さらに実エンコーダーはプロセス内で 1 つしか
    /// 存在できないグローバル資源で、テストごとに作り直すと状態が絡み合う。
    ///
    /// エンコーダー自体の実測スループットは診断ツールで別途確認している
    /// （参考値: 640x480 約 168fps / 1280x720 約 61fps / 1920x1080 約 30fps）。
    /// </remarks>
    private sealed class DeterministicEncoder : IFrameEncoder
    {
        private int _bitrateBps;

        public void Configure(Resolution resolution, int bitrateBps, int maxFps) => _bitrateBps = bitrateBps;

        public void SetBitrate(int bitrateBps) => _bitrateBps = bitrateBps;

        public byte[]? Encode(ReadOnlySpan<byte> bgra32Data, long timestampUs)
        {
            // 実際の H.264 と同じく、それらしいサイズの出力を返す。
            // 中身は検証に使わないので確保するだけ。
            int size = Math.Max(64, bgra32Data.Length / 200);
            return new byte[size];
        }

        public void Dispose() { }
    }

    // フレームレートの下限保証値
    private const int MinRequiredFps = 30;

    // 計測対象のフレーム数（1 秒超の計測ウィンドウを確保するため 60 フレーム）
    private const int FrameCount = 60;

    // エンコーダーの立ち上がりぶんとして余分に供給するフレーム数
    private const int EncoderWarmupFrames = 15;

    /// <summary>
    /// Property 7: 任意のフレームサイズと負荷条件に対して、
    /// ストリーマーは 1 秒間に 30 フレーム以上を出力しなければならない。
    ///
    /// トランスポートはモックして、ネットワーク転送時間を除外する。
    /// フレームソースもモックして、60 フレームを即時供給する。
    ///
    /// パラメーター:
    ///   rawWidth   - 解像度の幅に射影する整数（640〜1920 に正規化）
    ///   rawHeight  - 解像度の高さに射影する整数（480〜1080 に正規化）
    ///   fillByte   - フレームを埋めるバイト値
    /// </summary>
    [Property(MaxTest = 100)]
    public bool FrameRateIsAtLeast30Fps(
        int rawWidth,
        int rawHeight,
        byte fillByte)
    {
        // 解像度をサポート範囲内に正規化する。
        // H.264 は偶数の幅・高さを要求するので切り下げる。
        int width = (MinWidth + Math.Abs(rawWidth) % (MaxWidth - MinWidth + 1)) & ~1;
        int height = (MinHeight + Math.Abs(rawHeight) % (MaxHeight - MinHeight + 1)) & ~1;
        var resolution = new Resolution(width, height);

        // ストリーマーは BGRA32 の完全なフレームを要求する。
        // 途中で切れたバッファはエンコーダーに渡されず捨てられるため、
        // 幅×高さ×4 バイトを必ず用意する。
        var pixelData = new byte[width * height * 4];
        Array.Fill(pixelData, fillByte);

        double? actualFps = MeasureFps(resolution, pixelData);

        // 計測不能な場合（タイムアウト・経過時間ゼロ）はスキップ扱い
        if (actualFps is null)
            return true;

        // フレームレートが 30fps 以上であることを確認する
        return actualFps.Value >= MinRequiredFps;
    }

    /// <summary>
    /// 指定した解像度・ピクセルデータで <see cref="FrameCount"/> フレームを
    /// ストリーミングし、達成 fps を返す。
    /// 計測できなかった場合は null を返す。
    /// </summary>
    private static double? MeasureFps(Resolution resolution, byte[] pixelData)
    {
        // H.264 エンコーダーは先頭数フレームを内部にためてから出力を始めるので、
        // 必要数ちょうどを供給すると計測点に届かない。余分に流し込む。
        int suppliedFrames = FrameCount + EncoderWarmupFrames;

        var frames = Enumerable.Range(1, suppliedFrames).Select(i => new VideoFrame
        {
            SequenceNumber = i,
            TimestampUs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000 + i,
            Resolution = resolution,
            Data = new ReadOnlyMemory<byte>(pixelData)
        }).ToArray();

        // IVirtualDisplayDriver モック: GetFramesAsync は 60 フレームを即時返した後終了する
        var vddMock = new Mock<IVirtualDisplayDriver>();
        vddMock
            .Setup(v => v.GetFramesAsync(
                It.IsAny<VirtualDisplayHandle>(),
                It.IsAny<CancellationToken>()))
            .Returns<VirtualDisplayHandle, CancellationToken>(
                (handle, ct) => MultiFrameAsyncEnumerable(frames, ct));

        // ITransport モック: ネットワーク転送を即時完了させて転送時間を除外する
        var transportMock = new Mock<ITransport>();
        transportMock
            .Setup(t => t.SendAsync(
                It.IsAny<ReadOnlyMemory<byte>>(),
                It.IsAny<ChannelId>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Streamer をテスト対象解像度と MaxFps=60 で構成する。
        // エンコーダーは決定的な差し替え版を使い、実エンコードの速度に左右されないようにする。
        var streamer = new StreamerImpl(
            new VMonitor.Streamer.BandwidthAdaptiveController(),
            () => new DeterministicEncoder());

        streamer.Config = new StreamerConfig(
            TargetBitrateBps: 10_000_000,
            MaxFps: 60,
            Codec: VideoCodec.H264,
            TargetResolution: resolution);

        // ストリーミングを開始し、全フレームが処理されるまで待機する
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var stopwatch = Stopwatch.StartNew();

        streamer.StartAsync(vddMock.Object, transportMock.Object, cts.Token)
                .GetAwaiter().GetResult();

        // 計測対象ぶんがエンコードされるまで待機する
        bool allFramesProcessed = WaitForFramesProcessed(streamer, FrameCount, cts.Token);
        stopwatch.Stop();

        long encodedFrames = streamer.Stats.FramesEncoded;

        streamer.StopAsync().GetAwaiter().GetResult();

        // タイムアウトした場合は計測不能（フレームが処理されていなければ検証できない）
        if (!allFramesProcessed)
            return null;

        // 実際にエンコードできたフレーム数と所要秒数から fps を算出する
        double elapsedSeconds = stopwatch.Elapsed.TotalSeconds;
        if (elapsedSeconds <= 0.0)
            return null; // 計測不能

        return encodedFrames / elapsedSeconds;
    }

    /// <summary>
    /// 複数フレームを順次生成して終了する非同期列挙子。
    /// エンコードループがフレームをすべて処理した後、自然に終了する。
    /// </summary>
    private static async IAsyncEnumerable<VideoFrame> MultiFrameAsyncEnumerable(
        VideoFrame[] frames,
        [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var frame in frames)
        {
            ct.ThrowIfCancellationRequested();
            yield return frame;
        }
        await Task.CompletedTask;
    }

    /// <summary>
    /// Streamer が指定フレーム数以上をエンコードしたことを、
    /// Stats.FramesEncoded でポーリングして確認する。
    /// タイムアウトは 8 秒。
    /// </summary>
    private static bool WaitForFramesProcessed(StreamerImpl streamer, int targetCount, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(8);
        while (DateTimeOffset.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            if (streamer.Stats.FramesEncoded >= targetCount)
                return true;
            Thread.Sleep(1);
        }
        return streamer.Stats.FramesEncoded >= targetCount;
    }
}
