using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Makaretu.Dns;

namespace VMonitor.Session.Transport;

/// <summary>
/// Makaretu.Dns.Multicast を使った実ネットワーク mDNS バックエンド。
/// Bonjour / dns-sd.exe のインストールなしで動作する。
/// </summary>
public sealed class MakaretuMdnsBackend : IMdnsBackend
{
    private ServiceDiscovery? _advertiser;
    private ServiceProfile?   _profile;
    private bool _disposed;

    /// <inheritdoc/>
    public Task AdvertiseAsync(MdnsServiceRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        var addresses = record.IPAddress.Equals(IPAddress.Any) || record.IPAddress.Equals(IPAddress.Loopback)
            ? GetLocalIPAddresses()
            : new[] { record.IPAddress };

        _profile = new ServiceProfile(
            instanceName: record.ServiceName,
            serviceName:  MdnsService.ServiceType,
            port:         (ushort)record.Port,
            addresses:    addresses);

        _advertiser = new ServiceDiscovery();
        _advertiser.Advertise(_profile);

        // Announce で即座にネットワークへ通知する（探索側の初回発見を速くする）
        _advertiser.Announce(_profile);

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task UnadvertiseAsync()
    {
        if (_advertiser is not null && _profile is not null)
        {
            // goodbye パケット（TTL=0）を送出して探索側から即座に消えるようにする
            try { _advertiser.Unadvertise(_profile); } catch { /* ネットワーク断は無視 */ }
        }

        _advertiser?.Dispose();
        _advertiser = null;
        _profile    = null;

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<MdnsServiceRecord>> DiscoverAsync(int timeoutMs, CancellationToken ct = default)
    {
        var results = new List<MdnsServiceRecord>();
        var seen    = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var gate    = new object();

        using var sd  = new ServiceDiscovery();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);

        var serviceName = new DomainName(MdnsService.ServiceType + "." + MdnsService.Domain);

        void OnInstanceDiscovered(object? sender, ServiceInstanceDiscoveryEventArgs e)
        {
            try
            {
                var instanceName = e.ServiceInstanceName?.ToString();
                if (string.IsNullOrEmpty(instanceName)) return;
                if (!instanceName.Contains(MdnsService.ServiceType, StringComparison.OrdinalIgnoreCase)) return;

                var record = ResolveRecord(instanceName, e);
                if (record is null) return;

                lock (gate)
                {
                    if (seen.Add(record.ServiceName))
                        results.Add(record);
                }
            }
            catch
            {
                // 不正なレコードは黙って読み飛ばす（探索は best-effort）
            }
        }

        sd.ServiceInstanceDiscovered += OnInstanceDiscovered;

        try
        {
            // 該当サービスのインスタンスを明示的に問い合わせる
            sd.QueryServiceInstances(serviceName);

            // 応答を取りこぼさないよう、探索期間中に数回リトライする
            var deadline = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTimeOffset.UtcNow < deadline && !cts.IsCancellationRequested)
            {
                var remaining = (int)(deadline - DateTimeOffset.UtcNow).TotalMilliseconds;
                if (remaining <= 0) break;

                try { await Task.Delay(Math.Min(700, remaining), cts.Token); }
                catch (OperationCanceledException) { break; }

                if (DateTimeOffset.UtcNow < deadline)
                {
                    try { sd.QueryServiceInstances(serviceName); } catch { break; }
                }
            }
        }
        finally
        {
            sd.ServiceInstanceDiscovered -= OnInstanceDiscovered;
        }

        lock (gate) return results.AsReadOnly();
    }

    // ── レコード解決 ─────────────────────────────────────────────────────

