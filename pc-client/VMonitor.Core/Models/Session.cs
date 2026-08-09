namespace VMonitor.Core.Models;

/// <summary>PC クライアントとスマホアプリ間の一つの接続インスタンスを表す。</summary>
public record Session(
    Guid SessionId,
    DeviceIdentifier DeviceId,
    TransportType Transport,
    SessionState State,
    DateTimeOffset EstablishedAt,
    VirtualDisplayHandle DisplayHandle
);

/// <summary>セッションのライフサイクル状態。</summary>
public enum SessionState
{
    /// <summary>接続確立中。</summary>
    Connecting,

    /// <summary>セッションがアクティブで通信中。</summary>
    Active,

    /// <summary>接続断を検出し、自動再接続を試みている。</summary>
    Reconnecting,

    /// <summary>セッションが終了した（再接続失敗またはユーザーによる切断）。</summary>
    Terminated
}
