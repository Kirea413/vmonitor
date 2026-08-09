namespace VMonitor.Core.Models;

/// <summary>トランスポートの接続種別。</summary>
public enum TransportType
{
    /// <summary>Wi-Fi (mDNS + TCP/TLS) 接続。</summary>
    WiFi,

    /// <summary>USB 接続（Android: ADB フォワード、iOS: libimobiledevice）。</summary>
    USB
}

/// <summary>
/// 単一 TCP コネクション上でデータを多重化するチャンネルの識別子。
/// </summary>
public enum ChannelId
{
    /// <summary>エンコード済み映像フレームの転送チャンネル。</summary>
    Video,

    /// <summary>タッチ入力イベントの転送チャンネル。</summary>
    Touch,

    /// <summary>セッション制御メッセージ（確立・終了・設定変更等）のチャンネル。</summary>
    Control
}

/// <summary>
/// 仮想ディスプレイを識別するハンドル。
/// <see cref="Interfaces.IVirtualDisplayDriver.CreateDisplayAsync"/> が返し、
/// 各種操作の引数として使用する。
/// </summary>
public readonly record struct VirtualDisplayHandle(Guid Value)
{
    /// <summary>新しいランダムな VirtualDisplayHandle を生成する。</summary>
    public static VirtualDisplayHandle NewHandle() => new(Guid.NewGuid());

    /// <inheritdoc/>
    public override string ToString() => Value.ToString();
}

/// <summary>仮想ディスプレイから取得した 1 フレームの映像データ。</summary>
public class VideoFrame
{
    /// <summary>フレームを識別するシーケンス番号。</summary>
    public required long SequenceNumber { get; init; }

    /// <summary>フレームのキャプチャ時刻（Unix マイクロ秒）。</summary>
    public required long TimestampUs { get; init; }

    /// <summary>フレームの解像度。</summary>
    public required Resolution Resolution { get; init; }

    /// <summary>生ピクセルデータ（BGRA32 形式）。</summary>
    public required ReadOnlyMemory<byte> Data { get; init; }
}
