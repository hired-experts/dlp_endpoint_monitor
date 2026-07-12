using System.Text.Json;
using DlpEndpointMonitor.Core;
using Xunit;

namespace DlpEndpointMonitor.Tests;

public class EventEmitterTests
{
    // A type deliberately NOT added to AppJsonContext, so EventEmitter.Emit's
    // AppJsonContext.Default.GetTypeInfo lookup returns null for it (T-EVT-02).
    record FakeUnregisteredEvent(string Note) : IEvent
    {
        public string EventId { get; } = EventEmitter.NewEventId();
    }

    // T-EVT-01: Emit on a registered event type writes exactly one valid-JSON line,
    // terminated by the process's line terminator, and flushes the buffer.
    [Fact]
    public void Emit_RegisteredType_WritesSingleValidJsonLine()
    {
        var originalOut = Console.Out;
        using var writer = new StringWriter();
        Console.SetOut(writer);
        try
        {
            EventEmitter.Emit(new InfoEvent("hello", 123));
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        string output = writer.ToString();
        Assert.EndsWith(Environment.NewLine, output);

        string[] lines = output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(lines);

        using var doc = JsonDocument.Parse(lines[0]);
        Assert.Equal("info", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("hello", doc.RootElement.GetProperty("message").GetString());
    }

    // T-EVT-02: Emit on a type not registered in AppJsonContext falls back to an
    // ErrorEvent("event_emit", ...) rather than throwing or silently dropping the line.
    [Fact]
    public void Emit_UnregisteredType_FallsBackToErrorEvent()
    {
        var originalOut = Console.Out;
        using var writer = new StringWriter();
        Console.SetOut(writer);
        try
        {
            EventEmitter.Emit(new FakeUnregisteredEvent("irrelevant"));
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        string line = writer.ToString().Trim();
        using var doc = JsonDocument.Parse(line);
        Assert.Equal("error", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("event_emit", doc.RootElement.GetProperty("source").GetString());
        Assert.Contains(nameof(FakeUnregisteredEvent), doc.RootElement.GetProperty("message").GetString());
    }

    // T-EVT-03: concurrent EmitInfo calls must never interleave/corrupt output - every
    // captured line independently parses as valid JSON (no ordering assumption made).
    [Fact]
    public async Task Emit_ConcurrentCalls_ProduceOnlyWellFormedLines()
    {
        var originalOut = Console.Out;
        using var writer = new StringWriter();
        Console.SetOut(writer);
        try
        {
            var tasks = Enumerable.Range(0, 200)
                .Select(i => Task.Run(() => EventEmitter.EmitInfo($"msg-{i}")));
            await Task.WhenAll(tasks);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        string[] lines = writer.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(200, lines.Length);
        foreach (string line in lines)
        {
            using var doc = JsonDocument.Parse(line); // throws if malformed/interleaved
            Assert.Equal("info", doc.RootElement.GetProperty("type").GetString());
        }
    }

    // T-EVT-04: EmitError/EmitInfo wire shape - correct discriminant and a plausible
    // Unix-seconds timestamp close to "now".
    [Fact]
    public void EmitError_WireShape_HasErrorTypeAndPlausibleTimestamp()
    {
        var originalOut = Console.Out;
        using var writer = new StringWriter();
        Console.SetOut(writer);
        long before = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        try
        {
            EventEmitter.EmitError("some_source", "some_message");
        }
        finally
        {
            Console.SetOut(originalOut);
        }
        long after = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        using var doc = JsonDocument.Parse(writer.ToString().Trim());
        Assert.Equal("error", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("some_source", doc.RootElement.GetProperty("source").GetString());
        Assert.Equal("some_message", doc.RootElement.GetProperty("message").GetString());
        long ts = doc.RootElement.GetProperty("ts").GetInt64();
        Assert.InRange(ts, before, after);
    }

    [Fact]
    public void EmitInfo_WireShape_HasInfoTypeAndPlausibleTimestamp()
    {
        var originalOut = Console.Out;
        using var writer = new StringWriter();
        Console.SetOut(writer);
        long before = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        try
        {
            EventEmitter.EmitInfo("some_message");
        }
        finally
        {
            Console.SetOut(originalOut);
        }
        long after = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        using var doc = JsonDocument.Parse(writer.ToString().Trim());
        Assert.Equal("info", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("some_message", doc.RootElement.GetProperty("message").GetString());
        long ts = doc.RootElement.GetProperty("ts").GetInt64();
        Assert.InRange(ts, before, after);
    }

    // T-EVT-05: EventId is present (non-empty) and unique across two separate Emit calls.
    [Fact]
    public void Emit_TwoEvents_HaveNonEmptyDistinctEventIds()
    {
        var originalOut = Console.Out;
        using var writer = new StringWriter();
        Console.SetOut(writer);
        try
        {
            EventEmitter.Emit(new InfoEvent("first", 1));
            EventEmitter.Emit(new InfoEvent("second", 2));
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        string[] lines = writer.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);

        using var doc1 = JsonDocument.Parse(lines[0]);
        using var doc2 = JsonDocument.Parse(lines[1]);
        string eventId1 = doc1.RootElement.GetProperty("eventId").GetString()!;
        string eventId2 = doc2.RootElement.GetProperty("eventId").GetString()!;

        Assert.False(string.IsNullOrEmpty(eventId1));
        Assert.False(string.IsNullOrEmpty(eventId2));
        Assert.NotEqual(eventId1, eventId2);
    }

    // T-EVT-06: EventId is inherited correctly through an abstract base record
    // (ClipboardChangeEvent) - guards against EventId being declared only on a leaf
    // subtype and not actually flowing through the abstract base.
    [Fact]
    public void Emit_EventReachingIEventThroughAbstractBase_HasNonEmptyEventId()
    {
        var originalOut = Console.Out;
        using var writer = new StringWriter();
        Console.SetOut(writer);
        try
        {
            EventEmitter.Emit(new ClipboardTextEvent("copy", "some text", 123));
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        using var doc = JsonDocument.Parse(writer.ToString().Trim());
        string eventId = doc.RootElement.GetProperty("eventId").GetString()!;
        Assert.False(string.IsNullOrEmpty(eventId));
    }

    // T-EVT-07: Ts was added to these 9 records that previously had none - each must
    // serialize a plausible Unix-seconds "ts" close to "now".
    [Theory]
    [MemberData(nameof(RecordsWithNewTsField))]
    public void Emit_RecordsWithNewTsField_HavePlausibleTimestamp(IEvent payload)
    {
        var originalOut = Console.Out;
        using var writer = new StringWriter();
        Console.SetOut(writer);
        long before = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        try
        {
            EventEmitter.Emit(payload);
        }
        finally
        {
            Console.SetOut(originalOut);
        }
        long after = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        using var doc = JsonDocument.Parse(writer.ToString().Trim());
        long ts = doc.RootElement.GetProperty("ts").GetInt64();
        Assert.InRange(ts, before, after);
    }

    public static IEnumerable<object[]> RecordsWithNewTsField()
    {
        yield return new object[] { new ReplyEvent("id-1", true) };
        yield return new object[] { new ClipboardReadEvent("id-1", true, null) };
        yield return new object[] { new UsbStorageStatusEvent("id-1", true, true) };
        yield return new object[] { new DeviceProtectionStatusEvent("id-1", true, ProtectionMode.None) };
        yield return new object[] { new ClipboardProtectionStatusEvent("id-1", true, false, false) };
        yield return new object[] { new ClipboardWhitelistGetEvent("id-1", true, false, Array.Empty<ClipboardRuleEntryDto>()) };
        yield return new object[] { new ClipboardBlacklistGetEvent("id-1", true, false, Array.Empty<ClipboardRuleEntryDto>()) };
        yield return new object[] { new DeviceWhitelistGetEvent("id-1", true, false, Array.Empty<WhitelistEntryDto>()) };
        yield return new object[] { new DeviceBlacklistGetEvent("id-1", true, false, Array.Empty<WhitelistEntryDto>()) };
    }

    // T-EVT-07b: `with` on an IEvent record preserves its already-generated EventId - the
    // invariant UsbMonitor's group-anchor feature (SourceEventId) depends on: it constructs an
    // event, reads its real EventId as the anchor candidate, then folds the resolved SourceEventId
    // back in via `with`. If `with` re-ran the EventId field initializer instead of copying the
    // existing value, every anchor stored would point at a EventId nothing in the stream actually
    // carries.
    [Fact]
    public void With_OnUsbDeviceConnectedEvent_PreservesOriginalEventId()
    {
        var original = new UsbDeviceConnectedEvent(
            "1234", "abcd", null, null, DeviceKind.Storage, null, "group-1", "instance-1",
            @"\\?\usb#vid_1234", true, null, 123);

        var withSourceEventId = original with { SourceEventId = "anchor-event-id" };

        Assert.Equal(original.EventId, withSourceEventId.EventId);
        Assert.Equal("anchor-event-id", withSourceEventId.SourceEventId);
    }

    // T-EVT-08: UsbDeviceConnectedEvent.InstanceId is required - it always serializes.
    [Fact]
    public void Emit_UsbDeviceConnectedEvent_SerializesRequiredInstanceId()
    {
        var originalOut = Console.Out;
        using var writer = new StringWriter();
        Console.SetOut(writer);
        try
        {
            EventEmitter.Emit(new UsbDeviceConnectedEvent("1234", "abcd", null, null, DeviceKind.Storage, null, null, "instance-1", @"\\?\usb#vid_1234", true, null, 123));
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        using var doc = JsonDocument.Parse(writer.ToString().Trim());
        Assert.Equal("instance-1", doc.RootElement.GetProperty("instanceId").GetString());
    }

    // T-EVT-09: UsbDeviceDisconnectedEvent.InstanceId is nullable - present when set,
    // absent from the JSON entirely when null (WhenWritingNull ignore condition).
    [Fact]
    public void Emit_UsbDeviceDisconnectedEvent_InstanceIdPresentWhenSet()
    {
        var originalOut = Console.Out;
        using var writer = new StringWriter();
        Console.SetOut(writer);
        try
        {
            EventEmitter.Emit(new UsbDeviceDisconnectedEvent(null, null, null, @"\\?\usb#vid_1234", null, DeviceKind.Storage, null, null, "instance-2", null, 123));
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        using var doc = JsonDocument.Parse(writer.ToString().Trim());
        Assert.Equal("instance-2", doc.RootElement.GetProperty("instanceId").GetString());
    }

    [Fact]
    public void Emit_UsbDeviceDisconnectedEvent_InstanceIdAbsentWhenNull()
    {
        var originalOut = Console.Out;
        using var writer = new StringWriter();
        Console.SetOut(writer);
        try
        {
            EventEmitter.Emit(new UsbDeviceDisconnectedEvent(null, null, null, @"\\?\usb#vid_1234", null, DeviceKind.Storage, null, null, null, null, 123));
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        using var doc = JsonDocument.Parse(writer.ToString().Trim());
        Assert.False(doc.RootElement.TryGetProperty("instanceId", out _));
    }

    // T-EVT-10: UsbDeviceUnblockedEvent.GroupId is nullable - present when set, absent
    // from the JSON entirely when null.
    [Fact]
    public void Emit_UsbDeviceUnblockedEvent_GroupIdPresentWhenSet()
    {
        var originalOut = Console.Out;
        using var writer = new StringWriter();
        Console.SetOut(writer);
        try
        {
            EventEmitter.Emit(new UsbDeviceUnblockedEvent(null, null, null, DeviceKind.Storage, "group-1", "instance-3", null, 123));
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        using var doc = JsonDocument.Parse(writer.ToString().Trim());
        Assert.Equal("group-1", doc.RootElement.GetProperty("groupId").GetString());
    }

    [Fact]
    public void Emit_UsbDeviceUnblockedEvent_GroupIdAbsentWhenNull()
    {
        var originalOut = Console.Out;
        using var writer = new StringWriter();
        Console.SetOut(writer);
        try
        {
            EventEmitter.Emit(new UsbDeviceUnblockedEvent(null, null, null, DeviceKind.Storage, null, "instance-3", null, 123));
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        using var doc = JsonDocument.Parse(writer.ToString().Trim());
        Assert.False(doc.RootElement.TryGetProperty("groupId", out _));
    }

    // T-EVT-11: UsbDeviceUnblockFailedEvent.GroupId is nullable - same two-case pattern.
    [Fact]
    public void Emit_UsbDeviceUnblockFailedEvent_GroupIdPresentWhenSet()
    {
        var originalOut = Console.Out;
        using var writer = new StringWriter();
        Console.SetOut(writer);
        try
        {
            EventEmitter.Emit(new UsbDeviceUnblockFailedEvent(null, null, null, DeviceKind.Storage, "group-2", "instance-4", "some error", null, 123));
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        using var doc = JsonDocument.Parse(writer.ToString().Trim());
        Assert.Equal("group-2", doc.RootElement.GetProperty("groupId").GetString());
    }

    [Fact]
    public void Emit_UsbDeviceUnblockFailedEvent_GroupIdAbsentWhenNull()
    {
        var originalOut = Console.Out;
        using var writer = new StringWriter();
        Console.SetOut(writer);
        try
        {
            EventEmitter.Emit(new UsbDeviceUnblockFailedEvent(null, null, null, DeviceKind.Storage, null, "instance-4", "some error", null, 123));
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        using var doc = JsonDocument.Parse(writer.ToString().Trim());
        Assert.False(doc.RootElement.TryGetProperty("groupId", out _));
    }

    // T-EVT-12: NetworkDeviceConnectedEvent.InstanceId is required - it always serializes.
    [Fact]
    public void Emit_NetworkDeviceConnectedEvent_SerializesRequiredInstanceId()
    {
        var originalOut = Console.Out;
        using var writer = new StringWriter();
        Console.SetOut(writer);
        try
        {
            EventEmitter.Emit(new NetworkDeviceConnectedEvent(null, null, null, null, null, "instance-5", @"\\?\pci#ven_1234", true, 123));
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        using var doc = JsonDocument.Parse(writer.ToString().Trim());
        Assert.Equal("instance-5", doc.RootElement.GetProperty("instanceId").GetString());
    }

    // T-EVT-13: NetworkDeviceDisconnectedEvent.InstanceId is nullable - same two-case
    // pattern.
    [Fact]
    public void Emit_NetworkDeviceDisconnectedEvent_InstanceIdPresentWhenSet()
    {
        var originalOut = Console.Out;
        using var writer = new StringWriter();
        Console.SetOut(writer);
        try
        {
            EventEmitter.Emit(new NetworkDeviceDisconnectedEvent(null, null, null, @"\\?\pci#ven_1234", null, null, "instance-6", 123));
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        using var doc = JsonDocument.Parse(writer.ToString().Trim());
        Assert.Equal("instance-6", doc.RootElement.GetProperty("instanceId").GetString());
    }

    [Fact]
    public void Emit_NetworkDeviceDisconnectedEvent_InstanceIdAbsentWhenNull()
    {
        var originalOut = Console.Out;
        using var writer = new StringWriter();
        Console.SetOut(writer);
        try
        {
            EventEmitter.Emit(new NetworkDeviceDisconnectedEvent(null, null, null, @"\\?\pci#ven_1234", null, null, null, 123));
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        using var doc = JsonDocument.Parse(writer.ToString().Trim());
        Assert.False(doc.RootElement.TryGetProperty("instanceId", out _));
    }

    // T-EVT-14: RawLineSink is invoked with the exact same serialized JSON that Emit writes
    // to stdout, so a relay (a companion process forwarding its own events into this
    // process's stdout) can observe every locally-emitted event too. Must reset RawLineSink
    // to null in finally - it is a static field shared across every test in this assembly.
    [Fact]
    public void Emit_WithRawLineSinkSet_InvokesSinkExactlyOnceWithSameJson()
    {
        var captured = new List<string>();
        EventEmitter.RawLineSink = line => captured.Add(line);
        try
        {
            EventEmitter.Emit(new InfoEvent("hello-relay", 1));
        }
        finally
        {
            EventEmitter.RawLineSink = null;
        }

        Assert.Single(captured);
        using var doc = JsonDocument.Parse(captured[0]);
        Assert.Equal("info", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("hello-relay", doc.RootElement.GetProperty("message").GetString());
    }

    // T-EVT-15: EmitRawLine writes a pre-serialized JSON string to stdout verbatim - no
    // re-parse/re-serialize round trip - under the same lock/flush pattern as Emit.
    [Fact]
    public void EmitRawLine_WritesGivenStringVerbatimPlusNewline()
    {
        const string json = """{"type":"info","message":"already-serialized","ts":999}""";

        var originalOut = Console.Out;
        using var writer = new StringWriter();
        Console.SetOut(writer);
        try
        {
            EventEmitter.EmitRawLine(json);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        Assert.Equal(json + Environment.NewLine, writer.ToString());
    }
}
