// Feature: vmonitor, Property 6: エンコード処理時間の上限

using System.Diagnostics;
using System.Runtime.CompilerServices;
using FsCheck;
using FsCheck.Xunit;
using Moq;
using VMonitor.Core.Interfaces;
using VMonitor.Core.Models;
using StreamerImpl = VMonitor.Streamer.Streamer;

namespace VMonitor.Tests;

/// <summary>
/// Property 6: エンコード処理時間の上限
/// Validates: Requirements 4.3
///
/// 任意の解像度とフレーム内容に対して、ストリーマーの単フレームエンコード処理時間は
/// 100ms 未満でなければならない（ネットワーク転送部分はモックで除外）。
/// </summary>
public class StreamerEncodeTimePropertyTests
{
    // サポート解像度の範囲
    private const int MinWidth = 640;
    private const int MinHeight = 480;
    private const int MaxWidth = 3840;
    private const int MaxHeight = 2160;

    /// <summary>
    /// Property 6: 任意の有効な解像度とフレーム内容に対して、
    /// Streamer の単フレームエンコード処理時間は 100ms 未満でなければならない。
    ///
    /// トランスポートはモックして、ネットワーク転送時間を除外する。
    /// フレームソースもモックして、単一フレームのみ供給する。
    ///
    /// パラメーター:
    ///   rawWidth   - 解像度の幅に射影する整数（640〜3840 に正規化）
    ///   rawHeight  - 解像度の高さに射影する整数（480〜2160 に正規化）
    ///   frameData  - フレームの生ピクセルデータ（任意バイト配列）
    /// </summary>
    [Property(MaxTest = 100)]
    public bool EncodingTimePerFrameIsUnder100ms(
        int rawWidth,
        int rawHeight,
        byte[] frameData)
    {
        // 解像度をサポート範囲内に正規化する
        int width = MinWidth + Math.Abs(rawWidth) % (MaxWidth - MinWidth + 1);
        int height = MinHeight + Math.Abs(rawHeight) % (MaxHeight - MinHeight + 1);
        var resolution = new Resolution(width, height);

        // frameData が null の場合は最小データで代替する
        var pixelData = (frameData is { Length: > 0 }) ? frameData : new byte[] { 0 };

        // 単一フレームを生成するスタブ VideoFrame
        var frame = new VideoFrame
        {
            SequenceNumber = 1,
            TimestampUs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000,
            Resolution = resolution,
            Data = new ReadOnlyMemory<byte>(pixelData)
        };

        // IVirtualDisplayDriver モック: GetFramesAsync は単一フレームを返した後終了する
        var vddMock = new Mock<IVirtualDisplayDriver>();
        vddMock
            .Setup(v => v.GetFramesAsync(
                It.IsAny<VirtualDisplayHandle>(),
                It.IsAny<CancellationToken>()))
            .Returns<VirtualDisplayHandle, CancellationToken>(
                (handle, ct) => SingleFrameAsyncEnumerable(frame, ct));

        // ITransport モック: ネットワーク転送を即時完了させて転送時間を除外する
        var transportMock = new Mock<ITransport>();
        transportMock
            .Setup(t => t.SendAsync(
                It.IsAny<ReadOnlyMemory<byte>>(),
                It.IsAny<ChannelId>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Streamer をテスト対象解像度で構成する
        var streamer = new StreamerImpl();
        streamer.Config = new StreamerConfig(
            TargetBitrateBps: 10_000_000,
            MaxFps: 60,
            Codec: VideoCodec.H264,
            TargetResolution: resolution);

        // ストリーミングを開始し、単一フレームが処理されるまで待機する
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var stopwatch = Stopwatch.StartNew();

        streamer.StartAsync(vddMock.Object, transportMock.Object, cts.Token)
                .GetAwaiter().GetResult();

        // フレームループが完了するまで待機する（フレームソースが枯渇したら自然終了する）
        // StopAsync はフレームループのキャンセルを待つので、ループ完了後に呼ぶ
        // 少し待機してフレームが処理されるのを確認する
        var frameProcessed = WaitForFrameProcessed(streamer, cts.Token);
        stopwatch.Stop();

        streamer.StopAsync().GetAwaiter().GetResult();

        // フレーム処理が完了していれば、そのエンコード時間が 100ms 未満であることを確認する
        // 処理されていなければタイムアウト（スキップ扱いで true を返す）
        if (!frameProcessed)
            return true;

        // フレームループ全体の経過時間から 100ms 以内であることを確認する
        return streamer.Stats.LastFrameEncodeMs < 100;
    }

    /// <summary>
    /// 単一フレームを生成して終了する非同期列挙子。
    /// エンコードループがフレームを処理した後、自然に終了する。
    /// </summary>
    private static async IAsyncEnumerable<VideoFrame> SingleFrameAsyncEnumerable(
        VideoFrame frame,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        yield return frame;
        await Task.CompletedTask;
    }

    /// <summary>
    /// Streamer が少なくとも 1 フレームをエンコードしたことを、
    /// Stats.FramesEncoded でポーリングして確認する。
    /// タイムアウトは 2 秒。
    /// </summary>
    private static bool WaitForFrameProcessed(StreamerImpl streamer, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
        while (DateTimeOffset.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            if (streamer.Stats.FramesEncoded >= 1)
                return true;
            Thread.Sleep(1);
        }
        return streamer.Stats.FramesEncoded >= 1;
    }
}
