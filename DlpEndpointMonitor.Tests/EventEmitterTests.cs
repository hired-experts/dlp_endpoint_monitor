using System.Text.Json;
using DlpEndpointMonitor.Core;
using Xunit;

namespace DlpEndpointMonitor.Tests;

public class EventEmitterTests
{
    // A type deliberately NOT added to AppJsonContext, so EventEmitter.Emit's
    // AppJsonContext.Default.GetTypeInfo lookup returns null for it (T-EVT-02).
    record FakeUnregisteredEvent(string Note) : IEvent;

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
}
