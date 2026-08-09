using System.Collections.Concurrent;
using VMonitor.Core.Models;

namespace VMonitor.Driver;

/// <summary>
/// IddCx (Indirect Display Driver) アダプターのシミュレーション実装。
/// 実際のカーネルモードドライバの代わりに、マネージドコードで仮想アダプターを管理する。
/// </summary>
public class IddCxAdapter
{
    private readonly ConcurrentDictionary<VirtualDisplayHandle, DisplaySpec> _monitors = new();

    /// <summary>
    /// 向き調整後の有効解像度を格納する辞書。テストからの検証に使用する。
    /// </summary>
    private readonly ConcurrentDictionary<VirtualDisplayHandle, Resolution> _effectiveResolutions = new();

    private bool _isInitialized;

    /// <summary>アダプターが初期化済みかどうかを返す。</summary>
    public bool IsInitialized => _isInitialized;

    /// <summary>
    /// 仮想アダプターを初期化する（IddCxAdapterInitAsync に相当）。
    /// </summary>
    public Task IddCxAdapterInitAsync()
    {
        _isInitialized = true;
        return Task.CompletedTask;
    }

    /// <summary>
    /// 指定した仕様で仮想モニターを作成し、新しいハンドルを返す（IddCxMonitorCreate に相当）。
    /// </summary>
    /// <param name="spec">作成する仮想ディスプレイの仕様。</param>
    /// <returns>作成した仮想モニターのハンドル。</returns>
    /// <exception cref="InvalidOperationException">アダプターが初期化されていない場合。</exception>
    public VirtualDisplayHandle IddCxMonitorCreate(DisplaySpec spec)
    {
        if (!_isInitialized)
            throw new InvalidOperationException("IddCx アダプターが初期化されていません。IddCxAdapterInitAsync を先に呼び出してください。");

        var handle = VirtualDisplayHandle.NewHandle();
        _monitors[handle] = spec;
        return handle;
    }

    /// <summary>
    /// 指定ハンドルの仮想モニターを削除する（IddCxMonitorRemove に相当）。
    /// </summary>
    /// <param name="handle">削除する仮想モニターのハンドル。</param>
    public void IddCxMonitorRemove(VirtualDisplayHandle handle)
    {
        _monitors.TryRemove(handle, out _);
        _effectiveResolutions.TryRemove(handle, out _);
    }

    /// <summary>
    /// 指定ハンドルの仮想モニターの仕様を更新する。
    /// </summary>
    /// <param name="handle">更新対象のハンドル。</param>
    /// <param name="spec">新しい仕様。</param>
    /// <returns>更新に成功した場合は true、ハンドルが存在しない場合は false。</returns>
    public bool TryUpdateMonitor(VirtualDisplayHandle handle, DisplaySpec spec)
    {
        if (!_monitors.ContainsKey(handle))
            return false;

        _monitors[handle] = spec;
        return true;
    }

    /// <summary>
    /// 指定ハンドルの仮想モニター仕様を取得する。
    /// </summary>
    /// <param name="handle">取得対象のハンドル。</param>
    /// <param name="spec">取得した仕様（見つかった場合）。</param>
    /// <returns>ハンドルが存在する場合は true。</returns>
    public bool TryGetMonitor(VirtualDisplayHandle handle, out DisplaySpec? spec)
    {
        return _monitors.TryGetValue(handle, out spec);
    }

    /// <summary>
    /// 現在アクティブな全仮想モニターのスナップショットを返す。
    /// </summary>
    public IReadOnlyDictionary<VirtualDisplayHandle, DisplaySpec> GetAllMonitors()
    {
        return _monitors.ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    /// <summary>
    /// 指定した解像度と向きでディスプレイモードリストを更新する（IddCxMonitorUpdateModes に相当）。
    /// 向きに合わせて解像度の縦横を正規化し、辞書に格納する。
    /// <list type="bullet">
    ///   <item>Portrait / PortraitFlipped の場合: Width &lt; Height になるよう調整する。</item>
    ///   <item>Landscape / LandscapeFlipped の場合: Width &gt; Height になるよう調整する。</item>
    /// </list>
    /// </summary>
    /// <param name="handle">更新対象の仮想モニターハンドル。</param>
    /// <param name="resolution">要求する解像度（向き調整前）。</param>
    /// <param name="orientation">適用する向き。</param>
    /// <exception cref="KeyNotFoundException">指定ハンドルが存在しない場合。</exception>
    public void IddCxMonitorUpdateModes(VirtualDisplayHandle handle, Resolution resolution, Orientation orientation)
    {
        if (!_monitors.ContainsKey(handle))
            throw new KeyNotFoundException($"指定されたハンドルが存在しません: {handle}");

        var normalizedResolution = NormalizeResolution(resolution, orientation);

        // 有効解像度を別辞書にも保存する（テストからの検証用）
        _effectiveResolutions[handle] = normalizedResolution;

        if (_monitors.TryGetValue(handle, out var currentSpec))
        {
            var updatedSpec = currentSpec with
            {
                Resolution = normalizedResolution,
                Orientation = orientation
            };
            _monitors[handle] = updatedSpec;
        }
    }

    /// <summary>
    /// 最後に <see cref="IddCxMonitorUpdateModes"/> で設定された有効解像度を取得する。
    /// </summary>
    /// <param name="handle">取得対象のハンドル。</param>
    /// <param name="resolution">取得した有効解像度（見つかった場合）。</param>
    /// <returns>ハンドルが存在する場合は true。</returns>
    public bool TryGetEffectiveResolution(VirtualDisplayHandle handle, out Resolution? resolution)
    {
        return _effectiveResolutions.TryGetValue(handle, out resolution);
    }

    /// <summary>
    /// 向きに合わせて解像度の縦横を正規化する。
    /// </summary>
    /// <param name="resolution">正規化前の解像度。</param>
    /// <param name="orientation">目的の向き。</param>
    /// <returns>正規化された解像度。</returns>
    internal static Resolution NormalizeResolution(Resolution resolution, Orientation orientation)
    {
        bool isPortrait = orientation == Orientation.Portrait || orientation == Orientation.PortraitFlipped;

        if (isPortrait && resolution.Width > resolution.Height)
        {
            // 縦向きなのに Width > Height → 縦横を入れ替える
            return new Resolution(resolution.Height, resolution.Width);
        }

        if (!isPortrait && resolution.Width < resolution.Height)
        {
            // 横向きなのに Width < Height → 縦横を入れ替える
            return new Resolution(resolution.Height, resolution.Width);
        }

        return resolution;
    }
}
