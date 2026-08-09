using System.Net;
using VMonitor.Core.Models;

namespace VMonitor.Core.Interfaces;

/// <summary>
/// Wi-Fi (mDNS + TCP/TLS) と USB (ADB トンネル) を統一的に扱うトランスポートインターフェース。
/// 単一 TCP コネクション上で ChannelId によって映像・タッチ・制御を多重化する。
/// </summary>
public interface ITransport
{
    /// <summary>トランスポート種別（WiFi / USB）。</summary>
    TransportType Type { get; }

    /// <summary>指定エンドポイントに接続する。</summary>
    Task ConnectAsync(EndPoint endpoint, CancellationToken ct);

    /// <summary>接続を切断する。</summary>
    Task DisconnectAsync();

    /// <summary>指定チャンネルにデータを送信する。</summary>
    Task SendAsync(ReadOnlyMemory<byte> data, ChannelId channel, CancellationToken ct);

    /// <summary>受信データを非同期ストリームとして返す。各要素は (チャンネルID, データ) のタプル。</summary>
    IAsyncEnumerable<(ChannelId Channel, Memory<byte> Data)> ReceiveAsync(CancellationToken ct);

    /// <summary>現在の推定帯域幅（bps）。</summary>
    long EstimatedBandwidthBps { get; }
}
