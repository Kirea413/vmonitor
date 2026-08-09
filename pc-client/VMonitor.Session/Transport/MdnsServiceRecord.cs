using System.Net;

namespace VMonitor.Session.Transport;

/// <summary>
/// mDNS で検出または登録されたサービスレコードを表すデータモデル。
/// </summary>
/// <param name="ServiceName">サービスのインスタンス名（例: "vmonitor on MyPC"）。</param>
/// <param name="HostName">ホスト名（例: "MyPC.local"）。</param>
/// <param name="Port">サービスが待ち受けるポート番号。</param>
/// <param name="IPAddress">解決済み IP アドレス。</param>
public record MdnsServiceRecord(
    string ServiceName,
    string HostName,
    int Port,
    IPAddress IPAddress)
{
    /// <summary>このレコードからエンドポイントを生成する。</summary>
    public IPEndPoint ToEndPoint() => new(IPAddress, Port);
}
