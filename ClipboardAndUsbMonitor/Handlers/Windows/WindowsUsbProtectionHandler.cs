using ClipboardUsbMonitor.Commands;
using ClipboardUsbMonitor.Core;

namespace ClipboardUsbMonitor.Handlers.Windows;

sealed class WindowsUsbProtectionHandler : IUsbProtectionHandler
{
    readonly UsbWhitelist _whitelist;
    readonly UsbBlacklist _blacklist;
    readonly Action       _applyPolicy;

    public WindowsUsbProtectionHandler(UsbWhitelist whitelist, UsbBlacklist blacklist, Action applyPolicy)
    {
        _whitelist   = whitelist;
        _blacklist   = blacklist;
        _applyPolicy = applyPolicy;
    }

    // ── Protection status ─────────────────────────────────────────────────────

    public void Handle(UsbProtectionStatusCmd command)
    {
        bool whitelistEnabled  = _whitelist.IsEnabled;
        bool blacklistEnabled  = _blacklist.IsEnabled;
        bool conflict          = whitelistEnabled && blacklistEnabled;
        ProtectionMode mode    = conflict         ? ProtectionMode.Conflict
                               : whitelistEnabled ? ProtectionMode.Whitelist
                               : blacklistEnabled ? ProtectionMode.Blacklist
                               : ProtectionMode.None;
        string? error = conflict ? "both whitelist and blacklist are enabled" : null;
        EventEmitter.Emit(new UsbProtectionStatusEvent(command.Id, !conflict, mode, error));
    }

    // ── Whitelist ─────────────────────────────────────────────────────────────

    public void Handle(UsbWhitelistEnableCmd command)
    {
        _blacklist.SetEnabled(false);
        _whitelist.SetEnabled(true);
        EventEmitter.Emit(new ReplyEvent(command.Id, true));
    }

    public void Handle(UsbWhitelistDisableCmd command)
    {
        _whitelist.SetEnabled(false);
        EventEmitter.Emit(new ReplyEvent(command.Id, true));
    }

    public void Handle(UsbWhitelistGetCmd command)
    {
        var entries = _whitelist.GetAll()
            .Select(entry => new WhitelistEntryDto(entry.Vid, entry.Pid, entry.Serial, entry.Kind, entry.Label));
        EventEmitter.Emit(new UsbWhitelistGetEvent(command.Id, true, _whitelist.IsEnabled, entries));
    }

    public void Handle(UsbWhitelistClearCmd command)
    {
        _whitelist.Clear();
        EventEmitter.Emit(new ReplyEvent(command.Id, true));
    }

    public void Handle(UsbWhitelistAddCmd command)
    {
        _whitelist.Add(new UsbDeviceEntry(command.Vid, command.Pid, command.Serial, command.Kind, command.Label));
        EventEmitter.Emit(new ReplyEvent(command.Id, true));
    }

    public void Handle(UsbWhitelistRemoveCmd command)
    {
        _whitelist.Remove(command.Vid, command.Pid, command.Serial, command.Kind);
        EventEmitter.Emit(new ReplyEvent(command.Id, true));
    }

    public void Handle(UsbWhitelistSetCmd command)
    {
        _whitelist.Set(command.Entries
            .Select(entry => new UsbDeviceEntry(entry.Vid, entry.Pid, entry.Serial, entry.Kind, entry.Label))
            .ToList());
        EventEmitter.Emit(new ReplyEvent(command.Id, true));
    }

    // ── Blacklist ─────────────────────────────────────────────────────────────

    public void Handle(UsbBlacklistEnableCmd command)
    {
        _whitelist.SetEnabled(false);
        _blacklist.SetEnabled(true);
        Task.Run(_applyPolicy);
        EventEmitter.Emit(new ReplyEvent(command.Id, true));
    }

    public void Handle(UsbBlacklistDisableCmd command)
    {
        _blacklist.SetEnabled(false);
        EventEmitter.Emit(new ReplyEvent(command.Id, true));
    }

    public void Handle(UsbBlacklistGetCmd command)
    {
        var entries = _blacklist.GetAll()
            .Select(entry => new WhitelistEntryDto(entry.Vid, entry.Pid, entry.Serial, entry.Kind, entry.Label));
        EventEmitter.Emit(new UsbBlacklistGetEvent(command.Id, true, _blacklist.IsEnabled, entries));
    }

    public void Handle(UsbBlacklistClearCmd command)
    {
        _blacklist.Clear();
        EventEmitter.Emit(new ReplyEvent(command.Id, true));
    }

    public void Handle(UsbBlacklistAddCmd command)
    {
        _blacklist.Add(new UsbDeviceEntry(command.Vid, command.Pid, command.Serial, command.Kind, command.Label));
        Task.Run(_applyPolicy);
        EventEmitter.Emit(new ReplyEvent(command.Id, true));
    }

    public void Handle(UsbBlacklistRemoveCmd command)
    {
        _blacklist.Remove(command.Vid, command.Pid, command.Serial, command.Kind);
        EventEmitter.Emit(new ReplyEvent(command.Id, true));
    }

    public void Handle(UsbBlacklistSetCmd command)
    {
        _blacklist.Set(command.Entries
            .Select(entry => new UsbDeviceEntry(entry.Vid, entry.Pid, entry.Serial, entry.Kind, entry.Label))
            .ToList());
        Task.Run(_applyPolicy);
        EventEmitter.Emit(new ReplyEvent(command.Id, true));
    }
}
