using System.Reflection;
using System.Text.Json;
using DlpEndpointMonitor.Commands;
using DlpEndpointMonitor.Core;
using DlpEndpointMonitor.Handlers.Windows;
using Xunit;

namespace DlpEndpointMonitor.Tests;

/// <summary>
/// ResetAllPolicyCmd is a single call combining what DeviceWhitelistClearCmd +
/// DeviceBlacklistClearCmd + ClipboardWhitelistClearCmd + ClipboardBlacklistClearCmd already do
/// individually - each of those keeps working unchanged on its own (WindowsUsbProtectionHandlerTests
/// / WindowsClipboardProtectionHandlerTests cover them), this suite only covers the NEW combined
/// command: every list ends up in exactly the state its own individual Clear command would leave
/// it in, and both reconcile delegates (restoreDevices, clipboardReevaluate) fire exactly once.
/// </summary>
public class WindowsControlHandlerTests
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

    sealed class Fixture
    {
        public required WindowsControlHandler Handler;
        public required DeviceWhitelist DeviceWhitelist;
        public required DeviceBlacklist DeviceBlacklist;
        public required ClipboardWhitelist ClipboardWhitelist;
        public required ClipboardBlacklist ClipboardBlacklist;
        public required ScreenshotBlockPolicy ScreenshotBlockPolicy;
        public required CallCounter RestoreDevices;
        public required CallCounter ClipboardReevaluate;
    }

    static void WithFixture(Action<Fixture> test) =>
        WithTempDir(dwDir => WithTempDir(dbDir => WithTempDir(cwDir => WithTempDir(cbDir => WithTempDir(sbDir =>
        {
            var deviceWhitelist    = new DeviceWhitelist(dwDir);
            var deviceBlacklist    = new DeviceBlacklist(dbDir);
            var clipboardWhitelist = new ClipboardWhitelist(cwDir);
            var clipboardBlacklist = new ClipboardBlacklist(cbDir);
            var screenshotBlockPolicy = new ScreenshotBlockPolicy(sbDir);
            var restoreDevices     = new CallCounter();
            var clipboardReevaluate = new CallCounter();
            var handler = new WindowsControlHandler(
                stopCompanion: () => { },
                deviceWhitelist, deviceBlacklist, clipboardWhitelist, clipboardBlacklist,
                screenshotBlockPolicy,
                restoreDevices: restoreDevices.Increment,
                clipboardReevaluate: clipboardReevaluate.Increment);

            test(new Fixture
            {
                Handler = handler,
                DeviceWhitelist = deviceWhitelist,
                DeviceBlacklist = deviceBlacklist,
                ClipboardWhitelist = clipboardWhitelist,
                ClipboardBlacklist = clipboardBlacklist,
                ScreenshotBlockPolicy = screenshotBlockPolicy,
                RestoreDevices = restoreDevices,
                ClipboardReevaluate = clipboardReevaluate,
            });
        })))));

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

    [Fact]
    public void ResetAllPolicy_DeviceWhitelist_DisabledAndCleared_SameAsItsOwnClearCommand()
    {
        WithFixture(f =>
        {
            f.DeviceWhitelist.SetEnabled(true);
            f.DeviceWhitelist.Add(new UsbDeviceEntry(Vid: "1234", Pid: "abcd"));

            CaptureStdout(() => f.Handler.Handle(new ResetAllPolicyCmd("1")));

            Assert.False(f.DeviceWhitelist.IsEnabled);
            Assert.Empty(f.DeviceWhitelist.GetAll());
        });
    }

    [Fact]
    public void ResetAllPolicy_DeviceBlacklist_ClearedButEnabledFlagUntouched_SameAsItsOwnClearCommand()
    {
        WithFixture(f =>
        {
            f.DeviceBlacklist.SetEnabled(true);
            f.DeviceBlacklist.Add(new UsbDeviceEntry(Vid: "1234", Pid: "abcd"));

            CaptureStdout(() => f.Handler.Handle(new ResetAllPolicyCmd("1")));

            // Matches DeviceBlacklistClearCmd exactly: only entries are cleared, IsEnabled is
            // left alone (an enabled-but-empty blacklist is already the loosest state).
            Assert.True(f.DeviceBlacklist.IsEnabled);
            Assert.Empty(f.DeviceBlacklist.GetAll());
        });
    }

    [Fact]
    public void ResetAllPolicy_ClipboardWhitelistAndBlacklist_ClearedButEnabledFlagsUntouched()
    {
        WithFixture(f =>
        {
            f.ClipboardWhitelist.SetEnabled(true);
            f.ClipboardWhitelist.Add(new ClipboardRuleEntry("google\\.com", null, null));
            f.ClipboardBlacklist.SetEnabled(true);
            f.ClipboardBlacklist.Add(new ClipboardRuleEntry("secret", null, null));

            CaptureStdout(() => f.Handler.Handle(new ResetAllPolicyCmd("1")));

            Assert.True(f.ClipboardWhitelist.IsEnabled);
            Assert.Empty(f.ClipboardWhitelist.GetAll());
            Assert.True(f.ClipboardBlacklist.IsEnabled);
            Assert.Empty(f.ClipboardBlacklist.GetAll());
        });
    }

    [Fact]
    public void ResetAllPolicy_ScreenshotBlockPolicy_Disabled_SameAsScreenshotBlockDisableCmd()
    {
        WithFixture(f =>
        {
            f.ScreenshotBlockPolicy.SetEnabled(true);

            CaptureStdout(() => f.Handler.Handle(new ResetAllPolicyCmd("1")));

            Assert.False(f.ScreenshotBlockPolicy.IsEnabled);
        });
    }

    [Fact]
    public void ResetAllPolicy_FiresRestoreDevicesAndClipboardReevaluateExactlyOnceEach()
    {
        WithFixture(f =>
        {
            CaptureStdout(() => f.Handler.Handle(new ResetAllPolicyCmd("1")));

            WaitUntil(() => f.RestoreDevices.Count >= 1 && f.ClipboardReevaluate.Count >= 1);

            Assert.Equal(1, f.RestoreDevices.Count);
            Assert.Equal(1, f.ClipboardReevaluate.Count);
        });
    }

    [Fact]
    public void ResetAllPolicy_RepliesOk()
    {
        WithFixture(f =>
        {
            string output = CaptureStdout(() => f.Handler.Handle(new ResetAllPolicyCmd("42")));

            var reply = ParseLastLine(output);
            Assert.True(reply.GetProperty("ok").GetBoolean());
            Assert.Equal("42", reply.GetProperty("id").GetString());
        });
    }

    /// <summary>
    /// Standing rule this test enforces mechanically (see AGENTS.md "Policy list completeness"):
    /// every whitelist/blacklist-style policy list - every non-abstract <see cref="UsbDeviceList"/>
    /// or <see cref="ClipboardRuleList"/> subclass in Core/ - must be wired into
    /// <see cref="WindowsControlHandler"/>'s constructor, so <c>reset_all_policy</c> always clears
    /// every list that exists rather than only the four it happened to know about when written.
    /// This is a STRUCTURAL tripwire, not a behavior test: it fails the moment a new policy list
    /// type is added anywhere in Core/ without also updating WindowsControlHandler, forcing that
    /// decision to be made explicitly instead of silently forgotten - it does NOT (and cannot,
    /// via reflection alone) verify the other half of the rule, that the new list also got its
    /// own individual *_clear command; that half is a code-review/AGENTS.md-reading discipline.
    /// </summary>
    [Fact]
    public void ResetAllPolicyCmd_HandlerConstructorCoversEveryPolicyListType()
    {
        var listBaseTypes = new[] { typeof(UsbDeviceList), typeof(ClipboardRuleList) };
        var allPolicyListTypes = typeof(UsbDeviceList).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && listBaseTypes.Any(b => b.IsAssignableFrom(t)))
            .ToList();

        // Sanity check the check itself: if this ever comes back empty, the reflection query
        // above is broken (wrong assembly/base types), not that there are zero policy lists.
        Assert.NotEmpty(allPolicyListTypes);

        var ctorParamTypes = typeof(WindowsControlHandler)
            .GetConstructors().Single()
            .GetParameters().Select(p => p.ParameterType).ToHashSet();

        foreach (var listType in allPolicyListTypes)
        {
            Assert.True(ctorParamTypes.Contains(listType),
                $"{listType.Name} is a policy list (derives from {(typeof(UsbDeviceList).IsAssignableFrom(listType) ? nameof(UsbDeviceList) : nameof(ClipboardRuleList))}) " +
                $"but WindowsControlHandler's constructor does not take one - reset_all_policy would silently skip it. " +
                $"Add it as a constructor parameter and clear it (matching its own *_clear command's semantics) in Handle(ResetAllPolicyCmd).");
        }
    }
}
