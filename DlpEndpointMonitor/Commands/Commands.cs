using DlpEndpointMonitor.Core;

namespace DlpEndpointMonitor.Commands;

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

[JsonDiscriminant(CommandType.DeviceDisable)]
record DeviceDisableCmd(string? Id, string InstanceId) : ICommand;

[JsonDiscriminant(CommandType.DeviceEnable)]
record DeviceEnableCmd(string? Id, string InstanceId) : ICommand;

// ── USB — protection status ───────────────────────────────────────────────────

[JsonDiscriminant(CommandType.DeviceProtectionStatus)]
[EmitsEvent(EventType.DeviceProtectionStatus)]
record DeviceProtectionStatusCmd(string? Id) : ICommand;

// ── USB — whitelist ───────────────────────────────────────────────────────────

[JsonDiscriminant(CommandType.DeviceWhitelistEnable)]
record DeviceWhitelistEnableCmd(string? Id) : ICommand;

[JsonDiscriminant(CommandType.DeviceWhitelistDisable)]
record DeviceWhitelistDisableCmd(string? Id) : ICommand;

[JsonDiscriminant(CommandType.DeviceWhitelistGet)]
[EmitsEvent(EventType.DeviceWhitelistGet)]
record DeviceWhitelistGetCmd(string? Id) : ICommand;

[JsonDiscriminant(CommandType.DeviceWhitelistClear)]
record DeviceWhitelistClearCmd(string? Id) : ICommand;

[JsonDiscriminant(CommandType.DeviceWhitelistAdd)]
record DeviceWhitelistAddCmd(string? Id, string? Vid = null, string? Pid = null, string? Serial = null, string? Mac = null, DeviceKind? Kind = null, string? Label = null) : ICommand;

[JsonDiscriminant(CommandType.DeviceWhitelistRemove)]
record DeviceWhitelistRemoveCmd(string? Id, string? Vid = null, string? Pid = null, string? Serial = null, string? Mac = null, DeviceKind? Kind = null) : ICommand;

[JsonDiscriminant(CommandType.DeviceWhitelistSet)]
record DeviceWhitelistSetCmd(string? Id, DeviceEntryDto[] Entries) : ICommand;

// ── USB — blacklist ───────────────────────────────────────────────────────────

[JsonDiscriminant(CommandType.DeviceBlacklistEnable)]
record DeviceBlacklistEnableCmd(string? Id) : ICommand;

[JsonDiscriminant(CommandType.DeviceBlacklistDisable)]
record DeviceBlacklistDisableCmd(string? Id) : ICommand;

[JsonDiscriminant(CommandType.DeviceBlacklistGet)]
[EmitsEvent(EventType.DeviceBlacklistGet)]
record DeviceBlacklistGetCmd(string? Id) : ICommand;

[JsonDiscriminant(CommandType.DeviceBlacklistClear)]
record DeviceBlacklistClearCmd(string? Id) : ICommand;

[JsonDiscriminant(CommandType.DeviceBlacklistAdd)]
record DeviceBlacklistAddCmd(string? Id, string? Vid = null, string? Pid = null, string? Serial = null, string? Mac = null, DeviceKind? Kind = null, string? Label = null) : ICommand;

[JsonDiscriminant(CommandType.DeviceBlacklistRemove)]
record DeviceBlacklistRemoveCmd(string? Id, string? Vid = null, string? Pid = null, string? Serial = null, string? Mac = null, DeviceKind? Kind = null) : ICommand;

[JsonDiscriminant(CommandType.DeviceBlacklistSet)]
record DeviceBlacklistSetCmd(string? Id, DeviceEntryDto[] Entries) : ICommand;

// ── Control ───────────────────────────────────────────────────────────────────

[JsonDiscriminant(CommandType.Ping)]
record PingCmd(string? Id) : ICommand;

[JsonDiscriminant(CommandType.Shutdown)]
record ShutdownCmd(string? Id) : ICommand;