    /// <summary>
    /// 探索応答メッセージから SRV（ポート・ホスト名）と A/AAAA（IP アドレス）を
    /// 取り出して <see cref="MdnsServiceRecord"/> を組み立てる。
    /// SRV が無い応答は解決不能として null を返す。
    /// </summary>
    private static MdnsServiceRecord? ResolveRecord(string instanceName, ServiceInstanceDiscoveryEventArgs e)
    {
        var message = e.Message;
        if (message is null) return null;

        // 応答は Answers と AdditionalRecords に分かれて入るため両方を見る
        var records = message.Answers
            .Concat(message.AdditionalRecords)
            .ToList();

        var srv = records.OfType<SRVRecord>()
            .FirstOrDefault(r => string.Equals(r.Name?.ToString(), instanceName, StringComparison.OrdinalIgnoreCase))
            ?? records.OfType<SRVRecord>().FirstOrDefault();

        if (srv is null) return null;

        var hostName = srv.Target?.ToString() ?? string.Empty;

        // SRV の Target ホスト名に対応する A / AAAA レコードを探す
        var address =
            records.OfType<ARecord>()
                   .FirstOrDefault(r => string.Equals(r.Name?.ToString(), hostName, StringComparison.OrdinalIgnoreCase))?.Address
            ?? records.OfType<ARecord>().FirstOrDefault()?.Address
            ?? records.OfType<AAAARecord>()
                   .FirstOrDefault(r => string.Equals(r.Name?.ToString(), hostName, StringComparison.OrdinalIgnoreCase))?.Address
            ?? records.OfType<AAAARecord>().FirstOrDefault()?.Address
            // A/AAAA が同梱されていない場合は応答元アドレスを使う
            ?? e.RemoteEndPoint?.Address;

        if (address is null) return null;

        return new MdnsServiceRecord(
            ServiceName: instanceName,
            HostName:    hostName,
            Port:        srv.Port,
            IPAddress:   address);
    }

    // ── ヘルパー ─────────────────────────────────────────────────────────

    /// <summary>アドバタイズに使うローカル IPv4 アドレスを列挙する。</summary>
    /// <summary>
    /// スマホから届くアドレスを選ぶ。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 以前は動いている全インターフェースのアドレスを無差別に広告していた。
    /// VMware や VirtualBox、Hyper-V、WSL、Tailscale などを入れていると
    /// それらの仮想アダプターも混ざり、スマホがそちらを掴む。
    /// 同じ LAN に居るのに繋がらない、という形で表面化する。
    /// </para>
    /// <para>
    /// 実際に外と通じている口だけを選ぶ。判定にはゲートウェイの有無を使う。
    /// 仮想アダプターの多くはゲートウェイを持たない。
    /// </para>
    /// </remarks>
    internal static IReadOnlyList<IPAddress> GetLocalIPAddresses()
    {
        // ゲートウェイのあるものを先に並べる
        var routable = new List<IPAddress>();
        var others   = new List<IPAddress>();

        foreach (var iface in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (iface.OperationalStatus != OperationalStatus.Up) continue;
            if (iface.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            if (iface.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;

            if (IsVirtualAdapter(iface)) continue;

            var properties = iface.GetIPProperties();

            bool hasGateway = properties.GatewayAddresses
                .Any(g => g.Address is { } a &&
                          a.AddressFamily == AddressFamily.InterNetwork &&
                          !a.Equals(IPAddress.Any));

            foreach (var addr in properties.UnicastAddresses)
            {
                if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;

                // 169.254.x.x は DHCP に失敗したときの仮のアドレス。
                // これを広告しても誰も繋がれない。
                if (IsLinkLocal(addr.Address)) continue;

                (hasGateway ? routable : others).Add(addr.Address);
            }
        }

        var addresses = routable.Concat(others).ToList();

        if (addresses.Count == 0)
            addresses.Add(IPAddress.Loopback);

        return addresses;
    }

    /// <summary>仮想マシンや VPN が作るアダプターか。</summary>
    /// <remarks>
    /// 種別だけでは見分けられない（多くが Ethernet を名乗る）ため、
    /// 名前で判断する。取りこぼしても、ゲートウェイの有無で後ろに回る。
    /// </remarks>
    private static bool IsVirtualAdapter(NetworkInterface iface)
    {
        string text = $"{iface.Description} {iface.Name}";

        string[] markers =
        {
            "VMware", "VirtualBox", "Hyper-V", "Virtual Adapter", "vEthernet",
            "WSL", "Tailscale", "ZeroTier", "TAP-", "Loopback", "Bluetooth",
        };

        return markers.Any(m => text.Contains(m, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>169.254.x.x（DHCP に失敗したときの仮アドレス）か。</summary>
    private static bool IsLinkLocal(IPAddress address)
    {
        var bytes = address.GetAddressBytes();

        return bytes.Length == 4 && bytes[0] == 169 && bytes[1] == 254;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _advertiser?.Dispose();
        _advertiser = null;
        _profile    = null;
    }
}
