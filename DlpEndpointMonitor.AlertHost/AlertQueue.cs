using DlpEndpointMonitor.AlertContracts;

namespace DlpEndpointMonitor.AlertHost;

/// <summary>
/// The single in-memory queue owned by the pipe-owning AlertHost process. A dispatcher loop
/// drains it one alert at a time - never two windows visible at once - applying two policies
/// on the way in:
///   COALESCE - while an alert of the same (Type, Severity) is already queued (or is the one
///   currently being shown and hasn't been dequeued into a fresh pending slot yet), a new
///   request of that same pair does not open a second window; it increments a running count
///   that gets folded into the next shown message's title instead.
///   CAP - at most <see cref="MaxPending"/> distinct pending entries are held; anything beyond
///   that is dropped, and the drop is logged so it is never silent.
/// </summary>
public sealed class AlertQueue : IDisposable
{
    const int MaxPending = 5;

    readonly Action<AlertRequest> _show;
    readonly object _lock = new();
    readonly Queue<PendingAlert> _pending = new();
    readonly Dictionary<(AlertType Type, AlertSeverity Severity), PendingAlert> _pendingByKey = new();
    readonly SemaphoreSlim _signal = new(0);
    readonly CancellationTokenSource _cts = new();
    readonly Task _dispatchLoop;

    sealed class PendingAlert(AlertRequest request)
    {
        public AlertRequest Request { get; set; } = request;
        public int ExtraCount;
    }

    public AlertQueue(Action<AlertRequest> show)
    {
        _show = show;
        _dispatchLoop = Task.Run(() => DispatchLoopAsync(_cts.Token));
    }

    public void Enqueue(AlertRequest request)
    {
        // Id is required (AlertRequest.Id's doc comment) but arrives here over JSON (pipe or
        // --initial-alert), which cannot enforce non-null/non-blank at runtime despite the
        // compile-time `string Id` signature - reject rather than show an uncorrelatable alert.
        if (string.IsNullOrWhiteSpace(request.Id))
        {
            Console.Error.WriteLine(
                $"[AlertHost] discarding alert '{request.Title}' ({request.Type}/{request.Severity}): missing required Id.");
            return;
        }

        lock (_lock)
        {
            var key = (request.Type, request.Severity);
            if (_pendingByKey.TryGetValue(key, out PendingAlert? existing))
            {
                existing.ExtraCount++;
                return;
            }

            if (_pending.Count >= MaxPending)
            {
                Console.Error.WriteLine(
                    $"[AlertHost] queue at capacity ({MaxPending}); dropped alert '{request.Title}' ({request.Type}/{request.Severity}).");
                return;
            }

            var pending = new PendingAlert(request);
            _pendingByKey[key] = pending;
            _pending.Enqueue(pending);
            _signal.Release();
        }
    }

    async Task DispatchLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await _signal.WaitAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            PendingAlert? next;
            lock (_lock)
            {
                next = _pending.Count > 0 ? _pending.Dequeue() : null;
                // Remove from the coalesce map only once dequeued - anything that arrives after
                // this point for the same key starts a fresh pending entry rather than folding
                // into a request that is (or is about to be) already on screen.
                if (next is not null)
                    _pendingByKey.Remove((next.Request.Type, next.Request.Severity));
            }
            if (next is null) continue;

            AlertRequest toShow = next.ExtraCount > 0
                ? next.Request with { Title = $"{next.Request.Title} (+{next.ExtraCount} more)" }
                : next.Request;

            try
            {
                // Expected to block until the alert window is dismissed (Modal: acknowledged;
                // Toast/FullScreen: timer or click) so this loop naturally never shows two
                // windows at once - added in a later iteration, a stub for now.
                _show(toShow);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[AlertHost] failed to show alert: {ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        // Cancelling the token alone unblocks a pending _signal.WaitAsync(token) - no separate
        // Release() needed.
        _cts.Cancel();
        try { _dispatchLoop.Wait(TimeSpan.FromSeconds(2)); } catch { /* best-effort on shutdown */ }
        _cts.Dispose();
        _signal.Dispose();
    }
}
