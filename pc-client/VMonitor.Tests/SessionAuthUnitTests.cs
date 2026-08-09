using Moq;
using VMonitor.Core.Interfaces;
using VMonitor.Core.Models;
using VMonitor.Session;

// VMonitor.Core.Models.Session 型の名前衝突を回避するためにエイリアスを使用。
using SessionModel = VMonitor.Core.Models.Session;

namespace VMonitor.Tests;

/// <summary>
/// Task 5.6: セッション・認証のユニットテスト。
/// <list type="bullet">
///   <item>タイムアウト後に通知と再試行 UI が表示されること（2.4）</item>
///   <item>30 秒タイムアウト後にセッションが Terminated 状態になること（9.2）</item>
///   <item>未知デバイスからの接続で許可ダイアログが表示されること（8.1）</item>
/// </list>
/// Validates: Requirements 2.4, 8.1, 9.2
/// </summary>
public class SessionAuthUnitTests
{
    // ── ヘルパー ──────────────────────────────────────────────────────────

    private static DeviceInfo CreateDevice(
        DevicePlatform platform = DevicePlatform.Android,
        int width = 1080,
        int height = 1920) =>
        new(
            Id: DeviceIdentifier.NewIdentifier(),
            Name: "Test Phone",
            Platform: platform,
            PhysicalResolution: new Resolution(width, height),
            PixelDensity: 400f);

    private static SessionModel CreateActiveSession(VirtualDisplayHandle? handle = null) =>
        new(
            SessionId: Guid.NewGuid(),
            DeviceId: DeviceIdentifier.NewIdentifier(),
            Transport: TransportType.WiFi,
            State: SessionState.Active,
            EstablishedAt: DateTimeOffset.UtcNow,
            DisplayHandle: handle ?? VirtualDisplayHandle.NewHandle());

    // ── Requirement 2.4: タイムアウト後に通知と再試行 UI が表示されること ──────────────

    /// <summary>
    /// EstablishSessionAsync がタイムアウト（10 秒）したとき、
    /// TimeoutException がスローされることを検証する。
    /// これがスマホアプリ側のタイムアウト通知と再試行 UI 表示のトリガーとなる。
    /// Validates: Requirement 2.4
    /// </summary>
    [Fact]
    public async Task EstablishSessionAsync_ThrowsTimeoutException_WhenConnectionExceedsTenSeconds()
    {
        // Arrange: ConnectAsync が永遠に待機するトランスポートモックを作成する
        var transport = new Mock<ITransport>();
        transport.Setup(t => t.Type).Returns(TransportType.WiFi);
        transport
            .Setup(t => t.ConnectAsync(
                It.IsAny<System.Net.EndPoint>(),
                It.IsAny<CancellationToken>()))
            .Returns<System.Net.EndPoint, CancellationToken>(async (_, ct) =>
            {
                // キャンセルされるまで無限待機する（タイムアウトをシミュレート）
                await Task.Delay(Timeout.Infinite, ct);
            });

        var vdd = new Mock<IVirtualDisplayDriver>();
        var manager = new SessionManager(transport.Object, vdd.Object);
        var device = CreateDevice();

        // Act & Assert
        await Assert.ThrowsAsync<TimeoutException>(
            () => manager.EstablishSessionAsync(device, CancellationToken.None));
    }

    /// <summary>
    /// EstablishSessionAsync がタイムアウトしたとき、
    /// セッションが内部辞書から除去されることを検証する。
    /// タイムアウト後にリソースリークが発生しないことを確認する。
    /// Validates: Requirement 2.4
    /// </summary>
    [Fact]
    public async Task EstablishSessionAsync_RemovesSession_AfterTimeout()
    {
        // Arrange
        var transport = new Mock<ITransport>();
        transport.Setup(t => t.Type).Returns(TransportType.WiFi);
        transport
            .Setup(t => t.ConnectAsync(
                It.IsAny<System.Net.EndPoint>(),
                It.IsAny<CancellationToken>()))
            .Returns<System.Net.EndPoint, CancellationToken>(async (_, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
            });

        var vdd = new Mock<IVirtualDisplayDriver>();
        var manager = new SessionManager(transport.Object, vdd.Object);
        var device = CreateDevice();

        // Act
        try
        {
            await manager.EstablishSessionAsync(device, CancellationToken.None);
        }
        catch (TimeoutException)
        {
            // 期待される例外
        }

        // Assert: タイムアウト後にアクティブセッションが空であること
        Assert.Empty(manager.GetActiveSessions());
    }

