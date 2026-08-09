// Feature: vmonitor, Property 9: ビットレート設定変更の即時反映

using FsCheck;
using FsCheck.Xunit;
using VMonitor.Core.Models;
using VMonitor.Streamer;

namespace VMonitor.Tests;

/// <summary>
/// Property 9: ビットレート設定変更の即時反映
/// Validates: Requirements 4.6
///
/// 任意の有効なビットレート値に対して、設定変更後にストリーマーの
/// Config.TargetBitrateBps がその値と等しくなければならない。
/// </summary>
public class StreamerBitrateConfigPropertyTests
{
    // -------------------------------------------------------------------------
    // Property 9-A: Config setter で TargetBitrateBps が即座に反映される
    // -------------------------------------------------------------------------

    /// <summary>
    /// Property 9-A: 任意の有効なビットレート値（1 bps 〜 100 Mbps）に対して、
    /// Config setter で新しい StreamerConfig を設定した直後に
    /// Config.TargetBitrateBps が指定値と等しくなければならない。
    ///
    /// パラメーター:
    ///   rawBitrate - ビットレートのシード（1 〜 100_000_000 bps に正規化）
    /// </summary>
    [Property(MaxTest = 200)]
    public bool ConfigSetterImmediatelyReflectsNewBitrate(int rawBitrate)
    {
        // 1 〜 100_000_000 bps の有効な範囲に正規化
        int bitrate = (Math.Abs(rawBitrate) % 100_000_000) + 1;

        var streamer = new Streamer.Streamer();

        // 新しいビットレートを含む StreamerConfig を設定する
        var newConfig = streamer.Config with { TargetBitrateBps = bitrate };
        streamer.Config = newConfig;

        // 即座に反映されていること
        return streamer.Config.TargetBitrateBps == bitrate;
    }

    // -------------------------------------------------------------------------
    // Property 9-B: Config setter はその他のフィールドを変更しない
    // -------------------------------------------------------------------------

    /// <summary>
    /// Property 9-B: TargetBitrateBps のみ変更した場合、MaxFps・Codec・TargetResolution は
    /// 変更前の値を維持しなければならない。
    ///
    /// パラメーター:
    ///   rawBitrate - ビットレートのシード（1 〜 100_000_000 bps に正規化）
    /// </summary>
    [Property(MaxTest = 200)]
    public bool ConfigSetterPreservesOtherFields(int rawBitrate)
    {
        int bitrate = (Math.Abs(rawBitrate) % 100_000_000) + 1;

        var streamer = new Streamer.Streamer();
        var original = streamer.Config;

        // TargetBitrateBps だけ変更した新しい Config を設定する
        streamer.Config = original with { TargetBitrateBps = bitrate };

        var updated = streamer.Config;

        // TargetBitrateBps 以外のフィールドが変わっていないこと
        return updated.MaxFps == original.MaxFps
            && updated.Codec == original.Codec
            && updated.TargetResolution == original.TargetResolution;
    }

    // -------------------------------------------------------------------------
    // Property 9-C: 連続した Config 変更で最後の値が反映される
    // -------------------------------------------------------------------------

    /// <summary>
    /// Property 9-C: 複数回ビットレートを変更した場合、最後に設定した値が
    /// Config.TargetBitrateBps に反映されなければならない。
    ///
    /// パラメーター:
    ///   rawBitrate1 - 1 回目のビットレートシード
    ///   rawBitrate2 - 2 回目（最終）のビットレートシード
    /// </summary>
    [Property(MaxTest = 200)]
    public bool LastConfigUpdateWins(int rawBitrate1, int rawBitrate2)
    {
        int bitrate1 = (Math.Abs(rawBitrate1) % 100_000_000) + 1;
        int bitrate2 = (Math.Abs(rawBitrate2) % 100_000_000) + 1;

        var streamer = new Streamer.Streamer();

        streamer.Config = streamer.Config with { TargetBitrateBps = bitrate1 };
        streamer.Config = streamer.Config with { TargetBitrateBps = bitrate2 };

        // 最後に設定した値が反映されていること
        return streamer.Config.TargetBitrateBps == bitrate2;
    }

    // -------------------------------------------------------------------------
    // 具体的なユニットテスト（境界値・代表値）
    // -------------------------------------------------------------------------

    /// <summary>
    /// 最小ビットレート（1 bps）を設定しても即時反映されること。
    /// </summary>
    [Fact]
    public void MinimumBitrateIsReflectedImmediately()
    {
        var streamer = new Streamer.Streamer();
        streamer.Config = streamer.Config with { TargetBitrateBps = 1 };
        Assert.Equal(1, streamer.Config.TargetBitrateBps);
    }

    /// <summary>
    /// デフォルト値 (10 Mbps) から 5 Mbps への変更が即時反映されること。
    /// </summary>
    [Fact]
    public void BitrateChangeFrom10MbpsTo5MbpsIsReflectedImmediately()
    {
        var streamer = new Streamer.Streamer();
        Assert.Equal(10_000_000, streamer.Config.TargetBitrateBps); // デフォルト確認

        streamer.Config = streamer.Config with { TargetBitrateBps = 5_000_000 };

        Assert.Equal(5_000_000, streamer.Config.TargetBitrateBps);
    }

    /// <summary>
    /// 大きなビットレート (100 Mbps) への変更が即時反映されること。
    /// </summary>
    [Fact]
    public void HighBitrateIsReflectedImmediately()
    {
        var streamer = new Streamer.Streamer();
        streamer.Config = streamer.Config with { TargetBitrateBps = 100_000_000 };
        Assert.Equal(100_000_000, streamer.Config.TargetBitrateBps);
    }
}
