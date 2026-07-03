using System.Text.Json;

namespace DlpEndpointMonitor.Core;

internal sealed class UsbDeviceListState
{
    public bool                 Enabled { get; set; } = false;
    public List<UsbDeviceEntry> Entries { get; set; } = [];
}

/// <summary>
/// Thread-safe, persistent list of device entries covering both USB (vid/pid/serial)
/// and Bluetooth (mac) devices. Subclasses add their own allow/block decision on top.
/// </summary>
abstract class UsbDeviceList
{
    // ── Persistence ───────────────────────────────────────────────────────────

    static readonly string StorageDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "DlpEndpointMonitor");

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

    // ── Matching ──────────────────────────────────────────────────────────────

    /// <summary>
    /// USB device matching. An entry only participates if it has no Mac set.
    /// A kind-only entry (no vid/pid/serial/mac) matches any USB device of that kind.
    /// </summary>
    protected bool MatchesAnyUsb(string vid, string pid, string? serial, DeviceKind kind)
    {
        _lock.EnterReadLock();
        try
        {
            return _state.Entries.Any(e =>
                e.Mac is null &&
                (e.Vid    is null || e.Vid.Equals(vid,    StringComparison.OrdinalIgnoreCase)) &&
                (e.Pid    is null || e.Pid.Equals(pid,    StringComparison.OrdinalIgnoreCase)) &&
                (e.Serial is null || e.Serial.Equals(serial, StringComparison.OrdinalIgnoreCase)) &&
                (e.Kind   is null || e.Kind == kind));
        }
        finally { _lock.ExitReadLock(); }
    }

    /// <summary>
    /// Bluetooth device matching. An entry only participates if it has no Vid/Pid/Serial set.
    /// A kind-only entry (no vid/pid/serial/mac) matches any BT device of that kind.
    /// </summary>
    protected bool MatchesAnyBt(string mac, DeviceKind kind)
    {
        _lock.EnterReadLock();
        try
        {
            return _state.Entries.Any(e =>
                e.Vid is null && e.Pid is null && e.Serial is null &&
                (e.Mac  is null || e.Mac.Equals(mac,  StringComparison.OrdinalIgnoreCase)) &&
                (e.Kind is null || e.Kind == kind));
        }
        finally { _lock.ExitReadLock(); }
    }

    // ── Mutations ─────────────────────────────────────────────────────────────

    // Two entries are the SAME DEVICE when their identifying fields match (case-insensitive).
    // Label is cosmetic and intentionally ignored - a relabelled duplicate is still a duplicate.
    static bool SameDevice(UsbDeviceEntry a, UsbDeviceEntry b) =>
        string.Equals(a.Vid,    b.Vid,    StringComparison.OrdinalIgnoreCase) &&
        string.Equals(a.Pid,    b.Pid,    StringComparison.OrdinalIgnoreCase) &&
        string.Equals(a.Serial, b.Serial, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(a.Mac,    b.Mac,    StringComparison.OrdinalIgnoreCase) &&
        a.Kind == b.Kind;

    public void Add(UsbDeviceEntry device)
    {
        _lock.EnterWriteLock();
        try
        {
            if (!_state.Entries.Any(e => SameDevice(e, device))) _state.Entries.Add(device);
        }
        finally { _lock.ExitWriteLock(); }
        Save();
    }

    public void Remove(string? vid = null, string? pid = null, string? serial = null, string? mac = null, DeviceKind? kind = null)
    {
        _lock.EnterWriteLock();
        try
        {
            _state.Entries.RemoveAll(e =>
                (vid    is null || string.Equals(e.Vid,    vid,    StringComparison.OrdinalIgnoreCase)) &&
                (pid    is null || string.Equals(e.Pid,    pid,    StringComparison.OrdinalIgnoreCase)) &&
                (serial is null || string.Equals(e.Serial, serial, StringComparison.OrdinalIgnoreCase)) &&
                (mac    is null || string.Equals(e.Mac,    mac,    StringComparison.OrdinalIgnoreCase)) &&
                (kind   is null || e.Kind == kind));
        }
        finally { _lock.ExitWriteLock(); }
        Save();
    }

    /// <summary>Replace the entire list atomically, dropping duplicate devices.</summary>
    public void Set(IEnumerable<UsbDeviceEntry> devices)
    {
        _lock.EnterWriteLock();
        try
        {
            _state.Entries.Clear();
            foreach (var d in devices)
                if (!_state.Entries.Any(e => SameDevice(e, d))) _state.Entries.Add(d);
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
