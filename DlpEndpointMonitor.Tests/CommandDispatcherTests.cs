using System.Text.Json;
using DlpEndpointMonitor.Commands;
using DlpEndpointMonitor.Core;
using DlpEndpointMonitor.Handlers;
using Xunit;

namespace DlpEndpointMonitor.Tests;

public class CommandDispatcherTests
{
    // ── Shared test infrastructure ─────────────────────────────────────────────

    // Records every Handle(...) overload invocation so tests can assert which
    // handler/command reached the dispatcher, and can signal "done" without a fixed sleep.
    sealed class CallRecorder
    {
        readonly object _gate = new();
        readonly List<(string Method, ICommand Command)> _calls = new();

        public void Record(string method, ICommand command)
        {
            lock (_gate) { _calls.Add((method, command)); }
        }

        public int Count { get { lock (_gate) return _calls.Count; } }
        public IReadOnlyList<(string Method, ICommand Command)> Calls { get { lock (_gate) return _calls.ToList(); } }
    }

    sealed class FakeClipboardHandler(CallRecorder recorder) : IClipboardHandler
    {
        public void Handle(ClipboardReadCmd command) => recorder.Record(nameof(ClipboardReadCmd), command);
        public void Handle(ClipboardSetCmd command) => recorder.Record(nameof(ClipboardSetCmd), command);
        public void Handle(ClipboardClearCmd command) => recorder.Record(nameof(ClipboardClearCmd), command);
    }

    sealed class FakeUsbStorageHandler(CallRecorder recorder) : IUsbStorageHandler
    {
        public void Handle(UsbEjectCmd command) => recorder.Record(nameof(UsbEjectCmd), command);
        public void Handle(UsbDisableStorageCmd command) => recorder.Record(nameof(UsbDisableStorageCmd), command);
        public void Handle(UsbEnableStorageCmd command) => recorder.Record(nameof(UsbEnableStorageCmd), command);
        public void Handle(UsbStorageStatusCmd command) => recorder.Record(nameof(UsbStorageStatusCmd), command);
    }

    sealed class FakeUsbDeviceHandler(CallRecorder recorder) : IUsbDeviceHandler
    {
        public void Handle(DeviceDisableCmd command) => recorder.Record(nameof(DeviceDisableCmd), command);
        public void Handle(DeviceEnableCmd command) => recorder.Record(nameof(DeviceEnableCmd), command);
    }

    sealed class FakeUsbProtectionHandler(CallRecorder recorder) : IUsbProtectionHandler
    {
        public void Handle(DeviceProtectionStatusCmd command) => recorder.Record(nameof(DeviceProtectionStatusCmd), command);
        public void Handle(DeviceWhitelistEnableCmd command) => recorder.Record(nameof(DeviceWhitelistEnableCmd), command);
        public void Handle(DeviceWhitelistDisableCmd command) => recorder.Record(nameof(DeviceWhitelistDisableCmd), command);
        public void Handle(DeviceWhitelistGetCmd command) => recorder.Record(nameof(DeviceWhitelistGetCmd), command);
        public void Handle(DeviceWhitelistClearCmd command) => recorder.Record(nameof(DeviceWhitelistClearCmd), command);
        public void Handle(DeviceWhitelistAddCmd command) => recorder.Record(nameof(DeviceWhitelistAddCmd), command);
        public void Handle(DeviceWhitelistRemoveCmd command) => recorder.Record(nameof(DeviceWhitelistRemoveCmd), command);
        public void Handle(DeviceWhitelistSetCmd command) => recorder.Record(nameof(DeviceWhitelistSetCmd), command);
        public void Handle(DeviceBlacklistEnableCmd command) => recorder.Record(nameof(DeviceBlacklistEnableCmd), command);
        public void Handle(DeviceBlacklistDisableCmd command) => recorder.Record(nameof(DeviceBlacklistDisableCmd), command);
        public void Handle(DeviceBlacklistGetCmd command) => recorder.Record(nameof(DeviceBlacklistGetCmd), command);
        public void Handle(DeviceBlacklistClearCmd command) => recorder.Record(nameof(DeviceBlacklistClearCmd), command);
        public void Handle(DeviceBlacklistAddCmd command) => recorder.Record(nameof(DeviceBlacklistAddCmd), command);
        public void Handle(DeviceBlacklistRemoveCmd command) => recorder.Record(nameof(DeviceBlacklistRemoveCmd), command);
        public void Handle(DeviceBlacklistSetCmd command) => recorder.Record(nameof(DeviceBlacklistSetCmd), command);
    }

