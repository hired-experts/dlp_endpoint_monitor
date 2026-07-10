using DlpEndpointMonitor.Monitors;
using Xunit;

namespace DlpEndpointMonitor.Tests;

// T-USBMON-01..04: UsbMonitor.ResolveBluetoothBlock - the pure unpair-then-disable-fallback
// decision factored out of BlockDevice's Bluetooth branch. Real hardware confirmed the fallback
// is not just a defensive no-op: the legacy BluetoothRemoveDevice API can fail (ERROR_NOT_FOUND)
// even against an address confirmed present in Windows' own remembered-devices registry (Windows
// Settings' own "Remove device" succeeded against the identical address where this API did not),
// so a real device now relies on this fallback to be blocked at all. Neither RemovePairing nor
// DisableDevice is invoked here - fake delegates stand in for both.
public class UsbMonitorTests
{
    [Fact]
    public void ResolveBluetoothBlock_UnpairSucceeds_ReturnsOkWithNoDisabledIdAndNeverCallsDisable()
    {
        bool disableCalled = false;
        var result = UsbMonitor.ResolveBluetoothBlock(
            tryUnpair: () => (true, null),
            tryDisable: () => { disableCalled = true; return (true, null); },
            disableNodeId: "BTHLE\\DEV_D15799812BE9");

        Assert.True(result.ok);
        Assert.Null(result.error);
        Assert.Null(result.disabledId);
        Assert.False(disableCalled);
    }

    [Fact]
    public void ResolveBluetoothBlock_UnpairFailsDisableSucceeds_ReturnsOkWithDisabledId()
    {
        var result = UsbMonitor.ResolveBluetoothBlock(
            tryUnpair: () => (false, "BluetoothRemoveDevice failed: 0x00000490"),
            tryDisable: () => (true, null),
            disableNodeId: "BTHLE\\DEV_D15799812BE9");

        Assert.True(result.ok);
        Assert.Null(result.error);
        Assert.Equal("BTHLE\\DEV_D15799812BE9", result.disabledId);
    }

    [Fact]
    public void ResolveBluetoothBlock_BothFail_ReturnsAggregatedErrorAndNoDisabledId()
    {
        var result = UsbMonitor.ResolveBluetoothBlock(
            tryUnpair: () => (false, "unpair-reason"),
            tryDisable: () => (false, "disable-reason"),
            disableNodeId: "BTHLE\\DEV_D15799812BE9");

        Assert.False(result.ok);
        Assert.NotNull(result.error);
        Assert.Contains("unpair-reason", result.error);
        Assert.Contains("disable-reason", result.error);
        Assert.Null(result.disabledId);
    }

    [Fact]
    public void ResolveBluetoothBlock_UnpairFails_AlwaysTriesDisableFallback()
    {
        bool disableCalled = false;
        UsbMonitor.ResolveBluetoothBlock(
            tryUnpair: () => (false, "unpair-reason"),
            tryDisable: () => { disableCalled = true; return (false, "disable-reason"); },
            disableNodeId: "BTHLE\\DEV_D15799812BE9");

        Assert.True(disableCalled);
    }
}
