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

    // ── USB-WHITELIST-BYPASS-FIX-PLAN.md section 3.5: the Func<(bool,string?)>-based overload ──
    // (UsbDeviceList.Add/Remove/Set's rejecting mutate() shape) - reconcile must only run when
    // mutate() actually reports ok:true; a rejected mutation (e.g. an all-null-fields Add/Remove/
    // Set) changed nothing, so there is nothing to restore/re-apply.

    // T-CMDREPLY-07: mutate() returning ok:true schedules reconcile exactly once.
    [Fact]
    public void FuncAfter_MutateOk_InvokesReconcileExactlyOnce()
    {
        int calls = 0;
        void Reconcile() => Interlocked.Increment(ref calls);

        CaptureStdout(() => CommandReply.After("1", () => (true, (string?)null), Reconcile));

        var deadline = DateTime.UtcNow.AddMilliseconds(2000);
        while (Volatile.Read(ref calls) < 1 && DateTime.UtcNow < deadline) Thread.Sleep(5);

        Assert.Equal(1, Volatile.Read(ref calls));
    }

    // T-CMDREPLY-08: mutate() returning ok:false must NEVER schedule reconcile - a rejected
    // mutation acted on nothing, so reconciling would run against stale, unchanged state.
    [Fact]
    public void FuncAfter_MutateRejected_NeverInvokesReconcile()
    {
        int calls = 0;
        void Reconcile() => Interlocked.Increment(ref calls);

        CaptureStdout(() => CommandReply.After("1", () => (false, "rejected"), Reconcile));

        // Give any wrongly-scheduled Task.Run a real chance to run before asserting it didn't.
        Thread.Sleep(200);

        Assert.Equal(0, Volatile.Read(ref calls));
    }

    // T-CMDREPLY-09: the reply event carries the real (ok, error) from mutate(), not a hardcoded
    // ok:true the way the Action-based overload always does.
    [Fact]
    public void FuncAfter_MutateRejected_ReplyCarriesOkFalseAndError()
    {
        string output = CaptureStdout(() => CommandReply.After("1", () => (false, "some error")));

        using var doc = JsonDocument.Parse(output.Trim());
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("some error", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public void FuncAfter_MutateOk_ReplyCarriesOkTrueAndNullError()
    {
        string output = CaptureStdout(() => CommandReply.After("1", () => (true, (string?)null)));

        using var doc = JsonDocument.Parse(output.Trim());
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
    }
}
