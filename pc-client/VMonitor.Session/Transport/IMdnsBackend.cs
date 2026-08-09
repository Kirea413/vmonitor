namespace VMonitor.Session.Transport;

/// <summary>
/// mDNS のアドバタイズ・探索を行うバックエンド。
/// 実ネットワークを使う <see cref="MakaretuMdnsBackend"/> と、
/// テスト用の <see cref="InMemoryMdnsBackend"/> の 2 実装を持つ。
/// </summary>
/// <remarks>
/// マルチキャスト DNS は実行環境（ファイアウォール・仮想 NIC・CI コンテナ）に
/// 強く依存するため、探索ロジックの検証をネットワークから切り離せるよう
/// バックエンドを差し替え可能にしている。
/// </remarks>
public interface IMdnsBackend : IDisposable
{
    /// <summary>指定したサービスレコードをネットワークへアドバタイズする。</summary>
    Task AdvertiseAsync(MdnsServiceRecord record, CancellationToken ct = default);

    /// <summary>アドバタイズを停止し、goodbye パケットを送出する。</summary>
    Task UnadvertiseAsync();

    /// <summary>
    /// サービスを探索し、<paramref name="timeoutMs"/> の間に見つかったレコードを返す。
    /// </summary>
    Task<IReadOnlyList<MdnsServiceRecord>> DiscoverAsync(int timeoutMs, CancellationToken ct = default);
}
