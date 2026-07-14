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
[JsonSerializable(typeof(ClipboardRuleListState))]
[JsonSerializable(typeof(ScreenshotBlockPolicyState))]

// ── Enums ─────────────────────────────────────────────────────────────────────
[JsonSerializable(typeof(EventType))]
[JsonSerializable(typeof(DeviceKind))]
[JsonSerializable(typeof(ClipboardKind))]
[JsonSerializable(typeof(ProtectionMode))]
[JsonSerializable(typeof(DisplayTopology))]
[JsonSerializable(typeof(KeyboardShortcutAction))]

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
[JsonSerializable(typeof(ClipboardProtectionStatusEvent))]
[JsonSerializable(typeof(ClipboardRuleEntryDto[]))]
[JsonSerializable(typeof(ClipboardWhitelistGetEvent))]
[JsonSerializable(typeof(ClipboardBlacklistGetEvent))]
[JsonSerializable(typeof(ClipboardContentBlockedEvent))]
[JsonSerializable(typeof(ClipboardContentBlockFailedEvent))]

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
[JsonSerializable(typeof(UsbStorageBlockedEvent))]
[JsonSerializable(typeof(UsbStorageDeviceBlockedEvent))]
[JsonSerializable(typeof(UsbStorageDeviceBlockFailedEvent))]
[JsonSerializable(typeof(WhitelistEntryDto[]))]
[JsonSerializable(typeof(DeviceWhitelistGetEvent))]
[JsonSerializable(typeof(DeviceBlacklistGetEvent))]
[JsonSerializable(typeof(DeviceProtectionStatusEvent))]

// ── Display / Monitor ─────────────────────────────────────────────────────────
[JsonSerializable(typeof(MonitorConnectedEvent))]
[JsonSerializable(typeof(MonitorDisconnectedEvent))]
[JsonSerializable(typeof(MonitorBlockedEvent))]
[JsonSerializable(typeof(MonitorBlockFailedEvent))]
[JsonSerializable(typeof(MonitorProjectionChangedEvent))]

// ── Keyboard ──────────────────────────────────────────────────────────────────
[JsonSerializable(typeof(KeyboardShortcutEvent))]

// ── Screenshot ────────────────────────────────────────────────────────────────
[JsonSerializable(typeof(ScreenshotBlockStatusEvent))]
[JsonSerializable(typeof(ScreenshotBlockedEvent))]

// ── Bluetooth ─────────────────────────────────────────────────────────────────
[JsonSerializable(typeof(BluetoothDeviceConnectedEvent))]
[JsonSerializable(typeof(BluetoothDeviceDisconnectedEvent))]
[JsonSerializable(typeof(BluetoothDeviceBlockedEvent))]
[JsonSerializable(typeof(BluetoothDeviceBlockFailedEvent))]
[JsonSerializable(typeof(BluetoothDeviceUnblockedEvent))]
[JsonSerializable(typeof(BluetoothDeviceUnblockFailedEvent))]

// ── Bluetooth companion relay (primary <-> companion enumerate reply) ──────────
[JsonSerializable(typeof(System.Collections.Generic.List<DlpEndpointMonitor.Actions.BluetoothActions.BtDevice>))]

// ── Network ───────────────────────────────────────────────────────────────────
[JsonSerializable(typeof(NetworkDeviceConnectedEvent))]
[JsonSerializable(typeof(NetworkDeviceDisconnectedEvent))]
[JsonSerializable(typeof(NetworkDeviceBlockedEvent))]
[JsonSerializable(typeof(NetworkDeviceBlockFailedEvent))]
[JsonSerializable(typeof(NetworkDeviceUnblockedEvent))]
[JsonSerializable(typeof(NetworkDeviceUnblockFailedEvent))]

internal partial class AppJsonContext : JsonSerializerContext { }