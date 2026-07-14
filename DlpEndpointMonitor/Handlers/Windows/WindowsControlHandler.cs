using DlpEndpointMonitor.Commands;
using DlpEndpointMonitor.Core;

namespace DlpEndpointMonitor.Handlers.Windows;

sealed class WindowsControlHandler : IControlHandler
{
    readonly Action             _stopCompanion;
    readonly DeviceWhitelist    _whitelist;
    readonly DeviceBlacklist    _blacklist;
    readonly ClipboardWhitelist _clipboardWhitelist;
    readonly ClipboardBlacklist _clipboardBlacklist;
    readonly ScreenshotBlockPolicy _screenshotBlockPolicy;
    readonly Action             _restoreDevices;
    readonly Action             _clipboardReevaluate;

    // stopCompanion: kills this primary's own --session-companion process before exiting, so an
    // explicit shutdown command doesn't leave it running as an orphan for the next start to find -
    // see SessionActions.TerminateCompanionProcesses. A no-op if no companion was launched.
    // The four lists + two reconcile delegates are the exact same instances/delegates already
    // wired into WindowsUsbProtectionHandler/WindowsClipboardProtectionHandler in Program.cs -
    // ResetAllPolicyCmd reuses them rather than owning a second copy of any policy state.
    // screenshotBlockPolicy: the same instance wired into WindowsScreenshotProtectionHandler -
    // reset_all_policy means every policy domain, not just device/clipboard (see
    // UNINSTALL-POLICY-CLEANUP-FIX-PLAN.md - screenshot-block-off is folded in here rather than
    // added as a fourth explicit command from the Node side).
    public WindowsControlHandler(
        Action stopCompanion,
        DeviceWhitelist whitelist, DeviceBlacklist blacklist,
        ClipboardWhitelist clipboardWhitelist, ClipboardBlacklist clipboardBlacklist,
        ScreenshotBlockPolicy screenshotBlockPolicy,
        Action restoreDevices, Action clipboardReevaluate)
    {
        _stopCompanion          = stopCompanion;
        _whitelist              = whitelist;
        _blacklist              = blacklist;
        _clipboardWhitelist     = clipboardWhitelist;
        _clipboardBlacklist     = clipboardBlacklist;
        _screenshotBlockPolicy  = screenshotBlockPolicy;
        _restoreDevices         = restoreDevices;
        _clipboardReevaluate    = clipboardReevaluate;
    }

    public void Handle(PingCmd command) =>
        EventEmitter.Emit(new ReplyEvent(command.Id, true));

    public void Handle(ShutdownCmd command)
    {
        EventEmitter.Emit(new ReplyEvent(command.Id, true));
        _stopCompanion();
        Environment.Exit(0);
    }

    // Clears all four lists in one call, each exactly as its own individual *Clear command
    // already would - device whitelist also disables itself (an enabled-but-empty whitelist is
    // deny-all, same "factory reset" reasoning as DeviceWhitelistClearCmd), the other three only
    // empty (already the loosest state for a blacklist / for clipboard's non-conflicting model),
    // matching DeviceBlacklistClearCmd/ClipboardWhitelistClearCmd/ClipboardBlacklistClearCmd
    // exactly. This command does not replace those - each keeps working unchanged on its own.
    // Screenshot-block is a bare enable/disable with no entries (ScreenshotBlockDisableCmd's own
    // semantics), so "reset" for it is just SetEnabled(false) - folded in here rather than a
    // separate command, so reset_all_policy means every policy domain, full stop.
    public void Handle(ResetAllPolicyCmd command) => CommandReply.After(command.Id,
        () =>
        {
            _whitelist.SetEnabled(false);
            _whitelist.Clear();
            _blacklist.Clear();
            _clipboardWhitelist.Clear();
            _clipboardBlacklist.Clear();
            _screenshotBlockPolicy.SetEnabled(false);
        },
        () => { _restoreDevices(); _clipboardReevaluate(); });
}
