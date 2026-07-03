using DlpEndpointMonitor.Core;
using System.Text.Json.Serialization;

namespace DlpEndpointMonitor.Commands;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]

// ── Shared ────────────────────────────────────────────────────────────────────
[JsonSerializable(typeof(CommandType))]
[JsonSerializable(typeof(DeviceKind))]
[JsonSerializable(typeof(DeviceEntryDto))]

// ── Clipboard ─────────────────────────────────────────────────────────────────
[JsonSerializable(typeof(ClipboardReadCmd))]
[JsonSerializable(typeof(ClipboardSetCmd))]
[JsonSerializable(typeof(ClipboardClearCmd))]

// ── USB — storage ─────────────────────────────────────────────────────────────
[JsonSerializable(typeof(UsbEjectCmd))]
[JsonSerializable(typeof(UsbDisableStorageCmd))]
[JsonSerializable(typeof(UsbEnableStorageCmd))]
[JsonSerializable(typeof(UsbStorageStatusCmd))]

// ── USB — device level ────────────────────────────────────────────────────────
[JsonSerializable(typeof(DeviceDisableCmd))]
[JsonSerializable(typeof(DeviceEnableCmd))]

// ── USB — protection status ───────────────────────────────────────────────────
[JsonSerializable(typeof(DeviceProtectionStatusCmd))]

// ── USB — whitelist ───────────────────────────────────────────────────────────
[JsonSerializable(typeof(DeviceWhitelistEnableCmd))]
[JsonSerializable(typeof(DeviceWhitelistDisableCmd))]
[JsonSerializable(typeof(DeviceWhitelistGetCmd))]
[JsonSerializable(typeof(DeviceWhitelistClearCmd))]
[JsonSerializable(typeof(DeviceWhitelistAddCmd))]
[JsonSerializable(typeof(DeviceWhitelistRemoveCmd))]
[JsonSerializable(typeof(DeviceWhitelistSetCmd))]

// ── USB — blacklist ───────────────────────────────────────────────────────────
[JsonSerializable(typeof(DeviceBlacklistEnableCmd))]
[JsonSerializable(typeof(DeviceBlacklistDisableCmd))]
[JsonSerializable(typeof(DeviceBlacklistGetCmd))]
[JsonSerializable(typeof(DeviceBlacklistClearCmd))]
[JsonSerializable(typeof(DeviceBlacklistAddCmd))]
[JsonSerializable(typeof(DeviceBlacklistRemoveCmd))]
[JsonSerializable(typeof(DeviceBlacklistSetCmd))]

// ── Control ───────────────────────────────────────────────────────────────────
[JsonSerializable(typeof(PingCmd))]
[JsonSerializable(typeof(ShutdownCmd))]

internal partial class CommandsJsonContext : JsonSerializerContext { }
