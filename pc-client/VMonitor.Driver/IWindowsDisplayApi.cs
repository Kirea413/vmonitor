using VMonitor.Core.Interfaces;
using VMonitor.Core.Models;

namespace VMonitor.Driver;

/// <summary>
/// Windows Display API（SetDisplayConfig / ChangeDisplaySettingsEx / QueryDisplayConfig）
/// をラップするインターフェース。テスト時に差し替え可能な抽象化レイヤー。
/// </summary>
public interface IWindowsDisplayApi
{
    /// <summary>
    /// 指定したディスプレイハンドルに対してディスプレイモードを適用する。
    /// Clone / Extend / SecondaryOnly に相当する SetDisplayConfig フラグを使用する。
    /// </summary>
    /// <param name="handle">対象の仮想ディスプレイハンドル。</param>
    /// <param name="mode">適用するディスプレイモード。</param>
    void ApplyDisplayMode(VirtualDisplayHandle handle, DisplayMode mode);

    /// <summary>
    /// 指定したディスプレイハンドルの解像度を設定する（ChangeDisplaySettingsEx に相当）。
    /// </summary>
    /// <param name="handle">対象の仮想ディスプレイハンドル。</param>
    /// <param name="resolution">適用する解像度。</param>
    void ApplyResolution(VirtualDisplayHandle handle, Resolution resolution);

    /// <summary>
    /// 指定したディスプレイハンドルの現在の設定を照会する（QueryDisplayConfig に相当）。
    /// </summary>
    /// <param name="handle">照会対象の仮想ディスプレイハンドル。</param>
    /// <returns>現在のディスプレイ設定。ハンドルが存在しない場合は null。</returns>
    DisplayConfig? QueryConfig(VirtualDisplayHandle handle);

    /// <summary>
    /// 指定したディスプレイハンドルがサポートする解像度の一覧を返す。
    /// </summary>
    /// <param name="handle">照会対象の仮想ディスプレイハンドル。</param>
    /// <returns>サポート解像度のリスト。</returns>
    IReadOnlyList<Resolution> GetSupportedResolutions(VirtualDisplayHandle handle);
}
