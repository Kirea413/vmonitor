using Moq;
using VMonitor.Session;

namespace VMonitor.Tests;

/// <summary>
/// Task 14.2: ドライバ障害回復のユニットテスト。
/// <list type="bullet">
///   <item>ドライバ停止イベントで再起動試行が行われること（9.3）</item>
///   <item>再起動成功時にそれ以上の試行が行われないこと</item>
///   <item>再起動失敗時に最大 3 回まで再試行されること</item>
///   <item>3 回失敗後に DriverRecoveryFailed イベントが発火されること</item>
/// </list>
/// Validates: Requirements 9.3
/// </summary>
public class DriverRecoveryServiceTests
{
    private const string TestDeviceId = "PCI\\VEN_1234&DEV_5678\\4&12345678&0&0000";

    // ── ヘルパー ──────────────────────────────────────────────────────────

    /// <summary>
    /// テスト用の IWmiDriverEventSource モックを構築する。
    /// </summary>
    private static Mock<IWmiDriverEventSource> BuildWmiSourceMock()
    {
        var mock = new Mock<IWmiDriverEventSource>();
        // イベントのアタッチ/デタッチを許可する
        mock.SetupAdd(m => m.DriverStopped += It.IsAny<EventHandler<DriverStoppedEventArgs>>());
        mock.SetupRemove(m => m.DriverStopped -= It.IsAny<EventHandler<DriverStoppedEventArgs>>());
        return mock;
    }

    /// <summary>
    /// IWmiDriverEventSource モックから DriverStopped イベントを発火するヘルパー。
    /// </summary>
    private static void RaiseDriverStopped(
        Mock<IWmiDriverEventSource> wmiMock,
        string deviceInstanceId)
    {
        wmiMock.Raise(
            m => m.DriverStopped += null,
            new DriverStoppedEventArgs { DeviceInstanceId = deviceInstanceId });
    }

    // ── Requirement 9.3: ドライバ停止イベントで再起動試行が行われること ──────────────

    /// <summary>
    /// DriverStopped イベントが発火されたとき、
    /// RestartDeviceAsync が少なくとも 1 回呼び出されることを検証する。
    /// Validates: Requirements 9.3
    /// </summary>
    [Fact]
    public async Task DriverStopped_CallsRestartDeviceAsync_AtLeastOnce()
    {
        // Arrange
        var wmiMock = BuildWmiSourceMock();
        var runnerMock = new Mock<IDriverProcessRunner>();
        runnerMock
            .Setup(r => r.RestartDeviceAsync(It.IsAny<string>()))
            .ReturnsAsync(true);

        using var service = new DriverRecoveryService(
            wmiMock.Object, runnerMock.Object, TestDeviceId);

        // Act: ドライバ停止イベントを発火し、非同期処理の完了を待つ
        RaiseDriverStopped(wmiMock, TestDeviceId);
        await Task.Delay(100);

        // Assert: 再起動が試みられたこと
        runnerMock.Verify(
            r => r.RestartDeviceAsync(TestDeviceId),
            Times.AtLeastOnce);
    }

    /// <summary>
    /// DriverStopped イベントで対象デバイス ID が RestartDeviceAsync に渡されることを検証する。
    /// Validates: Requirements 9.3
    /// </summary>
    [Fact]
    public async Task DriverStopped_PassesCorrectDeviceInstanceId_ToRestartDeviceAsync()
    {
        // Arrange
        var wmiMock = BuildWmiSourceMock();
        var runnerMock = new Mock<IDriverProcessRunner>();
        string? capturedId = null;
        runnerMock
            .Setup(r => r.RestartDeviceAsync(It.IsAny<string>()))
            .Callback<string>(id => capturedId = id)
            .ReturnsAsync(true);

        using var service = new DriverRecoveryService(
            wmiMock.Object, runnerMock.Object, TestDeviceId);

        // Act
        RaiseDriverStopped(wmiMock, TestDeviceId);
        await Task.Delay(100);

        // Assert: 正しいデバイス ID が渡されたこと
        Assert.Equal(TestDeviceId, capturedId);
    }

