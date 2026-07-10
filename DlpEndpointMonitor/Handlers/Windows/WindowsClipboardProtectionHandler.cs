using DlpEndpointMonitor.Commands;
using DlpEndpointMonitor.Core;

namespace DlpEndpointMonitor.Handlers.Windows;

sealed class WindowsClipboardProtectionHandler : IClipboardProtectionHandler
{
    readonly ClipboardWhitelist _whitelist;
    readonly ClipboardBlacklist _blacklist;
    readonly Action             _reevaluate;

    // Only ONE delegate, unlike WindowsUsbProtectionHandler's applyPolicy+restoreDevices pair -
    // clipboard has no persisted "this content was blocked" record to restore later. Every
    // clipboard read is live and transient, so every mutation just needs to re-check whatever is
    // CURRENTLY on the clipboard against the (possibly changed) policy and clear it if it now
    // violates.
    public WindowsClipboardProtectionHandler(ClipboardWhitelist whitelist, ClipboardBlacklist blacklist, Action reevaluate)
    {
        _whitelist  = whitelist;
        _blacklist  = blacklist;
        _reevaluate = reevaluate;
    }

    // ── Protection status ─────────────────────────────────────────────────────

    public void Handle(ClipboardProtectionStatusCmd command) =>
        EventEmitter.Emit(new ClipboardProtectionStatusEvent(command.Id, true, _whitelist.IsEnabled, _blacklist.IsEnabled));

    // ── Whitelist ─────────────────────────────────────────────────────────────

    public void Handle(ClipboardWhitelistEnableCmd command) => CommandReply.After(command.Id,
        // Key divergence from device policy: do NOT disable the blacklist here - both clipboard
        // lists are independently toggleable, being enabled at once is a valid, intended state.
        () => _whitelist.SetEnabled(true),
        _reevaluate);

    public void Handle(ClipboardWhitelistDisableCmd command) =>
        CommandReply.After(command.Id, () => _whitelist.SetEnabled(false), _reevaluate);

    public void Handle(ClipboardWhitelistGetCmd command)
    {
        var entries = _whitelist.GetAll()
            .Select(entry => new ClipboardRuleEntryDto(entry.Pattern, entry.Kind, entry.Label));
        EventEmitter.Emit(new ClipboardWhitelistGetEvent(command.Id, true, _whitelist.IsEnabled, entries));
    }

    public void Handle(ClipboardWhitelistClearCmd command) =>
        CommandReply.After(command.Id, () => _whitelist.Clear(), _reevaluate);

    public void Handle(ClipboardWhitelistAddCmd command) => CommandReply.After(command.Id,
        () => _whitelist.Add(new ClipboardRuleEntry(command.Pattern, command.Kind, command.Label)),
        _reevaluate);

    public void Handle(ClipboardWhitelistRemoveCmd command) => CommandReply.After(command.Id,
        () => _whitelist.Remove(command.Pattern, command.Kind),
        _reevaluate);

    public void Handle(ClipboardWhitelistSetCmd command) => CommandReply.After(command.Id,
        () => _whitelist.Set(command.Entries
            .Select(entry => new ClipboardRuleEntry(entry.Pattern, entry.Kind, entry.Label))),
        _reevaluate);

    // ── Blacklist ─────────────────────────────────────────────────────────────

    public void Handle(ClipboardBlacklistEnableCmd command) => CommandReply.After(command.Id,
        // Same divergence as the whitelist side - do NOT disable the whitelist here.
        () => _blacklist.SetEnabled(true),
        _reevaluate);

    public void Handle(ClipboardBlacklistDisableCmd command) =>
        CommandReply.After(command.Id, () => _blacklist.SetEnabled(false), _reevaluate);

    public void Handle(ClipboardBlacklistGetCmd command)
    {
        var entries = _blacklist.GetAll()
            .Select(entry => new ClipboardRuleEntryDto(entry.Pattern, entry.Kind, entry.Label));
        EventEmitter.Emit(new ClipboardBlacklistGetEvent(command.Id, true, _blacklist.IsEnabled, entries));
    }

    public void Handle(ClipboardBlacklistClearCmd command) =>
        CommandReply.After(command.Id, () => _blacklist.Clear(), _reevaluate);

    public void Handle(ClipboardBlacklistAddCmd command) => CommandReply.After(command.Id,
        () => _blacklist.Add(new ClipboardRuleEntry(command.Pattern, command.Kind, command.Label)),
        _reevaluate);

    public void Handle(ClipboardBlacklistRemoveCmd command) => CommandReply.After(command.Id,
        () => _blacklist.Remove(command.Pattern, command.Kind),
        _reevaluate);

    public void Handle(ClipboardBlacklistSetCmd command) => CommandReply.After(command.Id,
        () => _blacklist.Set(command.Entries
            .Select(entry => new ClipboardRuleEntry(entry.Pattern, entry.Kind, entry.Label))),
        _reevaluate);
}
