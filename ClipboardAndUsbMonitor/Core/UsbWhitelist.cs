namespace ClipboardUsbMonitor.Core;

public record UsbDeviceEntry(
    string?     Vid    = null,
    string?     Pid    = null,
    string?     Serial = null,
    DeviceKind? Kind   = null,
    string?     Label  = null);

sealed class UsbWhitelist : UsbDeviceList
{
    public UsbWhitelist() : base("whitelist.json") { }

    /// <summary>
    /// Returns true if the device interface is allowed to connect.
    /// Always true when the whitelist is disabled.
    /// </summary>
    public bool IsAllowed(string vid, string pid, string? serial, DeviceKind kind)
    {
        if (!IsEnabled) return true;
        return MatchesAny(vid, pid, serial, kind);
    }
}
