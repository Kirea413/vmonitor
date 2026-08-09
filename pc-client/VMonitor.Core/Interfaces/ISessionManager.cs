using VMonitor.Core.Models;

namespace VMonitor.Core.Interfaces;

/// <summary>
/// セッションの確立・終了・再接続を管理するインターフェース。
/// </summary>
public interface ISessionManager
{
    /// <summary>指定デバイスとのセッションを確立する。10 秒以内に確立できない場合はタイムアウトする。</summary>
    Task<Session> EstablishSessionAsync(DeviceInfo device, CancellationToken ct);

    /// <summary>指定セッションを正常終了する。</summary>
    Task TerminateSessionAsync(Session session);

    /// <summary>
    /// 切断されたセッションへの再接続を試みる。
    /// 指数バックオフ（初回 1s → 最大 5s）で timeout に達するまで再試行する。
    /// </summary>
    Task<ReconnectResult> TryReconnectAsync(Session session, TimeSpan timeout, CancellationToken ct);

    /// <summary>セッションが切断されたときに発生するイベント。</summary>
    event EventHandler<SessionDisconnectedEventArgs> SessionDisconnected;
}

/// <summary>再接続試行の結果を表す。</summary>
public enum ReconnectResult
{
    /// <summary>再接続に成功した。</summary>
    Success,

    /// <summary>タイムアウトにより再接続を断念した。</summary>
    TimedOut,

    /// <summary>認証エラーなど、回復不可能なエラーが発生した。</summary>
    Failed
}

/// <summary>SessionDisconnected イベントのデータ。</summary>
public class SessionDisconnectedEventArgs : EventArgs
{
    /// <summary>切断されたセッション。</summary>
    public required Session Session { get; init; }

    /// <summary>切断の原因となった例外（原因が不明な場合は null）。</summary>
    public Exception? Reason { get; init; }
}
