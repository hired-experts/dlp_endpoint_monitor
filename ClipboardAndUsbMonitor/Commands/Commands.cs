using ClipboardUsbMonitor.Core;

namespace ClipboardUsbMonitor.Commands;

public interface ICommand { }

// ── Shared ────────────────────────────────────────────────────────────────────

record DeviceEntryDto(string? Vid, string? Pid, string? Serial, string? Mac, DeviceKind? Kind, string? Label);

// ── Clipboard ─────────────────────────────────────────────────────────────────

[JsonDiscriminant(CommandType.ClipboardRead)]
[EmitsEvent(EventType.ClipboardRead)]
record ClipboardReadCmd(string? Id) : ICommand;

[JsonDiscriminant(CommandType.ClipboardSet)]
record ClipboardSetCmd(string? Id, string Content) : ICommand;

[JsonDiscriminant(CommandType.ClipboardClear)]
record ClipboardClearCmd(string? Id) : ICommand;

// ── USB — storage ─────────────────────────────────────────────────────────────

[JsonDiscriminant(CommandType.UsbEject)]
record UsbEjectCmd(string? Id, string Drive) : ICommand;

[JsonDiscriminant(CommandType.UsbDisableStorage)]
record UsbDisableStorageCmd(string? Id) : ICommand;

[JsonDiscriminant(CommandType.UsbEnableStorage)]
record UsbEnableStorageCmd(string? Id) : ICommand;

[JsonDiscriminant(CommandType.UsbStorageStatus)]
[EmitsEvent(EventType.UsbStorageStatus)]
record UsbStorageStatusCmd(string? Id) : ICommand;

// ── USB — device level ────────────────────────────────────────────────────────

[JsonDiscriminant(CommandType.UsbDeviceDisable)]
record UsbDeviceDisableCmd(string? Id, string InstanceId) : ICommand;

[JsonDiscriminant(CommandType.UsbDeviceEnable)]
record UsbDeviceEnableCmd(string? Id, string InstanceId) : ICommand;

// ── USB — protection status ───────────────────────────────────────────────────

[JsonDiscriminant(CommandType.UsbProtectionStatus)]
[EmitsEvent(EventType.UsbProtectionStatus)]
record UsbProtectionStatusCmd(string? Id) : ICommand;

// ── USB — whitelist ───────────────────────────────────────────────────────────

[JsonDiscriminant(CommandType.UsbWhitelistEnable)]
record UsbWhitelistEnableCmd(string? Id) : ICommand;

[JsonDiscriminant(CommandType.UsbWhitelistDisable)]
record UsbWhitelistDisableCmd(string? Id) : ICommand;

[JsonDiscriminant(CommandType.UsbWhitelistGet)]
[EmitsEvent(EventType.UsbWhitelistGet)]
record UsbWhitelistGetCmd(string? Id) : ICommand;

[JsonDiscriminant(CommandType.UsbWhitelistClear)]
record UsbWhitelistClearCmd(string? Id) : ICommand;

[JsonDiscriminant(CommandType.UsbWhitelistAdd)]
record UsbWhitelistAddCmd(string? Id, string? Vid = null, string? Pid = null, string? Serial = null, string? Mac = null, DeviceKind? Kind = null, string? Label = null) : ICommand;

[JsonDiscriminant(CommandType.UsbWhitelistRemove)]
record UsbWhitelistRemoveCmd(string? Id, string? Vid = null, string? Pid = null, string? Serial = null, string? Mac = null, DeviceKind? Kind = null) : ICommand;

[JsonDiscriminant(CommandType.UsbWhitelistSet)]
record UsbWhitelistSetCmd(string? Id, DeviceEntryDto[] Entries) : ICommand;

// ── USB — blacklist ───────────────────────────────────────────────────────────

[JsonDiscriminant(CommandType.UsbBlacklistEnable)]
record UsbBlacklistEnableCmd(string? Id) : ICommand;

[JsonDiscriminant(CommandType.UsbBlacklistDisable)]
record UsbBlacklistDisableCmd(string? Id) : ICommand;

[JsonDiscriminant(CommandType.UsbBlacklistGet)]
[EmitsEvent(EventType.UsbBlacklistGet)]
record UsbBlacklistGetCmd(string? Id) : ICommand;

[JsonDiscriminant(CommandType.UsbBlacklistClear)]
record UsbBlacklistClearCmd(string? Id) : ICommand;

[JsonDiscriminant(CommandType.UsbBlacklistAdd)]
record UsbBlacklistAddCmd(string? Id, string? Vid = null, string? Pid = null, string? Serial = null, string? Mac = null, DeviceKind? Kind = null, string? Label = null) : ICommand;

[JsonDiscriminant(CommandType.UsbBlacklistRemove)]
record UsbBlacklistRemoveCmd(string? Id, string? Vid = null, string? Pid = null, string? Serial = null, string? Mac = null, DeviceKind? Kind = null) : ICommand;

[JsonDiscriminant(CommandType.UsbBlacklistSet)]
record UsbBlacklistSetCmd(string? Id, DeviceEntryDto[] Entries) : ICommand;

// ── Control ───────────────────────────────────────────────────────────────────

[JsonDiscriminant(CommandType.Ping)]
record PingCmd(string? Id) : ICommand;

[JsonDiscriminant(CommandType.Shutdown)]
record ShutdownCmd(string? Id) : ICommand;
