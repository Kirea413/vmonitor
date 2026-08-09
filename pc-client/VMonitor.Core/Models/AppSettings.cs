namespace VMonitor.Core.Models;

/// <summary>
/// USB 接続に使用するプロトコルモード。
/// </summary>
public enum UsbConnectionMode
{
    /// <summary>
    /// WinUSB + LibUsb による直接 USB 通信（ADB 不要、デフォルト）。
    /// </summary>
    WinUsb,

    /// <summary>
    /// ADB TCP フォワードによるループバック TCP 通信（フォールバック）。
    /// ADB がインストールされている場合に使用する。
    /// </summary>
    Adb
}

/// <summary>
/// %APPDATA%\vmonitor\settings.json に永続化するアプリ全体の設定。
/// </summary>
public record AppSettings(
    IReadOnlyList<TrustedDevice> TrustedDevices,
    StreamingSettings StreamingDefaults,
    DisplaySettings DisplayDefaults,
    string LogFilePath,
    UsbConnectionMode UsbMode = UsbConnectionMode.WinUsb
)
{
    /// <summary>
    /// すべてのフィールドをデフォルト値で初期化した AppSettings を返す。
    /// 設定ファイルが存在しない場合や破損している場合に使用する。
    /// </summary>
    public static AppSettings CreateDefault() => new(
        TrustedDevices: Array.Empty<TrustedDevice>(),
        StreamingDefaults: StreamingSettings.Default,
        DisplayDefaults: DisplaySettings.Default,
        LogFilePath: @"%APPDATA%\vmonitor\logs\vmonitor.log",
        UsbMode: UsbConnectionMode.WinUsb
    );
}