    sealed class FakeControlHandler(CallRecorder recorder) : IControlHandler
    {
        public void Handle(PingCmd command) => recorder.Record(nameof(PingCmd), command);
        public void Handle(ShutdownCmd command) => recorder.Record(nameof(ShutdownCmd), command);
    }

    // Redirects Console.In/Out, drives CommandDispatcher.RunAsync() to completion for one
    // test, and ALWAYS restores the original Console.In/Out — even on assertion failure —
    // since these are process-global (HARNESS rule 3). Cancellation + awaiting the run task
    // happen before the finally restores the console, so no ReadLine call is ever left
    // reading from the real console.
    static async Task RunDispatcherAsync(
        string input,
        StringWriter writer,
        CancellationTokenSource cts,
        CallRecorder recorder,
        Func<bool> isDone,
        int expectedCallCount = -1)
    {
        var originalIn  = Console.In;
        var originalOut = Console.Out;
        Console.SetIn(new StringReader(input));
        Console.SetOut(writer);
        try
        {
            var dispatcher = new CommandDispatcher(
                cts.Token,
                new FakeClipboardHandler(recorder),
                new FakeUsbStorageHandler(recorder),
                new FakeUsbDeviceHandler(recorder),
                new FakeUsbProtectionHandler(recorder),
                new FakeControlHandler(recorder));

            Task runTask = dispatcher.RunAsync();

            // Poll (no fixed sleep) until the expected recorded calls / output arrived, or
            // give up after a generous timeout so a real bug fails the test instead of hanging.
            DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (!isDone() && DateTime.UtcNow < deadline)
                await Task.Delay(10);

            if (expectedCallCount >= 0)
                Assert.Equal(expectedCallCount, recorder.Count);

            if (!cts.IsCancellationRequested)
                cts.Cancel();

            await Task.WhenAny(runTask, Task.Delay(TimeSpan.FromSeconds(2)));
        }
        finally
        {
            Console.SetIn(originalIn);
            Console.SetOut(originalOut);
        }
    }

    static JsonElement ParseSingleReply(string output)
    {
        string line = output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Single(l => l.Contains("\"type\":\"reply\""));
        return JsonDocument.Parse(line).RootElement;
    }

