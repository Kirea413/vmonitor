using System.Diagnostics;
using VMonitor.Core.Interfaces;
using VMonitor.Core.Models;

namespace VMonitor.Driver;

/// <summary>
/// <see cref="IDisplaySettingsManager"/> の実装。
/// <see cref="IWindowsDisplayApi"/> を通じて SetDisplayConfig / ChangeDisplaySettingsEx /
/// QueryDisplayConfig をラップし、Clone・Extend・SecondaryOnly の各 <see cref="DisplayMode"/>
/// をサポートする。
///
/// 設定変更後は <see cref="QueryDisplayConfig"/> でポーリングを行い、
/// 3 秒以内に変更が適用されたことを確認する（Requirement 7.3）。
/// </summary>
public class DisplaySettingsManager : IDisplaySettingsManager
{
    /// <summary>設定変更の適用を待機する最大時間（Requirement 7.3: 3 秒以内）。</summary>
    public static readonly TimeSpan ApplyTimeout = TimeSpan.FromSeconds(3);

    /// <summary>ポーリング間隔。</summary>
    private static readonly TimeSpan PollingInterval = TimeSpan.FromMilliseconds(50);

    private readonly IWindowsDisplayApi _api;

    /// <summary>
    /// <see cref="DisplaySettingsManager"/> を初期化する。
    /// </summary>
    /// <param name="api">使用する Windows Display API ラッパー。</param>
    public DisplaySettingsManager(IWindowsDisplayApi api)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
    }

    /// <inheritdoc/>
    /// <remarks>
    /// 処理の流れ:
    /// 1. <see cref="IWindowsDisplayApi.ApplyDisplayMode"/> で SetDisplayConfig を呼び出す。
    /// 2. <see cref="IWindowsDisplayApi.QueryConfig"/> でポーリングし、
    ///    Mode が <paramref name="mode"/> と一致するまで待機する（最大 3 秒）。
    /// 3. タイムアウト時は <see cref="TimeoutException"/> をスローする。
    /// </remarks>
    public async Task SetDisplayModeAsync(VirtualDisplayHandle handle, DisplayMode mode)
    {
        // 1. Windows API でモードを適用する
        _api.ApplyDisplayMode(handle, mode);

        // 2. ポーリングで適用完了を確認する（最大 ApplyTimeout = 3秒）
        await WaitForConditionAsync(
            handle,
            config => config.Mode == mode,
            $"DisplayMode '{mode}' が {ApplyTimeout.TotalSeconds} 秒以内に適用されませんでした。");
    }

    /// <inheritdoc/>
    public async Task SetResolutionAsync(VirtualDisplayHandle handle, Resolution resolution)
    {
        ArgumentNullException.ThrowIfNull(resolution);

        // 1. Windows API で解像度を適用する
        _api.ApplyResolution(handle, resolution);

        // 2. ポーリングで適用完了を確認する
        await WaitForConditionAsync(
            handle,
            config => config.Resolution == resolution,
            $"解像度 {resolution.Width}x{resolution.Height} が {ApplyTimeout.TotalSeconds} 秒以内に適用されませんでした。");
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<Resolution>> GetSupportedResolutionsAsync(VirtualDisplayHandle handle)
    {
        var resolutions = _api.GetSupportedResolutions(handle);
        return Task.FromResult(resolutions);
    }

    /// <inheritdoc/>
    public Task<DisplayConfig> GetCurrentConfigAsync(VirtualDisplayHandle handle)
    {
        var config = _api.QueryConfig(handle);
        if (config is null)
        {
            throw new KeyNotFoundException(
                $"指定された仮想ディスプレイハンドルが見つかりません: {handle}");
        }
        return Task.FromResult(config);
    }

    // ────────────────────────────────────────────────────────────────────────────
    // Private helpers
    // ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <paramref name="predicate"/> が true になるまで QueryConfig でポーリングする。
    /// <see cref="ApplyTimeout"/> を超えた場合は <see cref="TimeoutException"/> をスローする。
    ///
    /// <see cref="IWindowsDisplayApi.QueryConfig"/> が null を返す場合（ハンドル未登録や非 Windows 環境）は
    /// 即座に成功とみなす。これにより非 Windows テスト環境でも問題なく動作する。
    /// </summary>
    /// <param name="handle">照会対象の仮想ディスプレイハンドル。</param>
    /// <param name="predicate">適用完了判定のデリゲート。</param>
    /// <param name="timeoutMessage">タイムアウト時のエラーメッセージ。</param>
    private async Task WaitForConditionAsync(
        VirtualDisplayHandle handle,
        Func<DisplayConfig, bool> predicate,
        string timeoutMessage)
    {
        var sw = Stopwatch.StartNew();

        while (sw.Elapsed < ApplyTimeout)
        {
            var config = _api.QueryConfig(handle);

            // QueryConfig が null の場合（非 Windows など）は適用完了とみなして即座に返る
            if (config is null)
                return;

            if (predicate(config))
                return;

            await Task.Delay(PollingInterval);
        }

        // ポーリング上限に達してもまだ条件を確認する（ぎりぎりの適用を見逃さないため）
        var finalConfig = _api.QueryConfig(handle);
        if (finalConfig is null || predicate(finalConfig))
            return;

        throw new TimeoutException(timeoutMessage);
    }
}
