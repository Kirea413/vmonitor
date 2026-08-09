using VMonitor.Core.Models;

namespace VMonitor.Core.Interfaces;

/// <summary>
/// デバイス認証と信頼済みデバイス管理を担うインターフェース。
/// </summary>
public interface IAuthManager
{
    /// <summary>
    /// 指定デバイスへの接続許可をユーザーに確認する（初回接続時の UI ダイアログ表示）。
    /// </summary>
    Task<AuthResult> RequestAuthorizationAsync(DeviceInfo device);

    /// <summary>指定デバイス識別子が信頼済みかどうかを返す。</summary>
    bool IsTrusted(DeviceIdentifier deviceId);

    /// <summary>指定デバイス識別子を信頼済みリストに追加する。</summary>
    void TrustDevice(DeviceIdentifier deviceId);

    /// <summary>指定デバイス識別子の信頼を取り消す。</summary>
    void RevokeTrust(DeviceIdentifier deviceId);

    /// <summary>信頼済みデバイスの一覧を返す。</summary>
    IReadOnlyList<TrustedDevice> GetTrustedDevices();
}

/// <summary>認証リクエストの結果を表す。</summary>
public enum AuthResult
{
    /// <summary>ユーザーが接続を許可した。</summary>
    Approved,

    /// <summary>ユーザーが接続を拒否した。</summary>
    Denied,

    /// <summary>デバイスは既に信頼済みだったため確認なしで許可された。</summary>
    AlreadyTrusted
}
