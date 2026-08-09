using VMonitor.Core.Models;

namespace VMonitor.Driver;

/// <summary>
/// 仮想ディスプレイに適用する解像度を決定するセレクター。
/// 手動指定解像度が存在する場合はそれを優先し、存在しない場合は自動検出解像度を使用する。
/// 要件 5.4: ユーザーが PC クライアントの設定で解像度を手動指定したとき、
///           仮想ディスプレイドライバは自動調整より手動指定を優先して解像度を設定する。
/// </summary>
public static class ResolutionSelector
{
    /// <summary>
    /// 適用する解像度を選択する。
    /// 手動指定解像度 (<paramref name="manualResolution"/>) が null でない場合はそれを返す。
    /// null の場合は自動検出解像度 (<paramref name="autoDetectedResolution"/>) を返す。
    /// </summary>
    /// <param name="autoDetectedResolution">
    /// スマートフォンから自動検出した解像度。null にはならない。
    /// </param>
    /// <param name="manualResolution">
    /// PC クライアントの設定で手動指定された解像度。指定なしの場合は null。
    /// </param>
    /// <returns>
    /// 仮想ディスプレイに適用する解像度。手動指定がある場合は手動指定値、ない場合は自動検出値。
    /// </returns>
    public static Resolution Select(Resolution autoDetectedResolution, Resolution? manualResolution)
    {
        ArgumentNullException.ThrowIfNull(autoDetectedResolution);

        return manualResolution ?? autoDetectedResolution;
    }
}
