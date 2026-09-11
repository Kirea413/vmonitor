using VMonitor.Core.Models;
using VMonitor.Session.Transport;

namespace VMonitor.Tests;

public sealed class TransportFrameLimitsTests
{
    [Fact]
    public void Receive_AcceptsMaximumPayload()
    {
        int length = TransportFrameLimits.ValidateForReceive(
            TransportFrameLimits.MaxPayloadSize,
            (byte)ChannelId.Video);

        Assert.Equal(TransportFrameLimits.MaxPayloadSize, length);
    }

    [Fact]
    public void Receive_RejectsPayloadAboveMaximum()
    {
        Assert.Throws<InvalidDataException>(() =>
            TransportFrameLimits.ValidateForReceive(
                TransportFrameLimits.MaxPayloadSize + 1u,
                (byte)ChannelId.Video));
    }

    [Fact]
    public void Receive_RejectsUnknownChannel()
    {
        Assert.Throws<InvalidDataException>(() =>
            TransportFrameLimits.ValidateForReceive(0, byte.MaxValue));
    }

    [Fact]
    public void Send_RejectsPayloadAboveMaximum()
    {
        Assert.Throws<InvalidDataException>(() =>
            TransportFrameLimits.ValidateForSend(
                TransportFrameLimits.MaxPayloadSize + 1,
                ChannelId.Control));
    }
}
