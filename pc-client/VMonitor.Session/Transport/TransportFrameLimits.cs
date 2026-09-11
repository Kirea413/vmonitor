using VMonitor.Core.Models;

namespace VMonitor.Session.Transport;

/// <summary>全トランスポートで共通に適用するワイヤーフレームの制限。</summary>
internal static class TransportFrameLimits
{
    /// <summary>
    /// 1 フレームの最大ペイロード。4K H.264 のキーフレームにも十分な余裕を持たせつつ、
    /// 相手が指定した長さをそのまま巨大配列へ変換することを防ぐ。
    /// </summary>
    public const int MaxPayloadSize = 32 * 1024 * 1024;

    public static void ValidateForSend(int length, ChannelId channel)
    {
        if (length < 0 || length > MaxPayloadSize)
            throw new InvalidDataException(
                $"ペイロードが上限を超えています: {length} bytes (max {MaxPayloadSize})");

        ValidateChannel(channel);
    }

    public static int ValidateForReceive(uint length, byte channelByte)
    {
        if (length > MaxPayloadSize)
            throw new InvalidDataException(
                $"受信ペイロードが上限を超えています: {length} bytes (max {MaxPayloadSize})");

        var channel = (ChannelId)channelByte;
        ValidateChannel(channel);
        return checked((int)length);
    }

    private static void ValidateChannel(ChannelId channel)
    {
        if (!Enum.IsDefined(channel))
            throw new InvalidDataException($"未知のチャンネル番号です: {(byte)channel}");
    }
}
