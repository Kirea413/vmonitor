using Moq;
using VMonitor.Core.Interfaces;
using VMonitor.Core.Models;
using VMonitor.Session;
using VMonitor.Session.Transport;

// VMonitor.Core.Models.Session 型の名前衝突を回避するためにエイリアスを使用。
using SessionModel = VMonitor.Core.Models.Session;

namespace VMonitor.Tests;

/// <summary>
/// Task 4.4: USB 接続イベントでセッション確立が試みられることを検証するユニットテスト。
/// Validates: Requirements 2.2
/// </summary>
public class UsbConnectionListenerTests
{
    // ── ヘルパー ──────────────────────────────────────────────────────────

    /// <summary>
    /// テスト用のセッションを返す ISessionManager モックを構築する。
    /// </summary>
    private static Mock<ISessionManager> BuildSessionManagerMock()
    {
        var mock = new Mock<ISessionManager>();
        mock.Setup(m => m.EstablishSessionAsync(It.IsAny<DeviceInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SessionModel(
                SessionId: Guid.NewGuid(),
                DeviceId: DeviceIdentifier.NewIdentifier(),
                Transport: TransportType.USB,
                State: SessionState.Active,
                EstablishedAt: DateTimeOffset.UtcNow,
                DisplayHandle: VirtualDisplayHandle.NewHandle()));

        return mock;
    }

    // ── Requirement 2.2: USB 接続時にセッション確立が試みられること ────────────────

    /// <summary>
    /// Android USB デバイス接続イベントが発火されたとき、
    /// EstablishSessionAsync が 1 回呼び出されることを検証する。
    /// Validates: Requirements 2.2
    /// </summary>
    [Fact]
    public async Task DeviceConnected_Android_CallsEstablishSessionAsync()
    {
        var sessionManager = BuildSessionManagerMock();
        var monitor = new UsbDeviceMonitor();

        using var listener = new UsbConnectionListener(monitor, sessionManager.Object);

        // USB 接続イベントを手動で発火する
        monitor.RaiseDeviceConnected(new UsbDeviceEventArgs
        {
            DeviceId = "USB\\VID_18D1&PID_4EE7\\12345",
            IsAndroid = true
        });

        // fire-and-forget タスクが完了するのを待つ
        await Task.Delay(100);

        sessionManager.Verify(
            m => m.EstablishSessionAsync(It.IsAny<DeviceInfo>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// iOS USB デバイス接続イベントが発火されたとき、
    /// EstablishSessionAsync が 1 回呼び出されることを検証する。
    /// Validates: Requirements 2.2
    /// </summary>
    [Fact]
    public async Task DeviceConnected_iOS_CallsEstablishSessionAsync()
    {
        var sessionManager = BuildSessionManagerMock();
        var monitor = new UsbDeviceMonitor();

        using var listener = new UsbConnectionListener(monitor, sessionManager.Object);

        monitor.RaiseDeviceConnected(new UsbDeviceEventArgs
        {
            DeviceId = "USB\\VID_05AC&PID_12AB\\67890",
            IsAndroid = false
        });

        await Task.Delay(100);

        sessionManager.Verify(
            m => m.EstablishSessionAsync(It.IsAny<DeviceInfo>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// Android USB 接続イベント時に EstablishSessionAsync に渡される DeviceInfo が
    /// Android プラットフォームであることを検証する。
    /// Validates: Requirements 2.2
    /// </summary>
    [Fact]
    public async Task DeviceConnected_Android_PassesAndroidPlatformToEstablishSession()
    {
        var sessionManager = BuildSessionManagerMock();
        var monitor = new UsbDeviceMonitor();

        DeviceInfo? capturedDevice = null;
        sessionManager
            .Setup(m => m.EstablishSessionAsync(It.IsAny<DeviceInfo>(), It.IsAny<CancellationToken>()))
            .Callback<DeviceInfo, CancellationToken>((d, _) => capturedDevice = d)
            .ReturnsAsync(new SessionModel(
                SessionId: Guid.NewGuid(),
                DeviceId: DeviceIdentifier.NewIdentifier(),
                Transport: TransportType.USB,
                State: SessionState.Active,
                EstablishedAt: DateTimeOffset.UtcNow,
                DisplayHandle: VirtualDisplayHandle.NewHandle()));

        using var listener = new UsbConnectionListener(monitor, sessionManager.Object);

        monitor.RaiseDeviceConnected(new UsbDeviceEventArgs
        {
            DeviceId = "USB\\VID_18D1&PID_4EE7\\12345",
            IsAndroid = true
        });

        await Task.Delay(100);

        Assert.NotNull(capturedDevice);
        Assert.Equal(DevicePlatform.Android, capturedDevice!.Platform);
    }

    /// <summary>
    /// iOS USB 接続イベント時に EstablishSessionAsync に渡される DeviceInfo が
    /// iOS プラットフォームであることを検証する。
    /// Validates: Requirements 2.2
    /// </summary>
    [Fact]
    public async Task DeviceConnected_iOS_PassesiOSPlatformToEstablishSession()
    {
        var sessionManager = BuildSessionManagerMock();
        var monitor = new UsbDeviceMonitor();

        DeviceInfo? capturedDevice = null;
        sessionManager
            .Setup(m => m.EstablishSessionAsync(It.IsAny<DeviceInfo>(), It.IsAny<CancellationToken>()))
            .Callback<DeviceInfo, CancellationToken>((d, _) => capturedDevice = d)
            .ReturnsAsync(new SessionModel(
                SessionId: Guid.NewGuid(),
                DeviceId: DeviceIdentifier.NewIdentifier(),
                Transport: TransportType.USB,
                State: SessionState.Active,
                EstablishedAt: DateTimeOffset.UtcNow,
                DisplayHandle: VirtualDisplayHandle.NewHandle()));

        using var listener = new UsbConnectionListener(monitor, sessionManager.Object);

        monitor.RaiseDeviceConnected(new UsbDeviceEventArgs
        {
            DeviceId = "USB\\VID_05AC&PID_12AB\\67890",
            IsAndroid = false
        });

        await Task.Delay(100);

        Assert.NotNull(capturedDevice);
        Assert.Equal(DevicePlatform.iOS, capturedDevice!.Platform);
    }

    /// <summary>
    /// Dispose 後に USB 接続イベントが発火されても、
    /// EstablishSessionAsync が呼び出されないことを検証する。
    /// Validates: Requirements 2.2
    /// </summary>
    [Fact]
    public async Task DeviceConnected_AfterDispose_DoesNotCallEstablishSessionAsync()
    {
        var sessionManager = BuildSessionManagerMock();
        var monitor = new UsbDeviceMonitor();

        var listener = new UsbConnectionListener(monitor, sessionManager.Object);
        listener.Dispose();

        monitor.RaiseDeviceConnected(new UsbDeviceEventArgs
        {
            DeviceId = "USB\\VID_18D1&PID_4EE7\\12345",
            IsAndroid = true
        });

        await Task.Delay(100);

        sessionManager.Verify(
            m => m.EstablishSessionAsync(It.IsAny<DeviceInfo>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// 複数の USB 接続イベントが発火されたとき、
    /// それぞれに対して EstablishSessionAsync が呼び出されることを検証する。
    /// Validates: Requirements 2.2
    /// </summary>
    [Fact]
    public async Task DeviceConnected_MultipleTimes_CallsEstablishSessionAsyncForEach()
    {
        var sessionManager = BuildSessionManagerMock();
        var monitor = new UsbDeviceMonitor();

        using var listener = new UsbConnectionListener(monitor, sessionManager.Object);

        monitor.RaiseDeviceConnected(new UsbDeviceEventArgs
        {
            DeviceId = "USB\\VID_18D1&PID_4EE7\\DEVICE1",
            IsAndroid = true
        });
        monitor.RaiseDeviceConnected(new UsbDeviceEventArgs
        {
            DeviceId = "USB\\VID_04E8&PID_6860\\DEVICE2",
            IsAndroid = true
        });

        await Task.Delay(200);

        sessionManager.Verify(
            m => m.EstablishSessionAsync(It.IsAny<DeviceInfo>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }
}
