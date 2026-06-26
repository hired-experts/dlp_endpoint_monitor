using ClipboardUsbMonitor.Core;
using System.Text.Json.Serialization;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]

// ── Persistence ───────────────────────────────────────────────────────────────
[JsonSerializable(typeof(UsbDeviceListState))]

// ── Enums ─────────────────────────────────────────────────────────────────────
[JsonSerializable(typeof(EventType))]
[JsonSerializable(typeof(DeviceKind))]
[JsonSerializable(typeof(ClipboardKind))]
[JsonSerializable(typeof(ProtectionMode))]

// ── Replies ───────────────────────────────────────────────────────────────────
[JsonSerializable(typeof(ReplyEvent))]
[JsonSerializable(typeof(ErrorEvent))]
[JsonSerializable(typeof(InfoEvent))]

// ── Clipboard ─────────────────────────────────────────────────────────────────
[JsonSerializable(typeof(ClipboardReadEvent))]
[JsonSerializable(typeof(ClipboardTextEvent))]
[JsonSerializable(typeof(ClipboardFilesEvent))]
[JsonSerializable(typeof(ClipboardImageEvent))]
[JsonSerializable(typeof(ClipboardUnknownEvent))]

// ── USB ───────────────────────────────────────────────────────────────────────
[JsonSerializable(typeof(UsbDriveConnectedEvent))]
[JsonSerializable(typeof(UsbDriveDisconnectedEvent))]
[JsonSerializable(typeof(UsbDeviceDetectedEvent))]
[JsonSerializable(typeof(UsbDeviceConnectedEvent))]
[JsonSerializable(typeof(UsbDeviceDisconnectedEvent))]
[JsonSerializable(typeof(UsbDeviceBlockedEvent))]
[JsonSerializable(typeof(UsbDeviceBlockFailedEvent))]
[JsonSerializable(typeof(UsbStorageStatusEvent))]
[JsonSerializable(typeof(WhitelistEntryDto[]))]
[JsonSerializable(typeof(UsbWhitelistGetEvent))]
[JsonSerializable(typeof(UsbBlacklistGetEvent))]
[JsonSerializable(typeof(UsbProtectionStatusEvent))]

// ── Keyboard ──────────────────────────────────────────────────────────────────
[JsonSerializable(typeof(KeyboardShortcutEvent))]

internal partial class AppJsonContext : JsonSerializerContext { }