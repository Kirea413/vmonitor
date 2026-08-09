using VMonitor.Core.Models;

namespace VMonitor.Core.Interfaces;

/// <summary>
/// アプリ設定の読み書きと永続化を担うインターフェース。
/// 設定は %APPDATA%\vmonitor\settings.json に保存される。
/// </summary>
public interface ISettingsManager
{
    /// <summary>
    /// 現在の設定を返す。まだロードされていない場合は設定ファイルを読み込む。
    /// </summary>
    AppSettings Current { get; }

    /// <summary>
    /// 設定ファイルを読み込む。ファイルが存在しない場合や破損している場合はデフォルト値を返す。
    /// </summary>
    Task<AppSettings> LoadAsync();

    /// <summary>
    /// 指定した設定を設定ファイルへ書き込む。
    /// </summary>
    Task SaveAsync(AppSettings settings);

    /// <summary>
    /// StreamingDefaults を更新して保存する。
    /// </summary>
    Task SaveStreamingSettingsAsync(StreamingSettings streamingSettings);

    /// <summary>
    /// DisplayDefaults を更新して保存する。
    /// </summary>
    Task SaveDisplaySettingsAsync(DisplaySettings displaySettings);

    /// <summary>
    /// trustedDevices リストを更新して保存する。
    /// </summary>
    Task SaveTrustedDevicesAsync(IReadOnlyList<TrustedDevice> trustedDevices);
}
