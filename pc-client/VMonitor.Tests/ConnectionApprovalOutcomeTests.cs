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
