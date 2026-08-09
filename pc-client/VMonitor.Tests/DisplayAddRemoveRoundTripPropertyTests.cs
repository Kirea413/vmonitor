// Feature: vmonitor, Property 3: ディスプレイ追加・削除のラウンドトリップ

using System.Collections.Concurrent;
using FsCheck;
using FsCheck.Xunit;
using Moq;
using VMonitor.Core.Interfaces;
using VMonitor.Core.Models;
using VMonitor.Session;

namespace VMonitor.Tests;

/// <summary>
/// Property 3: ディスプレイ追加・削除のラウンドトリップ
/// Validates: Requirements 3.1, 3.5
///
/// 任意の有効なデバイス情報に対して、セッション確立後にディスプレイ一覧が
/// 仮想ディスプレイを含み、セッション切断後にはそのエントリを含まなければならない
/// （追加→削除ラウンドトリップ）。
/// </summary>
public class DisplayAddRemoveRoundTripPropertyTests
{
    // ── ヘルパー ────────────────────────────────────────────────────────────

    /// <summary>
    /// FsCheck が生成したパラメーターから有効な DeviceInfo を組み立てる。
    /// 解像度は仮想ディスプレイのサポート範囲（640×480 〜 3840×2160）に正規化する。
    /// </summary>
    private static DeviceInfo BuildDeviceInfo(
        NonEmptyString rawName,
        DevicePlatform platform,
        int rawWidth,
        int rawHeight,
        float rawPpi)
    {
        // 解像度を MinSupported〜MaxSupported の範囲に正規化する
        int width  = Normalize(rawWidth,  Resolution.MinSupported.Width,  Resolution.MaxSupported.Width);
        int height = Normalize(rawHeight, Resolution.MinSupported.Height, Resolution.MaxSupported.Height);

        // PPI は 72〜600 の範囲に正規化する（0 や負値を回避）
        float ppi = Math.Max(72f, Math.Min(600f, Math.Abs(rawPpi) == 0f ? 72f : Math.Abs(rawPpi)));

        return new DeviceInfo(
            Id: DeviceIdentifier.NewIdentifier(),
            Name: rawName.Get,
            Platform: platform,
            PhysicalResolution: new Resolution(width, height),
            PixelDensity: ppi);
    }

    private static int Normalize(int raw, int min, int max)
    {
        // Math.Abs で負値を正にしてから範囲に収める
        int abs = Math.Abs(raw);
        if (abs == 0) abs = min;
        // モジュロで範囲内に収める
        int range = max - min + 1;
        return min + (abs % range);
    }

    /// <summary>
    /// ITransport モックを生成する（ConnectAsync / DisconnectAsync は即座に成功）。
    /// </summary>
    private static Mock<ITransport> CreateTransportMock()
    {
        var transport = new Mock<ITransport>();
        transport.Setup(t => t.Type).Returns(TransportType.WiFi);
        transport.Setup(t => t.ConnectAsync(It.IsAny<System.Net.EndPoint>(), It.IsAny<CancellationToken>()))
                 .Returns(Task.CompletedTask);
        transport.Setup(t => t.DisconnectAsync())
                 .Returns(Task.CompletedTask);
        return transport;
    }

    /// <summary>
    /// IVirtualDisplayDriver モックを生成する。
    /// <para>
    /// <c>CreateDisplayAsync</c> は新しいハンドルを生成してローカルセットに追加し、
    /// <c>RemoveDisplayAsync</c> はそのハンドルをセットから削除する。
    /// </para>
    /// </summary>
    private static (Mock<IVirtualDisplayDriver> mock, ConcurrentDictionary<VirtualDisplayHandle, bool> activeDisplays)
        CreateVddMock()
    {
        var activeDisplays = new ConcurrentDictionary<VirtualDisplayHandle, bool>();
        var vdd = new Mock<IVirtualDisplayDriver>();

        vdd.Setup(v => v.CreateDisplayAsync(It.IsAny<DisplaySpec>()))
           .ReturnsAsync(() =>
           {
               var handle = VirtualDisplayHandle.NewHandle();
               activeDisplays[handle] = true;
               return handle;
           });

        vdd.Setup(v => v.RemoveDisplayAsync(It.IsAny<VirtualDisplayHandle>()))
           .Callback<VirtualDisplayHandle>(handle => activeDisplays.TryRemove(handle, out _))
           .Returns(Task.CompletedTask);

        return (vdd, activeDisplays);
    }

    // ── Property 3-A: セッション確立後にディスプレイ一覧に仮想ディスプレイが含まれること ──

