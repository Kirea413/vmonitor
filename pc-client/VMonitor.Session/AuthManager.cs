using System.Collections.Concurrent;
using VMonitor.Core.Interfaces;
using VMonitor.Core.Models;

namespace VMonitor.Session;

/// <summary>
/// IAuthManager の実装。デバイス認証と信頼済みデバイスの管理を行う。
/// </summary>
public sealed class AuthManager : IAuthManager
{
    private readonly Func<DeviceInfo, Task<bool>> _showAuthorizationDialog;
    private readonly ConcurrentDictionary<Guid, TrustedDevice> _trustedDevices = new();

    /// <summary>
    /// AuthManager を初期化する。
    /// </summary>
    /// <param name="showAuthorizationDialog">
    /// 初回接続時に表示する許可確認ダイアログのコールバック。
    /// true を返した場合は許可、false を返した場合は拒否。
    /// </param>
    public AuthManager(Func<DeviceInfo, Task<bool>> showAuthorizationDialog)
    {
        _showAuthorizationDialog = showAuthorizationDialog
            ?? throw new ArgumentNullException(nameof(showAuthorizationDialog));
    }

    /// <inheritdoc/>
    public async Task<AuthResult> RequestAuthorizationAsync(DeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);

        if (IsTrusted(device.Id))
            return AuthResult.AlreadyTrusted;

        var approved = await _showAuthorizationDialog(device);
        if (approved)
        {
            TrustDevice(device.Id, device.Name);
            return AuthResult.Approved;
        }

        return AuthResult.Denied;
    }

    /// <inheritdoc/>
    public bool IsTrusted(DeviceIdentifier deviceId)
        => _trustedDevices.ContainsKey(deviceId.Value);

    /// <inheritdoc/>
    public void TrustDevice(DeviceIdentifier deviceId)
        => TrustDevice(deviceId, deviceId.ToString());

    /// <summary>
    /// デバイスを名前付きで信頼済みリストに追加する。
    /// </summary>
    public void TrustDevice(DeviceIdentifier deviceId, string name)
    {
        var trusted = new TrustedDevice(
            Id: deviceId,
            Name: name,
            TrustedAt: DateTimeOffset.UtcNow,
            LastConnectedAt: null);
        _trustedDevices[deviceId.Value] = trusted;
    }

    /// <inheritdoc/>
    public void RevokeTrust(DeviceIdentifier deviceId)
        => _trustedDevices.TryRemove(deviceId.Value, out _);

    /// <inheritdoc/>
    public IReadOnlyList<TrustedDevice> GetTrustedDevices()
        => _trustedDevices.Values.ToList().AsReadOnly();

    /// <summary>
    /// 指定デバイスの最終接続日時を現在時刻に更新する。
    /// </summary>
    public void UpdateLastConnected(DeviceIdentifier deviceId)
    {
        if (_trustedDevices.TryGetValue(deviceId.Value, out var existing))
        {
            _trustedDevices[deviceId.Value] = existing with
            {
                LastConnectedAt = DateTimeOffset.UtcNow
            };
        }
    }
}
