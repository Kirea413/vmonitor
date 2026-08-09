namespace VMonitor.Session;

/// <summary>
/// WMI ドライバ停止イベントのソースインターフェース。
/// テスト時にはモックを注入する。
/// </summary>
public interface IWmiDriverEventSource
{
    /// <summary>ドライバが停止したときに発生するイベント。</summary>
    event EventHandler<DriverStoppedEventArgs>? DriverStopped;
}

/// <summary>
/// pnputil プロセス実行のインターフェース。
/// テスト時にはモックを注入する。
/// </summary>
public interface IDriverProcessRunner
{
    /// <summary>
    /// 指定デバイスインスタンス ID のドライバを再起動する。
    /// </summary>
    /// <param name="deviceInstanceId">再起動対象のデバイスインスタンス ID。</param>
    /// <returns>再起動が成功した場合は <c>true</c>、失敗した場合は <c>false</c>。</returns>
    Task<bool> RestartDeviceAsync(string deviceInstanceId);
}

/// <summary>
/// ドライバ停止イベントのデータ。
/// </summary>
public sealed class DriverStoppedEventArgs : EventArgs
{
    /// <summary>停止したデバイスのインスタンス ID。</summary>
    public required string DeviceInstanceId { get; init; }
}