    /// <summary>
    /// Property 3-A: 任意の有効なデバイス情報に対して、
    /// <see cref="SessionManager.EstablishSessionAsync"/> の完了後に
    /// 仮想ディスプレイドライバのアクティブディスプレイ一覧が
    /// 作成されたハンドルを含まなければならない。
    ///
    /// Validates: Requirements 3.1
    /// </summary>
    [Property(MaxTest = 100)]
    public bool AfterEstablish_DisplayListContainsVirtualDisplay(
        NonEmptyString rawName,
        DevicePlatform platform,
        int rawWidth,
        int rawHeight,
        float rawPpi)
    {
        var device = BuildDeviceInfo(rawName, platform, rawWidth, rawHeight, rawPpi);
        var (vddMock, activeDisplays) = CreateVddMock();
        var manager = new SessionManager(CreateTransportMock().Object, vddMock.Object);

        var session = manager.EstablishSessionAsync(device, CancellationToken.None)
                             .GetAwaiter().GetResult();

        // セッション確立後: 返却されたハンドルがアクティブ一覧に含まれていること（Requirement 3.1）
        return activeDisplays.ContainsKey(session.DisplayHandle);
    }

    // ── Property 3-B: セッション終了後にディスプレイ一覧から仮想ディスプレイが削除されること ──

    /// <summary>
    /// Property 3-B: 任意の有効なデバイス情報に対して、
    /// <see cref="SessionManager.TerminateSessionAsync"/> の完了後に
    /// 仮想ディスプレイドライバのアクティブディスプレイ一覧が
    /// そのハンドルを含まないこと。
    ///
    /// Validates: Requirements 3.5
    /// </summary>
    [Property(MaxTest = 100)]
    public bool AfterTerminate_DisplayListDoesNotContainVirtualDisplay(
        NonEmptyString rawName,
        DevicePlatform platform,
        int rawWidth,
        int rawHeight,
        float rawPpi)
    {
        var device = BuildDeviceInfo(rawName, platform, rawWidth, rawHeight, rawPpi);
        var (vddMock, activeDisplays) = CreateVddMock();
        var manager = new SessionManager(CreateTransportMock().Object, vddMock.Object);

        // セッション確立
        var session = manager.EstablishSessionAsync(device, CancellationToken.None)
                             .GetAwaiter().GetResult();

        // セッション終了
        manager.TerminateSessionAsync(session).GetAwaiter().GetResult();

        // セッション終了後: ハンドルがアクティブ一覧に含まれていないこと（Requirement 3.5）
        return !activeDisplays.ContainsKey(session.DisplayHandle);
    }

    // ── Property 3-C（ラウンドトリップ）: 追加→確認→削除→確認 ────────────────

    /// <summary>
    /// Property 3-C（ラウンドトリップ）: 任意の有効なデバイス情報に対して、
    /// <list type="number">
    ///   <item>セッション確立後: ディスプレイ一覧にハンドルが含まれる（3.1）</item>
    ///   <item>セッション終了後: ディスプレイ一覧にハンドルが含まれない（3.5）</item>
    /// </list>
    /// 両条件が同一セッションで成立すること（追加→削除ラウンドトリップ）。
    ///
    /// Validates: Requirements 3.1, 3.5
    /// </summary>
    [Property(MaxTest = 100)]
    public bool DisplayAddRemoveRoundTrip(
        NonEmptyString rawName,
        DevicePlatform platform,
        int rawWidth,
        int rawHeight,
        float rawPpi)
    {
        var device = BuildDeviceInfo(rawName, platform, rawWidth, rawHeight, rawPpi);
        var (vddMock, activeDisplays) = CreateVddMock();
        var manager = new SessionManager(CreateTransportMock().Object, vddMock.Object);

        // Step 1: セッション確立
        var session = manager.EstablishSessionAsync(device, CancellationToken.None)
                             .GetAwaiter().GetResult();
        var handle = session.DisplayHandle;

        // Step 2: 確立後の確認 — ハンドルが一覧に含まれること（Requirement 3.1）
        bool presentAfterEstablish = activeDisplays.ContainsKey(handle);

        // Step 3: セッション終了
        manager.TerminateSessionAsync(session).GetAwaiter().GetResult();

        // Step 4: 終了後の確認 — ハンドルが一覧に含まれないこと（Requirement 3.5）
        bool absentAfterTerminate = !activeDisplays.ContainsKey(handle);

        return presentAfterEstablish && absentAfterTerminate;
    }

    // ── 具体的なユニットテスト（代表値）──────────────────────────────────────

