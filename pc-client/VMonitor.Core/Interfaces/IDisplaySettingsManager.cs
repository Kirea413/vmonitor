using VMonitor.Core.Models;

namespace VMonitor.Core.Interfaces;

/// <summary>
/// Windows の複数ディスプレイ設定（複製・拡張・解像度等）を
/// SetDisplayConfig / ChangeDisplaySettingsEx API 経由で制御するインターフェース。
/// </summary>
public interface IDisplaySettingsManager
{
    /// <summary>仮想ディスプレイのディスプレイモードを設定する。3 秒以内の適用を保証する。</summary>
    Task SetDisplayModeAsync(VirtualDisplayHandle handle, DisplayMode mode);

    /// <summary>仮想ディスプレイの解像度を設定する。</summary>
    Task SetResolutionAsync(VirtualDisplayHandle handle, Resolution resolution);

    /// <summary>仮想ディスプレイがサポートする解像度の一覧を返す。</summary>
    Task<IReadOnlyList<Resolution>> GetSupportedResolutionsAsync(VirtualDisplayHandle handle);

    /// <summary>仮想ディスプレイの現在の設定を返す。</summary>
    Task<DisplayConfig> GetCurrentConfigAsync(VirtualDisplayHandle handle);
}

/// <summary>仮想ディスプレイの現在の設定スナップショット。</summary>
public record DisplayConfig(
    Resolution Resolution,
    int RefreshRateHz,
    Orientation Orientation,
    DisplayMode Mode
);
