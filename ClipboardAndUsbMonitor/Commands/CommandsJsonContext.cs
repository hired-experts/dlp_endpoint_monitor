using ClipboardUsbMonitor.Core;
using System.Text.Json.Serialization;

namespace ClipboardUsbMonitor.Commands;

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
[JsonSerializable(typeof(UsbDeviceDisableCmd))]
[JsonSerializable(typeof(UsbDeviceEnableCmd))]

// ── USB — protection status ───────────────────────────────────────────────────
[JsonSerializable(typeof(UsbProtectionStatusCmd))]

// ── USB — whitelist ───────────────────────────────────────────────────────────
[JsonSerializable(typeof(UsbWhitelistEnableCmd))]
[JsonSerializable(typeof(UsbWhitelistDisableCmd))]
[JsonSerializable(typeof(UsbWhitelistGetCmd))]
[JsonSerializable(typeof(UsbWhitelistClearCmd))]
[JsonSerializable(typeof(UsbWhitelistAddCmd))]
[JsonSerializable(typeof(UsbWhitelistRemoveCmd))]
[JsonSerializable(typeof(UsbWhitelistSetCmd))]

// ── USB — blacklist ───────────────────────────────────────────────────────────
[JsonSerializable(typeof(UsbBlacklistEnableCmd))]
[JsonSerializable(typeof(UsbBlacklistDisableCmd))]
[JsonSerializable(typeof(UsbBlacklistGetCmd))]
[JsonSerializable(typeof(UsbBlacklistClearCmd))]
[JsonSerializable(typeof(UsbBlacklistAddCmd))]
[JsonSerializable(typeof(UsbBlacklistRemoveCmd))]
[JsonSerializable(typeof(UsbBlacklistSetCmd))]

// ── Control ───────────────────────────────────────────────────────────────────
[JsonSerializable(typeof(PingCmd))]
[JsonSerializable(typeof(ShutdownCmd))]

internal partial class CommandsJsonContext : JsonSerializerContext { }
