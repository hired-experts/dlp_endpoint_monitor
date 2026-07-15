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

    // ── In-memory state ───────────────────────────────────────────────────────

    readonly ReaderWriterLockSlim _lock        = new();
    readonly object               _saveLock    = new();
    readonly string               _storageDir;
    readonly string               _storagePath;
    UsbDeviceListState            _state       = new();

    /// <param name="storageDir">
    /// Directory to persist under. Defaults to <see cref="StorageLocation.Default"/>
    /// (%ProgramData%\DlpEndpointMonitor) when null - pass an explicit directory only to
    /// isolate storage, e.g. in tests.
    /// </param>
    protected UsbDeviceList(string fileName, string? storageDir = null)
    {
        _storageDir  = storageDir ?? StorageLocation.Default;
        _storagePath = Path.Combine(_storageDir, fileName);
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

    /// <summary>
    /// True only if <see cref="Load"/> hit its catch block (a genuine parse/read failure) - never
    /// true for the legitimate "file doesn't exist yet" case, which is a normal, unconfigured
    /// startup state, not a corruption.
    /// </summary>
    public bool LoadFailed { get; private set; }

    /// <summary>
    /// The state to fall back to when the persisted file exists but fails to load (corrupted,
    /// truncated, unreadable) - deliberately distinct from the "file simply doesn't exist yet"
    /// case (which always means "nothing configured", handled separately, before this is ever
    /// consulted). Base default is fail-open (Enabled=false) - safe for a blacklist, where
    /// "nothing blocked" is already the system's normal unconfigured state. Overridden by
    /// DeviceWhitelist to fail CLOSED instead, since an allow-list's safe failure direction is
    /// deny-all: we cannot know what the corrupted file used to permit, so the conservative
    /// assumption is "permit nothing" rather than "permit everything."
    /// </summary>
    protected virtual UsbDeviceListState CorruptedLoadFallback() => new();

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

    // An entry/criteria set with no identifying field at all is a wildcard - `Add` would create a
    // match-everything entry (whitelist mode becomes allow-all; blacklist mode becomes block-all),
    // and `Remove` would delete every entry (whitelist degrades to deny-all; blacklist fails OPEN,
    // wiping every block). Reject at the point of mutation so every current and future caller is
    // covered, not just today's command-handler call sites.
    static bool HasAnyIdentifyingField(string? vid, string? pid, string? serial, string? mac, DeviceKind? kind) =>
        vid is not null || pid is not null || serial is not null || mac is not null || kind is not null;

    public (bool ok, string? error) Add(UsbDeviceEntry device)
    {
        if (!HasAnyIdentifyingField(device.Vid, device.Pid, device.Serial, device.Mac, device.Kind))
            return (false, "at least one of vid/pid/serial/mac/kind is required");

        _lock.EnterWriteLock();
        try
        {
            if (!_state.Entries.Any(e => SameDevice(e, device))) _state.Entries.Add(device);
        }
        finally { _lock.ExitWriteLock(); }
        Save();
        return (true, null);
    }

    public (bool ok, string? error) Remove(string? vid = null, string? pid = null, string? serial = null, string? mac = null, DeviceKind? kind = null)
    {
        if (!HasAnyIdentifyingField(vid, pid, serial, mac, kind))
            return (false, "at least one of vid/pid/serial/mac/kind is required");

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
        return (true, null);
    }

    /// <summary>Replace the entire list atomically, dropping duplicate devices. Validates every
    /// entry BEFORE mutating anything, so a Set either fully applies or fully rejects - never
    /// partially applies with some entries silently dropped.</summary>
    public (bool ok, string? error) Set(IEnumerable<UsbDeviceEntry> devices)
    {
        var list = devices.ToList();
        foreach (var d in list)
            if (!HasAnyIdentifyingField(d.Vid, d.Pid, d.Serial, d.Mac, d.Kind))
                return (false, $"entry with no identifying fields is not allowed (label={d.Label})");

        _lock.EnterWriteLock();
        try
        {
            _state.Entries.Clear();
            foreach (var d in list)
                if (!_state.Entries.Any(e => SameDevice(e, d))) _state.Entries.Add(d);
        }
        finally { _lock.ExitWriteLock(); }
        Save();
        return (true, null);
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

            AtomicFileWriter.Save(_saveLock, _storageDir, _storagePath,
                JsonSerializer.Serialize(snapshot, AppJsonContext.Default.UsbDeviceListState));
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
            if (loaded is null) throw new InvalidDataException("deserialized to null");

            _lock.EnterWriteLock();
            try   { _state = loaded; }
            finally { _lock.ExitWriteLock(); }

            EventEmitter.EmitInfo(
                $"{GetType().Name} loaded — enabled={_state.Enabled}, {_state.Entries.Count} device(s)");
        }
        catch (Exception ex)
        {
            _lock.EnterWriteLock();
            try   { _state = CorruptedLoadFallback(); }
            finally { _lock.ExitWriteLock(); }

            LoadFailed = true;

            EventEmitter.EmitError($"{GetType().Name}_load_corrupted",
                $"{ex.Message} — falling back to {(_state.Enabled ? "deny-all (protective)" : "empty/disabled")}");

            // Best-effort forensic preservation - a Save() later in this process's life would
            // otherwise silently overwrite the only evidence of what went wrong. Never let a
            // failure here mask the original error.
            try { File.Copy(_storagePath, $"{_storagePath}.corrupted-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}", overwrite: false); }
            catch { /* best-effort only */ }
        }
    }
}
