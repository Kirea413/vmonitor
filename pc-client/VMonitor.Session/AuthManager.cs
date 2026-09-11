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
    private readonly Func<IReadOnlyList<TrustedDevice>, Task>? _persistTrustedDevices;
    private readonly ConcurrentDictionary<Guid, TrustedDevice> _trustedDevices = new();
    private readonly SemaphoreSlim _persistGate = new(1, 1);

    /// <summary>
    /// AuthManager を初期化する。
    /// </summary>
    /// <param name="showAuthorizationDialog">
    /// 初回接続時に表示する許可確認ダイアログのコールバック。
    /// true を返した場合は許可、false を返した場合は拒否。
    /// </param>
    public AuthManager(
        Func<DeviceInfo, Task<bool>> showAuthorizationDialog,
        Func<IReadOnlyList<TrustedDevice>, Task>? persistTrustedDevices = null)
    {
        _showAuthorizationDialog = showAuthorizationDialog
            ?? throw new ArgumentNullException(nameof(showAuthorizationDialog));
        _persistTrustedDevices = persistTrustedDevices;
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
        PersistTrustedDevices();
    }

    /// <inheritdoc/>
    public void RevokeTrust(DeviceIdentifier deviceId)
    {
        if (_trustedDevices.TryRemove(deviceId.Value, out _))
            PersistTrustedDevices();
    }

    /// <inheritdoc/>
    public IReadOnlyList<TrustedDevice> GetTrustedDevices()
        => _trustedDevices.Values.ToList().AsReadOnly();

    /// <summary>設定ファイルから読み込んだ信頼済み一覧で初期化する。</summary>
    public void LoadTrustedDevices(IEnumerable<TrustedDevice> devices)
    {
        ArgumentNullException.ThrowIfNull(devices);

        _trustedDevices.Clear();
        foreach (var device in devices)
            _trustedDevices[device.Id.Value] = device;
    }

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
            PersistTrustedDevices();
        }
    }

    private void PersistTrustedDevices()
    {
        var persist = _persistTrustedDevices;
        if (persist is null) return;

        // IAuthManager の変更APIは同期契約なので、保存は設定側のロックへ渡す。
        // 失敗しても接続自体を巻き込まない。
        _ = PersistTrustedDevicesAsync(persist);
    }

    private async Task PersistTrustedDevicesAsync(
        Func<IReadOnlyList<TrustedDevice>, Task> persist)
    {
        await _persistGate.WaitAsync().ConfigureAwait(false);
        try
        {
            // 待ち行列に入った後の最新版を保存する。複数台が同時に信頼された
            // 場合でも、古いスナップショットが後から上書きしない。
            await persist(GetTrustedDevices()).ConfigureAwait(false);
        }
        catch
        {
            // 保存失敗で接続自体は巻き込まない。
        }
        finally
        {
            _persistGate.Release();
        }
    }
}
