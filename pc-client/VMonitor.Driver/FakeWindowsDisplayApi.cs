using System.Collections.Concurrent;
using VMonitor.Core.Interfaces;
using VMonitor.Core.Models;

namespace VMonitor.Driver;

/// <summary>
/// <see cref="IWindowsDisplayApi"/> のテスト用フェイク実装。
/// 実際の Windows Display API を呼び出さず、メモリ内で設定を管理する。
/// </summary>
public class FakeWindowsDisplayApi : IWindowsDisplayApi
{
    /// <summary>既定でサポートされる解像度プリセット。</summary>
    private static readonly IReadOnlyList<Resolution> DefaultSupportedResolutions = new[]
    {
        new Resolution(640,  480),
        new Resolution(1280, 720),
        new Resolution(1920, 1080),
        new Resolution(2560, 1440),
        new Resolution(3840, 2160),
    };

    // ハンドルごとの現在設定
    private readonly ConcurrentDictionary<VirtualDisplayHandle, DisplayConfig> _configs = new();

    // ハンドルごとのサポート解像度（カスタマイズ可能）
    private readonly ConcurrentDictionary<VirtualDisplayHandle, IReadOnlyList<Resolution>> _supportedResolutions = new();

    /// <summary>
    /// テスト目的でハンドルの初期設定を登録する。
    /// </summary>
    public void RegisterDisplay(VirtualDisplayHandle handle, DisplayConfig initialConfig)
    {
        _configs[handle] = initialConfig;
    }

    /// <summary>
    /// テスト目的でハンドルのサポート解像度をカスタム設定する。
    /// </summary>
    public void SetSupportedResolutions(VirtualDisplayHandle handle, IReadOnlyList<Resolution> resolutions)
    {
        _supportedResolutions[handle] = resolutions;
    }

    /// <inheritdoc/>
    public void ApplyDisplayMode(VirtualDisplayHandle handle, DisplayMode mode)
    {
        var current = _configs.GetOrAdd(handle, _ => new DisplayConfig(
            Resolution: new Resolution(1920, 1080),
            RefreshRateHz: 60,
            Orientation: Orientation.Landscape,
            Mode: DisplayMode.Extend));

        _configs[handle] = current with { Mode = mode };
    }

    /// <inheritdoc/>
    public void ApplyResolution(VirtualDisplayHandle handle, Resolution resolution)
    {
        var current = _configs.GetOrAdd(handle, _ => new DisplayConfig(
            Resolution: resolution,
            RefreshRateHz: 60,
            Orientation: Orientation.Landscape,
            Mode: DisplayMode.Extend));

        _configs[handle] = current with { Resolution = resolution };
    }

    /// <inheritdoc/>
    public DisplayConfig? QueryConfig(VirtualDisplayHandle handle)
    {
        return _configs.TryGetValue(handle, out var config) ? config : null;
    }

    /// <inheritdoc/>
    public IReadOnlyList<Resolution> GetSupportedResolutions(VirtualDisplayHandle handle)
    {
        return _supportedResolutions.TryGetValue(handle, out var resolutions)
            ? resolutions
            : DefaultSupportedResolutions;
    }
}
