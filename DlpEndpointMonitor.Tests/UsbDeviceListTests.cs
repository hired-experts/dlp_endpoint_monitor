using DlpEndpointMonitor.Core;
using Xunit;

namespace DlpEndpointMonitor.Tests;

/// <summary>
/// docs/TEST-PLAN.md section 2.5 (T-LIST-01..15): DeviceWhitelist/DeviceBlacklist matching
/// and dedup, via the storage-dir constructor seam so every test uses its own throwaway
/// directory instead of the real %ProgramData%\DlpEndpointMonitor\.
/// </summary>
public class UsbDeviceListTests
{
    // Never construct DeviceWhitelist()/DeviceBlacklist() with no arguments in a test -
    // that reads/writes the real %ProgramData%\DlpEndpointMonitor\ files. Always pass a
    // fresh temp directory.
    static void WithTempDir(Action<string> test)
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try   { test(dir); }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }

    // ── T-LIST-01/02: disabled list is fail-open (whitelist) / fail-closed-off (blacklist) ──

    [Fact] // T-LIST-01
    public void Whitelist_Disabled_AllowsAnyUsbAndBtDevice()
    {
        WithTempDir(dir =>
        {
            var whitelist = new DeviceWhitelist(dir);

            Assert.False(whitelist.IsEnabled);
            Assert.True(whitelist.IsAllowed("046D", "C52B", "SERIAL1", DeviceKind.Mouse));
            Assert.True(whitelist.IsAllowed("AA:BB:CC:DD:EE:FF", DeviceKind.Keyboard));
        });
    }

    [Fact] // T-LIST-02
    public void Blacklist_Disabled_BlocksNoUsbOrBtDevice()
    {
        WithTempDir(dir =>
        {
            var blacklist = new DeviceBlacklist(dir);

            Assert.False(blacklist.IsEnabled);
            Assert.False(blacklist.IsBlocked("046D", "C52B", "SERIAL1", DeviceKind.Mouse));
            Assert.False(blacklist.IsBlocked("AA:BB:CC:DD:EE:FF", DeviceKind.Keyboard));
        });
    }

    // ── T-LIST-03: enabled + empty list denies everything ──────────────────────────────

    [Fact] // T-LIST-03
    public void Whitelist_EnabledEmpty_DeniesEverything()
    {
        WithTempDir(dir =>
        {
            var whitelist = new DeviceWhitelist(dir);
            whitelist.SetEnabled(true);

            Assert.False(whitelist.IsAllowed("046D", "C52B", "SERIAL1", DeviceKind.Mouse));
            Assert.False(whitelist.IsAllowed("AA:BB:CC:DD:EE:FF", DeviceKind.Keyboard));
        });
    }

    // ── T-LIST-04/05: kind-only entry matches any device of that kind, and only that kind ──

    [Fact] // T-LIST-04, T-LIST-05
    public void Whitelist_KindOnlyEntry_MatchesSameKindOnly()
    {
        WithTempDir(dir =>
        {
            var whitelist = new DeviceWhitelist(dir);
            whitelist.Add(new UsbDeviceEntry(Kind: DeviceKind.Keyboard));
            whitelist.SetEnabled(true);

            Assert.True(whitelist.IsAllowed("046D", "C52B", "SERIAL1", DeviceKind.Keyboard));
            Assert.False(whitelist.IsAllowed("046D", "C52B", "SERIAL1", DeviceKind.Mouse));
        });
    }

    // ── T-LIST-06: vid/pid-only entry matches regardless of kind ───────────────────────

    [Fact] // T-LIST-06
    public void Whitelist_VidPidOnlyEntry_MatchesAnyKind()
    {
        WithTempDir(dir =>
        {
            var whitelist = new DeviceWhitelist(dir);
            whitelist.Add(new UsbDeviceEntry(Vid: "046D", Pid: "C52B"));
            whitelist.SetEnabled(true);

            Assert.True(whitelist.IsAllowed("046D", "C52B", "SERIAL1", DeviceKind.Mouse));
            Assert.True(whitelist.IsAllowed("046D", "C52B", "SERIAL2", DeviceKind.Keyboard));
        });
    }

    // ── T-LIST-07: a Mac-bearing entry never matches via the USB overload ──────────────

    [Fact] // T-LIST-07
    public void Whitelist_MacEntry_NeverMatchesUsbOverload()
    {
        WithTempDir(dir =>
        {
            var whitelist = new DeviceWhitelist(dir);
            whitelist.Add(new UsbDeviceEntry(Mac: "AA:BB:CC:DD:EE:FF"));
            whitelist.SetEnabled(true);

            Assert.False(whitelist.IsAllowed("046D", "C52B", "SERIAL1", DeviceKind.Mouse));
        });
    }

    // ── T-LIST-08: a Vid/Pid/Serial-bearing entry never matches via the BT overload ────

    [Fact] // T-LIST-08
    public void Whitelist_VidPidSerialEntry_NeverMatchesBtOverload()
    {
        WithTempDir(dir =>
        {
            var whitelist = new DeviceWhitelist(dir);
            whitelist.Add(new UsbDeviceEntry(Vid: "046D", Pid: "C52B", Serial: "SERIAL1"));
            whitelist.SetEnabled(true);

            Assert.False(whitelist.IsAllowed("AA:BB:CC:DD:EE:FF", DeviceKind.Mouse));
        });
    }

    // ── T-LIST-09: every field is matched case-insensitively ────────────────────────────

    [Theory] // T-LIST-09
    [InlineData("046d", "c52b", "serial1")]
    [InlineData("046D", "C52B", "SERIAL1")]
    [InlineData("046D", "c52b", "Serial1")]
    public void Whitelist_UsbFields_MatchCaseInsensitively(string vid, string pid, string serial)
    {
        WithTempDir(dir =>
        {
            var whitelist = new DeviceWhitelist(dir);
            whitelist.Add(new UsbDeviceEntry(Vid: "046D", Pid: "C52B", Serial: "SERIAL1"));
            whitelist.SetEnabled(true);

            Assert.True(whitelist.IsAllowed(vid, pid, serial, DeviceKind.Mouse));
        });
    }

    [Fact] // T-LIST-09 (Mac field)
    public void Whitelist_MacField_MatchesCaseInsensitively()
    {
        WithTempDir(dir =>
        {
            var whitelist = new DeviceWhitelist(dir);
            whitelist.Add(new UsbDeviceEntry(Mac: "AA:BB:CC:DD:EE:FF"));
            whitelist.SetEnabled(true);

            Assert.True(whitelist.IsAllowed("aa:bb:cc:dd:ee:ff", DeviceKind.Mouse));
        });
    }

    // ── T-LIST-10: Serial=null on the entry is a wildcard ───────────────────────────────

    [Fact] // T-LIST-10
    public void Whitelist_NullSerialOnEntry_MatchesAnyDeviceSerial()
    {
        WithTempDir(dir =>
        {
            var whitelist = new DeviceWhitelist(dir);
            whitelist.Add(new UsbDeviceEntry(Vid: "046D", Pid: "C52B", Serial: null));
            whitelist.SetEnabled(true);

            Assert.True(whitelist.IsAllowed("046D", "C52B", "ANY-SPECIFIC-SERIAL", DeviceKind.Mouse));
        });
    }

    // ── T-LIST-11: SameDevice dedup ignores Label ───────────────────────────────────────

    [Fact] // T-LIST-11
    public void Add_SameDeviceTwiceWithDifferentLabel_IsNoOpOnSecondAdd()
    {
        WithTempDir(dir =>
        {
            var whitelist = new DeviceWhitelist(dir);
            whitelist.Add(new UsbDeviceEntry(Vid: "046D", Pid: "C52B", Serial: "S1", Kind: DeviceKind.Storage, Label: "First"));
            whitelist.Add(new UsbDeviceEntry(Vid: "046D", Pid: "C52B", Serial: "S1", Kind: DeviceKind.Storage, Label: "Second"));

            var all = whitelist.GetAll();
            Assert.Single(all);
            Assert.Equal("First", all[0].Label);
        });
    }

    // ── T-LIST-12: Kind is part of device identity ──────────────────────────────────────

    [Fact] // T-LIST-12
    public void Add_SameFieldsDifferentKind_BothPersist()
    {
        WithTempDir(dir =>
        {
            var whitelist = new DeviceWhitelist(dir);
            whitelist.Add(new UsbDeviceEntry(Vid: "046D", Pid: "C52B", Serial: "S1", Kind: DeviceKind.Keyboard));
            whitelist.Add(new UsbDeviceEntry(Vid: "046D", Pid: "C52B", Serial: "S1", Kind: DeviceKind.Mouse));

            Assert.Equal(2, whitelist.GetAll().Count);
        });
    }

    // ── T-LIST-13: Set collapses duplicates ─────────────────────────────────────────────

    [Fact] // T-LIST-13
    public void Set_WithDuplicateContainingInput_CollapsesToOne()
    {
        WithTempDir(dir =>
        {
            var whitelist = new DeviceWhitelist(dir);
            var a1 = new UsbDeviceEntry(Vid: "046D", Pid: "C52B", Serial: "S1", Label: "A1");
            var a2 = new UsbDeviceEntry(Vid: "046D", Pid: "C52B", Serial: "S1", Label: "A2");
            var b  = new UsbDeviceEntry(Vid: "1234", Pid: "5678", Serial: "S2");

            whitelist.Set([a1, a2, b]);

            var all = whitelist.GetAll();
            Assert.Equal(2, all.Count);
            Assert.Contains(all, e => e.Vid == "046D" && e.Label == "A1");
            Assert.Contains(all, e => e.Vid == "1234");
        });
    }

    // ── T-LIST-14: partial-filter Remove matches only on the fields specified ──────────

    [Fact] // T-LIST-14
    public void Remove_WithKindOnlyFilter_RemovesEveryEntryOfThatKind()
    {
        WithTempDir(dir =>
        {
            var whitelist = new DeviceWhitelist(dir);
            whitelist.Add(new UsbDeviceEntry(Vid: "046D", Pid: "C52B", Kind: DeviceKind.Keyboard));
            whitelist.Add(new UsbDeviceEntry(Vid: "1234", Pid: "5678", Kind: DeviceKind.Keyboard));
            whitelist.Add(new UsbDeviceEntry(Vid: "AAAA", Pid: "BBBB", Kind: DeviceKind.Mouse));

            whitelist.Remove(kind: DeviceKind.Keyboard);

            var all = whitelist.GetAll();
            Assert.Single(all);
            Assert.Equal(DeviceKind.Mouse, all[0].Kind);
        });
    }

    // ── T-LIST-15: Clear empties the list ───────────────────────────────────────────────

    [Fact] // T-LIST-15
    public void Clear_ThenGetAll_IsEmpty()
    {
        WithTempDir(dir =>
        {
            var whitelist = new DeviceWhitelist(dir);
            whitelist.Add(new UsbDeviceEntry(Vid: "046D", Pid: "C52B"));
            whitelist.Add(new UsbDeviceEntry(Mac: "AA:BB:CC:DD:EE:FF"));

            whitelist.Clear();

            Assert.Empty(whitelist.GetAll());
        });
    }

    // ── Blacklist mirrors the same matching logic through IsBlocked - spot-checked here ──

    [Fact] // T-LIST-04/05/06 mirrored for DeviceBlacklist's IsBlocked
    public void Blacklist_EnabledWithEntry_BlocksMatchingDeviceOnly()
    {
        WithTempDir(dir =>
        {
            var blacklist = new DeviceBlacklist(dir);
            blacklist.Add(new UsbDeviceEntry(Kind: DeviceKind.Keyboard));
            blacklist.SetEnabled(true);

            Assert.True(blacklist.IsBlocked("046D", "C52B", "SERIAL1", DeviceKind.Keyboard));
            Assert.False(blacklist.IsBlocked("046D", "C52B", "SERIAL1", DeviceKind.Mouse));
        });
    }

    [Fact] // T-LIST-09 mirrored for DeviceBlacklist's BT overload
    public void Blacklist_BtMacField_MatchesCaseInsensitively()
    {
        WithTempDir(dir =>
        {
            var blacklist = new DeviceBlacklist(dir);
            blacklist.Add(new UsbDeviceEntry(Mac: "AA:BB:CC:DD:EE:FF"));
            blacklist.SetEnabled(true);

            Assert.True(blacklist.IsBlocked("aa:bb:cc:dd:ee:ff", DeviceKind.Mouse));
        });
    }
}
