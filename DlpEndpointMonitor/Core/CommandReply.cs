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
}
