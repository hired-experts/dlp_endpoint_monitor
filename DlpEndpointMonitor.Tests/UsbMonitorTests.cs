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

// T-USBMON-05..08: UsbMonitor.ResolveGroupAnchorCore - the pure get-or-create decision behind the
// group-anchor correlation feature (a composite device's SourceEventId), factored out the same way
// ResolveBluetoothBlock is - no locking, no Win32, just the dictionary logic, so it is driven here
// against a plain Dictionary<string, string> instead of a real UsbMonitor instance. The
// release-if-last-sibling cleanup (ReleaseGroupAnchorIfLastSibling) is NOT covered here: it calls
// UsbActions.EnumerateGroupSiblings directly (a live SetupDi enumeration) with no seam to fake "is
// any sibling still connected" without a larger refactor of that call path - out of scope for this
// change per the design's own guidance to skip rather than force an awkward test.
public class UsbMonitorGroupAnchorTests
{
    [Fact]
    public void ResolveGroupAnchorCore_FirstSighting_EstablishesAnchorAndReturnsNull()
    {
        var anchors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        string? result = UsbMonitor.ResolveGroupAnchorCore(anchors, "group-1", "event-A");

        Assert.Null(result);
        Assert.Equal("event-A", anchors["group-1"]);
    }

    [Fact]
    public void ResolveGroupAnchorCore_SecondSighting_ReturnsExistingAnchorNotCandidate()
    {
        var anchors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["group-1"] = "event-A"
        };

        string? result = UsbMonitor.ResolveGroupAnchorCore(anchors, "group-1", "event-B");

        Assert.Equal("event-A", result);
        Assert.Equal("event-A", anchors["group-1"]); // candidate never overwrites the existing anchor
    }

    [Fact]
    public void ResolveGroupAnchorCore_CaseInsensitiveGroupId_TreatsAsSameGroup()
    {
        var anchors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["GROUP-1"] = "event-A"
        };

        string? result = UsbMonitor.ResolveGroupAnchorCore(anchors, "group-1", "event-B");

        Assert.Equal("event-A", result);
    }

    [Fact]
    public void ResolveGroupAnchorCore_NullGroupId_AlwaysReturnsNullAndDoesNotMutateDictionary()
    {
        var anchors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        string? result = UsbMonitor.ResolveGroupAnchorCore(anchors, null, "event-A");

        Assert.Null(result);
        Assert.Empty(anchors);
    }

    [Fact]
    public void ResolveGroupAnchorCore_DistinctGroups_EachGetsItsOwnAnchor()
    {
        var anchors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        string? first = UsbMonitor.ResolveGroupAnchorCore(anchors, "group-1", "event-A");
        string? second = UsbMonitor.ResolveGroupAnchorCore(anchors, "group-2", "event-B");

        Assert.Null(first);
        Assert.Null(second);
        Assert.Equal(2, anchors.Count);
        Assert.Equal("event-A", anchors["group-1"]);
        Assert.Equal("event-B", anchors["group-2"]);
    }
}
