namespace DlpEndpointMonitor.Core;

/// <summary>
/// Shared "mutate; maybe reconcile; reply ok" shape used by every simple protection-mutating
/// command handler (WindowsUsbProtectionHandler, WindowsClipboardProtectionHandler). Order is
/// load-bearing: mutate must finish before reconcile is scheduled, since reconcile (applyPolicy/
/// restoreDevices/reevaluate) reads the just-mutated policy state to decide what to block/restore
/// - scheduling it first would have it act on stale, pre-mutation state.
/// </summary>
static class CommandReply
{
    public static void After(string? commandId, Action mutate, Action? reconcile = null)
    {
        mutate();
        if (reconcile is not null) Task.Run(reconcile);
        EventEmitter.Emit(new ReplyEvent(commandId, true));
    }

    /// <summary>
    /// Same shape as the Action-based overload above, but for a mutate step that can itself fail
    /// validation (e.g. UsbDeviceList.Add/Remove/Set rejecting an all-null-fields input) - reconcile
    /// only runs when mutate() actually changed something, and the reply carries the real (ok, error)
    /// instead of always claiming success.
    /// </summary>
    public static void After(string? commandId, Func<(bool ok, string? error)> mutate, Action? reconcile = null)
    {
        var (ok, error) = mutate();
        if (ok && reconcile is not null) Task.Run(reconcile);
        EventEmitter.Emit(new ReplyEvent(commandId, ok, error));
    }
}
