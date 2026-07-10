namespace DlpEndpointMonitor.Core;

/// <summary>
/// Atomic temp-file-then-rename save, shared by UsbDeviceList/ClipboardRuleList/DisabledDevices -
/// previously each duplicated this identical logic with the actual disk I/O running OUTSIDE any
/// lock (only the in-memory snapshot was lock-protected). Two threads saving around the same time
/// (e.g. two devices blocked concurrently, each on its own Task.Run) could collide on the shared
/// ".tmp" path - observed in production as "file...being used by another process". The caller
/// supplies its OWN dedicated lock object (one per persisted file/class) so unrelated saves (e.g.
/// DeviceWhitelist vs ClipboardBlacklist) are never serialized against each other for no reason.
/// </summary>
static class AtomicFileWriter
{
    public static void Save(object lockObject, string storageDir, string path, string json)
    {
        lock (lockObject)
        {
            Directory.CreateDirectory(storageDir);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, path, overwrite: true);
        }
    }
}
