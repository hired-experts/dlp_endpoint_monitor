using System.Text.Json;
using DlpEndpointMonitor.Core;
using Xunit;

namespace DlpEndpointMonitor.Tests;

/// <summary>
/// CommandReply.After is the shared "mutate; maybe reconcile; reply ok" shape extracted from
/// WindowsUsbProtectionHandler/WindowsClipboardProtectionHandler - these tests pin its three
/// guarantees directly, independent of either handler.
/// </summary>
public class CommandReplyTests
{
    static string CaptureStdout(Action act)
    {
        var originalOut = Console.Out;
        var writer = new StringWriter();
        Console.SetOut(writer);
        try   { act(); }
        finally { Console.SetOut(originalOut); }
        return writer.ToString();
    }

    // T-CMDREPLY-01: mutate runs synchronously, before After() returns - not deferred to a
    // background task the way reconcile is.
    [Fact]
    public void After_RunsMutateSynchronouslyBeforeReturning()
    {
        bool mutated = false;

        CaptureStdout(() => CommandReply.After("1", () => mutated = true));

        Assert.True(mutated);
    }

    // T-CMDREPLY-02: a non-null reconcile is invoked exactly once.
    [Fact]
    public void After_WithReconcile_InvokesReconcileExactlyOnce()
    {
        int calls = 0;
        void Reconcile() => Interlocked.Increment(ref calls);

        CaptureStdout(() => CommandReply.After("1", () => { }, Reconcile));

        var deadline = DateTime.UtcNow.AddMilliseconds(2000);
        while (Volatile.Read(ref calls) < 1 && DateTime.UtcNow < deadline) Thread.Sleep(5);

        Assert.Equal(1, Volatile.Read(ref calls));
    }

    // T-CMDREPLY-03: a null reconcile (the default) is simply never invoked - no exception, no
    // Task.Run scheduled at all.
    [Fact]
    public void After_WithoutReconcile_DoesNotThrowAndSkipsReconcile()
    {
        var ex = Record.Exception(() => CaptureStdout(() => CommandReply.After("1", () => { })));

        Assert.Null(ex);
    }

    // T-CMDREPLY-04: emits exactly one ReplyEvent carrying the given id and ok=true.
    [Fact]
    public void After_EmitsReplyEventWithGivenIdAndOkTrue()
    {
        string output = CaptureStdout(() => CommandReply.After("some-id", () => { }));

        string[] lines = output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(lines);

        using var doc = JsonDocument.Parse(lines[0]);
        Assert.Equal("reply", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("some-id", doc.RootElement.GetProperty("id").GetString());
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
    }

    // T-CMDREPLY-05: a null command id is passed through as-is (ids are optional per the wire
    // format - ICommand.Id is string?).
    [Fact]
    public void After_WithNullId_EmitsReplyEventWithNullId()
    {
        string output = CaptureStdout(() => CommandReply.After(null, () => { }));

        using var doc = JsonDocument.Parse(output.Trim());
        Assert.False(doc.RootElement.TryGetProperty("id", out var idProp) && idProp.ValueKind != JsonValueKind.Null);
    }

    // T-CMDREPLY-06: mutate always completes before reconcile is scheduled - reconcile observing
    // a side effect only mutate could have produced proves the order, not just that both ran.
    [Fact]
    public void After_ReconcileObservesMutateSideEffect_ProvingMutateRunsFirst()
    {
        int state = 0;
        int observedByReconcile = -1;

        CaptureStdout(() => CommandReply.After("1",
            mutate: () => state = 42,
            reconcile: () => observedByReconcile = state));

        var deadline = DateTime.UtcNow.AddMilliseconds(2000);
        while (observedByReconcile == -1 && DateTime.UtcNow < deadline) Thread.Sleep(5);

        Assert.Equal(42, observedByReconcile);
    }
}
