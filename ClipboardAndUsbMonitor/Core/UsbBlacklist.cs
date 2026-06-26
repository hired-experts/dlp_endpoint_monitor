namespace ClipboardUsbMonitor.Core;

sealed class UsbBlacklist : UsbDeviceList
{
    public UsbBlacklist() : base("blacklist.json") { }

    /// <summary>
    /// Returns true if the device interface should be blocked.
    /// Always false when the blacklist is disabled.
    /// </summary>
    public bool IsBlocked(string vid, string pid, string? serial, DeviceKind kind)
    {
        if (!IsEnabled) return false;
        return MatchesAny(vid, pid, serial, kind);
    }
}
