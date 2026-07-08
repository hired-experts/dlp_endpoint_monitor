namespace DlpEndpointMonitor.Core;

/// <summary>
/// The single shared machine-wide storage directory for every persisted DLP state file
/// (whitelist.json, blacklist.json, disabled-devices.json) - one computation, reused by
/// UsbDeviceList and DisabledDevices instead of each duplicating it. Machine-wide and
/// user-agnostic on purpose: this process runs elevated and may run under a different
/// effective user context (e.g. a service/SYSTEM account) than the interactive user, where
/// a per-user profile folder would resolve to the wrong place.
/// </summary>
static class StorageLocation
{
    public static readonly string Default = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "DlpEndpointMonitor");
}
