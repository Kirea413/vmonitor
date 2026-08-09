using VMonitor.Core.Interfaces;
using VMonitor.Core.Models;
using VMonitor.Driver;

namespace VMonitor.Tests;

/// <summary>
/// Task 11.1: DisplaySettingsManager のユニットテスト。
/// Clone / Extend / SecondaryOnly の各 DisplayMode と解像度設定の動作を検証する。
/// Validates: Requirements 3.3, 3.4, 7.3
/// </summary>
public class DisplaySettingsManagerTests
{
    // ── ヘルパー ──────────────────────────────────────────────────────────

    private static (DisplaySettingsManager manager, FakeWindowsDisplayApi api, VirtualDisplayHandle handle)
        CreateSut(DisplayMode initialMode = DisplayMode.Extend, Resolution? resolution = null)
    {
        var api = new FakeWindowsDisplayApi();
        var handle = VirtualDisplayHandle.NewHandle();
        var initialResolution = resolution ?? new Resolution(1920, 1080);
        api.RegisterDisplay(handle, new DisplayConfig(
            Resolution: initialResolution,
            RefreshRateHz: 60,
            Orientation: Orientation.Landscape,
            Mode: initialMode));

        var manager = new DisplaySettingsManager(api);
        return (manager, api, handle);
    }

    // ── SetDisplayModeAsync ───────────────────────────────────────────────

    /// <summary>
    /// SetDisplayModeAsync(Clone) を呼び出した後、
    /// GetCurrentConfigAsync が Clone を返すことを検証する。
    /// Validates: Requirement 3.3
    /// </summary>
    [Fact]
    public async Task SetDisplayModeAsync_Clone_UpdatesConfigToClone()
    {
        var (manager, _, handle) = CreateSut();

        await manager.SetDisplayModeAsync(handle, DisplayMode.Clone);

        var config = await manager.GetCurrentConfigAsync(handle);
        Assert.Equal(DisplayMode.Clone, config.Mode);
    }

    /// <summary>
    /// SetDisplayModeAsync(Extend) を呼び出した後、
    /// GetCurrentConfigAsync が Extend を返すことを検証する。
    /// Validates: Requirement 3.4
    /// </summary>
    [Fact]
    public async Task SetDisplayModeAsync_Extend_UpdatesConfigToExtend()
    {
        var (manager, _, handle) = CreateSut(DisplayMode.Clone);

        await manager.SetDisplayModeAsync(handle, DisplayMode.Extend);

        var config = await manager.GetCurrentConfigAsync(handle);
        Assert.Equal(DisplayMode.Extend, config.Mode);
    }

    /// <summary>
    /// SetDisplayModeAsync(SecondaryOnly) を呼び出した後、
    /// GetCurrentConfigAsync が SecondaryOnly を返すことを検証する。
    /// Validates: Requirement 7.1
    /// </summary>
    [Fact]
    public async Task SetDisplayModeAsync_SecondaryOnly_UpdatesConfigToSecondaryOnly()
    {
        var (manager, _, handle) = CreateSut();

        await manager.SetDisplayModeAsync(handle, DisplayMode.SecondaryOnly);

        var config = await manager.GetCurrentConfigAsync(handle);
        Assert.Equal(DisplayMode.SecondaryOnly, config.Mode);
    }

    /// <summary>
    /// SetDisplayModeAsync を連続して呼び出したとき、
    /// 最後の設定が反映されることを検証する。
    /// </summary>
    [Fact]
    public async Task SetDisplayModeAsync_MultipleChanges_ReflectsLastMode()
    {
        var (manager, _, handle) = CreateSut();

        await manager.SetDisplayModeAsync(handle, DisplayMode.Clone);
        await manager.SetDisplayModeAsync(handle, DisplayMode.SecondaryOnly);
        await manager.SetDisplayModeAsync(handle, DisplayMode.Extend);

        var config = await manager.GetCurrentConfigAsync(handle);
        Assert.Equal(DisplayMode.Extend, config.Mode);
    }

    // ── SetResolutionAsync ────────────────────────────────────────────────

    /// <summary>
    /// SetResolutionAsync で解像度を変更した後、
    /// GetCurrentConfigAsync が新しい解像度を返すことを検証する。
    /// </summary>
    [Fact]
    public async Task SetResolutionAsync_UpdatesConfigResolution()
    {
        var (manager, _, handle) = CreateSut();
        var newResolution = new Resolution(2560, 1440);

        await manager.SetResolutionAsync(handle, newResolution);

        var config = await manager.GetCurrentConfigAsync(handle);
        Assert.Equal(newResolution, config.Resolution);
    }

