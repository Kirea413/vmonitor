using Moq;
using VMonitor.Core.Interfaces;
using VMonitor.Core.Models;
using VMonitor.Session;

// VMonitor.Core.Models.Session 型の名前衝突を回避するためにエイリアスを使用。
using SessionModel = VMonitor.Core.Models.Session;

namespace VMonitor.Tests;

/// <summary>
/// Task 5.5: セッション確立フローを VDD と接続し、仮想ディスプレイの自動作成・削除を検証するユニットテスト。
/// Validates: Requirements 2.5, 3.1, 3.5
/// </summary>
public class SessionManagerVddTests
{
    // ── 共通フィクスチャ ─────────────────────────────────────────────────────

    private static DeviceInfo CreateDevice(int width = 1080, int height = 1920) =>
        new(
            Id: DeviceIdentifier.NewIdentifier(),
            Name: "Test Phone",
            Platform: DevicePlatform.Android,
            PhysicalResolution: new Resolution(width, height),
            PixelDensity: 400f);

    private static VirtualDisplayHandle CreateHandle() => VirtualDisplayHandle.NewHandle();

    /// <summary>
    /// 指定の DeviceInfo から SessionManager を構築し、モックも一緒に返す。
    /// </summary>
    private static (SessionManager manager, Mock<ITransport> transport, Mock<IVirtualDisplayDriver> vdd)
        BuildManager()
    {
        var transport = new Mock<ITransport>();
        transport.Setup(t => t.Type).Returns(TransportType.WiFi);
        transport.Setup(t => t.ConnectAsync(It.IsAny<System.Net.EndPoint>(), It.IsAny<CancellationToken>()))
                 .Returns(Task.CompletedTask);
        transport.Setup(t => t.DisconnectAsync())
                 .Returns(Task.CompletedTask);

        var vdd = new Mock<IVirtualDisplayDriver>();

        var manager = new SessionManager(transport.Object, vdd.Object);
        return (manager, transport, vdd);
    }

    // ── Requirement 2.5 / 3.1: セッション確立時に CreateDisplayAsync が呼ばれること ──

    /// <summary>
    /// セッション確立後に CreateDisplayAsync が 1 回呼び出されることを検証する。
    /// Validates: Requirements 2.5, 3.1
    /// </summary>
    [Fact]
    public async Task EstablishSessionAsync_CallsCreateDisplayAsync_OnSuccess()
    {
        var (manager, _, vdd) = BuildManager();
        var device = CreateDevice();
        var expectedHandle = CreateHandle();

        vdd.Setup(v => v.CreateDisplayAsync(It.IsAny<DisplaySpec>()))
           .ReturnsAsync(expectedHandle);

        var session = await manager.EstablishSessionAsync(device, CancellationToken.None);

        vdd.Verify(v => v.CreateDisplayAsync(It.IsAny<DisplaySpec>()), Times.Once);
    }

    /// <summary>
    /// 返却されたセッションの DisplayHandle が CreateDisplayAsync の戻り値と一致することを検証する。
    /// Validates: Requirements 2.5, 3.1
    /// </summary>
    [Fact]
    public async Task EstablishSessionAsync_SessionHasCorrectDisplayHandle()
    {
        var (manager, _, vdd) = BuildManager();
        var device = CreateDevice();
        var expectedHandle = CreateHandle();

        vdd.Setup(v => v.CreateDisplayAsync(It.IsAny<DisplaySpec>()))
           .ReturnsAsync(expectedHandle);

        var session = await manager.EstablishSessionAsync(device, CancellationToken.None);

        Assert.Equal(expectedHandle, session.DisplayHandle);
    }

    /// <summary>
    /// セッション確立時に渡す DisplaySpec がデバイスの物理解像度を含むことを検証する。
    /// Validates: Requirements 2.5, 3.1
    /// </summary>
    [Fact]
    public async Task EstablishSessionAsync_CreatesDisplayWithDeviceResolution()
    {
        var (manager, _, vdd) = BuildManager();
        var device = CreateDevice(width: 1080, height: 1920);
        var handle = CreateHandle();

        DisplaySpec? capturedSpec = null;
        vdd.Setup(v => v.CreateDisplayAsync(It.IsAny<DisplaySpec>()))
           .Callback<DisplaySpec>(spec => capturedSpec = spec)
           .ReturnsAsync(handle);

        await manager.EstablishSessionAsync(device, CancellationToken.None);

        Assert.NotNull(capturedSpec);
        Assert.Equal(device.PhysicalResolution, capturedSpec!.Resolution);
    }

