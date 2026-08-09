using FsCheck;
using FsCheck.Xunit;
using VMonitor.Core.Models;
using VMonitor.Session;

namespace VMonitor.Tests;

// Feature: vmonitor, Property 20: デバイス信頼管理のライフサイクル

/// <summary>
/// Property 20: デバイス信頼管理のライフサイクル
/// Validates: Requirements 8.2, 8.3, 8.5
///
/// 任意のデバイス識別子に対して、TrustDevice(id) 呼び出し後に IsTrusted(id) は true を返し、
/// RevokeTrust(id) 呼び出し後には false を返さなければならない（追加→確認→削除サイクル）。
/// </summary>
public class DeviceTrustLifecyclePropertyTests
{
    /// <summary>
    /// Property 20: 任意のデバイス識別子に対して、
    /// TrustDevice(id) の後は IsTrusted(id) が true を返し（Requirements 8.2, 8.3）、
    /// RevokeTrust(id) の後は IsTrusted(id) が false を返す（Requirements 8.5）
    /// ことを検証する（追加→確認→削除サイクル）。
    ///
    /// パラメーター:
    ///   rawGuid - FsCheck が生成する Guid 値。DeviceIdentifier にラップして使用する。
    /// </summary>
    [Property(MaxTest = 100)]
    public bool TrustLifecycleAddCheckRevoke(Guid rawGuid)
    {
        // テストごとに独立した AuthManager インスタンスを使用する
        var authManager = new AuthManager(_ => Task.FromResult(true));
        var deviceId = new DeviceIdentifier(rawGuid);

        // 前提: 初期状態では信頼されていないこと
        if (authManager.IsTrusted(deviceId))
            return true; // 前提条件が崩れる場合はスキップ（実際には起こらない）

        // 1. TrustDevice 後は IsTrusted が true を返すこと（Requirements 8.2, 8.3）
        authManager.TrustDevice(deviceId);
        if (!authManager.IsTrusted(deviceId))
            return false;

        // 2. RevokeTrust 後は IsTrusted が false を返すこと（Requirements 8.5）
        authManager.RevokeTrust(deviceId);
        if (authManager.IsTrusted(deviceId))
            return false;

        return true;
    }

    /// <summary>
    /// Property 20（追加検証）: 信頼済みリストへの追加・削除がリスト自体に正しく反映されること。
    ///
    /// TrustDevice 後は GetTrustedDevices() にそのデバイスが含まれ、
    /// RevokeTrust 後には含まれなくなることを確認する。
    /// </summary>
    [Property(MaxTest = 100)]
    public bool TrustListConsistency(Guid rawGuid)
    {
        var authManager = new AuthManager(_ => Task.FromResult(true));
        var deviceId = new DeviceIdentifier(rawGuid);

        // TrustDevice 後はリストにエントリが含まれること
        authManager.TrustDevice(deviceId);
        var afterTrust = authManager.GetTrustedDevices();
        if (!afterTrust.Any(d => d.Id == deviceId))
            return false;

        // RevokeTrust 後はリストからエントリが除去されること
        authManager.RevokeTrust(deviceId);
        var afterRevoke = authManager.GetTrustedDevices();
        if (afterRevoke.Any(d => d.Id == deviceId))
            return false;

        return true;
    }
}