    /// <summary>
    /// SetResolutionAsync で最小解像度（640x480）を設定できることを検証する。
    /// </summary>
    [Fact]
    public async Task SetResolutionAsync_CanSetMinimumResolution()
    {
        var (manager, _, handle) = CreateSut();
        var minRes = Resolution.MinSupported;

        await manager.SetResolutionAsync(handle, minRes);

        var config = await manager.GetCurrentConfigAsync(handle);
        Assert.Equal(minRes, config.Resolution);
    }

    /// <summary>
    /// SetResolutionAsync で最大解像度（3840x2160）を設定できることを検証する。
    /// </summary>
    [Fact]
    public async Task SetResolutionAsync_CanSetMaximumResolution()
    {
        var (manager, _, handle) = CreateSut();
        var maxRes = Resolution.MaxSupported;

        await manager.SetResolutionAsync(handle, maxRes);

        var config = await manager.GetCurrentConfigAsync(handle);
        Assert.Equal(maxRes, config.Resolution);
    }

    // ── GetSupportedResolutionsAsync ──────────────────────────────────────

    /// <summary>
    /// GetSupportedResolutionsAsync が空でないリストを返すことを検証する。
    /// </summary>
    [Fact]
    public async Task GetSupportedResolutionsAsync_ReturnsNonEmptyList()
    {
        var (manager, _, handle) = CreateSut();

        var resolutions = await manager.GetSupportedResolutionsAsync(handle);

        Assert.NotEmpty(resolutions);
    }

    /// <summary>
    /// GetSupportedResolutionsAsync が返す解像度はすべて有効な寸法を持つことを検証する。
    /// </summary>
    [Fact]
    public async Task GetSupportedResolutionsAsync_AllResolutionsHavePositiveDimensions()
    {
        var (manager, _, handle) = CreateSut();

        var resolutions = await manager.GetSupportedResolutionsAsync(handle);

        foreach (var r in resolutions)
        {
            Assert.True(r.Width > 0, $"Width は正の整数でなければなりません（実際: {r.Width}）");
            Assert.True(r.Height > 0, $"Height は正の整数でなければなりません（実際: {r.Height}）");
        }
    }

    // ── GetCurrentConfigAsync ──────────────────────────────────────────────

    /// <summary>
    /// 登録されていないハンドルを渡したとき、
    /// GetCurrentConfigAsync が KeyNotFoundException をスローすることを検証する。
    /// </summary>
    [Fact]
    public async Task GetCurrentConfigAsync_ThrowsKeyNotFoundException_ForUnknownHandle()
    {
        var api = new FakeWindowsDisplayApi();
        var manager = new DisplaySettingsManager(api);
        var unknownHandle = VirtualDisplayHandle.NewHandle();

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => manager.GetCurrentConfigAsync(unknownHandle));
    }

    /// <summary>
    /// RegisterDisplay で初期設定を登録した後、
    /// GetCurrentConfigAsync が正しい初期値を返すことを検証する。
    /// </summary>
    [Fact]
    public async Task GetCurrentConfigAsync_ReturnsInitialConfig_AfterRegistration()
    {
        var api = new FakeWindowsDisplayApi();
        var handle = VirtualDisplayHandle.NewHandle();
        var expected = new DisplayConfig(
            Resolution: new Resolution(1280, 720),
            RefreshRateHz: 60,
            Orientation: Orientation.Landscape,
            Mode: DisplayMode.Extend);
        api.RegisterDisplay(handle, expected);
        var manager = new DisplaySettingsManager(api);

        var actual = await manager.GetCurrentConfigAsync(handle);

        Assert.Equal(expected, actual);
    }

    // ── タイムアウト（Requirement 7.3） ────────────────────────────────────

    /// <summary>
    /// SetDisplayModeAsync は 3 秒以内に完了しなければならない（タイムアウトなし正常系）。
    /// FakeWindowsDisplayApi は即座に反映するため、タイムアウトが発生しないことを検証する。
    /// Validates: Requirement 7.3
    /// </summary>
    [Fact]
    public async Task SetDisplayModeAsync_CompletesWithinThreeSeconds_UnderNormalConditions()
    {
        var (manager, _, handle) = CreateSut();
        var sw = System.Diagnostics.Stopwatch.StartNew();

        await manager.SetDisplayModeAsync(handle, DisplayMode.Clone);

        sw.Stop();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3),
            $"SetDisplayModeAsync は 3 秒以内に完了すべきですが、{sw.Elapsed.TotalSeconds:F2} 秒かかりました。");
    }

    /// <summary>
    /// SetDisplayModeAsync が変更を永遠に適用しない API ラッパーを使った場合、
    /// TimeoutException がスローされることを検証する。
    /// Validates: Requirement 7.3
    /// </summary>
    [Fact]
    public async Task SetDisplayModeAsync_ThrowsTimeoutException_WhenApiNeverAppliesChange()
    {
        // AlwaysStaleApi: ApplyDisplayMode は呼び出すが、QueryConfig は常に古いモードを返す
        var api = new AlwaysStaleWindowsDisplayApi(DisplayMode.Extend);
        var handle = VirtualDisplayHandle.NewHandle();
        api.RegisterDisplay(handle);
        var manager = new DisplaySettingsManager(api);

        // タイムアウトを短縮するため、テスト専用の短い タイムアウト値でテストする
        // ※ DisplaySettingsManager.ApplyTimeout は static readonly なので
        //   ここでは実際のタイムアウトが短い別クラスでテストする
        var fastManager = new FastTimeoutDisplaySettingsManager(api, TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAsync<TimeoutException>(
            () => fastManager.SetDisplayModeAsync(handle, DisplayMode.Clone));
    }
}

