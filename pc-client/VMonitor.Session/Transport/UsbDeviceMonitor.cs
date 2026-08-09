using System.Runtime.Versioning;

#if WINDOWS
using System.Management;
#endif

namespace VMonitor.Session.Transport;

/// <summary>
/// WMI イベント監視で USB デバイスの接続・切断を検出するモニター。
/// Windows 専用コードは <c>OperatingSystem.IsWindows()</c> ガードで保護されている。
/// </summary>
public sealed class UsbDeviceMonitor : IDisposable
{
    // ── 既知の Android ベンダー PID プレフィックス ───────────────────────
    // Vendor ID は USB デバイスインスタンス ID の "VID_XXXX" 部分に含まれる。
    // 代表的な Android ベンダー ID を列挙する。
    private static readonly HashSet<string> AndroidVendorIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "18D1", // Google
        "04E8", // Samsung
        "22B8", // Motorola
        "12D1", // Huawei
        "2717", // Xiaomi
        "1BBB", // Alcatel/TCL
        "0BB4", // HTC
        "1F3A", // Allwinner
        "0FCE", // Sony
        "0489", // Foxconn
        "19D2", // ZTE
        "05C6", // Qualcomm（ADB インターフェース共通）
    };

    // ── イベント ────────────────────────────────────────────────────────

    /// <summary>USB デバイスが接続されたときに発生する。</summary>
    public event EventHandler<UsbDeviceEventArgs>? DeviceConnected;

    /// <summary>USB デバイスが切断されたときに発生する。</summary>
    public event EventHandler<UsbDeviceEventArgs>? DeviceDisconnected;

    // ── プライベートフィールド ───────────────────────────────────────────
#if WINDOWS
    private ManagementEventWatcher? _connectWatcher;
    private ManagementEventWatcher? _disconnectWatcher;
#endif
    private bool _disposed;

    // ── 公開メソッド ────────────────────────────────────────────────────

    /// <summary>
    /// テスト用: DeviceConnected イベントを手動で発火する。
    /// </summary>
    /// <param name="args">発火するイベントの引数。</param>
    public void RaiseDeviceConnected(UsbDeviceEventArgs args)
        => DeviceConnected?.Invoke(this, args);

    /// <summary>
    /// テスト用: DeviceDisconnected イベントを手動で発火する。
    /// </summary>
    /// <param name="args">発火するイベントの引数。</param>
    public void RaiseDeviceDisconnected(UsbDeviceEventArgs args)
        => DeviceDisconnected?.Invoke(this, args);

    /// <summary>
    /// WMI イベント監視を開始する。Windows 以外の環境では何もしない。
    /// </summary>
    public void StartMonitoring()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(UsbDeviceMonitor));

        if (!OperatingSystem.IsWindows()) return;

        StartMonitoringWindows();
    }

    /// <summary>
    /// WMI イベント監視を停止する。
    /// </summary>
    public void StopMonitoring()
    {
        if (!OperatingSystem.IsWindows()) return;

        StopMonitoringWindows();
    }

    /// <summary>
    /// デバイスインスタンス ID が Android デバイスを示すかどうかを判定する。
    /// </summary>
    /// <param name="deviceId">デバイスインスタンス ID（例: "USB\VID_18D1&amp;PID_4EE7\..."）。</param>
    /// <returns>Android デバイスと判定される場合は true。</returns>
    public static bool IsAndroidDevice(string deviceId)
    {
        if (string.IsNullOrEmpty(deviceId)) return false;

        // デバイスインスタンス ID から VID を抽出する
        // 例: USB\VID_18D1&PID_4EE7\... → "18D1"
        var upper = deviceId.ToUpperInvariant();
        var vidIndex = upper.IndexOf("VID_", StringComparison.Ordinal);
        if (vidIndex < 0) return false;

        var vidStart = vidIndex + 4; // "VID_" の後
        if (vidStart + 4 > upper.Length) return false;

        var vid = upper.Substring(vidStart, 4);
        return AndroidVendorIds.Contains(vid);
    }

    // ── IDisposable ─────────────────────────────────────────────────────

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        StopMonitoring();

#if WINDOWS
        _connectWatcher?.Dispose();
        _disconnectWatcher?.Dispose();
        _connectWatcher = null;
        _disconnectWatcher = null;
#endif
    }

    // ── Windows 専用実装 ────────────────────────────────────────────────

#if WINDOWS
    [SupportedOSPlatform("windows")]
    private void StartMonitoringWindows()
    {
        // WMI クエリ: Win32_DeviceChangeEvent で USB デバイスの接続を監視する
        // EventType 2 = デバイス到着、EventType 3 = デバイス削除
        var connectQuery = new WqlEventQuery(
            "SELECT * FROM Win32_DeviceChangeEvent WHERE EventType = 2");
        _connectWatcher = new ManagementEventWatcher(connectQuery);
        _connectWatcher.EventArrived += OnDeviceConnected;
        _connectWatcher.Start();

        var disconnectQuery = new WqlEventQuery(
            "SELECT * FROM Win32_DeviceChangeEvent WHERE EventType = 3");
        _disconnectWatcher = new ManagementEventWatcher(disconnectQuery);
        _disconnectWatcher.EventArrived += OnDeviceDisconnected;
        _disconnectWatcher.Start();
    }

    [SupportedOSPlatform("windows")]
    private void StopMonitoringWindows()
    {
        try { _connectWatcher?.Stop(); } catch { /* 停止失敗は無視 */ }
        try { _disconnectWatcher?.Stop(); } catch { /* 停止失敗は無視 */ }
    }

    [SupportedOSPlatform("windows")]
    private void OnDeviceConnected(object sender, EventArrivedEventArgs e)
    {
        var deviceId = ExtractDeviceId(e.NewEvent);
        if (string.IsNullOrEmpty(deviceId)) return;

        // USB デバイスのみ対象にする
        if (!deviceId.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase)) return;

        var isAndroid = IsAndroidDevice(deviceId);
        DeviceConnected?.Invoke(this, new UsbDeviceEventArgs
        {
            DeviceId = deviceId,
            IsAndroid = isAndroid
        });
    }

    [SupportedOSPlatform("windows")]
    private void OnDeviceDisconnected(object sender, EventArrivedEventArgs e)
    {
        var deviceId = ExtractDeviceId(e.NewEvent);
        if (string.IsNullOrEmpty(deviceId)) return;

        if (!deviceId.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase)) return;

        var isAndroid = IsAndroidDevice(deviceId);
        DeviceDisconnected?.Invoke(this, new UsbDeviceEventArgs
        {
            DeviceId = deviceId,
            IsAndroid = isAndroid
        });
    }

    /// <summary>WMI イベントオブジェクトからデバイスインスタンス ID を抽出する。</summary>
    [SupportedOSPlatform("windows")]
    private static string? ExtractDeviceId(ManagementBaseObject wmiEvent)
    {
        try
        {
            // Win32_DeviceChangeEvent の TargetInstance から Win32_USBHub を取得する
            // TargetInstance が存在しない場合は null を返す
            var targetInstance = wmiEvent["TargetInstance"] as ManagementBaseObject;
            if (targetInstance is null) return null;

            return targetInstance["DeviceID"] as string
                ?? targetInstance["PNPDeviceID"] as string;
        }
        catch
        {
            return null;
        }
    }
#else
    private void StartMonitoringWindows() { }
    private void StopMonitoringWindows() { }
#endif
}
