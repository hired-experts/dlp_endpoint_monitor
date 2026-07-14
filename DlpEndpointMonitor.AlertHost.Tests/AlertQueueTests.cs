using System.IO;
using DlpEndpointMonitor.AlertContracts;
using Xunit;

namespace DlpEndpointMonitor.AlertHost.Tests;

// Covers AlertQueue's two enqueue-time policies only (docs/TEST-PLAN.md-style split: the
// dispatcher loop's blocking "show one at a time" behavior is exercised as a side effect of
// these tests, but a real WPF window is never involved - see AGENTS.md section 8.1/CRITERIA).
public class AlertQueueTests
{
    static AlertRequest Make(AlertType type, AlertSeverity severity, string title) =>
        new(type, title, "message", "evt-test-id", severity);

    // T-AQ-01: a second/third request sharing (Type, Severity) with an entry still sitting in
    // the pending queue (not yet dequeued for display) folds into that entry's count instead of
    // opening its own window - the count is appended to the title when it is finally shown.
    [Fact]
    public void Enqueue_SameKeyWhileStillPending_CoalescesIntoNextShownTitle()
    {
        var shown = new List<AlertRequest>();
        var showEntered = new SemaphoreSlim(0);
        var releaseShow = new SemaphoreSlim(0);

        void Show(AlertRequest request)
        {
            lock (shown) shown.Add(request);
            showEntered.Release();
            // Blocks the dispatch loop here so the test controls exactly when the next pending
            // entry is dequeued, making the coalesce window deterministic instead of racy.
            Assert.True(releaseShow.Wait(TimeSpan.FromSeconds(5)));
        }

        using var queue = new AlertQueue(Show);

        // Dequeued and shown immediately - the dispatch loop removes it from the coalesce map
        // the moment it starts showing, so anything enqueued after this point starts fresh.
        queue.Enqueue(Make(AlertType.Toast, AlertSeverity.Warning, "First"));
        Assert.True(showEntered.Wait(TimeSpan.FromSeconds(5)));

        // New pending entry for the (Toast, Warning) key - nothing to coalesce into yet.
        queue.Enqueue(Make(AlertType.Toast, AlertSeverity.Warning, "Second"));
        // Same key, arrives while "Second" is still pending (not dequeued) - folds in as +1.
        queue.Enqueue(Make(AlertType.Toast, AlertSeverity.Warning, "Third"));
        // A different key must never coalesce with the above, regardless of ordering.
        queue.Enqueue(Make(AlertType.FullScreen, AlertSeverity.Blocked, "Unrelated"));

        releaseShow.Release();
        Assert.True(showEntered.Wait(TimeSpan.FromSeconds(5)));
        releaseShow.Release();
        Assert.True(showEntered.Wait(TimeSpan.FromSeconds(5)));
        releaseShow.Release();

        lock (shown)
        {
            Assert.Equal(3, shown.Count);
            Assert.Equal("First", shown[0].Title);
            Assert.Equal("Second (+1 more)", shown[1].Title);
            Assert.Equal("Unrelated", shown[2].Title);
        }
    }

