using DlpEndpointMonitor.Commands;
using DlpEndpointMonitor.Core;

namespace DlpEndpointMonitor.Handlers.Windows;

sealed class WindowsUsbProtectionHandler : IUsbProtectionHandler
{
    readonly DeviceWhitelist _whitelist;
    readonly DeviceBlacklist _blacklist;
    readonly Action       _applyPolicy;
    readonly Action       _restoreDevices;

    public WindowsUsbProtectionHandler(DeviceWhitelist whitelist, DeviceBlacklist blacklist, Action applyPolicy, Action restoreDevices)
    {
        _whitelist      = whitelist;
        _blacklist      = blacklist;
        _applyPolicy    = applyPolicy;
        _restoreDevices = restoreDevices;
    }

    // ── Protection status ─────────────────────────────────────────────────────

    public void Handle(DeviceProtectionStatusCmd command)
    {
        bool whitelistEnabled  = _whitelist.IsEnabled;
        bool blacklistEnabled  = _blacklist.IsEnabled;
        bool conflict          = whitelistEnabled && blacklistEnabled;
        ProtectionMode mode    = conflict         ? ProtectionMode.Conflict
                               : whitelistEnabled ? ProtectionMode.Whitelist
                               : blacklistEnabled ? ProtectionMode.Blacklist
                               : ProtectionMode.None;
        string? error = conflict ? "both whitelist and blacklist are enabled" : null;
        EventEmitter.Emit(new DeviceProtectionStatusEvent(command.Id, !conflict, mode, error));
    }

    // ── Whitelist ─────────────────────────────────────────────────────────────

    public void Handle(DeviceWhitelistEnableCmd command)
    {
        _blacklist.SetEnabled(false);
        _whitelist.SetEnabled(true);
        // Enforce immediately on already-connected devices (mirrors blacklist enable) so
        // switching to Whitelist mode blocks non-allowed devices without waiting for a replug.
        Task.Run(_applyPolicy);
        EventEmitter.Emit(new ReplyEvent(command.Id, true));
    }

    public void Handle(DeviceWhitelistDisableCmd command)
    {
        _whitelist.SetEnabled(false);
        Task.Run(_restoreDevices);
        EventEmitter.Emit(new ReplyEvent(command.Id, true));
    }

    public void Handle(DeviceWhitelistGetCmd command)
    {
        var entries = _whitelist.GetAll()
            .Select(entry => new WhitelistEntryDto(entry.Vid, entry.Pid, entry.Serial, entry.Mac, entry.Kind, entry.Label));
        EventEmitter.Emit(new DeviceWhitelistGetEvent(command.Id, true, _whitelist.IsEnabled, entries));
    }

    public void Handle(DeviceWhitelistClearCmd command)
    {
        // Factory reset (criterion 3): also DISABLE the list. An enabled-but-empty whitelist is
        // deny-all (IsAllowed matches nothing), so a bare Clear() would leave every device the
        // whitelist blocked still disabled - RestoreCompliant would re-enable nothing. Disabling
        // makes IsAllowed return true so restore re-enables all previously-blocked devices.
        _whitelist.SetEnabled(false);
        _whitelist.Clear();
        Task.Run(_restoreDevices);
        EventEmitter.Emit(new ReplyEvent(command.Id, true));
    }

    public void Handle(DeviceWhitelistAddCmd command)
    {
        _whitelist.Add(new UsbDeviceEntry(command.Vid, command.Pid, command.Serial, command.Mac, command.Kind, command.Label));
        // Adding a whitelist entry allows a previously-blocked device - restore it.
        Task.Run(_restoreDevices);
        EventEmitter.Emit(new ReplyEvent(command.Id, true));
    }

    public void Handle(DeviceWhitelistRemoveCmd command)
    {
        _whitelist.Remove(command.Vid, command.Pid, command.Serial, command.Mac, command.Kind);
        EventEmitter.Emit(new ReplyEvent(command.Id, true));
    }

    public void Handle(DeviceWhitelistSetCmd command)
    {
        _whitelist.Set(command.Entries
            .Select(entry => new UsbDeviceEntry(entry.Vid, entry.Pid, entry.Serial, entry.Mac, entry.Kind, entry.Label))
            .ToList());
        // A `set` can both loosen and tighten: restore re-enables now-allowed devices, apply
        // blocks newly-disallowed ones. They act on disjoint sets, so restore-then-apply is safe.
        Task.Run(() => { _restoreDevices(); _applyPolicy(); });
        EventEmitter.Emit(new ReplyEvent(command.Id, true));
    }

    // ── Blacklist ─────────────────────────────────────────────────────────────

    public void Handle(DeviceBlacklistEnableCmd command)
    {
        _whitelist.SetEnabled(false);
        _blacklist.SetEnabled(true);
        Task.Run(_applyPolicy);
        EventEmitter.Emit(new ReplyEvent(command.Id, true));
    }

    public void Handle(DeviceBlacklistDisableCmd command)
    {
        _blacklist.SetEnabled(false);
        Task.Run(_restoreDevices);
        EventEmitter.Emit(new ReplyEvent(command.Id, true));
    }

    public void Handle(DeviceBlacklistGetCmd command)
    {
        var entries = _blacklist.GetAll()
            .Select(entry => new WhitelistEntryDto(entry.Vid, entry.Pid, entry.Serial, entry.Mac, entry.Kind, entry.Label));
        EventEmitter.Emit(new DeviceBlacklistGetEvent(command.Id, true, _blacklist.IsEnabled, entries));
    }

    public void Handle(DeviceBlacklistClearCmd command)
    {
        _blacklist.Clear();
        Task.Run(_restoreDevices);
        EventEmitter.Emit(new ReplyEvent(command.Id, true));
    }

    public void Handle(DeviceBlacklistAddCmd command)
    {
        _blacklist.Add(new UsbDeviceEntry(command.Vid, command.Pid, command.Serial, command.Mac, command.Kind, command.Label));
        Task.Run(_applyPolicy);
        EventEmitter.Emit(new ReplyEvent(command.Id, true));
    }

    public void Handle(DeviceBlacklistRemoveCmd command)
    {
        _blacklist.Remove(command.Vid, command.Pid, command.Serial, command.Mac, command.Kind);
        // Removing a blacklist entry un-blocks whatever it matched - restore those devices.
        Task.Run(_restoreDevices);
        EventEmitter.Emit(new ReplyEvent(command.Id, true));
    }

    public void Handle(DeviceBlacklistSetCmd command)
    {
        _blacklist.Set(command.Entries
            .Select(entry => new UsbDeviceEntry(entry.Vid, entry.Pid, entry.Serial, entry.Mac, entry.Kind, entry.Label))
            .ToList());
        // A `set` can drop entries that were blocking connected devices; restore re-enables those,
        // apply blocks any newly-listed ones. Restore-then-apply (was apply-only, which never
        // re-enabled devices a set now allows).
        Task.Run(() => { _restoreDevices(); _applyPolicy(); });
        EventEmitter.Emit(new ReplyEvent(command.Id, true));
    }
}
