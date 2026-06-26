using ClipboardUsbMonitor.Commands;

namespace ClipboardUsbMonitor.Handlers;

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
    void Handle(UsbDeviceDisableCmd command);
    void Handle(UsbDeviceEnableCmd command);
}

// Whitelist and blacklist share one interface because mutual-exclusivity logic
// needs both lists in the same place (enabling one disables the other).
interface IUsbProtectionHandler
{
    void Handle(UsbProtectionStatusCmd command);
    void Handle(UsbWhitelistEnableCmd command);
    void Handle(UsbWhitelistDisableCmd command);
    void Handle(UsbWhitelistGetCmd command);
    void Handle(UsbWhitelistClearCmd command);
    void Handle(UsbWhitelistAddCmd command);
    void Handle(UsbWhitelistRemoveCmd command);
    void Handle(UsbWhitelistSetCmd command);
    void Handle(UsbBlacklistEnableCmd command);
    void Handle(UsbBlacklistDisableCmd command);
    void Handle(UsbBlacklistGetCmd command);
    void Handle(UsbBlacklistClearCmd command);
    void Handle(UsbBlacklistAddCmd command);
    void Handle(UsbBlacklistRemoveCmd command);
    void Handle(UsbBlacklistSetCmd command);
}

interface IControlHandler
{
    void Handle(PingCmd command);
    void Handle(ShutdownCmd command);
}
