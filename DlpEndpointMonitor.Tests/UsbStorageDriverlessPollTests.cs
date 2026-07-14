using DlpEndpointMonitor.Monitors;
using Xunit;

namespace DlpEndpointMonitor.Tests;

// Covers UsbStorageDriverlessPoll.ReconcileCycle - the pure new/seen/evict/first-cycle-baseline
// decision behind the usb_storage_blocked driverless-devnode poll (see
// ai_agent_doc/USB-STORAGE-BLOCKED-POLL-DESIGN.md sections 4.2/7.2), factored out the same way
// UsbMonitor.ResolveGroupAnchorCore is - no Win32, just set logic, driven here against a plain
// HashSet the test constructs itself. The actual SetupDiGetClassDevsByEnumerator enumeration and
// IsMassStorageDevice Compatible-IDs read are hardware-only and not covered here - see
// ai_agent_doc/TEST-PLAN.md section 1.
public class UsbStorageDriverlessPollReconcileTests
{
    static HashSet<string> Ids(params string[] ids) => new(ids, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void ReconcileCycle_FirstCycle_BaselinesSilentlyAndNeverEmits()
    {
        var seen = Ids();
        var present = Ids("USB\\A", "USB\\B");

        var (newlyAppeared, evicted) = UsbStorageDriverlessPoll.ReconcileCycle(seen, present, isFirstCycle: true);

        Assert.Empty(newlyAppeared);
        Assert.Empty(evicted);
        Assert.Equal(2, seen.Count);
        Assert.Contains("USB\\A", seen);
        Assert.Contains("USB\\B", seen);
    }

    [Fact]
    public void ReconcileCycle_SecondCycleSameDevice_NoNewNoEvict()
    {
        var seen = Ids("USB\\A");
        var present = Ids("USB\\A");

        var (newlyAppeared, evicted) = UsbStorageDriverlessPoll.ReconcileCycle(seen, present, isFirstCycle: false);

        Assert.Empty(newlyAppeared);
        Assert.Empty(evicted);
        Assert.Single(seen);
    }

    [Fact]
    public void ReconcileCycle_GenuinelyNewDevice_ReportedAndAddedToSeen()
    {
        var seen = Ids("USB\\A");
        var present = Ids("USB\\A", "USB\\B");

        var (newlyAppeared, evicted) = UsbStorageDriverlessPoll.ReconcileCycle(seen, present, isFirstCycle: false);

        Assert.Equal(["USB\\B"], newlyAppeared);
        Assert.Empty(evicted);
        Assert.Equal(2, seen.Count);
    }

    [Fact]
    public void ReconcileCycle_DeviceDisappears_EvictedFromSeenSet()
    {
        var seen = Ids("USB\\A", "USB\\B");
        var present = Ids("USB\\A");

        var (newlyAppeared, evicted) = UsbStorageDriverlessPoll.ReconcileCycle(seen, present, isFirstCycle: false);

        Assert.Empty(newlyAppeared);
        Assert.Equal(["USB\\B"], evicted);
        Assert.Single(seen);
        Assert.DoesNotContain("USB\\B", seen);
    }

    [Fact]
    public void ReconcileCycle_ReplugAfterEviction_ReportedAsNewAgain()
    {
        var seen = Ids("USB\\A");
        UsbStorageDriverlessPoll.ReconcileCycle(seen, Ids(), isFirstCycle: false); // device disappears
        Assert.Empty(seen);

        var (newlyAppeared, _) = UsbStorageDriverlessPoll.ReconcileCycle(seen, Ids("USB\\A"), isFirstCycle: false);

        Assert.Equal(["USB\\A"], newlyAppeared);
    }

    [Fact]
    public void ReconcileCycle_CaseInsensitiveInstanceId_TreatedAsSameDevice()
    {
        var seen = Ids("USB\\VID_1234&PID_5678\\SERIAL");
        var present = Ids("usb\\vid_1234&pid_5678\\serial");

        var (newlyAppeared, evicted) = UsbStorageDriverlessPoll.ReconcileCycle(seen, present, isFirstCycle: false);

        Assert.Empty(newlyAppeared);
        Assert.Empty(evicted);
    }

    [Fact]
    public void ReconcileCycle_EmptySeenSetNotMistakenForFirstCycle_WhenFlagIsFalse()
    {
        // Regression guard for design doc 7.2: an empty seen-set (e.g. every device evicted by a
        // prior cycle) must NOT silently re-arm baseline mode just because it happens to be empty -
        // the caller's own isFirstCycle flag is authoritative, never inferred from content.
        var seen = Ids();
        var present = Ids("USB\\A");

        var (newlyAppeared, _) = UsbStorageDriverlessPoll.ReconcileCycle(seen, present, isFirstCycle: false);

        Assert.Equal(["USB\\A"], newlyAppeared); // reported as new, not silently swallowed
    }
}

// Covers UsbStorageDriverlessPoll.TryClaimNewArrival - the dedup guard shared between this poll's
// own cycles and UsbMonitor.HandleArrival's inline check (design doc section 4.3 edge case 1).
// Exercised against a real instance rather than a pure function, but needs no Win32/hardware:
// Start() is never called, so the instance's Timer stays null for its whole lifetime and
// TryClaimNewArrival only ever touches the in-memory seen-set.
public class UsbStorageDriverlessPollTryClaimTests
{
    [Fact]
    public void TryClaimNewArrival_FirstClaim_ReturnsTrue()
    {
        using var poll = new UsbStorageDriverlessPoll();
        Assert.True(poll.TryClaimNewArrival("USB\\A"));
    }

    [Fact]
    public void TryClaimNewArrival_AlreadyClaimed_ReturnsFalse()
    {
        using var poll = new UsbStorageDriverlessPoll();
        Assert.True(poll.TryClaimNewArrival("USB\\A"));
        Assert.False(poll.TryClaimNewArrival("USB\\A"));
    }

    [Fact]
    public void TryClaimNewArrival_CaseInsensitive_TreatedAsSameClaim()
    {
        using var poll = new UsbStorageDriverlessPoll();
        Assert.True(poll.TryClaimNewArrival("USB\\A"));
        Assert.False(poll.TryClaimNewArrival("usb\\a"));
    }
}