    /// <summary>
    /// EstablishSessionAsync がタイムアウトしたとき、
    /// CreateDisplayAsync が呼ばれないことを検証する。
    /// タイムアウト時に仮想ディスプレイが作成されないことを確認する。
    /// Validates: Requirement 2.4
    /// </summary>
    [Fact]
    public async Task EstablishSessionAsync_DoesNotCreateDisplay_AfterTimeout()
    {
        // Arrange
        var transport = new Mock<ITransport>();
        transport.Setup(t => t.Type).Returns(TransportType.WiFi);
        transport
            .Setup(t => t.ConnectAsync(
                It.IsAny<System.Net.EndPoint>(),
                It.IsAny<CancellationToken>()))
            .Returns<System.Net.EndPoint, CancellationToken>(async (_, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
            });

        var vdd = new Mock<IVirtualDisplayDriver>();
        var manager = new SessionManager(transport.Object, vdd.Object);
        var device = CreateDevice();

        // Act
        try
        {
            await manager.EstablishSessionAsync(device, CancellationToken.None);
        }
        catch (TimeoutException)
        {
            // 期待される例外
        }

        // Assert: 仮想ディスプレイが作成されていないこと
        vdd.Verify(v => v.CreateDisplayAsync(It.IsAny<DisplaySpec>()), Times.Never);
    }

    // ── Requirement 9.2: 30 秒タイムアウト後にセッションが Terminated 状態になること ──

