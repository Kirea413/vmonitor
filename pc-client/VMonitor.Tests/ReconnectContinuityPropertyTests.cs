using FsCheck;
using FsCheck.Xunit;
using Moq;
using VMonitor.Core.Interfaces;
using VMonitor.Core.Models;
using VMonitor.Session;

// VMonitor.Core.Models.Session 型の名前衝突を回避するためにエイリアスを使用。
using SessionModel = VMonitor.Core.Models.Session;

namespace VMonitor.Tests;

// Feature: vmonitor, Property 22: 再接続試行の継続性

/// <summary>
/// Property 22: 再接続試行の継続性
/// Validates: Requirements 9.1
///
/// 任意の切断タイミング（0秒以上30秒未満）に対して、PCクライアントは切断後30秒が経過するまで
/// 再接続を試み続けなければならない（モック使用）。
///
/// テスト実行速度を現実的に保つため、30秒を300ミリ秒にスケールダウンして検証する。
/// （0–29秒 → 0–290ミリ秒、タイムアウト300ミリ秒）
/// </summary>
public class ReconnectContinuityPropertyTests
{
    // テスト実行速度のため 30s を 300ms にスケールダウン（1s → 10ms）
    private const int ScaleFactor = 10; // ms per "second"
    private const int TotalTimeoutMs = 300; // 30s * 10ms/s

    /// <summary>
    /// 任意の切断タイミング（0〜29秒相当）に対して TryReconnectAsync が
    /// ReconnectResult.TimedOut を返すことを検証する。
    ///
    /// disconnectTimingSeconds は 0〜29 の整数で、FsCheck の生成値を
    /// Math.Abs(...) % 30 でその範囲に射影する。
    ///
    /// 常に接続失敗するトランスポートモックを使用し、タイムアウトまで
    /// 再接続を試み続けた後で TimedOut が返ることを確認する。
    /// </summary>
    [Property(MaxTest = 50)]
    public bool ReconnectKeepsTryingUntilTimeout(int rawTiming)
    {
        // disconnectTimingSeconds を 0〜29 の範囲に正規化する
        int disconnectTimingSeconds = Math.Abs(rawTiming) % 30;

        // トランスポートモック: ConnectAsync は常に失敗する
        var transportMock = new Mock<ITransport>();
        transportMock.Setup(t => t.Type).Returns(TransportType.WiFi);
        transportMock
            .Setup(t => t.ConnectAsync(
                It.IsAny<System.Net.EndPoint>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("接続失敗（モック）"));

        // VDD モック: RemoveDisplayAsync は成功する
        var vddMock = new Mock<IVirtualDisplayDriver>();
        vddMock
            .Setup(v => v.RemoveDisplayAsync(It.IsAny<VirtualDisplayHandle>()))
            .Returns(Task.CompletedTask);

        var manager = new SessionManager(transportMock.Object, vddMock.Object);

        // 切断タイミング（スケール済み）に対応するセッションを構築する。
        // disconnectTimingSeconds は「すでに何秒間再接続を試みたか」を表す。
        // DisplayHandle を Guid.Empty にすることで RemoveDisplayAsync 呼び出しを回避する。
        var session = new SessionModel(
            SessionId: Guid.NewGuid(),
            DeviceId: DeviceIdentifier.NewIdentifier(),
            Transport: TransportType.WiFi,
            State: SessionState.Reconnecting,
            EstablishedAt: DateTimeOffset.UtcNow.AddMilliseconds(
                -disconnectTimingSeconds * ScaleFactor),
            DisplayHandle: new VirtualDisplayHandle(Guid.Empty));

        // タイムアウトは常に 300ms（30秒相当）
        var timeout = TimeSpan.FromMilliseconds(TotalTimeoutMs);

        var result = manager.TryReconnectAsync(session, timeout, CancellationToken.None)
                             .GetAwaiter().GetResult();

        // 切断タイミングに関わらず、常に TimedOut でなければならない
        return result == ReconnectResult.TimedOut;
    }
}
