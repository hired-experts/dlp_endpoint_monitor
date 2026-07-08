using DlpEndpointMonitor.Core;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

[assembly: InternalsVisibleTo("DlpEndpointMonitor.Tests")]

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]

// ── Persistence ───────────────────────────────────────────────────────────────
[JsonSerializable(typeof(UsbDeviceListState))]
[JsonSerializable(typeof(DisabledDevicesState))]

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
[JsonSerializable(typeof(UsbDeviceUnblockedEvent))]
[JsonSerializable(typeof(UsbDeviceUnblockFailedEvent))]
[JsonSerializable(typeof(UsbStorageStatusEvent))]
[JsonSerializable(typeof(WhitelistEntryDto[]))]
[JsonSerializable(typeof(DeviceWhitelistGetEvent))]
[JsonSerializable(typeof(DeviceBlacklistGetEvent))]
[JsonSerializable(typeof(DeviceProtectionStatusEvent))]

// ── Display / Monitor ─────────────────────────────────────────────────────────
[JsonSerializable(typeof(MonitorConnectedEvent))]
[JsonSerializable(typeof(MonitorDisconnectedEvent))]
[JsonSerializable(typeof(MonitorBlockedEvent))]
[JsonSerializable(typeof(MonitorBlockFailedEvent))]

// ── Keyboard ──────────────────────────────────────────────────────────────────
[JsonSerializable(typeof(KeyboardShortcutEvent))]

// ── Bluetooth ─────────────────────────────────────────────────────────────────
[JsonSerializable(typeof(BluetoothDeviceConnectedEvent))]
[JsonSerializable(typeof(BluetoothDeviceDisconnectedEvent))]
[JsonSerializable(typeof(BluetoothDeviceBlockedEvent))]
[JsonSerializable(typeof(BluetoothDeviceBlockFailedEvent))]

internal partial class AppJsonContext : JsonSerializerContext { }