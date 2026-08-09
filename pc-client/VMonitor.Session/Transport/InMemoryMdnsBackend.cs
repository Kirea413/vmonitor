using System.Collections.Concurrent;

namespace VMonitor.Session.Transport;

/// <summary>
/// 実マルチキャストを使わないインメモリ mDNS バックエンド。
/// テストおよび mDNS がブロックされた環境でのフォールバックに使う。
/// </summary>
/// <remarks>
/// 同じ <see cref="InMemoryMdnsNetwork"/> を共有するバックエンド同士は、
/// 一方の <see cref="AdvertiseAsync"/> がもう一方の <see cref="DiscoverAsync"/>
/// から見える。ネットワークを共有しなければ完全に独立するため、
/// テストは互いに干渉しない。
/// </remarks>
public sealed class InMemoryMdnsBackend : IMdnsBackend
{
    private readonly InMemoryMdnsNetwork _network;
    private MdnsServiceRecord? _advertised;
    private bool _disposed;

    /// <summary>専用の（他と共有しない）ネットワーク上にバックエンドを作る。</summary>
    public InMemoryMdnsBackend() : this(new InMemoryMdnsNetwork()) { }

    /// <summary>指定した仮想ネットワークに参加するバックエンドを作る。</summary>
    public InMemoryMdnsBackend(InMemoryMdnsNetwork network)
    {
        _network = network ?? throw new ArgumentNullException(nameof(network));
    }

    /// <summary>このバックエンドが参加している仮想ネットワーク。</summary>
    public InMemoryMdnsNetwork Network => _network;

    /// <inheritdoc/>
    public Task AdvertiseAsync(MdnsServiceRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        _advertised = record;
        _network.Publish(record);

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task UnadvertiseAsync()
    {
        if (_advertised is not null)
        {
            _network.Withdraw(_advertised);
            _advertised = null;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<MdnsServiceRecord>> DiscoverAsync(int timeoutMs, CancellationToken ct = default)
        => Task.FromResult(_network.Snapshot());

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _ = UnadvertiseAsync();
    }
}

/// <summary>
/// <see cref="InMemoryMdnsBackend"/> 同士が共有する仮想ネットワーク。
/// アドバタイズされたレコードを保持するだけの単純なレジストリ。
/// </summary>
public sealed class InMemoryMdnsNetwork
{
    private readonly ConcurrentDictionary<string, MdnsServiceRecord> _records =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>レコードを公開する。同名のレコードは上書きされる。</summary>
    public void Publish(MdnsServiceRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        _records[record.ServiceName] = record;
    }

    /// <summary>レコードを取り下げる。</summary>
    public void Withdraw(MdnsServiceRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        _records.TryRemove(record.ServiceName, out _);
    }

    /// <summary>公開中のレコードをすべて消す。</summary>
    public void Clear() => _records.Clear();

    /// <summary>現在公開されているレコードのスナップショットを返す。</summary>
    public IReadOnlyList<MdnsServiceRecord> Snapshot() => _records.Values.ToList().AsReadOnly();
}
