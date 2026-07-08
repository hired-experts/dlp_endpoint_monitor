using System.Text.Json.Serialization;

namespace DlpEndpointMonitor.Core;

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
    [JsonStringEnumMemberName("usb_device_unblocked")]      UsbDeviceUnblocked,
    [JsonStringEnumMemberName("usb_device_unblock_failed")] UsbDeviceUnblockFailed,
    [JsonStringEnumMemberName("usb_storage_status")]       UsbStorageStatus,
    [JsonStringEnumMemberName("device_protection_status")]    DeviceProtectionStatus,
    [JsonStringEnumMemberName("device_whitelist_get")]        DeviceWhitelistGet,
    [JsonStringEnumMemberName("device_blacklist_get")]        DeviceBlacklistGet,
    [JsonStringEnumMemberName("clipboard_protection_status")]  ClipboardProtectionStatus,
    [JsonStringEnumMemberName("clipboard_whitelist_get")]      ClipboardWhitelistGet,
    [JsonStringEnumMemberName("clipboard_blacklist_get")]      ClipboardBlacklistGet,
    [JsonStringEnumMemberName("clipboard_content_blocked")]      ClipboardContentBlocked,
    [JsonStringEnumMemberName("clipboard_content_block_failed")] ClipboardContentBlockFailed,
    [JsonStringEnumMemberName("keyboard_shortcut")]              KeyboardShortcut,
    [JsonStringEnumMemberName("bluetooth_device_connected")]     BluetoothDeviceConnected,
    [JsonStringEnumMemberName("bluetooth_device_disconnected")]  BluetoothDeviceDisconnected,
    [JsonStringEnumMemberName("bluetooth_device_blocked")]       BluetoothDeviceBlocked,
    [JsonStringEnumMemberName("bluetooth_device_block_failed")]  BluetoothDeviceBlockFailed,
    [JsonStringEnumMemberName("bluetooth_device_unblocked")]       BluetoothDeviceUnblocked,
    [JsonStringEnumMemberName("bluetooth_device_unblock_failed")]  BluetoothDeviceUnblockFailed,
    [JsonStringEnumMemberName("network_device_connected")]        NetworkDeviceConnected,
    [JsonStringEnumMemberName("network_device_disconnected")]     NetworkDeviceDisconnected,
    [JsonStringEnumMemberName("network_device_blocked")]          NetworkDeviceBlocked,
    [JsonStringEnumMemberName("network_device_block_failed")]     NetworkDeviceBlockFailed,
    [JsonStringEnumMemberName("network_device_unblocked")]        NetworkDeviceUnblocked,
    [JsonStringEnumMemberName("network_device_unblock_failed")]   NetworkDeviceUnblockFailed,
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
    [JsonStringEnumMemberName("device_disable")]     DeviceDisable,
    [JsonStringEnumMemberName("device_enable")]      DeviceEnable,
    [JsonStringEnumMemberName("device_protection_status")]  DeviceProtectionStatus,
    [JsonStringEnumMemberName("device_whitelist_enable")]   DeviceWhitelistEnable,
    [JsonStringEnumMemberName("device_whitelist_disable")]  DeviceWhitelistDisable,
    [JsonStringEnumMemberName("device_whitelist_get")]      DeviceWhitelistGet,
    [JsonStringEnumMemberName("device_whitelist_clear")]    DeviceWhitelistClear,
    [JsonStringEnumMemberName("device_whitelist_add")]      DeviceWhitelistAdd,
    [JsonStringEnumMemberName("device_whitelist_remove")]   DeviceWhitelistRemove,
    [JsonStringEnumMemberName("device_whitelist_set")]      DeviceWhitelistSet,
    [JsonStringEnumMemberName("device_blacklist_enable")]   DeviceBlacklistEnable,
    [JsonStringEnumMemberName("device_blacklist_disable")]  DeviceBlacklistDisable,
    [JsonStringEnumMemberName("device_blacklist_get")]      DeviceBlacklistGet,
    [JsonStringEnumMemberName("device_blacklist_clear")]    DeviceBlacklistClear,
    [JsonStringEnumMemberName("device_blacklist_add")]      DeviceBlacklistAdd,
    [JsonStringEnumMemberName("device_blacklist_remove")]   DeviceBlacklistRemove,
    [JsonStringEnumMemberName("device_blacklist_set")]      DeviceBlacklistSet,
    [JsonStringEnumMemberName("clipboard_protection_status")]  ClipboardProtectionStatus,
    [JsonStringEnumMemberName("clipboard_whitelist_enable")]    ClipboardWhitelistEnable,
    [JsonStringEnumMemberName("clipboard_whitelist_disable")]   ClipboardWhitelistDisable,
    [JsonStringEnumMemberName("clipboard_whitelist_get")]       ClipboardWhitelistGet,
    [JsonStringEnumMemberName("clipboard_whitelist_clear")]     ClipboardWhitelistClear,
    [JsonStringEnumMemberName("clipboard_whitelist_add")]       ClipboardWhitelistAdd,
    [JsonStringEnumMemberName("clipboard_whitelist_remove")]    ClipboardWhitelistRemove,
    [JsonStringEnumMemberName("clipboard_whitelist_set")]       ClipboardWhitelistSet,
    [JsonStringEnumMemberName("clipboard_blacklist_enable")]    ClipboardBlacklistEnable,
    [JsonStringEnumMemberName("clipboard_blacklist_disable")]   ClipboardBlacklistDisable,
    [JsonStringEnumMemberName("clipboard_blacklist_get")]       ClipboardBlacklistGet,
    [JsonStringEnumMemberName("clipboard_blacklist_clear")]     ClipboardBlacklistClear,
    [JsonStringEnumMemberName("clipboard_blacklist_add")]       ClipboardBlacklistAdd,
    [JsonStringEnumMemberName("clipboard_blacklist_remove")]    ClipboardBlacklistRemove,
    [JsonStringEnumMemberName("clipboard_blacklist_set")]       ClipboardBlacklistSet,
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
