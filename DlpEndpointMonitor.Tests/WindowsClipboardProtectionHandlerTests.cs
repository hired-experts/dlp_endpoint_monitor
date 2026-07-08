using System.Text.Json;
using DlpEndpointMonitor.Commands;
using DlpEndpointMonitor.Core;
using DlpEndpointMonitor.Handlers.Windows;
using Xunit;

namespace DlpEndpointMonitor.Tests;

/// <summary>
/// ai_agent_doc/TEST-PLAN.md section 2.10: WindowsClipboardProtectionHandler previously had zero
/// automated coverage despite being the single most safety-critical behavioral divergence in
/// the clipboard feature - unlike WindowsUsbProtectionHandler's device-policy handler (which
/// DOES force mutual exclusivity between whitelist/blacklist), enabling one clipboard list must
/// NEVER touch the other. Uses real ClipboardWhitelist/ClipboardBlacklist instances against
/// throwaway temp directories (never the parameterless constructor - would touch the real
/// %ProgramData%\DlpEndpointMonitor\), mirroring ClipboardRuleListTests.cs's WithTempDir seam.
/// </summary>
public class WindowsClipboardProtectionHandlerTests
{
    // Never construct ClipboardWhitelist()/ClipboardBlacklist() with no arguments in a test -
    // that reads/writes the real %ProgramData%\DlpEndpointMonitor\ files. Always pass a
    // fresh temp directory.
    static void WithTempDir(Action<string> test)
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try   { test(dir); }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }

    // Two independent temp dirs - ClipboardWhitelist/ClipboardBlacklist persist to different
    // files and must never share one directory's state.
    static void WithHandler(Action<WindowsClipboardProtectionHandler, ClipboardWhitelist, ClipboardBlacklist, CallCounter> test) =>
        WithTempDir(wDir => WithTempDir(bDir =>
        {
            var whitelist = new ClipboardWhitelist(wDir);
            var blacklist = new ClipboardBlacklist(bDir);
            var counter   = new CallCounter();
            var handler   = new WindowsClipboardProtectionHandler(whitelist, blacklist, counter.Increment);
            test(handler, whitelist, blacklist, counter);
        }));

    // Plain int counter incremented by the reevaluate delegate - this project has zero
    // mocking-library dependencies (HARNESS rule), so a hand-rolled counter is the idiomatic
    // substitute for a mock framework's call-verification here.
    sealed class CallCounter
    {
        int _count;
        public void Increment() => Interlocked.Increment(ref _count);
        public int Count => Volatile.Read(ref _count);
    }

    // Every mutation dispatches reevaluate via Task.Run (fire-and-forget, see the handler's own
    // comment) - poll instead of assuming it already ran by the time Handle() returns.
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

    // ── T-CLIP-HANDLER-01: enabling whitelist never touches blacklist ──────────────────────

    [Theory]
    [InlineData(false)]
    [InlineData(true)] // the critical case: proves blacklist STAYS true, not just starts false
    public void WhitelistEnable_SetsWhitelistTrue_LeavesBlacklistUnchanged(bool startingBlacklistEnabled)
    {
        WithHandler((handler, whitelist, blacklist, counter) =>
        {
            blacklist.SetEnabled(startingBlacklistEnabled);

            CaptureStdout(() => handler.Handle(new ClipboardWhitelistEnableCmd("1")));

            Assert.True(whitelist.IsEnabled);
            Assert.Equal(startingBlacklistEnabled, blacklist.IsEnabled);
        });
    }

    // ── T-CLIP-HANDLER-02: symmetric case for blacklist ─────────────────────────────────────

    [Theory]
    [InlineData(false)]
    [InlineData(true)] // the critical case: proves whitelist STAYS true, not just starts false
    public void BlacklistEnable_SetsBlacklistTrue_LeavesWhitelistUnchanged(bool startingWhitelistEnabled)
    {
        WithHandler((handler, whitelist, blacklist, counter) =>
        {
            whitelist.SetEnabled(startingWhitelistEnabled);

            CaptureStdout(() => handler.Handle(new ClipboardBlacklistEnableCmd("1")));

            Assert.True(blacklist.IsEnabled);
            Assert.Equal(startingWhitelistEnabled, whitelist.IsEnabled);
        });
    }

    // ── T-CLIP-HANDLER-03: both lists enabled at once is a valid state, no conflict concept ─

    [Fact]
    public void BothLists_CanBeEnabledIndependentlyAtOnce_NoConflict()
    {
        WithHandler((handler, whitelist, blacklist, counter) =>
        {
            CaptureStdout(() => handler.Handle(new ClipboardWhitelistEnableCmd("1")));
            string output = CaptureStdout(() => handler.Handle(new ClipboardBlacklistEnableCmd("2")));

            Assert.True(whitelist.IsEnabled);
            Assert.True(blacklist.IsEnabled);

            // Unlike device policy's ProtectionMode/conflict, clipboard's reply carries no
            // conflict signal at all - confirm the plain ReplyEvent(ok:true) shape.
            var reply = ParseLastLine(output);
            Assert.True(reply.GetProperty("ok").GetBoolean());
            Assert.DoesNotContain("conflict", output, StringComparison.OrdinalIgnoreCase);
        });
    }

    // ── T-CLIP-HANDLER-04: every mutating command invokes reevaluate exactly once ──────────

    // WindowsClipboardProtectionHandler is internal, so an Action<WindowsClipboardProtectionHandler>
    // cannot appear in a public [Theory] method's signature (CS0051) - MemberData carries only the
    // public command-name string, and this internal dispatch table (referenced solely from test
    // bodies, never as a public parameter) maps it to the actual Handle(...) call.
    static readonly Dictionary<string, Action<WindowsClipboardProtectionHandler>> MutatingCommandInvokers = new()
    {
        ["whitelist_enable"]  = h => h.Handle(new ClipboardWhitelistEnableCmd("1")),
        ["whitelist_disable"] = h => h.Handle(new ClipboardWhitelistDisableCmd("1")),
        ["whitelist_clear"]   = h => h.Handle(new ClipboardWhitelistClearCmd("1")),
        ["whitelist_add"]     = h => h.Handle(new ClipboardWhitelistAddCmd("1", "pattern")),
        ["whitelist_remove"]  = h => h.Handle(new ClipboardWhitelistRemoveCmd("1", "pattern")),
        ["whitelist_set"]     = h => h.Handle(new ClipboardWhitelistSetCmd("1", [])),
        ["blacklist_enable"]  = h => h.Handle(new ClipboardBlacklistEnableCmd("1")),
        ["blacklist_disable"] = h => h.Handle(new ClipboardBlacklistDisableCmd("1")),
        ["blacklist_clear"]   = h => h.Handle(new ClipboardBlacklistClearCmd("1")),
        ["blacklist_add"]     = h => h.Handle(new ClipboardBlacklistAddCmd("1", "pattern")),
        ["blacklist_remove"]  = h => h.Handle(new ClipboardBlacklistRemoveCmd("1", "pattern")),
        ["blacklist_set"]     = h => h.Handle(new ClipboardBlacklistSetCmd("1", [])),
    };

    public static IEnumerable<object[]> MutatingCommandNames() =>
        MutatingCommandInvokers.Keys.Select(name => new object[] { name });

    [Theory]
    [MemberData(nameof(MutatingCommandNames))]
    public void MutatingCommand_InvokesReevaluateExactlyOnce(string commandName)
    {
        var invoke = MutatingCommandInvokers[commandName];
        WithHandler((handler, whitelist, blacklist, counter) =>
        {
            CaptureStdout(() => invoke(handler));

            WaitUntil(() => counter.Count >= 1);
            Assert.Equal(1, counter.Count);
        });
    }

    // ── T-CLIP-HANDLER-05: protection status reports current state accurately ─────────────

    [Fact]
    public void ProtectionStatus_ReportsCurrentStateAfterCombinationsOfEnableDisable()
    {
        WithHandler((handler, whitelist, blacklist, counter) =>
        {
            CaptureStdout(() => handler.Handle(new ClipboardWhitelistEnableCmd("1")));
            CaptureStdout(() => handler.Handle(new ClipboardBlacklistEnableCmd("2")));

            string output = CaptureStdout(() => handler.Handle(new ClipboardProtectionStatusCmd("3")));
            var status = ParseLastLine(output);
            Assert.True(status.GetProperty("whitelistEnabled").GetBoolean());
            Assert.True(status.GetProperty("blacklistEnabled").GetBoolean());

            CaptureStdout(() => handler.Handle(new ClipboardWhitelistDisableCmd("4")));

            output = CaptureStdout(() => handler.Handle(new ClipboardProtectionStatusCmd("5")));
            status = ParseLastLine(output);
            Assert.False(status.GetProperty("whitelistEnabled").GetBoolean());
            Assert.True(status.GetProperty("blacklistEnabled").GetBoolean());
        });
    }

    // ── T-CLIP-HANDLER-06/07: Get reports exactly the entries currently in the list ────────

    [Fact]
    public void WhitelistGet_ReportsExactEntriesAdded()
    {
        WithHandler((handler, whitelist, blacklist, counter) =>
        {
            CaptureStdout(() => handler.Handle(new ClipboardWhitelistAddCmd("1", "^allowed-", ClipboardKind.Text, "Label A")));
            CaptureStdout(() => handler.Handle(new ClipboardWhitelistAddCmd("2", @"\.approved$", ClipboardKind.Files, "Label B")));

            string output = CaptureStdout(() => handler.Handle(new ClipboardWhitelistGetCmd("3")));
            var get = ParseLastLine(output);
            var entries = get.GetProperty("entries").EnumerateArray().ToList();

            Assert.Equal(2, entries.Count);
            Assert.Contains(entries, e => e.GetProperty("pattern").GetString() == "^allowed-" && e.GetProperty("label").GetString() == "Label A");
            Assert.Contains(entries, e => e.GetProperty("pattern").GetString() == @"\.approved$" && e.GetProperty("label").GetString() == "Label B");
        });
    }

    [Fact]
    public void BlacklistGet_ReportsExactEntriesAdded()
    {
        WithHandler((handler, whitelist, blacklist, counter) =>
        {
            CaptureStdout(() => handler.Handle(new ClipboardBlacklistAddCmd("1", "secret", ClipboardKind.Text, "SSN rule")));

            string output = CaptureStdout(() => handler.Handle(new ClipboardBlacklistGetCmd("2")));
            var get = ParseLastLine(output);
            var entries = get.GetProperty("entries").EnumerateArray().ToList();

            Assert.Single(entries);
            Assert.Equal("secret", entries[0].GetProperty("pattern").GetString());
            Assert.Equal("SSN rule", entries[0].GetProperty("label").GetString());
            Assert.Equal("text", entries[0].GetProperty("kind").GetString());
        });
    }
}
