using Moq;
using VMonitor.Core.Interfaces;
using VMonitor.Core.Models;
using VMonitor.UI.ViewModels;

namespace VMonitor.Tests;

public sealed class ConnectionViewModelMultiDeviceTests
{
    [Fact]
    public void TracksTwoConnectedDevicesIndependently()
    {
        var sessions = new Mock<ISessionManager>();
        using var vm = new ConnectionViewModel(sessions.Object);

        var first = Device("iPhone", DevicePlatform.iOS);
        var second = Device("Tablet", DevicePlatform.Android);

        vm.SetConnected(first, TransportType.WiFi);
        vm.SetConnected(second, TransportType.WiFi);

        Assert.Equal(2, vm.Candidates.Count);
        Assert.All(vm.Candidates, candidate => Assert.True(candidate.IsConnected));
        Assert.Equal("2 台接続中", vm.ConnectionStatus);

        vm.SetDisconnected(first.Id);

        Assert.False(vm.Candidates.Single(c => c.Device.Id == first.Id).IsConnected);
        Assert.True(vm.Candidates.Single(c => c.Device.Id == second.Id).IsConnected);
        Assert.Equal("1 台接続中 — Tablet", vm.ConnectionStatus);
        Assert.True(vm.IsAnythingConnected);
    }

    [Fact]
    public void DiscoveryRemovalDoesNotRemoveAnActiveDevice()
    {
        var sessions = new Mock<ISessionManager>();
        using var vm = new ConnectionViewModel(sessions.Object);
        var device = Device("iPad", DevicePlatform.iOS);

        vm.SetConnected(device, TransportType.WiFi);
        vm.RemoveCandidate(device.Id);

        Assert.Single(vm.Candidates);
        Assert.True(vm.Candidates[0].IsConnected);
    }

    private static DeviceInfo Device(string name, DevicePlatform platform) => new(
        DeviceIdentifier.NewIdentifier(),
        name,
        platform,
        new Resolution(1080, 1920),
        420f);
}
