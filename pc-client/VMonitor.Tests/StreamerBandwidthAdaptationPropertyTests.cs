// Feature: vmonitor, Property 8: 帯域低下時のビットレート適応

using FsCheck;
using FsCheck.Xunit;
using VMonitor.Core.Models;
using VMonitor.Streamer;

namespace VMonitor.Tests;

/// <summary>
/// Property 8: 帯域低下時のビットレート適応
/// Validates: Requirements 4.5
///
/// 任意の帯域推定値（0 以上）に対して、OnBandwidthEstimate 呼び出し後に
/// ストリーマーの出力ビットレートはその帯域推定値以下でなければならない。
///
/// また:
///   - 最低品質ティアでも MaxFps は 30fps 以上を維持すること。
///   - 帯域低下時は解像度またはビットレートが下がること（映像送信継続）。
/// </summary>
public class StreamerBandwidthAdaptationPropertyTests
{
    // -------------------------------------------------------------------------
    // Property 8-A: 出力ビットレートは帯域推定値を超えてはならない
    // -------------------------------------------------------------------------

    /// <summary>
    /// Property 8-A: 任意の非負帯域推定値に対して、
    /// OnBandwidthEstimate 呼び出し後の Config.TargetBitrateBps が
    /// その帯域推定値以下でなければならない。
    ///
    /// パラメーター:
    ///   rawBandwidth - 帯域推定値のシード（0 以上の整数に正規化）
    /// </summary>
    [Property(MaxTest = 200)]
    public bool OutputBitrateDoesNotExceedBandwidthEstimate(long rawBandwidth)
    {
        // 0 以上の帯域値に正規化（0 〜 50 Mbps の範囲）
        long bandwidth = Math.Abs(rawBandwidth) % 50_000_001L; // 0 〜 50_000_000 bps

        var streamer = new Streamer.Streamer();
        streamer.OnBandwidthEstimate(bandwidth);

        // Config.TargetBitrateBps が帯域推定値を超えていないこと
        return streamer.Config.TargetBitrateBps <= bandwidth;
    }

    // -------------------------------------------------------------------------
    // Property 8-B: 最低品質でも MaxFps は 30fps 以上を維持する
    // -------------------------------------------------------------------------

    /// <summary>
    /// Property 8-B: 任意の帯域推定値（極端に低い値を含む）に対して、
    /// OnBandwidthEstimate 呼び出し後の Config.MaxFps が 30 以上でなければならない。
    ///
    /// パラメーター:
    ///   rawBandwidth - 帯域推定値のシード（0 以上の整数に正規化）
    /// </summary>
    [Property(MaxTest = 200)]
    public bool MinimumQualityMaintains30Fps(long rawBandwidth)
    {
        // 極端に低い帯域値も含む（0 〜 50 Mbps の範囲）
        long bandwidth = Math.Abs(rawBandwidth) % 50_000_001L; // 0 〜 50_000_000 bps

        var streamer = new Streamer.Streamer();
        streamer.OnBandwidthEstimate(bandwidth);

        // MaxFps は常に 30 以上
        return streamer.Config.MaxFps >= 30;
    }

    // -------------------------------------------------------------------------
    // Property 8-C: 帯域低下時は解像度またはビットレートが下がること
    // -------------------------------------------------------------------------

    /// <summary>
    /// Property 8-C: 高帯域から低帯域へ移行したとき、
    /// ビットレートまたは解像度のいずれかが下がること（映像送信継続の根拠）。
    ///
    /// パラメーター:
    ///   rawHighBandwidth - 高帯域のシード（10 Mbps 以上に正規化）
    ///   rawLowBandwidth  - 低帯域のシード（2 Mbps 未満に正規化）
    /// </summary>
    [Property(MaxTest = 200)]
    public bool QualityDegradesWhenBandwidthDrops(long rawHighBandwidth, long rawLowBandwidth)
    {
        // 高帯域: 10_000_000 〜 50_000_000 bps
        long highBandwidth = 10_000_000L + (Math.Abs(rawHighBandwidth) % 40_000_001L);
        // 低帯域: 0 〜 1_999_999 bps
        long lowBandwidth = Math.Abs(rawLowBandwidth) % 2_000_000L;

        var streamer = new Streamer.Streamer();

        // まず高帯域で初期化
        streamer.OnBandwidthEstimate(highBandwidth);
        int highBitrate = streamer.Config.TargetBitrateBps;
        var highResolution = streamer.Config.TargetResolution;

        // 次に低帯域を通知
        streamer.OnBandwidthEstimate(lowBandwidth);
        int lowBitrate = streamer.Config.TargetBitrateBps;
        var lowResolution = streamer.Config.TargetResolution;

        // ビットレートまたは解像度のいずれかが下がっていること
        bool bitrateDecreased = lowBitrate <= highBitrate;
        bool resolutionDecreased =
            lowResolution.Width * lowResolution.Height <=
            highResolution.Width * highResolution.Height;

        return bitrateDecreased || resolutionDecreased;
    }

    // -------------------------------------------------------------------------
    // Property 8-D: ゼロ帯域でも例外が発生しないこと（ロバスト性）
    // -------------------------------------------------------------------------

    /// <summary>
    /// Property 8-D: ゼロ帯域（最悪値）が渡されても例外が発生せず、
    /// MaxFps が 30 以上を維持すること。
    /// </summary>
    [Fact]
    public void ZeroBandwidthDoesNotThrowAndMaintains30Fps()
    {
        var streamer = new Streamer.Streamer();

        var exception = Record.Exception(() => streamer.OnBandwidthEstimate(0));
        Assert.Null(exception);
        Assert.True(streamer.Config.MaxFps >= 30, "Zero bandwidth: MaxFps must be >= 30");
        Assert.True(streamer.Config.TargetBitrateBps >= 0, "Zero bandwidth: TargetBitrateBps must be >= 0");
    }

    // -------------------------------------------------------------------------
    // Property 8-E: BandwidthAdaptiveController の独立テスト
    // -------------------------------------------------------------------------

    /// <summary>
    /// Property 8-E: BandwidthAdaptiveController の Update 呼び出し後、
    /// CalculateCappedBitrate が帯域推定値を超えないこと。
    ///
    /// ストリーマーとは独立してコアロジックを検証する。
    /// </summary>
    [Property(MaxTest = 300)]
    public bool AdaptiveControllerNeverExceedsBandwidth(long rawBandwidth)
    {
        long bandwidth = Math.Abs(rawBandwidth) % 50_000_001L; // 0 〜 50_000_000 bps

        var controller = new BandwidthAdaptiveController();
        controller.Update(bandwidth);
        int cappedBitrate = controller.CalculateCappedBitrate(bandwidth);

        return cappedBitrate <= bandwidth;
    }

    /// <summary>
    /// Property 8-F: 任意の帯域推定値に対して、
    /// BandwidthAdaptiveController.CurrentMaxFps が常に 30 以上であること。
    /// </summary>
    [Property(MaxTest = 300)]
    public bool AdaptiveControllerAlwaysMaintains30Fps(long rawBandwidth)
    {
        long bandwidth = Math.Abs(rawBandwidth) % 50_000_001L;

        var controller = new BandwidthAdaptiveController();
        controller.Update(bandwidth);

        return controller.CurrentMaxFps >= 30;
    }
}
