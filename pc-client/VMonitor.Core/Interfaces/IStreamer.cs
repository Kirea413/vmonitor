using VMonitor.Core.Models;

namespace VMonitor.Core.Interfaces;

/// <summary>
/// 映像エンコードおよびスマホへの送信を担うストリーマーのインターフェース。
/// Windows Media Foundation MFT を使った GPU ハードウェアエンコードを想定する。
/// </summary>
public interface IStreamer
{
    /// <summary>エンコード設定（ビットレート・fps・コーデック・解像度）。</summary>
    StreamerConfig Config { get; set; }

    /// <summary>フレームソースとトランスポートを指定してストリーミングを開始する。</summary>
    Task StartAsync(IVirtualDisplayDriver source, ITransport transport, CancellationToken ct);

    /// <summary>ストリーミングを停止する。</summary>
    Task StopAsync();

    /// <summary>帯域推定値を受け取り、アダプティブビットレート制御に反映する。</summary>
    void OnBandwidthEstimate(long bitsPerSecond);
}