    // T-CMD-01: a well-formed line for every CommandType dispatches to the correct fake
    // handler's Handle overload, exactly once each.
    [Fact]
    public async Task Dispatch_OneLinePerCommandType_ReachesCorrectHandlerExactlyOnce()
    {
        string[] expectedMethods =
        [
            nameof(ClipboardReadCmd), nameof(ClipboardSetCmd), nameof(ClipboardClearCmd),
            nameof(UsbEjectCmd), nameof(UsbDisableStorageCmd), nameof(UsbEnableStorageCmd), nameof(UsbStorageStatusCmd),
            nameof(DeviceDisableCmd), nameof(DeviceEnableCmd),
            nameof(DeviceProtectionStatusCmd),
            nameof(DeviceWhitelistEnableCmd), nameof(DeviceWhitelistDisableCmd), nameof(DeviceWhitelistGetCmd),
            nameof(DeviceWhitelistClearCmd), nameof(DeviceWhitelistAddCmd), nameof(DeviceWhitelistRemoveCmd), nameof(DeviceWhitelistSetCmd),
            nameof(DeviceBlacklistEnableCmd), nameof(DeviceBlacklistDisableCmd), nameof(DeviceBlacklistGetCmd),
            nameof(DeviceBlacklistClearCmd), nameof(DeviceBlacklistAddCmd), nameof(DeviceBlacklistRemoveCmd), nameof(DeviceBlacklistSetCmd),
            nameof(PingCmd), nameof(ShutdownCmd),
        ];

        string input = string.Join('\n',
        [
            """{"id":"1","cmd":"clipboard_read"}""",
            """{"id":"2","cmd":"clipboard_set","content":"hello"}""",
            """{"id":"3","cmd":"clipboard_clear"}""",
            """{"id":"4","cmd":"usb_eject","drive":"E:"}""",
            """{"id":"5","cmd":"usb_disable_storage"}""",
            """{"id":"6","cmd":"usb_enable_storage"}""",
            """{"id":"7","cmd":"usb_storage_status"}""",
            """{"id":"8","cmd":"device_disable","instanceId":"USB\\VID_1234"}""",
            """{"id":"9","cmd":"device_enable","instanceId":"USB\\VID_1234"}""",
            """{"id":"10","cmd":"device_protection_status"}""",
            """{"id":"11","cmd":"device_whitelist_enable"}""",
            """{"id":"12","cmd":"device_whitelist_disable"}""",
            """{"id":"13","cmd":"device_whitelist_get"}""",
            """{"id":"14","cmd":"device_whitelist_clear"}""",
            """{"id":"15","cmd":"device_whitelist_add","vid":"1234","pid":"5678"}""",
            """{"id":"16","cmd":"device_whitelist_remove","vid":"1234","pid":"5678"}""",
            """{"id":"17","cmd":"device_whitelist_set","entries":[]}""",
            """{"id":"18","cmd":"device_blacklist_enable"}""",
            """{"id":"19","cmd":"device_blacklist_disable"}""",
            """{"id":"20","cmd":"device_blacklist_get"}""",
            """{"id":"21","cmd":"device_blacklist_clear"}""",
            """{"id":"22","cmd":"device_blacklist_add","vid":"1234","pid":"5678"}""",
            """{"id":"23","cmd":"device_blacklist_remove","vid":"1234","pid":"5678"}""",
            """{"id":"24","cmd":"device_blacklist_set","entries":[]}""",
            """{"id":"25","cmd":"ping"}""",
            """{"id":"26","cmd":"shutdown"}""",
        ]) + "\n";

        var writer = new StringWriter();
        var cts = new CancellationTokenSource();
        var recorder = new CallRecorder();

        await RunDispatcherAsync(input, writer, cts, recorder, () => recorder.Count >= expectedMethods.Length);

        Assert.Equal(
            expectedMethods.OrderBy(m => m, StringComparer.Ordinal),
            recorder.Calls.Select(c => c.Method).OrderBy(m => m, StringComparer.Ordinal));
    }

    // T-CMD-02: a line that is not valid JSON at all emits reply {ok:false} carrying the
    // exception message, and Dispatch does not throw out of RunAsync.
    [Fact]
    public async Task Dispatch_MalformedJson_EmitsFailureReplyWithoutThrowing()
    {
        var writer = new StringWriter();
        var cts = new CancellationTokenSource();
        var recorder = new CallRecorder();

        await RunDispatcherAsync(
            "this is not json at all\n", writer, cts, recorder,
            () => writer.ToString().Contains("\"type\":\"reply\""),
            expectedCallCount: 0);

        var reply = ParseSingleReply(writer.ToString());
        Assert.False(reply.GetProperty("ok").GetBoolean());
        Assert.False(string.IsNullOrEmpty(reply.GetProperty("error").GetString()));
        Assert.False(reply.TryGetProperty("id", out _)); // never parsed - JSON itself failed first
    }

