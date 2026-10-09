using VMonitor.Session.Transport;

namespace VMonitor.Tests;

public sealed class AndroidUsbDetectionTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BmaxI10Plus_IsDetectedEvenWhenDriverPreventsOpening(bool openable)
    {
        var tablet = new AoaDevice.UsbDeviceSummary(0x1782, 0x4003,
            "I10_Plus", "BMAX", false, openable, "");
        Assert.True(AoaTransport.IsLikelyAndroid(tablet));
        Assert.True(AoaTransport.IsKnownAndroidVendor(tablet.VendorId));
    }

    [Fact]
    public void NonAndroidPeripheral_IsNotTreatedAsTabletJustBecauseItCanOpen()
    {
        var webcam = new AoaDevice.UsbDeviceSummary(0x04F2, 0xB67E,
            "Camera", "", false, true, "");
        Assert.False(AoaTransport.IsLikelyAndroid(webcam));
    }
}
