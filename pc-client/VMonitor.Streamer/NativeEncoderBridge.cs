namespace VMonitor.Streamer;

/// <summary>
/// VMonitor.Encoder.dll (C++ ネイティブ) への公開ブリッジ。
/// VMonitor.UI など外部アセンブリから NativeEncoder にアクセスするために使用する。
/// </summary>
public static class NativeEncoderBridge
{
    /// <summary>VMonitor.Encoder.dll が利用可能かどうかを返す。</summary>
    public static bool IsAvailable => NativeEncoder.IsAvailable;

    /// <summary>
    /// BGRA32 ピクセルデータを H.264 NAL ユニットにエンコードする。
    /// </summary>
    public static byte[]? Encode(
        ReadOnlySpan<byte> bgraData,
        int width, int height,
        int bitrateBps, int fps,
        long timestampUs)
    {
        return NativeEncoder.Encode(bgraData, width, height, bitrateBps, fps, timestampUs);
    }
}
