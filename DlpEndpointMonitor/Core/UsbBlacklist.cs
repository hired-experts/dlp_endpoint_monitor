namespace DlpEndpointMonitor.Core;

sealed class DeviceBlacklist : UsbDeviceList
{
    public DeviceBlacklist() : base("blacklist.json") { }

    /// <summary>USB device: always false when disabled. Entries with a Mac field never match USB.</summary>
    public bool IsBlocked(string vid, string pid, string? serial, DeviceKind kind)
    {
        if (!IsEnabled) return false;
        return MatchesAnyUsb(vid, pid, serial, kind);
    }

    /// <summary>Bluetooth device: always false when disabled. Entries with vid/pid/serial never match BT.</summary>
    public bool IsBlocked(string mac, DeviceKind kind)
    {
        if (!IsEnabled) return false;
        return MatchesAnyBt(mac, kind);
    }
}
