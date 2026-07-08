namespace DlpEndpointMonitor.Core;

public record UsbDeviceEntry(
    string?     Vid    = null,
    string?     Pid    = null,
    string?     Serial = null,
    string?     Mac    = null,
    DeviceKind? Kind   = null,
    string?     Label  = null);

sealed class DeviceWhitelist : UsbDeviceList
{
    public DeviceWhitelist(string? storageDir = null) : base("whitelist.json", storageDir) { }

    /// <summary>USB device: always true when disabled. Entries with a Mac field never match USB.</summary>
    public bool IsAllowed(string vid, string pid, string? serial, DeviceKind kind)
    {
        if (!IsEnabled) return true;
        return MatchesAnyUsb(vid, pid, serial, kind);
    }

    /// <summary>Bluetooth device: always true when disabled. Entries with vid/pid/serial never match BT.</summary>
    public bool IsAllowed(string mac, DeviceKind kind)
    {
        if (!IsEnabled) return true;
        return MatchesAnyBt(mac, kind);
    }
}