    /// <summary>
    /// 対象外のデバイス ID で DriverStopped イベントが発火されたとき、
    /// RestartDeviceAsync が呼び出されないことを検証する。
    /// Validates: Requirements 9.3
    /// </summary>
    [Fact]
    public async Task DriverStopped_DoesNotCallRestart_WhenDeviceIdDoesNotMatch()
    {
        // Arrange
        var wmiMock = BuildWmiSourceMock();
        var runnerMock = new Mock<IDriverProcessRunner>();
        runnerMock
            .Setup(r => r.RestartDeviceAsync(It.IsAny<string>()))
            .ReturnsAsync(true);

        using var service = new DriverRecoveryService(
            wmiMock.Object, runnerMock.Object, TestDeviceId);

        // Act: 異なるデバイス ID でイベントを発火する
        RaiseDriverStopped(wmiMock, "OTHER\\DEVICE\\ID");
        await Task.Delay(100);

        // Assert: 再起動が試みられていないこと
        runnerMock.Verify(
            r => r.RestartDeviceAsync(It.IsAny<string>()),
            Times.Never);
    }

    // ── 再起動成功時の動作 ────────────────────────────────────────────────

    /// <summary>
    /// 1 回目の再起動が成功したとき、
    /// RestartDeviceAsync が 1 回だけ呼び出されることを検証する（追加試行なし）。
    /// Validates: Requirements 9.3
    /// </summary>
    [Fact]
    public async Task RecoverAsync_StopsRetrying_AfterFirstSuccess()
    {
        // Arrange
        var wmiMock = BuildWmiSourceMock();
        var runnerMock = new Mock<IDriverProcessRunner>();
        runnerMock
            .Setup(r => r.RestartDeviceAsync(TestDeviceId))
            .ReturnsAsync(true); // 1 回目で成功

        using var service = new DriverRecoveryService(
            wmiMock.Object, runnerMock.Object, TestDeviceId);

        // Act
        await service.RecoverAsync(TestDeviceId);

        // Assert: 1 回のみ試行されたこと
        runnerMock.Verify(r => r.RestartDeviceAsync(TestDeviceId), Times.Once);
    }

    /// <summary>
    /// 1 回目の再起動が成功したとき、
    /// DriverRecoveryFailed イベントが発火されないことを検証する。
    /// Validates: Requirements 9.3
    /// </summary>
    [Fact]
    public async Task RecoverAsync_DoesNotFireFailedEvent_WhenFirstAttemptSucceeds()
    {
        // Arrange
        var wmiMock = BuildWmiSourceMock();
        var runnerMock = new Mock<IDriverProcessRunner>();
        runnerMock
            .Setup(r => r.RestartDeviceAsync(TestDeviceId))
            .ReturnsAsync(true);

        using var service = new DriverRecoveryService(
            wmiMock.Object, runnerMock.Object, TestDeviceId);

        bool failedEventFired = false;
        service.DriverRecoveryFailed += (_, _) => failedEventFired = true;

        // Act
        await service.RecoverAsync(TestDeviceId);

        // Assert: 失敗イベントが発火されていないこと
        Assert.False(failedEventFired,
            "再起動成功時に DriverRecoveryFailed イベントが発火されるべきではありません");
    }

    /// <summary>
    /// 2 回目の再起動が成功したとき、
    /// RestartDeviceAsync が 2 回呼び出されることを検証する。
    /// Validates: Requirements 9.3
    /// </summary>
    [Fact]
    public async Task RecoverAsync_StopsRetrying_AfterSecondSuccess()
    {
        // Arrange
        var wmiMock = BuildWmiSourceMock();
        var runnerMock = new Mock<IDriverProcessRunner>();
        int callCount = 0;
        runnerMock
            .Setup(r => r.RestartDeviceAsync(TestDeviceId))
            .ReturnsAsync(() =>
            {
                callCount++;
                return callCount >= 2; // 2 回目で成功
            });

        // RetryInterval をゼロにオーバーライドするため直接 RecoverAsync を呼ぶ
        using var service = new DriverRecoveryService(
            wmiMock.Object, runnerMock.Object, TestDeviceId);

        // Act: RecoverAsync を直接呼ぶ（RetryInterval の待機あり）
        // インターバルを短縮するため小さいタイムアウト内で検証
        await service.RecoverAsync(TestDeviceId);

        // Assert: 2 回だけ試行されたこと
        runnerMock.Verify(r => r.RestartDeviceAsync(TestDeviceId), Times.Exactly(2));
    }

