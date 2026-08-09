namespace VMonitor.Core.Models;

/// <summary>映像ストリーミングの設定値（永続化対象）。</summary>
public record StreamingSettings(
    int BitrateBps,
    int MaxFps,
    VideoCodec Codec,
    bool AdaptiveBitrateEnabled
)
{
    /// <summary>デフォルトのストリーミング設定。</summary>
    public static readonly StreamingSettings Default = new(
        BitrateBps: 10_000_000,
        MaxFps: 60,
        Codec: VideoCodec.H264,
        AdaptiveBitrateEnabled: true
    );
}

/// <summary>ストリーマーの動作設定。IStreamer.Config として使用される。</summary>
public record StreamerConfig(
    int TargetBitrateBps,
    int MaxFps,
    VideoCodec Codec,
    Resolution TargetResolution
);

/// <summary>映像エンコードに使用するコーデック。</summary>
public enum VideoCodec
{
    /// <summary>H.264 (AVC) コーデック。デフォルト。</summary>
    H264,

    /// <summary>H.265 (HEVC) コーデック。端末が対応している場合に使用する。</summary>
    H265
}