    /// <summary>
    /// セッション確立時にセッション状態が Active であることを検証する。
    /// Validates: Requirements 2.5
    /// </summary>
    [Fact]
    public async Task EstablishSessionAsync_ReturnsActiveSession()
    {
        var (manager, _, vdd) = BuildManager();
        var device = CreateDevice();

        vdd.Setup(v => v.CreateDisplayAsync(It.IsAny<DisplaySpec>()))
           .ReturnsAsync(CreateHandle());

        var session = await manager.EstablishSessionAsync(device, CancellationToken.None);

        Assert.Equal(SessionState.Active, session.State);
    }

    // ── Requirement 3.5: セッション終了時に RemoveDisplayAsync が呼ばれること ──────────

    /// <summary>
    /// TerminateSessionAsync 呼び出し後に RemoveDisplayAsync が 1 回呼ばれることを検証する。
    /// Validates: Requirement 3.5
    /// </summary>
    [Fact]
    public async Task TerminateSessionAsync_CallsRemoveDisplayAsync()
    {
        var (manager, _, vdd) = BuildManager();
        var device = CreateDevice();
        var handle = CreateHandle();

        vdd.Setup(v => v.CreateDisplayAsync(It.IsAny<DisplaySpec>()))
           .ReturnsAsync(handle);
        vdd.Setup(v => v.RemoveDisplayAsync(handle))
           .Returns(Task.CompletedTask);

        var session = await manager.EstablishSessionAsync(device, CancellationToken.None);
        await manager.TerminateSessionAsync(session);

        vdd.Verify(v => v.RemoveDisplayAsync(handle), Times.Once);
    }

    /// <summary>
    /// DisplayHandle が空（Guid.Empty）のセッションでは RemoveDisplayAsync を呼ばないことを検証する。
    /// </summary>
    [Fact]
    public async Task TerminateSessionAsync_DoesNotCallRemoveDisplayAsync_WhenHandleIsEmpty()
    {
        var (manager, transport, vdd) = BuildManager();

        // DisplayHandle = Guid.Empty のセッションを直接作成
        var emptySession = new SessionModel(
            SessionId: Guid.NewGuid(),
            DeviceId: DeviceIdentifier.NewIdentifier(),
            Transport: TransportType.WiFi,
            State: SessionState.Active,
            EstablishedAt: DateTimeOffset.UtcNow,
            DisplayHandle: new VirtualDisplayHandle(Guid.Empty));

        await manager.TerminateSessionAsync(emptySession);

        vdd.Verify(v => v.RemoveDisplayAsync(It.IsAny<VirtualDisplayHandle>()), Times.Never);
    }

    // ── タイムアウト時に RemoveDisplayAsync が呼ばれること ────────────────────────────

    /// <summary>
    /// TryReconnectAsync がタイムアウトした場合に RemoveDisplayAsync が呼ばれることを検証する。
    /// Validates: Requirement 3.5
    /// </summary>
    [Fact]
    public async Task TryReconnectAsync_CallsRemoveDisplayAsync_OnTimeout()
    {
        var transport = new Mock<ITransport>();
        transport.Setup(t => t.Type).Returns(TransportType.WiFi);
        // 再接続試行は常に失敗する
        transport.Setup(t => t.ConnectAsync(It.IsAny<System.Net.EndPoint>(), It.IsAny<CancellationToken>()))
                 .ThrowsAsync(new IOException("接続失敗"));

        var vdd = new Mock<IVirtualDisplayDriver>();
        var handle = CreateHandle();
        vdd.Setup(v => v.RemoveDisplayAsync(handle)).Returns(Task.CompletedTask);

        var manager = new SessionManager(transport.Object, vdd.Object);

        // 既存の Active セッション（DisplayHandle 付き）を直接用意する
        var existingSession = new SessionModel(
            SessionId: Guid.NewGuid(),
            DeviceId: DeviceIdentifier.NewIdentifier(),
            Transport: TransportType.WiFi,
            State: SessionState.Active,
            EstablishedAt: DateTimeOffset.UtcNow,
            DisplayHandle: handle);

        // タイムアウト 500ms で再接続試行する
        var result = await manager.TryReconnectAsync(
            existingSession,
            timeout: TimeSpan.FromMilliseconds(500),
            ct: CancellationToken.None);

        Assert.Equal(ReconnectResult.TimedOut, result);
        vdd.Verify(v => v.RemoveDisplayAsync(handle), Times.Once);
    }
}