    /// <summary>
    /// TryReconnectAsync が 30 秒タイムアウトした後、
    /// ReconnectResult.TimedOut が返ることを検証する。
    /// Validates: Requirement 9.2
    /// </summary>
    [Fact]
    public async Task TryReconnectAsync_ReturnsTimedOut_AfterThirtySecondTimeout()
    {
        // Arrange: 再接続試行が常に失敗するトランスポートモック
        var transport = new Mock<ITransport>();
        transport.Setup(t => t.Type).Returns(TransportType.WiFi);
        transport
            .Setup(t => t.ConnectAsync(
                It.IsAny<System.Net.EndPoint>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("再接続失敗（モック）"));

        var vdd = new Mock<IVirtualDisplayDriver>();
        vdd.Setup(v => v.RemoveDisplayAsync(It.IsAny<VirtualDisplayHandle>()))
           .Returns(Task.CompletedTask);

        var manager = new SessionManager(transport.Object, vdd.Object);

        // DisplayHandle を Guid.Empty にして RemoveDisplayAsync の呼び出しを制御する
        var session = new SessionModel(
            SessionId: Guid.NewGuid(),
            DeviceId: DeviceIdentifier.NewIdentifier(),
            Transport: TransportType.WiFi,
            State: SessionState.Active,
            EstablishedAt: DateTimeOffset.UtcNow,
            DisplayHandle: new VirtualDisplayHandle(Guid.Empty));

        // Act: 30 秒相当を 300ms にスケールダウンしてテスト実行速度を確保する
        var result = await manager.TryReconnectAsync(
            session,
            timeout: TimeSpan.FromMilliseconds(300),
            ct: CancellationToken.None);

        // Assert
        Assert.Equal(ReconnectResult.TimedOut, result);
    }

    /// <summary>
    /// TryReconnectAsync がタイムアウトしたとき、
    /// セッションが Terminated 状態に遷移することを検証する。
    /// Validates: Requirement 9.2
    /// </summary>
    [Fact]
    public async Task TryReconnectAsync_SessionBecomesTerminated_AfterTimeout()
    {
        // Arrange
        var transport = new Mock<ITransport>();
        transport.Setup(t => t.Type).Returns(TransportType.WiFi);
        transport
            .Setup(t => t.ConnectAsync(
                It.IsAny<System.Net.EndPoint>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("再接続失敗（モック）"));

        var vdd = new Mock<IVirtualDisplayDriver>();
        vdd.Setup(v => v.RemoveDisplayAsync(It.IsAny<VirtualDisplayHandle>()))
           .Returns(Task.CompletedTask);

        var manager = new SessionManager(transport.Object, vdd.Object);

        SessionModel? terminatedSession = null;

        // SessionDisconnected イベントからセッション状態をキャプチャする
        manager.SessionDisconnected += (_, args) =>
        {
            terminatedSession = args.Session;
        };

        var sessionId = Guid.NewGuid();
        var session = new SessionModel(
            SessionId: sessionId,
            DeviceId: DeviceIdentifier.NewIdentifier(),
            Transport: TransportType.WiFi,
            State: SessionState.Active,
            EstablishedAt: DateTimeOffset.UtcNow,
            DisplayHandle: new VirtualDisplayHandle(Guid.Empty));

        // Act
        await manager.TryReconnectAsync(
            session,
            timeout: TimeSpan.FromMilliseconds(300),
            ct: CancellationToken.None);

        // Assert: イベントが発火され、セッション状態が Terminated であること
        Assert.NotNull(terminatedSession);
        Assert.Equal(SessionState.Terminated, terminatedSession!.State);
        Assert.Equal(sessionId, terminatedSession.SessionId);
    }

    /// <summary>
    /// TryReconnectAsync がタイムアウトしたとき、
    /// SessionDisconnected イベントが発火されることを検証する。
    /// これがユーザーへのタイムアウト通知のトリガーとなる。
    /// Validates: Requirement 9.2
    /// </summary>
    [Fact]
    public async Task TryReconnectAsync_FiresSessionDisconnectedEvent_AfterTimeout()
    {
        // Arrange
        var transport = new Mock<ITransport>();
        transport.Setup(t => t.Type).Returns(TransportType.WiFi);
        transport
            .Setup(t => t.ConnectAsync(
                It.IsAny<System.Net.EndPoint>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("再接続失敗（モック）"));

        var vdd = new Mock<IVirtualDisplayDriver>();
        vdd.Setup(v => v.RemoveDisplayAsync(It.IsAny<VirtualDisplayHandle>()))
           .Returns(Task.CompletedTask);

        var manager = new SessionManager(transport.Object, vdd.Object);

        bool eventFired = false;
        manager.SessionDisconnected += (_, _) => { eventFired = true; };

        var session = new SessionModel(
            SessionId: Guid.NewGuid(),
            DeviceId: DeviceIdentifier.NewIdentifier(),
            Transport: TransportType.WiFi,
            State: SessionState.Active,
            EstablishedAt: DateTimeOffset.UtcNow,
            DisplayHandle: new VirtualDisplayHandle(Guid.Empty));

        // Act
        await manager.TryReconnectAsync(
            session,
            timeout: TimeSpan.FromMilliseconds(300),
            ct: CancellationToken.None);

        // Assert: 通知イベントが発火されていること
        Assert.True(eventFired, "タイムアウト後に SessionDisconnected イベントが発火されるべきです");
    }

    /// <summary>
    /// TryReconnectAsync がタイムアウトしたとき、
    /// DisplayHandle が有効なセッションの仮想ディスプレイが削除されることを検証する。
    /// Validates: Requirement 9.2
    /// </summary>
    [Fact]
    public async Task TryReconnectAsync_RemovesDisplay_AfterTimeout()
    {
        // Arrange
        var transport = new Mock<ITransport>();
        transport.Setup(t => t.Type).Returns(TransportType.WiFi);
        transport
            .Setup(t => t.ConnectAsync(
                It.IsAny<System.Net.EndPoint>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("再接続失敗（モック）"));

        var vdd = new Mock<IVirtualDisplayDriver>();
        var handle = VirtualDisplayHandle.NewHandle();
        vdd.Setup(v => v.RemoveDisplayAsync(handle)).Returns(Task.CompletedTask);

        var manager = new SessionManager(transport.Object, vdd.Object);

        var session = new SessionModel(
            SessionId: Guid.NewGuid(),
            DeviceId: DeviceIdentifier.NewIdentifier(),
            Transport: TransportType.WiFi,
            State: SessionState.Active,
            EstablishedAt: DateTimeOffset.UtcNow,
            DisplayHandle: handle);

        // Act
        await manager.TryReconnectAsync(
            session,
            timeout: TimeSpan.FromMilliseconds(300),
            ct: CancellationToken.None);

        // Assert: 仮想ディスプレイが削除されていること
        vdd.Verify(v => v.RemoveDisplayAsync(handle), Times.Once);
    }

    // ── Requirement 8.1: 未知デバイスからの接続で許可ダイアログが表示されること ────────

    /// <summary>
    /// 信頼済みリストに登録されていない未知デバイスが接続を試みたとき、
    /// 許可確認ダイアログのコールバックが呼び出されることを検証する。
    /// Validates: Requirement 8.1
    /// </summary>
    [Fact]
    public async Task RequestAuthorizationAsync_ShowsAuthorizationDialog_ForUnknownDevice()
    {
        // Arrange: ダイアログ呼び出しをカウントするためのフラグ
        bool dialogShown = false;
        Func<DeviceInfo, Task<bool>> showDialog = device =>
        {
            dialogShown = true;
            return Task.FromResult(true); // 許可
        };

        var authManager = new AuthManager(showDialog);
        var device = CreateDevice();

        // Act
        await authManager.RequestAuthorizationAsync(device);

        // Assert: ダイアログが表示されたこと
        Assert.True(dialogShown, "未知デバイスの初回接続時に許可確認ダイアログが表示されるべきです");
    }

    /// <summary>
    /// ユーザーが許可ダイアログで承認したとき、
    /// AuthResult.Approved が返ることを検証する。
    /// Validates: Requirement 8.1
    /// </summary>
    [Fact]
    public async Task RequestAuthorizationAsync_ReturnsApproved_WhenUserApproves()
    {
        // Arrange: ダイアログが true（許可）を返す
        Func<DeviceInfo, Task<bool>> showDialog = _ => Task.FromResult(true);
        var authManager = new AuthManager(showDialog);
        var device = CreateDevice();

        // Act
        var result = await authManager.RequestAuthorizationAsync(device);

        // Assert
        Assert.Equal(AuthResult.Approved, result);
    }

    /// <summary>
    /// ユーザーが許可ダイアログで拒否したとき、
    /// AuthResult.Denied が返ることを検証する。
    /// Validates: Requirement 8.1
    /// </summary>
    [Fact]
    public async Task RequestAuthorizationAsync_ReturnsDenied_WhenUserDenies()
    {
        // Arrange: ダイアログが false（拒否）を返す
        Func<DeviceInfo, Task<bool>> showDialog = _ => Task.FromResult(false);
        var authManager = new AuthManager(showDialog);
        var device = CreateDevice();

        // Act
        var result = await authManager.RequestAuthorizationAsync(device);

        // Assert
        Assert.Equal(AuthResult.Denied, result);
    }

    /// <summary>
    /// ユーザーが許可したとき、デバイスが信頼済みリストに追加されることを検証する。
    /// Validates: Requirement 8.1 (および 8.2)
    /// </summary>
    [Fact]
    public async Task RequestAuthorizationAsync_TrustsDevice_WhenUserApproves()
    {
        // Arrange
        Func<DeviceInfo, Task<bool>> showDialog = _ => Task.FromResult(true);
        var authManager = new AuthManager(showDialog);
        var device = CreateDevice();

        // Act
        await authManager.RequestAuthorizationAsync(device);

        // Assert: 許可後はデバイスが信頼済みリストに追加されていること
        Assert.True(authManager.IsTrusted(device.Id));
    }

    /// <summary>
    /// ユーザーが拒否したとき、デバイスが信頼済みリストに追加されないことを検証する。
    /// Validates: Requirement 8.1
    /// </summary>
    [Fact]
    public async Task RequestAuthorizationAsync_DoesNotTrustDevice_WhenUserDenies()
    {
        // Arrange
        Func<DeviceInfo, Task<bool>> showDialog = _ => Task.FromResult(false);
        var authManager = new AuthManager(showDialog);
        var device = CreateDevice();

        // Act
        await authManager.RequestAuthorizationAsync(device);

        // Assert: 拒否後はデバイスが信頼済みリストに追加されていないこと
        Assert.False(authManager.IsTrusted(device.Id));
    }

    /// <summary>
    /// 既に信頼済みのデバイスが再接続を試みたとき、
    /// 許可確認ダイアログが表示されないことを検証する。
    /// Validates: Requirement 8.1 (および 8.3 の前提確認)
    /// </summary>
    [Fact]
    public async Task RequestAuthorizationAsync_DoesNotShowDialog_ForTrustedDevice()
    {
        // Arrange
        bool dialogShown = false;
        Func<DeviceInfo, Task<bool>> showDialog = _ =>
        {
            dialogShown = true;
            return Task.FromResult(true);
        };

        var authManager = new AuthManager(showDialog);
        var device = CreateDevice();

        // デバイスを事前に信頼済みリストに追加する
        authManager.TrustDevice(device.Id);

        // Act
        var result = await authManager.RequestAuthorizationAsync(device);

        // Assert: ダイアログが表示されず、AlreadyTrusted が返ること
        Assert.False(dialogShown, "信頼済みデバイスに対してダイアログが表示されるべきではありません");
        Assert.Equal(AuthResult.AlreadyTrusted, result);
    }

    /// <summary>
    /// 許可確認ダイアログに渡される DeviceInfo が、
    /// 接続を試みたデバイスの情報と一致することを検証する。
    /// Validates: Requirement 8.1
    /// </summary>
    [Fact]
    public async Task RequestAuthorizationAsync_PassesCorrectDeviceInfoToDialog()
    {
        // Arrange
        DeviceInfo? capturedDevice = null;
        Func<DeviceInfo, Task<bool>> showDialog = device =>
        {
            capturedDevice = device;
            return Task.FromResult(true);
        };

        var authManager = new AuthManager(showDialog);
        var expectedDevice = CreateDevice(DevicePlatform.iOS, width: 390, height: 844);

        // Act
        await authManager.RequestAuthorizationAsync(expectedDevice);

        // Assert: ダイアログに渡されたデバイス情報が正しいこと
        Assert.NotNull(capturedDevice);
        Assert.Equal(expectedDevice.Id, capturedDevice!.Id);
        Assert.Equal(expectedDevice.Name, capturedDevice.Name);
        Assert.Equal(expectedDevice.Platform, capturedDevice.Platform);
    }
}
