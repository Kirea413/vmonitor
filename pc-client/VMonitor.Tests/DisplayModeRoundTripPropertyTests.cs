// Feature: vmonitor, Property 4: DisplayMode 設定の即時反映

using FsCheck;
using FsCheck.Xunit;
using VMonitor.Core.Interfaces;
using VMonitor.Core.Models;
using VMonitor.Driver;

namespace VMonitor.Tests;

/// <summary>
/// Property 4: DisplayMode 設定の即時反映
/// Validates: Requirements 3.3, 3.4, 7.3
///
/// 任意の DisplayMode 値（Clone / Extend / SecondaryOnly）に対して、
/// SetDisplayModeAsync の呼び出し後に GetCurrentConfigAsync が同じ値を返さなければならない。
/// </summary>
public class DisplayModeRoundTripPropertyTests
{
    // ── ヘルパー ────────────────────────────────────────────────────────────

    /// <summary>
    /// テスト用の SUT（System Under Test）を生成するファクトリ。
    /// FakeWindowsDisplayApi を使って Windows API 呼び出しを回避する。
    /// </summary>
    private static (DisplaySettingsManager manager, VirtualDisplayHandle handle) CreateSut()
    {
        var api = new FakeWindowsDisplayApi();
        var handle = VirtualDisplayHandle.NewHandle();

        // ハンドルを登録しておく（FakeWindowsDisplayApi の初期設定）
        api.RegisterDisplay(handle, new DisplayConfig(
            Resolution: new Resolution(1920, 1080),
            RefreshRateHz: 60,
            Orientation: Orientation.Landscape,
            Mode: DisplayMode.Extend));

        var manager = new DisplaySettingsManager(api);
        return (manager, handle);
    }

    // ── Property 4-A: 任意の DisplayMode 値がラウンドトリップする ─────────────

    /// <summary>
    /// Property 4-A: 任意の DisplayMode 値（Clone / Extend / SecondaryOnly）に対して、
    /// SetDisplayModeAsync 呼び出し後に GetCurrentConfigAsync が同じ値を返さなければならない。
    ///
    /// FsCheck は DisplayMode 列挙型のすべての値をランダムに生成して検証する。
    /// Validates: Requirements 3.3, 3.4, 7.3
    /// </summary>
    [Property(MaxTest = 100)]
    public bool DisplayModeRoundTrip(DisplayMode mode)
    {
        var (manager, handle) = CreateSut();

        // 同期的に実行するため .GetAwaiter().GetResult() を使用する
        // （FsCheck の [Property] はデフォルトでは非同期をサポートしないため）
        manager.SetDisplayModeAsync(handle, mode).GetAwaiter().GetResult();
        var config = manager.GetCurrentConfigAsync(handle).GetAwaiter().GetResult();

        return config.Mode == mode;
    }

    // ── Property 4-B: 連続した DisplayMode 変更で最後の値が反映される ────────

    /// <summary>
    /// Property 4-B: 任意の 2 つの DisplayMode 値（first / second）に対して、
    /// 2 回連続で SetDisplayModeAsync を呼び出した後、
    /// GetCurrentConfigAsync が最後に設定した値（second）を返さなければならない。
    ///
    /// Validates: Requirements 3.3, 3.4, 7.3
    /// </summary>
    [Property(MaxTest = 100)]
    public bool DisplayModeLastUpdateWins(DisplayMode first, DisplayMode second)
    {
        var (manager, handle) = CreateSut();

        manager.SetDisplayModeAsync(handle, first).GetAwaiter().GetResult();
        manager.SetDisplayModeAsync(handle, second).GetAwaiter().GetResult();
        var config = manager.GetCurrentConfigAsync(handle).GetAwaiter().GetResult();

        return config.Mode == second;
    }

    // ── Property 4-C: DisplayMode 変更後も他のフィールドは変化しない ─────────

    /// <summary>
    /// Property 4-C: 任意の DisplayMode 値に対して、
    /// SetDisplayModeAsync の呼び出しは Mode フィールドのみを変更し、
    /// Resolution・RefreshRateHz・Orientation は変化しないことを確認する。
    ///
    /// Validates: Requirements 3.3, 3.4
    /// </summary>
    [Property(MaxTest = 100)]
    public bool DisplayModeChangePreservesOtherFields(DisplayMode mode)
    {
        var api = new FakeWindowsDisplayApi();
        var handle = VirtualDisplayHandle.NewHandle();
        var initialConfig = new DisplayConfig(
            Resolution: new Resolution(1920, 1080),
            RefreshRateHz: 60,
            Orientation: Orientation.Landscape,
            Mode: DisplayMode.Extend);
        api.RegisterDisplay(handle, initialConfig);

        var manager = new DisplaySettingsManager(api);

        manager.SetDisplayModeAsync(handle, mode).GetAwaiter().GetResult();
        var after = manager.GetCurrentConfigAsync(handle).GetAwaiter().GetResult();

        // Mode だけが変わり、他のフィールドは保持される
        return after.Resolution == initialConfig.Resolution
            && after.RefreshRateHz == initialConfig.RefreshRateHz
            && after.Orientation == initialConfig.Orientation;
    }

    // ── 具体的なユニットテスト（代表値・境界値） ───────────────────────────

    /// <summary>
    /// Clone モードを設定した後、GetCurrentConfigAsync が Clone を返すこと。
    /// Validates: Requirement 3.3
    /// </summary>
    [Fact]
    public async Task SetDisplayModeClone_GetCurrentConfig_ReturnsClone()
    {
        var (manager, handle) = CreateSut();

        await manager.SetDisplayModeAsync(handle, DisplayMode.Clone);
        var config = await manager.GetCurrentConfigAsync(handle);

        Assert.Equal(DisplayMode.Clone, config.Mode);
    }

    /// <summary>
    /// Extend モードを設定した後、GetCurrentConfigAsync が Extend を返すこと。
    /// Validates: Requirement 3.4
    /// </summary>
    [Fact]
    public async Task SetDisplayModeExtend_GetCurrentConfig_ReturnsExtend()
    {
        var (manager, handle) = CreateSut();

        await manager.SetDisplayModeAsync(handle, DisplayMode.Extend);
        var config = await manager.GetCurrentConfigAsync(handle);

        Assert.Equal(DisplayMode.Extend, config.Mode);
    }

    /// <summary>
    /// SecondaryOnly モードを設定した後、GetCurrentConfigAsync が SecondaryOnly を返すこと。
    /// Validates: Requirement 7.1
    /// </summary>
    [Fact]
    public async Task SetDisplayModeSecondaryOnly_GetCurrentConfig_ReturnsSecondaryOnly()
    {
        var (manager, handle) = CreateSut();

        await manager.SetDisplayModeAsync(handle, DisplayMode.SecondaryOnly);
        var config = await manager.GetCurrentConfigAsync(handle);

        Assert.Equal(DisplayMode.SecondaryOnly, config.Mode);
    }

    /// <summary>
    /// SetDisplayModeAsync は 3 秒以内に完了しなければならない（Requirement 7.3）。
    /// </summary>
    [Fact]
    public async Task SetDisplayModeAsync_CompletesWithinThreeSeconds()
    {
        var (manager, handle) = CreateSut();
        var sw = System.Diagnostics.Stopwatch.StartNew();

        await manager.SetDisplayModeAsync(handle, DisplayMode.Clone);

        sw.Stop();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3),
            $"SetDisplayModeAsync は 3 秒以内に完了すべきですが、{sw.Elapsed.TotalSeconds:F2} 秒かかりました。");
    }
}