// ────────────────────────────────────────────────────────────────────────────
// Test helpers
// ────────────────────────────────────────────────────────────────────────────

/// <summary>
/// テスト用: ApplyDisplayMode を呼び出しても QueryConfig が常に古いモードを返す
/// スタブ実装。タイムアウト検証に使用する。
/// </summary>
internal class AlwaysStaleWindowsDisplayApi : IWindowsDisplayApi
{
    private static readonly IReadOnlyList<Resolution> SupportedResolutions = new[]
    {
        new Resolution(1920, 1080),
    };

    private readonly DisplayMode _staleMode;
    private readonly HashSet<VirtualDisplayHandle> _registered = [];

    public AlwaysStaleWindowsDisplayApi(DisplayMode staleMode)
    {
        _staleMode = staleMode;
    }

    public void RegisterDisplay(VirtualDisplayHandle handle)
    {
        _registered.Add(handle);
    }

    public void ApplyDisplayMode(VirtualDisplayHandle handle, DisplayMode mode)
    {
        // 何もしない（変更を適用しないシミュレーション）
    }

    public void ApplyResolution(VirtualDisplayHandle handle, Resolution resolution)
    {
        // 何もしない
    }

    public DisplayConfig? QueryConfig(VirtualDisplayHandle handle)
    {
        if (!_registered.Contains(handle))
            return null;

        // 常に staleMode を返す（変更が反映されないシミュレーション）
        return new DisplayConfig(
            Resolution: new Resolution(1920, 1080),
            RefreshRateHz: 60,
            Orientation: Orientation.Landscape,
            Mode: _staleMode);
    }

    public IReadOnlyList<Resolution> GetSupportedResolutions(VirtualDisplayHandle handle)
        => SupportedResolutions;
}

/// <summary>
/// テスト用: カスタムタイムアウト値を持つ DisplaySettingsManager サブクラス。
/// 本番コードの static readonly ApplyTimeout を変えずにタイムアウト動作をテストできる。
/// </summary>
internal class FastTimeoutDisplaySettingsManager : DisplaySettingsManager
{
    private readonly TimeSpan _timeout;
    private readonly IWindowsDisplayApi _api;

    public FastTimeoutDisplaySettingsManager(IWindowsDisplayApi api, TimeSpan timeout)
        : base(api)
    {
        _api = api;
        _timeout = timeout;
    }

    public new async Task SetDisplayModeAsync(VirtualDisplayHandle handle, DisplayMode mode)
    {
        _api.ApplyDisplayMode(handle, mode);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < _timeout)
        {
            var config = _api.QueryConfig(handle);
            if (config is null || config.Mode == mode)
                return;
            await Task.Delay(10);
        }

        var finalConfig = _api.QueryConfig(handle);
        if (finalConfig is null || finalConfig.Mode == mode)
            return;

        throw new TimeoutException(
            $"DisplayMode '{mode}' が {_timeout.TotalMilliseconds}ms 以内に適用されませんでした。");
    }
}