    // ── 最大 3 回の再試行 ──────────────────────────────────────────────────

    /// <summary>
    /// 全ての再起動試行が失敗したとき、
    /// RestartDeviceAsync が MaxRetryCount（3 回）呼び出されることを検証する。
    /// Validates: Requirements 9.3
    /// </summary>
    [Fact]
    public async Task RecoverAsync_TriesExactlyMaxRetryCount_OnAllFailures()
    {
        // Arrange
        var wmiMock = BuildWmiSourceMock();
        var runnerMock = new Mock<IDriverProcessRunner>();
        runnerMock
            .Setup(r => r.RestartDeviceAsync(TestDeviceId))
            .ReturnsAsync(false); // 常に失敗

        using var service = new DriverRecoveryService(
            wmiMock.Object, runnerMock.Object, TestDeviceId);

        // RetryInterval が 5 秒でテストがタイムアウトしないよう RecoverAsync を直接呼ぶ代わりに
        // サービスの内部インターバルをテスト向けに短縮したサブクラスを使う方法を採用する。
        // ただし DriverRecoveryService はシールドクラスのため、
        // RetryInterval フィールドはテスト用に実装をモックで代替する。
        // 代わりに RecoverAsync を直接テストするが、Task.Delay を挟む設計なので
        // MaxRetryCount × RetryInterval 分の待機を許容する。
        //
        // テスト速度優先のため: RecoverAsync の中の Task.Delay(RetryInterval) を
        // テストでは待機しない代わりに、内部で RecoverAsync を呼ぶ専用のモックサービスを使う。
        // ここでは public な RecoverAsync を直接呼び出す（RetryInterval は実際の 5 秒）。
        // → テスト用インターバルで CancellationToken を使って早期終了させる代わりに、
        //    RecoverAsync を直接テストするには別アプローチが必要。
        //
        // 設計上 RetryInterval は static readonly フィールドなので、
        // ここではテスト用の短いタイムアウト（最大 3 秒）で Task をキャンセルせず実行する。
        // 各インターバルは実際は 5 秒だが、テスト用途では RecoverAsync を呼ばずに
        // DriverStopped イベント経由で fire-and-forget の完了を Task.Delay で待つ方法を使う。
        // 
        // → 最もシンプルなアプローチ: RecoverAsync を直接呼び、
        //    RetryInterval = 5s × 2 インターバル = 10 秒待機を許容する。
        //    代替: IDriverProcessRunner のコールバックでインターバルを無視して呼び出し回数のみ検証。

        // Act: 直接 RecoverAsync を呼ぶ（インターバル待機あり）
        await service.RecoverAsync(TestDeviceId);

        // Assert: MaxRetryCount 回試行されたこと
        runnerMock.Verify(
            r => r.RestartDeviceAsync(TestDeviceId),
            Times.Exactly(DriverRecoveryService.MaxRetryCount));
    }

    // ── DriverRecoveryFailed イベント ────────────────────────────────────

