using System.Text.Json.Serialization;

namespace ClipboardUsbMonitor.Core;

[JsonConverter(typeof(JsonStringEnumConverter<EventType>))]
public enum EventType
{
    [JsonStringEnumMemberName("error")]                    Error,
    [JsonStringEnumMemberName("info")]                     Info,
    [JsonStringEnumMemberName("reply")]                    Reply,
    [JsonStringEnumMemberName("clipboard_read")]           ClipboardRead,
    [JsonStringEnumMemberName("clipboard_change")]         ClipboardChange,
    [JsonStringEnumMemberName("usb_drive_connected")]      UsbDriveConnected,
    [JsonStringEnumMemberName("usb_drive_disconnected")]   UsbDriveDisconnected,
    [JsonStringEnumMemberName("usb_device_detected")]      UsbDeviceDetected,
    [JsonStringEnumMemberName("usb_device_connected")]     UsbDeviceConnected,
    [JsonStringEnumMemberName("usb_device_disconnected")]  UsbDeviceDisconnected,
    [JsonStringEnumMemberName("usb_device_blocked")]       UsbDeviceBlocked,
    [JsonStringEnumMemberName("usb_device_block_failed")]  UsbDeviceBlockFailed,
    [JsonStringEnumMemberName("usb_storage_status")]       UsbStorageStatus,
    [JsonStringEnumMemberName("usb_protection_status")]    UsbProtectionStatus,
    [JsonStringEnumMemberName("usb_whitelist_get")]        UsbWhitelistGet,
    [JsonStringEnumMemberName("usb_blacklist_get")]        UsbBlacklistGet,
    [JsonStringEnumMemberName("keyboard_shortcut")]              KeyboardShortcut,
    [JsonStringEnumMemberName("bluetooth_device_connected")]     BluetoothDeviceConnected,
    [JsonStringEnumMemberName("bluetooth_device_disconnected")]  BluetoothDeviceDisconnected,
    [JsonStringEnumMemberName("bluetooth_device_blocked")]       BluetoothDeviceBlocked,
    [JsonStringEnumMemberName("bluetooth_device_block_failed")]  BluetoothDeviceBlockFailed,
    [JsonStringEnumMemberName("monitor_connected")]              MonitorConnected,
    [JsonStringEnumMemberName("monitor_disconnected")]           MonitorDisconnected,
    [JsonStringEnumMemberName("monitor_blocked")]                MonitorBlocked,
    [JsonStringEnumMemberName("monitor_block_failed")]           MonitorBlockFailed,
}

[JsonConverter(typeof(JsonStringEnumConverter<CommandType>))]
public enum CommandType
{
    [JsonStringEnumMemberName("clipboard_read")]     ClipboardRead,
    [JsonStringEnumMemberName("clipboard_set")]      ClipboardSet,
    [JsonStringEnumMemberName("clipboard_clear")]    ClipboardClear,
    [JsonStringEnumMemberName("usb_eject")]              UsbEject,
    [JsonStringEnumMemberName("usb_disable_storage")]    UsbDisableStorage,
    [JsonStringEnumMemberName("usb_enable_storage")]     UsbEnableStorage,
    [JsonStringEnumMemberName("usb_storage_status")]     UsbStorageStatus,
    [JsonStringEnumMemberName("usb_device_disable")]     UsbDeviceDisable,
    [JsonStringEnumMemberName("usb_device_enable")]      UsbDeviceEnable,
    [JsonStringEnumMemberName("usb_protection_status")]  UsbProtectionStatus,
    [JsonStringEnumMemberName("usb_whitelist_enable")]   UsbWhitelistEnable,
    [JsonStringEnumMemberName("usb_whitelist_disable")]  UsbWhitelistDisable,
    [JsonStringEnumMemberName("usb_whitelist_get")]      UsbWhitelistGet,
    [JsonStringEnumMemberName("usb_whitelist_clear")]    UsbWhitelistClear,
    [JsonStringEnumMemberName("usb_whitelist_add")]      UsbWhitelistAdd,
    [JsonStringEnumMemberName("usb_whitelist_remove")]   UsbWhitelistRemove,
    [JsonStringEnumMemberName("usb_whitelist_set")]      UsbWhitelistSet,
    [JsonStringEnumMemberName("usb_blacklist_enable")]   UsbBlacklistEnable,
    [JsonStringEnumMemberName("usb_blacklist_disable")]  UsbBlacklistDisable,
    [JsonStringEnumMemberName("usb_blacklist_get")]      UsbBlacklistGet,
    [JsonStringEnumMemberName("usb_blacklist_clear")]    UsbBlacklistClear,
    [JsonStringEnumMemberName("usb_blacklist_add")]      UsbBlacklistAdd,
    [JsonStringEnumMemberName("usb_blacklist_remove")]   UsbBlacklistRemove,
    [JsonStringEnumMemberName("usb_blacklist_set")]      UsbBlacklistSet,
    [JsonStringEnumMemberName("ping")]                   Ping,
    [JsonStringEnumMemberName("shutdown")]               Shutdown,
}

[JsonConverter(typeof(JsonStringEnumConverter<DeviceKind>))]
public enum DeviceKind
{
    [JsonStringEnumMemberName("unknown")]   Unknown,
    [JsonStringEnumMemberName("audio")]     Audio,
    [JsonStringEnumMemberName("biometric")] Biometric,
    [JsonStringEnumMemberName("bluetooth")] Bluetooth,
    [JsonStringEnumMemberName("camera")]    Camera,
    [JsonStringEnumMemberName("hid")]       Hid,
    [JsonStringEnumMemberName("hub")]       Hub,
    [JsonStringEnumMemberName("keyboard")]  Keyboard,
    [JsonStringEnumMemberName("monitor")]   Monitor,
    [JsonStringEnumMemberName("mouse")]     Mouse,
    [JsonStringEnumMemberName("mtp")]       Mtp,
    [JsonStringEnumMemberName("network")]   Network,
    [JsonStringEnumMemberName("printer")]   Printer,
    [JsonStringEnumMemberName("sensor")]    Sensor,
    [JsonStringEnumMemberName("smartcard")] Smartcard,
    [JsonStringEnumMemberName("storage")]   Storage,
    [JsonStringEnumMemberName("vendor")]    Vendor,
    [JsonStringEnumMemberName("video")]     Video,
}

[JsonConverter(typeof(JsonStringEnumConverter<ClipboardKind>))]
public enum ClipboardKind
{
    [JsonStringEnumMemberName("text")]    Text,
    [JsonStringEnumMemberName("files")]   Files,
    [JsonStringEnumMemberName("image")]   Image,
    [JsonStringEnumMemberName("unknown")] Unknown,
}

[JsonConverter(typeof(JsonStringEnumConverter<ProtectionMode>))]
public enum ProtectionMode
{
    [JsonStringEnumMemberName("whitelist")] Whitelist,
    [JsonStringEnumMemberName("blacklist")] Blacklist,
    [JsonStringEnumMemberName("none")]      None,
    [JsonStringEnumMemberName("conflict")]  Conflict,
}
