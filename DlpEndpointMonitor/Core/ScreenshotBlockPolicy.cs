using System.Text.Json;

namespace DlpEndpointMonitor.Core;

internal sealed class ScreenshotBlockPolicyState
{
    public bool Enabled { get; set; } = false;
}

/// <summary>
/// A single-boolean persisted policy - NOT a UsbDeviceList/ClipboardRuleList subclass, since
/// there are no entries to match, just enable/disable. Mirrors DisabledDevices' persistence
/// shape (ReaderWriterLockSlim + AtomicFileWriter.Save). <see cref="Reload"/> exists so a
/// --session-companion instance's own KeyboardHook can pick up a change the primary's
/// CommandDispatcher makes to the shared %ProgramData% file, via the same FileSystemWatcher
/// debounce mechanism ClipboardWhitelist/ClipboardBlacklist already use.
/// </summary>
sealed class ScreenshotBlockPolicy
{
    readonly ReaderWriterLockSlim _lock = new();
    readonly object               _saveLock = new();
    readonly string               _storageDir;
    readonly string               _storagePath;
    ScreenshotBlockPolicyState    _state = new();

    /// <param name="storageDir">
    /// Directory to persist under. Defaults to <see cref="StorageLocation.Default"/>
    /// (%ProgramData%\DlpEndpointMonitor) when null - pass an explicit directory only to
    /// isolate storage, e.g. in tests.
    /// </param>
    public ScreenshotBlockPolicy(string? storageDir = null)
    {
        _storageDir  = storageDir ?? StorageLocation.Default;
        _storagePath = Path.Combine(_storageDir, "screenshot-block.json");
        Load();
    }

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

    // ── Disk I/O (mirrors DisabledDevices: source-gen JSON, atomic temp-file write) ──

    void Save()
    {
        try
        {
            ScreenshotBlockPolicyState snapshot;
            _lock.EnterReadLock();
            try   { snapshot = new ScreenshotBlockPolicyState { Enabled = _state.Enabled }; }
            finally { _lock.ExitReadLock(); }

            AtomicFileWriter.Save(_saveLock, _storageDir, _storagePath,
                JsonSerializer.Serialize(snapshot, AppJsonContext.Default.ScreenshotBlockPolicyState));
        }
        catch (Exception ex) { EventEmitter.EmitError("screenshot_block_policy_save", ex.Message); }
    }

    void Load() => Reload();

    /// <summary>
    /// Re-reads <see cref="_storagePath"/> from disk and replaces the in-memory state wholesale.
    /// Called once from the constructor, and callable again later (e.g. by a FileSystemWatcher
    /// reacting to the primary's own mutation of the shared %ProgramData% storage file) so a
    /// --session-companion instance's KeyboardHook picks up the change without a restart.
    /// </summary>
    public void Reload()
    {
        try
        {
            if (!File.Exists(_storagePath)) return;

            var loaded = JsonSerializer.Deserialize(File.ReadAllText(_storagePath), AppJsonContext.Default.ScreenshotBlockPolicyState);
            if (loaded is null) return;

            _lock.EnterWriteLock();
            try   { _state = loaded; }
            finally { _lock.ExitWriteLock(); }

            EventEmitter.EmitInfo($"screenshot-block policy loaded - enabled={_state.Enabled}");
        }
        catch (Exception ex) { EventEmitter.EmitError("screenshot_block_policy_load", $"{ex.Message} - starting disabled"); }
    }
}
