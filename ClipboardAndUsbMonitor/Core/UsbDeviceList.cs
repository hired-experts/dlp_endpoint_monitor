using System.Text.Json;

namespace ClipboardUsbMonitor.Core;

internal sealed class UsbDeviceListState
{
    public bool                 Enabled { get; set; } = false;
    public List<UsbDeviceEntry> Entries { get; set; } = [];
}

/// <summary>
/// Thread-safe, persistent list of USB device entries.
/// Subclasses add their own allow/block decision on top.
/// </summary>
abstract class UsbDeviceList
{
    // ── Persistence ───────────────────────────────────────────────────────────

    static readonly string StorageDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "ClipboardUsbMonitor");

    // ── In-memory state ───────────────────────────────────────────────────────

    readonly ReaderWriterLockSlim _lock        = new();
    readonly string               _storagePath;
    UsbDeviceListState            _state       = new();

    protected UsbDeviceList(string fileName)
    {
        _storagePath = Path.Combine(StorageDir, fileName);
        Load();
    }

    // ── Enabled flag ──────────────────────────────────────────────────────────

    public bool IsEnabled
    {
        get
        {
            _lock.EnterReadLock();
            try   { return _state.Enabled; }
            finally { _lock.ExitReadLock(); }
        }
    }

    public void SetEnabled(bool enabled)
    {
        _lock.EnterWriteLock();
        try   { _state.Enabled = enabled; }
        finally { _lock.ExitWriteLock(); }
        Save();
    }

    // ── Shared matching helper ─────────────────────────────────────────────────

    /// <summary>
    /// Returns true if any entry matches the given device interface.
    /// Null fields on an entry act as wildcards; VID/PID on the entry are also optional.
    /// </summary>
    protected bool MatchesAny(string vid, string pid, string? serial, DeviceKind kind)
    {
        _lock.EnterReadLock();
        try
        {
            return _state.Entries.Any(e =>
                (e.Vid    is null || e.Vid.Equals(vid,    StringComparison.OrdinalIgnoreCase)) &&
                (e.Pid    is null || e.Pid.Equals(pid,    StringComparison.OrdinalIgnoreCase)) &&
                (e.Serial is null || e.Serial.Equals(serial, StringComparison.OrdinalIgnoreCase)) &&
                (e.Kind   is null || e.Kind == kind));
        }
        finally { _lock.ExitReadLock(); }
    }

    // ── Mutations ─────────────────────────────────────────────────────────────

    public void Add(UsbDeviceEntry device)
    {
        _lock.EnterWriteLock();
        try
        {
            bool exists = _state.Entries.Any(e =>
                string.Equals(e.Vid,    device.Vid,    StringComparison.OrdinalIgnoreCase) &&
                string.Equals(e.Pid,    device.Pid,    StringComparison.OrdinalIgnoreCase) &&
                string.Equals(e.Serial, device.Serial, StringComparison.OrdinalIgnoreCase) &&
                e.Kind == device.Kind);
            if (!exists) _state.Entries.Add(device);
        }
        finally { _lock.ExitWriteLock(); }
        Save();
    }

    /// <summary>
    /// Removes entries matching the given fields. All parameters are optional;
    /// omitting a field removes all entries regardless of that field's value.
    /// </summary>
    public void Remove(string? vid = null, string? pid = null, string? serial = null, DeviceKind? kind = null)
    {
        _lock.EnterWriteLock();
        try
        {
            _state.Entries.RemoveAll(e =>
                (vid    is null || string.Equals(e.Vid,    vid,    StringComparison.OrdinalIgnoreCase)) &&
                (pid    is null || string.Equals(e.Pid,    pid,    StringComparison.OrdinalIgnoreCase)) &&
                (serial is null || string.Equals(e.Serial, serial, StringComparison.OrdinalIgnoreCase)) &&
                (kind   is null || e.Kind == kind));
        }
        finally { _lock.ExitWriteLock(); }
        Save();
    }

    /// <summary>Replace the entire list atomically.</summary>
    public void Set(IEnumerable<UsbDeviceEntry> devices)
    {
        _lock.EnterWriteLock();
        try
        {
            _state.Entries.Clear();
            _state.Entries.AddRange(devices);
        }
        finally { _lock.ExitWriteLock(); }
        Save();
    }

    public void Clear()
    {
        _lock.EnterWriteLock();
        try   { _state.Entries.Clear(); }
        finally { _lock.ExitWriteLock(); }
        Save();
    }

    public IReadOnlyList<UsbDeviceEntry> GetAll()
    {
        _lock.EnterReadLock();
        try   { return _state.Entries.ToArray(); }
        finally { _lock.ExitReadLock(); }
    }

    // ── Disk I/O ──────────────────────────────────────────────────────────────

    void Save()
    {
        try
        {
            Directory.CreateDirectory(StorageDir);

            UsbDeviceListState snapshot;
            _lock.EnterReadLock();
            try
            {
                snapshot = new UsbDeviceListState
                {
                    Enabled = _state.Enabled,
                    Entries = _state.Entries.ToList(),
                };
            }
            finally { _lock.ExitReadLock(); }

            string tmp = _storagePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(snapshot, AppJsonContext.Default.UsbDeviceListState));
            File.Move(tmp, _storagePath, overwrite: true);
        }
        catch (Exception ex)
        {
            EventEmitter.EmitError($"{GetType().Name}_save", ex.Message);
        }
    }

    void Load()
    {
        try
        {
            if (!File.Exists(_storagePath)) return;

            string json = File.ReadAllText(_storagePath);
            var loaded  = JsonSerializer.Deserialize(json, AppJsonContext.Default.UsbDeviceListState);
            if (loaded is null) return;

            _lock.EnterWriteLock();
            try   { _state = loaded; }
            finally { _lock.ExitWriteLock(); }

            EventEmitter.EmitInfo(
                $"{GetType().Name} loaded — enabled={_state.Enabled}, {_state.Entries.Count} device(s)");
        }
        catch (Exception ex)
        {
            EventEmitter.EmitError($"{GetType().Name}_load", $"{ex.Message} — starting empty");
        }
    }
}