    /// <summary>
    /// 全試行が失敗したとき、
    /// DriverRecoveryFailed イベントが 1 回発火されることを検証する。
    /// Validates: Requirements 9.3
    /// </summary>
    [Fact]
    public async Task RecoverAsync_FiresDriverRecoveryFailedEvent_WhenAllAttemptsFaile()
    {
        // Arrange
        var wmiMock = BuildWmiSourceMock();
        var runnerMock = new Mock<IDriverProcessRunner>();
        runnerMock
            .Setup(r => r.RestartDeviceAsync(TestDeviceId))
            .ReturnsAsync(false);

        using var service = new DriverRecoveryService(
            wmiMock.Object, runnerMock.Object, TestDeviceId);

        bool failedEventFired = false;
        service.DriverRecoveryFailed += (_, _) => failedEventFired = true;

        // Act
        await service.RecoverAsync(TestDeviceId);

        // Assert: 失敗イベントが発火されたこと
        Assert.True(failedEventFired,
            "全試行失敗後に DriverRecoveryFailed イベントが発火されるべきです");
    }

    /// <summary>
    /// DriverRecoveryFailed イベントの DeviceInstanceId が
    /// 停止したデバイスの ID と一致することを検証する。
    /// Validates: Requirements 9.3
    /// </summary>
    [Fact]
    public async Task RecoverAsync_FailedEventArgs_ContainsCorrectDeviceInstanceId()
    {
        // Arrange
        var wmiMock = BuildWmiSourceMock();
        var runnerMock = new Mock<IDriverProcessRunner>();
        runnerMock
            .Setup(r => r.RestartDeviceAsync(TestDeviceId))
            .ReturnsAsync(false);

        using var service = new DriverRecoveryService(
            wmiMock.Object, runnerMock.Object, TestDeviceId);

        DriverRecoveryFailedEventArgs? capturedArgs = null;
        service.DriverRecoveryFailed += (_, args) => capturedArgs = args;

        // Act
        await service.RecoverAsync(TestDeviceId);

        // Assert: イベント引数のデバイス ID が正しいこと
        Assert.NotNull(capturedArgs);
        Assert.Equal(TestDeviceId, capturedArgs!.DeviceInstanceId);
    }

    /// <summary>
    /// DriverRecoveryFailed イベントの AttemptCount が MaxRetryCount と等しいことを検証する。
    /// Validates: Requirements 9.3
    /// </summary>
    [Fact]
    public async Task RecoverAsync_FailedEventArgs_AttemptCountEqualsMaxRetryCount()
    {
        // Arrange
        var wmiMock = BuildWmiSourceMock();
        var runnerMock = new Mock<IDriverProcessRunner>();
        runnerMock
            .Setup(r => r.RestartDeviceAsync(TestDeviceId))
            .ReturnsAsync(false);

        using var service = new DriverRecoveryService(
            wmiMock.Object, runnerMock.Object, TestDeviceId);

        DriverRecoveryFailedEventArgs? capturedArgs = null;
        service.DriverRecoveryFailed += (_, args) => capturedArgs = args;

        // Act
        await service.RecoverAsync(TestDeviceId);

        // Assert: 試行回数が MaxRetryCount と等しいこと
        Assert.NotNull(capturedArgs);
        Assert.Equal(DriverRecoveryService.MaxRetryCount, capturedArgs!.AttemptCount);
    }

    // ── Dispose 後の動作 ──────────────────────────────────────────────────

    /// <summary>
    /// Dispose 後に DriverStopped イベントが発火されても、
    /// RestartDeviceAsync が呼び出されないことを検証する。
    /// </summary>
    [Fact]
    public async Task DriverStopped_AfterDispose_DoesNotCallRestartDeviceAsync()
    {
        // Arrange
        var wmiMock = BuildWmiSourceMock();
        var runnerMock = new Mock<IDriverProcessRunner>();
        runnerMock
            .Setup(r => r.RestartDeviceAsync(It.IsAny<string>()))
            .ReturnsAsync(true);

        var service = new DriverRecoveryService(
            wmiMock.Object, runnerMock.Object, TestDeviceId);

        // Act: Dispose してからイベントを発火する
        service.Dispose();
        RaiseDriverStopped(wmiMock, TestDeviceId);
        await Task.Delay(100);

        // Assert: 再起動が試みられていないこと
        runnerMock.Verify(
            r => r.RestartDeviceAsync(It.IsAny<string>()),
            Times.Never);
    }
}
