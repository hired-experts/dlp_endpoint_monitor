using System.Text.Json;
using DlpEndpointMonitor.Commands;
using DlpEndpointMonitor.Core;
using DlpEndpointMonitor.Handlers.Windows;
using Xunit;

namespace DlpEndpointMonitor.Tests;

/// <summary>
/// WindowsUsbProtectionHandler previously had zero automated coverage. Uses real
/// DeviceWhitelist/DeviceBlacklist instances against throwaway temp directories (never the
/// parameterless constructor - would touch the real %ProgramData%\DlpEndpointMonitor\),
/// mirroring WindowsClipboardProtectionHandlerTests.cs's WithTempDir seam. Every assertion checks
/// exact post-mutation list state (IsEnabled / GetAll()) and exact reconcile call COUNTS, per
/// each of applyPolicy/restoreDevices independently - a double-fire, or a fire on the wrong
/// delegate, is itself a bug class this suite exists to catch.
/// </summary>
public class WindowsUsbProtectionHandlerTests
{
    static void WithTempDir(Action<string> test)
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try   { test(dir); }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }

    sealed class CallCounter
    {
        int _count;
        public void Increment() => Interlocked.Increment(ref _count);
        public int Count => Volatile.Read(ref _count);
    }

    static void WithHandler(Action<WindowsUsbProtectionHandler, DeviceWhitelist, DeviceBlacklist, CallCounter, CallCounter> test) =>
        WithTempDir(wDir => WithTempDir(bDir =>
        {
            var whitelist      = new DeviceWhitelist(wDir);
            var blacklist      = new DeviceBlacklist(bDir);
            var applyPolicy    = new CallCounter();
            var restoreDevices = new CallCounter();
            var handler        = new WindowsUsbProtectionHandler(whitelist, blacklist, applyPolicy.Increment, restoreDevices.Increment);
            test(handler, whitelist, blacklist, applyPolicy, restoreDevices);
        }));

    // Reconcile delegates fire via Task.Run (fire-and-forget) - poll instead of assuming they
    // already ran by the time Handle() returns.
    static void WaitUntil(Func<bool> condition, int timeoutMs = 2000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < deadline) Thread.Sleep(5);
    }

    static string CaptureStdout(Action act)
    {
        var originalOut = Console.Out;
        var writer = new StringWriter();
        Console.SetOut(writer);
        try   { act(); }
        finally { Console.SetOut(originalOut); }
        return writer.ToString();
    }

    static JsonElement ParseLastLine(string output)
    {
        string line = output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Last();
        return JsonDocument.Parse(line).RootElement;
    }

    // ── Whitelist ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void WhitelistEnable_DisablesBlacklist_EnablesWhitelist_FiresApplyPolicyOnceOnly()
    {
        WithHandler((handler, whitelist, blacklist, applyPolicy, restoreDevices) =>
        {
            blacklist.SetEnabled(true);

            string output = CaptureStdout(() => handler.Handle(new DeviceWhitelistEnableCmd("1")));

            Assert.False(blacklist.IsEnabled);
            Assert.True(whitelist.IsEnabled);

            WaitUntil(() => applyPolicy.Count >= 1);
            Assert.Equal(1, applyPolicy.Count);
            Assert.Equal(0, restoreDevices.Count);

            var reply = ParseLastLine(output);
            Assert.True(reply.GetProperty("ok").GetBoolean());
        });
    }

    [Fact]
    public void WhitelistDisable_DisablesWhitelist_FiresRestoreDevicesOnceOnly()
    {
        WithHandler((handler, whitelist, blacklist, applyPolicy, restoreDevices) =>
        {
            whitelist.SetEnabled(true);

            CaptureStdout(() => handler.Handle(new DeviceWhitelistDisableCmd("1")));

            Assert.False(whitelist.IsEnabled);
            WaitUntil(() => restoreDevices.Count >= 1);
            Assert.Equal(1, restoreDevices.Count);
            Assert.Equal(0, applyPolicy.Count);
        });
    }

    [Fact]
    public void WhitelistClear_DisablesAndEmptiesWhitelist_FiresRestoreDevicesOnceOnly()
    {
        WithHandler((handler, whitelist, blacklist, applyPolicy, restoreDevices) =>
        {
            whitelist.SetEnabled(true);
            whitelist.Add(new UsbDeviceEntry(Vid: "1234", Pid: "abcd"));

            CaptureStdout(() => handler.Handle(new DeviceWhitelistClearCmd("1")));

            // Both must hold - a bare Clear() without also disabling would leave an
            // enabled-but-empty (deny-all) whitelist, per the handler's own comment.
            Assert.False(whitelist.IsEnabled);
            Assert.Empty(whitelist.GetAll());

            WaitUntil(() => restoreDevices.Count >= 1);
            Assert.Equal(1, restoreDevices.Count);
            Assert.Equal(0, applyPolicy.Count);
        });
    }

    [Fact]
    public void WhitelistAdd_AddsEntry_FiresRestoreDevicesOnceOnly()
    {
        WithHandler((handler, whitelist, blacklist, applyPolicy, restoreDevices) =>
        {
            CaptureStdout(() => handler.Handle(new DeviceWhitelistAddCmd("1", "1234", "abcd", Label: "Test Device")));

            var entries = whitelist.GetAll();
            Assert.Single(entries);
            Assert.Equal("1234", entries[0].Vid);
            Assert.Equal("abcd", entries[0].Pid);
            Assert.Equal("Test Device", entries[0].Label);

            WaitUntil(() => restoreDevices.Count >= 1);
            Assert.Equal(1, restoreDevices.Count);
            Assert.Equal(0, applyPolicy.Count);
        });
    }

    // Removing a whitelist entry TIGHTENS policy - a device that relied on it may now no longer
    // be allowed - so this must trigger applyPolicy (re-block), not restoreDevices. This is the
    // mirror image of DeviceBlacklistRemoveCmd (removing a blacklist entry LOOSENS policy, so it
    // correctly fires restoreDevices) - the two are asymmetric on purpose because tightening and
    // loosening need opposite reconcile actions, not because one of them is allowed to fire none.
    [Fact]
    public void WhitelistRemove_RemovesEntry_FiresApplyPolicyOnceOnly()
    {
        WithHandler((handler, whitelist, blacklist, applyPolicy, restoreDevices) =>
        {
            whitelist.Add(new UsbDeviceEntry(Vid: "1234", Pid: "abcd"));

            string output = CaptureStdout(() => handler.Handle(new DeviceWhitelistRemoveCmd("1", "1234", "abcd")));

            Assert.Empty(whitelist.GetAll());
            var reply = ParseLastLine(output);
            Assert.True(reply.GetProperty("ok").GetBoolean());

            WaitUntil(() => applyPolicy.Count >= 1);
            Assert.Equal(1, applyPolicy.Count);
            Assert.Equal(0, restoreDevices.Count);
        });
    }

    [Fact]
    public void WhitelistSet_ReplacesEntries_FiresBothRestoreDevicesAndApplyPolicyOnceEach()
    {
        WithHandler((handler, whitelist, blacklist, applyPolicy, restoreDevices) =>
        {
            whitelist.Add(new UsbDeviceEntry(Vid: "0000", Pid: "0000"));

            CaptureStdout(() => handler.Handle(new DeviceWhitelistSetCmd("1",
                [new DeviceEntryDto("1234", "abcd", null, null, DeviceKind.Storage, "Kept")])));

            var entries = whitelist.GetAll();
            Assert.Single(entries);
            Assert.Equal("1234", entries[0].Vid);
            Assert.Equal(DeviceKind.Storage, entries[0].Kind);

            WaitUntil(() => restoreDevices.Count >= 1 && applyPolicy.Count >= 1);
            Assert.Equal(1, restoreDevices.Count);
            Assert.Equal(1, applyPolicy.Count);
        });
    }

    // ── Blacklist ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void BlacklistEnable_DisablesWhitelist_EnablesBlacklist_FiresApplyPolicyOnceOnly()
    {
        WithHandler((handler, whitelist, blacklist, applyPolicy, restoreDevices) =>
        {
            whitelist.SetEnabled(true);

            CaptureStdout(() => handler.Handle(new DeviceBlacklistEnableCmd("1")));

            Assert.False(whitelist.IsEnabled);
            Assert.True(blacklist.IsEnabled);

            WaitUntil(() => applyPolicy.Count >= 1);
            Assert.Equal(1, applyPolicy.Count);
            Assert.Equal(0, restoreDevices.Count);
        });
    }

    [Fact]
    public void BlacklistDisable_DisablesBlacklist_FiresRestoreDevicesOnceOnly()
    {
        WithHandler((handler, whitelist, blacklist, applyPolicy, restoreDevices) =>
        {
            blacklist.SetEnabled(true);

            CaptureStdout(() => handler.Handle(new DeviceBlacklistDisableCmd("1")));

            Assert.False(blacklist.IsEnabled);
            WaitUntil(() => restoreDevices.Count >= 1);
            Assert.Equal(1, restoreDevices.Count);
            Assert.Equal(0, applyPolicy.Count);
        });
    }

    // Ground truth (Handlers/Windows/WindowsUsbProtectionHandler.cs): DeviceBlacklistClearCmd
    // does NOT call SetEnabled(false), unlike DeviceWhitelistClearCmd - an enabled-but-empty
    // blacklist is allow-all (IsBlocked matches nothing), which is already the loosest state, so
    // there is no equivalent need to force-disable it. This test asserts the enabled flag survives
    // Clear() unchanged, not the whitelist's disable-on-clear behavior.
    [Fact]
    public void BlacklistClear_EmptiesEntriesOnly_LeavesEnabledFlagUnchanged_FiresRestoreDevicesOnceOnly()
    {
        WithHandler((handler, whitelist, blacklist, applyPolicy, restoreDevices) =>
        {
            blacklist.SetEnabled(true);
            blacklist.Add(new UsbDeviceEntry(Vid: "1234", Pid: "abcd"));

            CaptureStdout(() => handler.Handle(new DeviceBlacklistClearCmd("1")));

            Assert.True(blacklist.IsEnabled); // unchanged - divergence from whitelist's Clear
            Assert.Empty(blacklist.GetAll());

            WaitUntil(() => restoreDevices.Count >= 1);
            Assert.Equal(1, restoreDevices.Count);
            Assert.Equal(0, applyPolicy.Count);
        });
    }

    [Fact]
    public void BlacklistAdd_AddsEntry_FiresApplyPolicyOnceOnly()
    {
        WithHandler((handler, whitelist, blacklist, applyPolicy, restoreDevices) =>
        {
            CaptureStdout(() => handler.Handle(new DeviceBlacklistAddCmd("1", "1234", "abcd", Label: "Blocked Device")));

            var entries = blacklist.GetAll();
            Assert.Single(entries);
            Assert.Equal("1234", entries[0].Vid);
            Assert.Equal("Blocked Device", entries[0].Label);

            WaitUntil(() => applyPolicy.Count >= 1);
            Assert.Equal(1, applyPolicy.Count);
            Assert.Equal(0, restoreDevices.Count);
        });
    }

    [Fact]
    public void BlacklistRemove_RemovesEntry_FiresRestoreDevicesOnceOnly()
    {
        WithHandler((handler, whitelist, blacklist, applyPolicy, restoreDevices) =>
        {
            blacklist.Add(new UsbDeviceEntry(Vid: "1234", Pid: "abcd"));

            CaptureStdout(() => handler.Handle(new DeviceBlacklistRemoveCmd("1", "1234", "abcd")));

            Assert.Empty(blacklist.GetAll());
            WaitUntil(() => restoreDevices.Count >= 1);
            Assert.Equal(1, restoreDevices.Count);
            Assert.Equal(0, applyPolicy.Count);
        });
    }

    [Fact]
    public void BlacklistSet_ReplacesEntries_FiresBothRestoreDevicesAndApplyPolicyOnceEach()
    {
        WithHandler((handler, whitelist, blacklist, applyPolicy, restoreDevices) =>
        {
            blacklist.Add(new UsbDeviceEntry(Vid: "0000", Pid: "0000"));

            CaptureStdout(() => handler.Handle(new DeviceBlacklistSetCmd("1",
                [new DeviceEntryDto("1234", "abcd", null, null, DeviceKind.Storage, "Kept")])));

            var entries = blacklist.GetAll();
            Assert.Single(entries);
            Assert.Equal("1234", entries[0].Vid);
            Assert.Equal(DeviceKind.Storage, entries[0].Kind);

            WaitUntil(() => restoreDevices.Count >= 1 && applyPolicy.Count >= 1);
            Assert.Equal(1, restoreDevices.Count);
            Assert.Equal(1, applyPolicy.Count);
        });
    }

    // ── Protection status / Get (unchanged shape - not part of the CommandReply refactor,
    // covered here only for basic sanity since this file previously had zero coverage) ────────

    [Fact]
    public void ProtectionStatus_ReportsConflictWhenBothEnabled()
    {
        WithHandler((handler, whitelist, blacklist, applyPolicy, restoreDevices) =>
        {
            whitelist.SetEnabled(true);
            blacklist.SetEnabled(true);

            string output = CaptureStdout(() => handler.Handle(new DeviceProtectionStatusCmd("1")));
            var status = ParseLastLine(output);

            Assert.False(status.GetProperty("ok").GetBoolean());
            Assert.Equal("conflict", status.GetProperty("mode").GetString());
        });
    }

    [Fact]
    public void WhitelistGet_ReportsExactEntryAdded()
    {
        WithHandler((handler, whitelist, blacklist, applyPolicy, restoreDevices) =>
        {
            CaptureStdout(() => handler.Handle(new DeviceWhitelistAddCmd("1", "1234", "abcd", Label: "Test Device")));

            string output = CaptureStdout(() => handler.Handle(new DeviceWhitelistGetCmd("2")));
            var get = ParseLastLine(output);
            var entries = get.GetProperty("entries").EnumerateArray().ToList();

            Assert.Single(entries);
            Assert.Equal("1234", entries[0].GetProperty("vid").GetString());
            Assert.Equal("Test Device", entries[0].GetProperty("label").GetString());
        });
    }
}
