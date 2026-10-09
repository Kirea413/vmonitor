using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Moq;
using VMonitor.Core.Interfaces;
using VMonitor.Core.Models;
using VMonitor.Session;
using VMonitor.UI;
using VMonitor.UI.ViewModels;

namespace VMonitor.Tests;

public sealed class ConnectionApprovalOutcomeTests
{
    [Fact]
    public async Task AndroidHello_IsAcknowledgedBeforeUserConnects()
    {
        using var vm = new ConnectionViewModel(new Mock<ISessionManager>().Object);
        using var logger = new VMonitorLogger(Path.Combine(Path.GetTempPath(), $"vmonitor-ready-{Guid.NewGuid():N}.log"));
        bool authorizationRequested = false;
        var auth = new AuthManager(_ => { authorizationRequested = true; return Task.FromResult(true); });
        var server = new ConnectionServer(vm, null!, auth, logger);
        var sent = new List<string>();
        var transport = new Mock<ITransport>();
        transport.Setup(t => t.SendAsync(It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<ChannelId>(), It.IsAny<CancellationToken>()))
            .Returns((ReadOnlyMemory<byte> data, ChannelId _, CancellationToken _) =>
            {
                sent.Add(Encoding.UTF8.GetString(data.Span));
                return Task.CompletedTask;
            });
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var runtimeType = typeof(ConnectionServer).GetNestedType("ActiveClientSession", BindingFlags.NonPublic)!;
        var runtime = Activator.CreateInstance(runtimeType, nonPublic: true)!;
        var id = DeviceIdentifier.FromKey("test-bmax");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        runtimeType.GetProperty("DeviceId")!.SetValue(runtime, id);
        runtimeType.GetProperty("Cancellation")!.SetValue(runtime, cancellation);
        runtimeType.GetProperty("ReportTransportState")!.SetValue(runtime, true);
        var device = new DeviceInfo(id, "I10_Plus", DevicePlatform.Android, new Resolution(1080, 1920), 420f);
        await using var receiver = ReadyMessages(() =>
        {
            Assert.Contains(sent, m => m.Contains("usb_ready"));
            Assert.False(authorizationRequested);
        }, cancellation.Token).GetAsyncEnumerator(cancellation.Token);
        var negotiation = (Task<(bool Approved, Task<bool>? Pending)>)typeof(ConnectionServer)
            .GetMethod("NegotiateConnectAsync", flags)!.Invoke(server,
                [transport.Object, receiver, device, TransportType.USB, cancellation.Token, false, runtime])!;
        Assert.True((await negotiation).Approved);
        Assert.True(authorizationRequested);
        Assert.Contains(sent, m => m.Contains("connect_response") && m.Contains("true"));
    }

    private static async IAsyncEnumerable<(ChannelId Channel, Memory<byte> Data)> ReadyMessages(
        Action beforeConnect, [EnumeratorCancellation] CancellationToken cancellation)
    {
        await Task.Yield();
        cancellation.ThrowIfCancellationRequested();
        yield return (ChannelId.Control, Encoding.UTF8.GetBytes("{\"type\":\"hello\",\"name\":\"I10_Plus\"}"));
        beforeConnect();
        yield return (ChannelId.Control, Encoding.UTF8.GetBytes("{\"type\":\"connect_request\",\"initiator\":\"phone\"}"));
    }

    [Theory]
    [InlineData(true, "Failed")]
    [InlineData(false, "Denied")]
    public async Task UsbNegotiation_OnlyExplicitRejectionCountsAsDenied(bool sendFails, string expected)
    {
        using var vm = new ConnectionViewModel(new Mock<ISessionManager>().Object);
        using var logger = new VMonitorLogger(Path.Combine(Path.GetTempPath(), $"vmonitor-approval-{Guid.NewGuid():N}.log"));
        var server = new ConnectionServer(vm, null!, null!, logger);
        var transport = new Mock<ITransport>();
        transport.Setup(t => t.ReceiveAsync(It.IsAny<CancellationToken>())).Returns(Rejection);
        transport.Setup(t => t.SendAsync(It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<ChannelId>(), It.IsAny<CancellationToken>()))
            .Returns(sendFails ? Task.FromException(new IOException("USB disconnected")) : Task.CompletedTask);
        var device = new DeviceInfo(DeviceIdentifier.FromKey("test-android"), "Android", DevicePlatform.Android,
            new Resolution(1080, 1920), 420f);
        var task = (Task)typeof(ConnectionServer).GetMethod("RunSessionAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(server, [transport.Object, device, "USB test", TransportType.USB, CancellationToken.None, true, false])!;
        await task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(expected, task.GetType().GetProperty("Result")!.GetValue(task)!.ToString());
    }

    private static async IAsyncEnumerable<(ChannelId Channel, Memory<byte> Data)> Rejection(
        [EnumeratorCancellation] CancellationToken cancellation)
    {
        await Task.Yield();
        cancellation.ThrowIfCancellationRequested();
        yield return (ChannelId.Control, Encoding.UTF8.GetBytes("{\"type\":\"connect_response\",\"accepted\":false}"));
    }
}
