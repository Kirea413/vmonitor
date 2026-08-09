namespace VMonitor.Streamer;

public record StreamerStats(
    long FramesEncoded,
    long FramesSent,
    double CurrentFps,
    long CurrentBitrateBps,
    DateTimeOffset? LastEncodedAt,
    long LastFrameEncodeMs = 0);
