using DlpEndpointMonitor.Commands;

namespace DlpEndpointMonitor.Handlers;

interface IClipboardHandler
{
    void Handle(ClipboardReadCmd command);
    void Handle(ClipboardSetCmd command);
    void Handle(ClipboardClearCmd command);
}

interface IUsbStorageHandler
{
    void Handle(UsbEjectCmd command);
    void Handle(UsbDisableStorageCmd command);
    void Handle(UsbEnableStorageCmd command);
    void Handle(UsbStorageStatusCmd command);
}

interface IUsbDeviceHandler
{
    void Handle(DeviceDisableCmd command);
    void Handle(DeviceEnableCmd command);
}

// Whitelist and blacklist share one interface because mutual-exclusivity logic
// needs both lists in the same place (enabling one disables the other).
interface IUsbProtectionHandler
{
    void Handle(DeviceProtectionStatusCmd command);
    void Handle(DeviceWhitelistEnableCmd command);
    void Handle(DeviceWhitelistDisableCmd command);
    void Handle(DeviceWhitelistGetCmd command);
    void Handle(DeviceWhitelistClearCmd command);
    void Handle(DeviceWhitelistAddCmd command);
    void Handle(DeviceWhitelistRemoveCmd command);
    void Handle(DeviceWhitelistSetCmd command);
    void Handle(DeviceBlacklistEnableCmd command);
    void Handle(DeviceBlacklistDisableCmd command);
    void Handle(DeviceBlacklistGetCmd command);
    void Handle(DeviceBlacklistClearCmd command);
    void Handle(DeviceBlacklistAddCmd command);
    void Handle(DeviceBlacklistRemoveCmd command);
    void Handle(DeviceBlacklistSetCmd command);
}

interface IControlHandler
{
    void Handle(PingCmd command);
    void Handle(ShutdownCmd command);
}
