using System.Net;
using FsCheck;
using FsCheck.Xunit;
using VMonitor.Session.Transport;

namespace VMonitor.Tests;

// Feature: vmonitor, Property 2: デバイス探索のラウンドトリップ

/// <summary>
/// Property 2: デバイス探索のラウンドトリップ
/// Validates: Requirements 2.1
///
/// 任意の有効な mDNS サービスレコードに対して、discover() の結果に
/// そのエントリが含まれなければならない。
/// </summary>
public class MdnsDiscoveryPropertyTests
{
    /// <summary>
    /// Property 2: 任意の有効な mDNS サービスレコードをアドバタイズした後、
    /// 同じネットワーク上の探索結果にそのレコードが含まれなければならない。
    ///
    /// パラメーター:
    ///   rawServiceName - FsCheck が生成するサービス名文字列
    ///   rawPort        - ポート番号に射影する整数（Math.Abs(...) % 65535 + 1 で正規化）
    ///   octetA/B/C/D   - IPv4 アドレスの 4 オクテット（各 1〜254 に正規化）
    ///
    /// 実マルチキャストはファイアウォールや NIC 構成に左右されるため、
    /// 探索ロジックの検証にはインメモリバックエンドを使う。
    /// テストごとに独立した仮想ネットワークを作るので相互干渉しない。
    /// </summary>
    [Property(MaxTest = 100)]
    public bool DiscoveryRoundTrip(
        NonEmptyString rawServiceName,
        int rawPort,
        byte octetA,
        byte octetB,
        byte octetC,
        byte octetD)
    {
        // ポートを有効な範囲（1〜65535）に正規化する
        int port = Math.Abs(rawPort) % 65535 + 1;

        // サービス名から制御文字や '.' を除き、有効なインスタンス名を作成する
        string serviceName = SanitizeServiceName(rawServiceName.Get);
        if (string.IsNullOrEmpty(serviceName))
            return true; // スキップ（サービス名が無効な場合は前提条件から除外）

        // IPv4 アドレスのオクテットを有効範囲に正規化する（0 と 255 を避ける）
        int a = Math.Max(1, Math.Min(254, (int)octetA));
        int b = (int)octetB;
        int c = (int)octetC;
        int d = Math.Max(1, Math.Min(254, (int)octetD));
        var ip = IPAddress.Parse($"{a}.{b}.{c}.{d}");

        // このテスト実行専用の仮想ネットワークを用意する（テスト間の独立性）
        var network = new InMemoryMdnsNetwork();

        var record = new MdnsServiceRecord(
            ServiceName: $"{serviceName}.{MdnsService.ServiceType}.{MdnsService.Domain}.",
            HostName: $"{serviceName}.local",
            Port: port,
            IPAddress: ip);

        // アドバタイズ側と探索側は別インスタンスだが同じネットワークを共有する
        using var advertiser = new MdnsService(new InMemoryMdnsBackend(network), ownsBackend: true);
        advertiser.Backend.AdvertiseAsync(record).GetAwaiter().GetResult();

        using var discoverer = new MdnsService(new InMemoryMdnsBackend(network), ownsBackend: true);
        var results = discoverer.DiscoverServicesAsync(timeoutMs: 100)
                                .GetAwaiter().GetResult();

        // 登録したレコードが探索結果に含まれること
        return results.Any(r =>
            r.ServiceName == record.ServiceName &&
            r.HostName == record.HostName &&
            r.Port == record.Port &&
            r.IPAddress.Equals(record.IPAddress));
    }

    /// <summary>
    /// Property 2 の対偶: 取り下げた（Unadvertise した）レコードは
    /// 以降の探索結果に含まれてはならない。
    /// </summary>
    [Property(MaxTest = 100)]
    public bool WithdrawnServiceIsNotDiscovered(NonEmptyString rawServiceName, int rawPort)
    {
        string serviceName = SanitizeServiceName(rawServiceName.Get);
        if (string.IsNullOrEmpty(serviceName))
            return true;

        int port = Math.Abs(rawPort) % 65535 + 1;

        var network = new InMemoryMdnsNetwork();
        var record = new MdnsServiceRecord(
            ServiceName: $"{serviceName}.{MdnsService.ServiceType}.{MdnsService.Domain}.",
            HostName: $"{serviceName}.local",
            Port: port,
            IPAddress: IPAddress.Parse("192.168.1.10"));

        var backend = new InMemoryMdnsBackend(network);
        backend.AdvertiseAsync(record).GetAwaiter().GetResult();
        backend.UnadvertiseAsync().GetAwaiter().GetResult();

        using var discoverer = new MdnsService(new InMemoryMdnsBackend(network), ownsBackend: true);
        var results = discoverer.DiscoverServicesAsync(timeoutMs: 100).GetAwaiter().GetResult();

        return !results.Any(r => r.ServiceName == record.ServiceName);
    }

    /// <summary>
    /// サービス名から制御文字・空白・ドット（DNS ラベル区切り文字）を除去し、
    /// mDNS インスタンス名として有効な文字列を返す。
    /// </summary>
    private static string SanitizeServiceName(string raw)
    {
        var chars = raw
            .Where(c => !char.IsControl(c) && c != '.' && c != '\0')
            .ToArray();
        return new string(chars).Trim();
    }
}