    // T-CMD-03: valid JSON but missing the "cmd" field - GetProperty("cmd") throws after "id"
    // was already captured, so the id is still echoed in the failure reply.
    [Fact]
    public async Task Dispatch_ValidJsonMissingCmd_EmitsFailureReplyWithIdEchoed()
    {
        var writer = new StringWriter();
        var cts = new CancellationTokenSource();
        var recorder = new CallRecorder();

        await RunDispatcherAsync(
            """{"id":"abc"}""" + "\n", writer, cts, recorder,
            () => writer.ToString().Contains("\"type\":\"reply\""),
            expectedCallCount: 0);

        var reply = ParseSingleReply(writer.ToString());
        Assert.Equal("abc", reply.GetProperty("id").GetString());
        Assert.False(reply.GetProperty("ok").GetBoolean());
        Assert.False(string.IsNullOrEmpty(reply.GetProperty("error").GetString()));
    }

    // T-CMD-04: valid JSON, unrecognized "cmd" string - commandType deserializes to null,
    // producing reply {ok:false, error:"unknown command: <cmd>"} with the id echoed.
    [Fact]
    public async Task Dispatch_UnrecognizedCmd_EmitsUnknownCommandReply()
    {
        var writer = new StringWriter();
        var cts = new CancellationTokenSource();
        var recorder = new CallRecorder();

        await RunDispatcherAsync(
            """{"id":"xyz","cmd":"totally_bogus_cmd"}""" + "\n", writer, cts, recorder,
            () => writer.ToString().Contains("\"type\":\"reply\""),
            expectedCallCount: 0);

        var reply = ParseSingleReply(writer.ToString());
        Assert.Equal("xyz", reply.GetProperty("id").GetString());
        Assert.False(reply.GetProperty("ok").GetBoolean());
        Assert.Equal("unknown command: totally_bogus_cmd", reply.GetProperty("error").GetString());
    }

    // T-CMD-05 (adapted): TEST-PLAN describes "usb_eject without drive" as throwing during
    // Deserialize<TCommand>, but ground truth (verified by running this test) shows the
    // source-generated deserializer does NOT enforce the non-nullable `Drive` as required -
    // it silently leaves it null and the fake handler IS invoked, so that payload can't
    // exercise this path (HARNESS rule 1). A field with the wrong JSON *type* (a number
    // where `drive` expects a string) is the nearest real deserialization failure for a
    // recognized cmd, and does throw - same "id still echoed" contract.
    [Fact]
    public async Task Dispatch_RecognizedCmdFieldTypeMismatch_EmitsFailureReplyWithIdEchoed()
    {
        var writer = new StringWriter();
        var cts = new CancellationTokenSource();
        var recorder = new CallRecorder();

        await RunDispatcherAsync(
            """{"id":"e1","cmd":"usb_eject","drive":123}""" + "\n", writer, cts, recorder,
            () => writer.ToString().Contains("\"type\":\"reply\""),
            expectedCallCount: 0);

        var reply = ParseSingleReply(writer.ToString());
        Assert.Equal("e1", reply.GetProperty("id").GetString());
        Assert.False(reply.GetProperty("ok").GetBoolean());
        Assert.False(string.IsNullOrEmpty(reply.GetProperty("error").GetString()));
    }

    // T-CMD-06: id present but null (the field's value, not the field itself, is optional -
    // GetProperty("id") requires the KEY to exist; ground truth checked against
    // JsonElement.GetProperty, see HARNESS rule 1) - handler receives Id=null, no crash.
    [Fact]
    public async Task Dispatch_NullId_HandlerReceivesNullId()
    {
        var writer = new StringWriter();
        var cts = new CancellationTokenSource();
        var recorder = new CallRecorder();

        await RunDispatcherAsync(
            """{"id":null,"cmd":"ping"}""" + "\n", writer, cts, recorder,
            () => recorder.Count >= 1,
            expectedCallCount: 1);

        var (method, command) = Assert.Single(recorder.Calls);
        Assert.Equal(nameof(PingCmd), method);
        Assert.Null(Assert.IsType<PingCmd>(command).Id);
    }