    // T-AQ-02: once MaxPending (5) distinct-key entries are already queued, a further
    // distinct-key entry is dropped rather than queued, and the drop is logged to Console.Error
    // so it is never silent (CRITERIA: "no silent caps").
    [Fact]
    public void Enqueue_BeyondCap_DropsExcessAndLogsToConsoleError()
    {
        var showEntered = new SemaphoreSlim(0);
        var releaseShow = new SemaphoreSlim(0);

        void Show(AlertRequest request)
        {
            showEntered.Release();
            Assert.True(releaseShow.Wait(TimeSpan.FromSeconds(5)));
        }

        // Only 6 distinct (Type, Severity) keys exist (2 types x 3 severities): one to occupy
        // "currently showing", five to fill the pending cap. The overflow entry below reuses
        // combos[0]'s key rather than needing a 7th distinct combo - that key is removed from
        // the coalesce map the instant "Showing" is dequeued (see AlertQueue.DispatchLoopAsync),
        // so it is free again and this is a genuinely new pending entry, not a coalesce.
        (AlertType Type, AlertSeverity Severity)[] combos =
        [
            (AlertType.Toast, AlertSeverity.Info),
            (AlertType.Toast, AlertSeverity.Warning),
            (AlertType.Toast, AlertSeverity.Blocked),
            (AlertType.FullScreen, AlertSeverity.Info),
            (AlertType.FullScreen, AlertSeverity.Warning),
            (AlertType.FullScreen, AlertSeverity.Blocked),
        ];

        using var queue = new AlertQueue(Show);

        queue.Enqueue(Make(combos[0].Type, combos[0].Severity, "Showing"));
        Assert.True(showEntered.Wait(TimeSpan.FromSeconds(5)));

        for (int i = 1; i <= 5; i++)
            queue.Enqueue(Make(combos[i].Type, combos[i].Severity, $"Pending{i}"));

        var originalError = Console.Error;
        using var errorWriter = new StringWriter();
        Console.SetError(errorWriter);
        try
        {
            // Pending queue is already at MaxPending (5) - this 6th distinct-key entry must be
            // dropped, not queued as a 6th pending slot.
            queue.Enqueue(Make(combos[0].Type, combos[0].Severity, "Overflow"));
        }
        finally
        {
            Console.SetError(originalError);
        }

        releaseShow.Release();

        string logged = errorWriter.ToString();
        Assert.Contains("Overflow", logged);
        Assert.Contains("dropped", logged, StringComparison.OrdinalIgnoreCase);

        // Drain the remaining 5 pending shows so the dispatch loop is idle before Dispose runs.
        for (int i = 0; i < 5; i++)
        {
            Assert.True(showEntered.Wait(TimeSpan.FromSeconds(5)));
            releaseShow.Release();
        }
    }

    // T-AQ-03: Id is required for every alert type (AlertRequest.Id's doc comment) - a request
    // arriving with a null/blank Id (the compile-time `string Id` signature does not stop this
    // over JSON) is discarded before ever reaching Show, and the discard is logged rather than
    // silent (same "no silent caps" discipline as T-AQ-02).
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Enqueue_MissingId_DiscardsWithoutShowingAndLogsToConsoleError(string? id)
    {
        var shown = new List<AlertRequest>();
        void Show(AlertRequest request) { lock (shown) shown.Add(request); }

        using var queue = new AlertQueue(Show);

        var originalError = Console.Error;
        using var errorWriter = new StringWriter();
        Console.SetError(errorWriter);
        try
        {
            queue.Enqueue(new AlertRequest(AlertType.Toast, "No Id", "message", id!, AlertSeverity.Info));
        }
        finally
        {
            Console.SetError(originalError);
        }

        string logged = errorWriter.ToString();
        Assert.Contains("missing required Id", logged, StringComparison.OrdinalIgnoreCase);
        lock (shown) Assert.Empty(shown);
    }

    // T-AQ-04: covers the dispatch-loop-fault observability fix
    // (ALERTHOST-STALE-PROCESS-FIX-PLAN.md section 4 step 4). Forcing the private _dispatchLoop
    // Task itself to fault isn't something a unit test can cleanly do (its only failure surface is
    // an unexpected exception from SemaphoreSlim.WaitAsync) - instead this isolates the one pure,
    // directly-callable piece of the fix: the log-line formatting the OnlyOnFaulted continuation
    // hands a real exception to. The file-write half (LogDispatchLoopFault) is intentionally left
    // untested here since it touches the real %ProgramData%\DlpEndpointMonitor location.
    [Fact]
    public void FormatFaultLogLine_IncludesTimestampAndExceptionDetails()
    {
        var fault = new InvalidOperationException("boom");

        string line = AlertQueue.FormatFaultLogLine(fault);

        Assert.Contains("AlertQueue dispatch loop faulted", line);
        Assert.Contains(nameof(InvalidOperationException), line);
        Assert.Contains("boom", line);
        Assert.EndsWith(Environment.NewLine, line);

        // The leading timestamp must be genuinely parseable ("O" round-trip format), not just
        // present-looking text - a log a human/tool can't parse the time out of is half as useful.
        string timestampPart = line[..line.IndexOf(' ')];
        Assert.True(DateTimeOffset.TryParse(timestampPart, out _));
    }
}
