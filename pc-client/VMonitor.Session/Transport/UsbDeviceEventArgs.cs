namespace VMonitor.Session.Transport;

/// <summary>
/// USB デバイスの接続・切断イベントの引数。
/// </summary>
public class UsbDeviceEventArgs : EventArgs
{
    /// <summary>デバイスの識別子（例: PnP デバイスインスタンス ID）。</summary>
    public required string DeviceId { get; init; }

    /// <summary>Android デバイスの場合は true、iOS デバイスの場合は false。</summary>
    public required bool IsAndroid { get; init; }
}
