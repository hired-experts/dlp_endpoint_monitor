using DlpEndpointMonitor.AlertContracts;
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

// ── Clipboard protection ──────────────────────────────────────────────────────

record ClipboardRuleDto(string Pattern, ClipboardKind? Kind, string? Label);

[JsonDiscriminant(CommandType.ClipboardProtectionStatus)]
[EmitsEvent(EventType.ClipboardProtectionStatus)]
record ClipboardProtectionStatusCmd(string? Id) : ICommand;

[JsonDiscriminant(CommandType.ClipboardWhitelistEnable)]
record ClipboardWhitelistEnableCmd(string? Id) : ICommand;

[JsonDiscriminant(CommandType.ClipboardWhitelistDisable)]
record ClipboardWhitelistDisableCmd(string? Id) : ICommand;

[JsonDiscriminant(CommandType.ClipboardWhitelistGet)]
[EmitsEvent(EventType.ClipboardWhitelistGet)]
record ClipboardWhitelistGetCmd(string? Id) : ICommand;

[JsonDiscriminant(CommandType.ClipboardWhitelistClear)]
record ClipboardWhitelistClearCmd(string? Id) : ICommand;

[JsonDiscriminant(CommandType.ClipboardWhitelistAdd)]
record ClipboardWhitelistAddCmd(string? Id, string Pattern, ClipboardKind? Kind = null, string? Label = null) : ICommand;

[JsonDiscriminant(CommandType.ClipboardWhitelistRemove)]
record ClipboardWhitelistRemoveCmd(string? Id, string Pattern, ClipboardKind? Kind = null) : ICommand;

[JsonDiscriminant(CommandType.ClipboardWhitelistSet)]
record ClipboardWhitelistSetCmd(string? Id, ClipboardRuleDto[] Entries) : ICommand;

[JsonDiscriminant(CommandType.ClipboardBlacklistEnable)]
record ClipboardBlacklistEnableCmd(string? Id) : ICommand;

[JsonDiscriminant(CommandType.ClipboardBlacklistDisable)]
record ClipboardBlacklistDisableCmd(string? Id) : ICommand;

[JsonDiscriminant(CommandType.ClipboardBlacklistGet)]
[EmitsEvent(EventType.ClipboardBlacklistGet)]
record ClipboardBlacklistGetCmd(string? Id) : ICommand;

[JsonDiscriminant(CommandType.ClipboardBlacklistClear)]
record ClipboardBlacklistClearCmd(string? Id) : ICommand;

[JsonDiscriminant(CommandType.ClipboardBlacklistAdd)]
record ClipboardBlacklistAddCmd(string? Id, string Pattern, ClipboardKind? Kind = null, string? Label = null) : ICommand;

[JsonDiscriminant(CommandType.ClipboardBlacklistRemove)]
record ClipboardBlacklistRemoveCmd(string? Id, string Pattern, ClipboardKind? Kind = null) : ICommand;

[JsonDiscriminant(CommandType.ClipboardBlacklistSet)]
record ClipboardBlacklistSetCmd(string? Id, ClipboardRuleDto[] Entries) : ICommand;

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

// Clears every policy list at once - device whitelist, device blacklist, clipboard whitelist,
// clipboard blacklist - each exactly as its own individual *Clear command already would (device
// whitelist also disables itself, matching DeviceWhitelistClearCmd's factory-reset semantics;
// the other three lists only empty, matching their own individual Clear commands). Not a
// replacement for those - each keeps working unchanged on its own; this is a single combined
// call for when the caller wants every list cleared at once rather than four round trips.
[JsonDiscriminant(CommandType.ResetAllPolicy)]
record ResetAllPolicyCmd(string? Id) : ICommand;

// SourceEventId is deliberately NOT the same field as Id: Id is the ordinary reply-correlation
// id every command has; SourceEventId is the EventId of the blocked/detected event this alert is
// about (set by the agent) and maps into AlertRequest.Id, the alert-coalescing key AlertHost uses.
[JsonDiscriminant(CommandType.ShowAlert)]
record ShowAlertCmd(string? Id, string SourceEventId, string Title, string Message, AlertType Type = AlertType.Toast, AlertSeverity Severity = AlertSeverity.Blocked, int? DurationSeconds = null) : ICommand;

// ── Screenshot protection ─────────────────────────────────────────────────────
// Enable/disable only, like UsbEnableStorageCmd/UsbDisableStorageCmd/UsbStorageStatusCmd - not
// DeviceWhitelist/BlacklistCmd's shape. There is no entry list to match against: this policy
// swallows the OS-native screenshot keyboard shortcuts outright whenever enabled.

[JsonDiscriminant(CommandType.ScreenshotBlockEnable)]
record ScreenshotBlockEnableCmd(string? Id) : ICommand;

[JsonDiscriminant(CommandType.ScreenshotBlockDisable)]
record ScreenshotBlockDisableCmd(string? Id) : ICommand;

[JsonDiscriminant(CommandType.ScreenshotBlockStatus)]
[EmitsEvent(EventType.ScreenshotBlockStatus)]
record ScreenshotBlockStatusCmd(string? Id) : ICommand;
