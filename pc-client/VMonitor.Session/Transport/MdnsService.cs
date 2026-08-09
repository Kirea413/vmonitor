using System.Net;

namespace VMonitor.Session.Transport;

/// <summary>
/// mDNS (_vmonitor._tcp) サービスの登録と探索を担う。
/// 実際の送受信は差し替え可能な <see cref="IMdnsBackend"/> に委譲する。
/// </summary>
public sealed class MdnsService : IDisposable
{
    /// <summary>vmonitor の mDNS サービスタイプ。</summary>
    public const string ServiceType = "_vmonitor._tcp";

    /// <summary>デフォルトの mDNS ドメイン。</summary>
    public const string Domain = "local";

    private readonly IMdnsBackend _backend;
    private readonly bool _ownsBackend;
    private bool _disposed;

    /// <summary>実ネットワーク（Makaretu）バックエンドでサービスを作る。</summary>
    public MdnsService() : this(new MakaretuMdnsBackend(), ownsBackend: true) { }

    /// <summary>バックエンドを指定してサービスを作る（テスト・代替実装用）。</summary>
    /// <param name="backend">使用するバックエンド。</param>
    /// <param name="ownsBackend">
    /// true の場合、このインスタンスの <see cref="Dispose"/> でバックエンドも破棄する。
    /// </param>
    public MdnsService(IMdnsBackend backend, bool ownsBackend = false)
    {
        _backend     = backend ?? throw new ArgumentNullException(nameof(backend));
        _ownsBackend = ownsBackend;
    }

    /// <summary>使用中のバックエンド。</summary>
    public IMdnsBackend Backend => _backend;

    // ─────────────────────────────────────────
    // 登録（PC クライアント側）
    // ─────────────────────────────────────────

    /// <summary>
    /// <c>_vmonitor._tcp</c> サービスを指定ポートで登録し、アドバタイズを開始する。
    /// </summary>
    /// <param name="port">待ち受けポート番号。</param>
    /// <param name="instanceName">インスタンス名。省略時はマシン名。</param>
    /// <param name="ct">キャンセルトークン。</param>
    public Task RegisterServiceAsync(int port, string? instanceName = null, CancellationToken ct = default)
    {
        if (port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port), port, "ポート番号は 1〜65535 の範囲である必要があります。");

        instanceName ??= Environment.MachineName;

        var record = new MdnsServiceRecord(
            ServiceName: instanceName,
            HostName:    $"{instanceName}.{Domain}",
            Port:        port,
            IPAddress:   IPAddress.Any);   // バックエンド側でローカル NIC のアドレスに解決される

        return _backend.AdvertiseAsync(record, ct);
    }

    /// <summary>登録されたサービスをネットワークから削除する。</summary>
    public Task UnregisterServiceAsync() => _backend.UnadvertiseAsync();

    // ─────────────────────────────────────────
    // 探索（PC 側・テスト用。スマホ側は Flutter が独自に探索する）
    // ─────────────────────────────────────────

    /// <summary>
    /// <c>_vmonitor._tcp</c> サービスを探索し、見つかったレコードの一覧を返す。
    /// </summary>
    /// <param name="timeoutMs">探索を打ち切るまでのミリ秒。</param>
    /// <param name="ct">キャンセルトークン。</param>
    public Task<IReadOnlyList<MdnsServiceRecord>> DiscoverServicesAsync(
        int timeoutMs = 3000,
        CancellationToken ct = default)
        => _backend.DiscoverAsync(timeoutMs, ct);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_ownsBackend)
            _backend.Dispose();
    }
}