    /// <summary>
    /// Portrait デバイス（1080×1920）でセッション確立後、
    /// ディスプレイが追加されセッション終了後に削除されることを確認する。
    /// Validates: Requirements 3.1, 3.5
    /// </summary>
    [Fact]
    public async Task PortraitDevice_DisplayRoundTrip()
    {
        var device = new DeviceInfo(
            Id: DeviceIdentifier.NewIdentifier(),
            Name: "Portrait Phone",
            Platform: DevicePlatform.Android,
            PhysicalResolution: new Resolution(1080, 1920),
            PixelDensity: 420f);

        var (vddMock, activeDisplays) = CreateVddMock();
        var manager = new SessionManager(CreateTransportMock().Object, vddMock.Object);

        var session = await manager.EstablishSessionAsync(device, CancellationToken.None);

        // 確立後: ディスプレイが存在する
        Assert.True(activeDisplays.ContainsKey(session.DisplayHandle),
            "セッション確立後、仮想ディスプレイはアクティブ一覧に含まれなければならない（Requirement 3.1）");

        await manager.TerminateSessionAsync(session);

        // 終了後: ディスプレイが削除されている
        Assert.False(activeDisplays.ContainsKey(session.DisplayHandle),
            "セッション終了後、仮想ディスプレイはアクティブ一覧から削除されなければならない（Requirement 3.5）");
    }

    /// <summary>
    /// Landscape デバイス（1920×1080）でセッション確立後、
    /// ディスプレイが追加されセッション終了後に削除されることを確認する。
    /// Validates: Requirements 3.1, 3.5
    /// </summary>
    [Fact]
    public async Task LandscapeDevice_DisplayRoundTrip()
    {
        var device = new DeviceInfo(
            Id: DeviceIdentifier.NewIdentifier(),
            Name: "Landscape Tablet",
            Platform: DevicePlatform.iOS,
            PhysicalResolution: new Resolution(1920, 1080),
            PixelDensity: 264f);

        var (vddMock, activeDisplays) = CreateVddMock();
        var manager = new SessionManager(CreateTransportMock().Object, vddMock.Object);

        var session = await manager.EstablishSessionAsync(device, CancellationToken.None);

        Assert.True(activeDisplays.ContainsKey(session.DisplayHandle),
            "セッション確立後、仮想ディスプレイはアクティブ一覧に含まれなければならない（Requirement 3.1）");

        await manager.TerminateSessionAsync(session);

        Assert.False(activeDisplays.ContainsKey(session.DisplayHandle),
            "セッション終了後、仮想ディスプレイはアクティブ一覧から削除されなければならない（Requirement 3.5）");
    }

    /// <summary>
    /// 複数セッションが同時に存在する場合、
    /// 一方のセッション終了が他方のディスプレイに影響しないことを確認する。
    /// Validates: Requirements 3.1, 3.5
    /// </summary>
    [Fact]
    public async Task MultipleSessionsIsolated_TerminatingOneDoesNotAffectOther()
    {
        var deviceA = new DeviceInfo(
            Id: DeviceIdentifier.NewIdentifier(),
            Name: "Phone A",
            Platform: DevicePlatform.Android,
            PhysicalResolution: new Resolution(1080, 1920),
            PixelDensity: 400f);

        var deviceB = new DeviceInfo(
            Id: DeviceIdentifier.NewIdentifier(),
            Name: "Phone B",
            Platform: DevicePlatform.iOS,
            PhysicalResolution: new Resolution(1170, 2532),
            PixelDensity: 460f);

        var (vddMock, activeDisplays) = CreateVddMock();
        var managerA = new SessionManager(CreateTransportMock().Object, vddMock.Object);
        var managerB = new SessionManager(CreateTransportMock().Object, vddMock.Object);

        var sessionA = await managerA.EstablishSessionAsync(deviceA, CancellationToken.None);
        var sessionB = await managerB.EstablishSessionAsync(deviceB, CancellationToken.None);

        // 両方のセッションが確立後にディスプレイが存在する
        Assert.True(activeDisplays.ContainsKey(sessionA.DisplayHandle));
        Assert.True(activeDisplays.ContainsKey(sessionB.DisplayHandle));

        // sessionA のみ終了する
        await managerA.TerminateSessionAsync(sessionA);

        // sessionA のディスプレイは削除されている
        Assert.False(activeDisplays.ContainsKey(sessionA.DisplayHandle),
            "sessionA 終了後は sessionA のディスプレイが削除されていること（Requirement 3.5）");

        // sessionB のディスプレイは影響を受けていない
        Assert.True(activeDisplays.ContainsKey(sessionB.DisplayHandle),
            "sessionA 終了後も sessionB のディスプレイは残っていること（Requirement 3.1）");
    }
}
