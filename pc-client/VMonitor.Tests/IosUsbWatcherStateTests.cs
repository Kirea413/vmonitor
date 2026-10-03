using System.Reflection;
using System.Text;
using System.Threading.Channels;
using Moq;
using VMonitor.Core.Interfaces;
using VMonitor.Core.Models;
using VMonitor.Session;
using VMonitor.UI;
using VMonitor.UI.ViewModels;

namespace VMonitor.Tests;

public sealed class IosUsbWatcherStateTests
{
    [Fact]
    public async Task UsbNegotiation_WaitsForDeviceFrame_AndRepliesToPingWhileWaitingForButton()
    {
        using var vm = new ConnectionViewModel(new Mock<ISessionManager>().Object);
        using var logger = new VMonitorLogger(Path.Combine(Path.GetTempPath(), $"vmonitor-handshake-{Guid.NewGuid():N}.log"));
        var server = new ConnectionServer(vm, null!, null!, logger);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var incoming = Channel.CreateUnbounded<(ChannelId Channel, Memory<byte> Data)>();
        var outgoing = Channel.CreateUnbounded<string>();
        var transport = new Mock<ITransport>();
        transport.Setup(t => t.SendAsync(It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<ChannelId>(), It.IsAny<CancellationToken>()))
            .Returns((ReadOnlyMemory<byte> data, ChannelId _, CancellationToken _) =>
            {
                outgoing.Writer.TryWrite(Encoding.UTF8.GetString(data.Span));
                return Task.CompletedTask;
            });
        await using var receiver = incoming.Reader.ReadAllAsync(cancellation.Token).GetAsyncEnumerator(cancellation.Token);
        var id = DeviceIdentifier.FromKey("test-ios-usb");
        var device = new DeviceInfo(id, "iPhone", DevicePlatform.iOS, new Resolution(1080, 1920), 420f);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var runtimeType = typeof(ConnectionServer).GetNestedType("ActiveClientSession", BindingFlags.NonPublic)!;
        var runtime = Activator.CreateInstance(runtimeType, nonPublic: true)!;
        runtimeType.GetProperty("DeviceId")!.SetValue(runtime, id);
        runtimeType.GetProperty("Cancellation")!.SetValue(runtime, cancellation);
        var negotiation = (Task<(bool Approved, Task<bool>? Pending)>)typeof(ConnectionServer)
            .GetMethod("NegotiateConnectAsync", flags)!.Invoke(server,
                [transport.Object, receiver, device, TransportType.USB, cancellation.Token, true, runtime])!;

        Assert.False(outgoing.Reader.TryRead(out _));
        Assert.False(negotiation.IsCompleted);
        incoming.Writer.TryWrite((ChannelId.Control, Encoding.UTF8.GetBytes("{\"type\":\"hello\"}")));
        Assert.Contains("connect_request", await outgoing.Reader.ReadAsync(cancellation.Token));
        incoming.Writer.TryWrite((ChannelId.Control, Encoding.UTF8.GetBytes("{\"type\":\"ping\",\"t\":42}")));
        Assert.Contains("\"pong\"", await outgoing.Reader.ReadAsync(cancellation.Token));
        incoming.Writer.TryWrite((ChannelId.Control, Encoding.UTF8.GetBytes("{\"type\":\"connect_response\",\"accepted\":true}")));
        Assert.True((await negotiation.WaitAsync(cancellation.Token)).Approved);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Watcher_SkippingAnotherConnection_DoesNotClearItsState(bool connected, bool busy)
    {
        using var vm = new ConnectionViewModel(new Mock<ISessionManager>().Object);
        using var logger = new VMonitorLogger(Path.Combine(Path.GetTempPath(), $"vmonitor-watcher-{Guid.NewGuid():N}.log"));
        var server = new ConnectionServer(vm, null!, null!, logger);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(ConnectionServer).GetMethod("SetOutboundState", flags)!
            .Invoke(server, ["existing connection", connected, busy]);

        int changes = 0;
        server.OutboundStateChanged += (_, _) => Interlocked.Increment(ref changes);
        using var cancellation = new CancellationTokenSource();
        var watcher = (Task)typeof(ConnectionServer).GetMethod("StartIosUsbWatcherAsync", flags)!
            .Invoke(server, [cancellation.Token])!;

        // 起動待ち1.5秒と、見送り分岐の1秒待ちを通過させる。
        await Task.Delay(2800);
        cancellation.Cancel();
        await watcher.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(connected, server.IsOutboundConnected);
        Assert.Equal(busy, server.IsOutboundBusy);
        Assert.Equal("existing connection", server.OutboundStatus);
        Assert.Equal(0, changes);
    }
}
