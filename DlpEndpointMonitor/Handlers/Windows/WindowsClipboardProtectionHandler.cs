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

    public void Handle(ClipboardWhitelistEnableCmd command)
    {
        // Key divergence from device policy: do NOT disable the blacklist here - both clipboard
        // lists are independently toggleable, being enabled at once is a valid, intended state.
        _whitelist.SetEnabled(true);
        Task.Run(_reevaluate);
        EventEmitter.Emit(new ReplyEvent(command.Id, true));
    }

    public void Handle(ClipboardWhitelistDisableCmd command)
    {
        _whitelist.SetEnabled(false);
        Task.Run(_reevaluate);
        EventEmitter.Emit(new ReplyEvent(command.Id, true));
    }

    public void Handle(ClipboardWhitelistGetCmd command)
    {
        var entries = _whitelist.GetAll()
            .Select(entry => new ClipboardRuleEntryDto(entry.Pattern, entry.Kind, entry.Label));
        EventEmitter.Emit(new ClipboardWhitelistGetEvent(command.Id, true, _whitelist.IsEnabled, entries));
    }

    public void Handle(ClipboardWhitelistClearCmd command)
    {
        _whitelist.Clear();
        Task.Run(_reevaluate);
        EventEmitter.Emit(new ReplyEvent(command.Id, true));
    }

    public void Handle(ClipboardWhitelistAddCmd command)
    {
        _whitelist.Add(new ClipboardRuleEntry(command.Pattern, command.Kind, command.Label));
        Task.Run(_reevaluate);
        EventEmitter.Emit(new ReplyEvent(command.Id, true));
    }

    public void Handle(ClipboardWhitelistRemoveCmd command)
    {
        _whitelist.Remove(command.Pattern, command.Kind);
        Task.Run(_reevaluate);
        EventEmitter.Emit(new ReplyEvent(command.Id, true));
    }

    public void Handle(ClipboardWhitelistSetCmd command)
    {
        _whitelist.Set(command.Entries
            .Select(entry => new ClipboardRuleEntry(entry.Pattern, entry.Kind, entry.Label)));
        Task.Run(_reevaluate);
        EventEmitter.Emit(new ReplyEvent(command.Id, true));
    }

    // ── Blacklist ─────────────────────────────────────────────────────────────

    public void Handle(ClipboardBlacklistEnableCmd command)
    {
        // Same divergence as the whitelist side - do NOT disable the whitelist here.
        _blacklist.SetEnabled(true);
        Task.Run(_reevaluate);
        EventEmitter.Emit(new ReplyEvent(command.Id, true));
    }

    public void Handle(ClipboardBlacklistDisableCmd command)
    {
        _blacklist.SetEnabled(false);
        Task.Run(_reevaluate);
        EventEmitter.Emit(new ReplyEvent(command.Id, true));
    }

    public void Handle(ClipboardBlacklistGetCmd command)
    {
        var entries = _blacklist.GetAll()
            .Select(entry => new ClipboardRuleEntryDto(entry.Pattern, entry.Kind, entry.Label));
        EventEmitter.Emit(new ClipboardBlacklistGetEvent(command.Id, true, _blacklist.IsEnabled, entries));
    }

    public void Handle(ClipboardBlacklistClearCmd command)
    {
        _blacklist.Clear();
        Task.Run(_reevaluate);
        EventEmitter.Emit(new ReplyEvent(command.Id, true));
    }

    public void Handle(ClipboardBlacklistAddCmd command)
    {
        _blacklist.Add(new ClipboardRuleEntry(command.Pattern, command.Kind, command.Label));
        Task.Run(_reevaluate);
        EventEmitter.Emit(new ReplyEvent(command.Id, true));
    }

    public void Handle(ClipboardBlacklistRemoveCmd command)
    {
        _blacklist.Remove(command.Pattern, command.Kind);
        Task.Run(_reevaluate);
        EventEmitter.Emit(new ReplyEvent(command.Id, true));
    }

    public void Handle(ClipboardBlacklistSetCmd command)
    {
        _blacklist.Set(command.Entries
            .Select(entry => new ClipboardRuleEntry(entry.Pattern, entry.Kind, entry.Label)));
        Task.Run(_reevaluate);
        EventEmitter.Emit(new ReplyEvent(command.Id, true));
    }
}
