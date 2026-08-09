using VMonitor.Core.Interfaces;
using VMonitor.Core.Models;
using VMonitor.Session.Transport;

namespace VMonitor.Session;

/// <summary>
/// USB デバイス接続イベントを監視し、デバイスが接続されたときに
/// <see cref="ISessionManager.EstablishSessionAsync"/> を呼び出してセッション確立を試みるリスナー。
/// </summary>
/// <remarks>
/// Requirement 2.2: スマートフォンが USB ケーブルで PC に接続されたとき、
/// PC クライアントは USB 接続を検出してセッション確立を試みる。
/// </remarks>
public sealed class UsbConnectionListener : IDisposable
{
    private readonly UsbDeviceMonitor _monitor;
    private readonly ISessionManager _sessionManager;
    private bool _disposed;

    /// <summary>
    /// <see cref="UsbConnectionListener"/> を初期化し、デバイス接続イベントを購読する。
    /// </summary>
    /// <param name="monitor">USB デバイス接続・切断イベントのソース。</param>
    /// <param name="sessionManager">セッション確立に使用するセッションマネージャー。</param>
    public UsbConnectionListener(UsbDeviceMonitor monitor, ISessionManager sessionManager)
    {
        _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));

        _monitor.DeviceConnected += OnDeviceConnected;
    }

    /// <summary>
    /// USB デバイス接続イベントのハンドラー。
    /// 接続されたデバイスのセッション確立を非同期で試みる。
    /// </summary>
    private void OnDeviceConnected(object? sender, UsbDeviceEventArgs e)
    {
        // 接続イベントは同期コンテキストで発火されるため、
        // 非同期処理は fire-and-forget で開始する。
        // エラーは呼び出し元に伝播しない（ログに記録する想定）。
        _ = EstablishSessionForUsbDeviceAsync(e);
    }

    /// <summary>
    /// USB デバイス用のデバイス情報を構築してセッション確立を試みる。
    /// </summary>
    private async Task EstablishSessionForUsbDeviceAsync(UsbDeviceEventArgs e)
    {
        var device = BuildDeviceInfo(e);
        await _sessionManager.EstablishSessionAsync(device, CancellationToken.None);
    }

    /// <summary>
    /// <see cref="UsbDeviceEventArgs"/> からセッション確立に必要な <see cref="DeviceInfo"/> を構築する。
    /// </summary>
    /// <remarks>
    /// USB 接続イベント時点では物理解像度などデバイスの詳細情報は不明なため、
    /// 接続後のネゴシエーションで更新することを前提に既定値を使用する。
    /// </remarks>
    private static DeviceInfo BuildDeviceInfo(UsbDeviceEventArgs e)
    {
        var platform = e.IsAndroid ? DevicePlatform.Android : DevicePlatform.iOS;

        return new DeviceInfo(
            Id: DeviceIdentifier.NewIdentifier(),
            Name: $"USB Device ({e.DeviceId})",
            Platform: platform,
            PhysicalResolution: new Resolution(1080, 1920),
            PixelDensity: 400f);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _monitor.DeviceConnected -= OnDeviceConnected;
    }
}