    // T-CMD-07: two lines with the same id in a row dispatch independently - no
    // deduplication by id.
    [Fact]
    public async Task Dispatch_DuplicateIdConsecutiveLines_DispatchesBothIndependently()
    {
        var writer = new StringWriter();
        var cts = new CancellationTokenSource();
        var recorder = new CallRecorder();

        string line = """{"id":"dup","cmd":"ping"}""";
        await RunDispatcherAsync(
            line + "\n" + line + "\n", writer, cts, recorder,
            () => recorder.Count >= 2,
            expectedCallCount: 2);

        Assert.All(recorder.Calls, call => Assert.Equal("dup", Assert.IsType<PingCmd>(call.Command).Id));
    }

    // T-CMD-08: when Console.ReadLine returns null (stdin closed), RunAsync emits an info
    // event and then blocks on Task.Delay(Infinite) rather than exiting the loop with an
    // error. Written standalone (not via the shared helper) so the still-blocked state can
    // be observed before cancellation is issued.
    [Fact]
    public async Task RunAsync_StdinClosed_EmitsInfoThenBlocksInsteadOfExiting()
    {
        var originalIn  = Console.In;
        var originalOut = Console.Out;
        var writer = new StringWriter();
        Console.SetIn(new StringReader("")); // first ReadLine returns null immediately
        Console.SetOut(writer);
        var cts = new CancellationTokenSource();
        try
        {
            var dispatcher = new CommandDispatcher(
                cts.Token,
                new FakeClipboardHandler(new CallRecorder()),
                new FakeUsbStorageHandler(new CallRecorder()),
                new FakeUsbDeviceHandler(new CallRecorder()),
                new FakeUsbProtectionHandler(new CallRecorder()),
                new FakeControlHandler(new CallRecorder()));

            Task runTask = dispatcher.RunAsync();

            DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (!writer.ToString().Contains("stdin closed") && DateTime.UtcNow < deadline)
                await Task.Delay(10);

            using var doc = JsonDocument.Parse(writer.ToString().Trim());
            Assert.Equal("info", doc.RootElement.GetProperty("type").GetString());
            Assert.Contains("stdin closed", doc.RootElement.GetProperty("message").GetString());

            // Still blocked on Task.Delay(Infinite) - RunAsync must not have exited yet.
            Assert.False(runTask.IsCompleted);

            cts.Cancel();
            await Task.WhenAny(runTask, Task.Delay(TimeSpan.FromSeconds(2)));
            Assert.True(runTask.IsCompleted);
        }
        finally
        {
            Console.SetIn(originalIn);
            Console.SetOut(originalOut);
        }
    }

    // T-CMD-09: a CancellationToken that is already cancelled before the first read makes the
    // loop exit immediately - no dispatch attempted.
    [Fact]
    public async Task RunAsync_AlreadyCancelledToken_ExitsImmediatelyWithoutDispatching()
    {
        var originalIn  = Console.In;
        var originalOut = Console.Out;
        // Input would dispatch if read, so a passing test proves the loop never read it.
        Console.SetIn(new StringReader("""{"id":"1","cmd":"ping"}""" + "\n"));
        var writer = new StringWriter();
        Console.SetOut(writer);
        var cts = new CancellationTokenSource();
        cts.Cancel();
        var recorder = new CallRecorder();
        try
        {
            var dispatcher = new CommandDispatcher(
                cts.Token,
                new FakeClipboardHandler(recorder),
                new FakeUsbStorageHandler(recorder),
                new FakeUsbDeviceHandler(recorder),
                new FakeUsbProtectionHandler(recorder),
                new FakeControlHandler(recorder));

            Task runTask = dispatcher.RunAsync();
            Task completed = await Task.WhenAny(runTask, Task.Delay(TimeSpan.FromSeconds(2)));

            Assert.Same(runTask, completed);
            Assert.True(runTask.IsCompleted);
            Assert.Equal(0, recorder.Count);
        }
        finally
        {
            Console.SetIn(originalIn);
            Console.SetOut(originalOut);
        }
    }
}
