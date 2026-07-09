using System.Text.Json;

namespace DlpEndpointMonitor.Core;

/// <summary>
/// A USB or Bluetooth device this process disabled via CM_Disable_DevNode, with enough
/// context to reconcile it against policy later. <see cref="InstanceId"/> is the exact devnode
/// that was disabled (the interface node, the composite parent, the Bluetooth peripheral node,
/// whichever succeeded). <see cref="Mac"/> is set only for a Bluetooth-blocked device (mirrors
/// <see cref="UsbDeviceEntry"/>'s existing precedent of carrying both USB and Bluetooth
/// identity side by side) - <c>Vid</c>/<c>Pid</c> are empty strings in that case, since a
/// Bluetooth device has no USB vendor/product ID. <see cref="GroupId"/> is set only for a
/// USB-blocked composite device (mirrors <see cref="Actions.ParsedDevice.GroupId"/>) so
/// UsbMonitor's restore path can report which composite group an unblock corresponds to.
/// </summary>
public sealed record DisabledDeviceRecord(
    string InstanceId, string Vid, string Pid, string? Serial, DeviceKind Kind, string? Mac = null, string? GroupId = null);

internal sealed class DisabledDevicesState
{
    public List<DisabledDeviceRecord> Devices { get; set; } = [];
}

/// <summary>
/// Persistent record of the USB devices this process disabled by policy. Persisting is
/// essential: a disabled device has NO active device interface, so it cannot be found by
/// interface enumeration - the only way to re-enable it is by its exact instance ID, which
/// is kept here and survives restarts. On restore we re-enable (and forget) any recorded
/// device the current policy no longer blocks. This is what makes "unblock re-enables"
/// work even when the process was restarted between the block and the unblock.
/// </summary>
sealed class DisabledDevices
{
    readonly ReaderWriterLockSlim _lock = new();
    readonly string               _storageDir;
    readonly string               _storagePath;
    DisabledDevicesState          _state = new();

    /// <param name="storageDir">
    /// Directory to persist under. Defaults to <see cref="StorageLocation.Default"/>
    /// (%ProgramData%\DlpEndpointMonitor, same directory UsbDeviceList persists
    /// whitelist/blacklist under) when null - pass an explicit directory only to isolate
    /// storage, e.g. in tests.
    /// </param>
    public DisabledDevices(string? storageDir = null)
    {
        _storageDir  = storageDir ?? StorageLocation.Default;
        _storagePath = Path.Combine(_storageDir, "disabled-devices.json");
        Load();
    }

    /// <summary>Record a device as disabled by policy (idempotent by instance ID).</summary>
    public void Add(DisabledDeviceRecord device)
    {
        _lock.EnterWriteLock();
        try
        {
            _state.Devices.RemoveAll(d => d.InstanceId.Equals(device.InstanceId, StringComparison.OrdinalIgnoreCase));
            _state.Devices.Add(device);
        }
        finally { _lock.ExitWriteLock(); }
        Save();
    }

    public void Remove(string instanceId)
    {
        _lock.EnterWriteLock();
        try   { _state.Devices.RemoveAll(d => d.InstanceId.Equals(instanceId, StringComparison.OrdinalIgnoreCase)); }
        finally { _lock.ExitWriteLock(); }
        Save();
    }

    public IReadOnlyList<DisabledDeviceRecord> GetAll()
    {
        _lock.EnterReadLock();
        try   { return _state.Devices.ToArray(); }
        finally { _lock.ExitReadLock(); }
    }

    // ── Disk I/O (mirrors UsbDeviceList: source-gen JSON, atomic temp-file write) ──

    void Save()
    {
        try
        {
            Directory.CreateDirectory(_storageDir);

            DisabledDevicesState snapshot;
            _lock.EnterReadLock();
            try   { snapshot = new DisabledDevicesState { Devices = _state.Devices.ToList() }; }
            finally { _lock.ExitReadLock(); }

            string tmp = _storagePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(snapshot, AppJsonContext.Default.DisabledDevicesState));
            File.Move(tmp, _storagePath, overwrite: true);
        }
        catch (Exception ex) { EventEmitter.EmitError("disabled_devices_save", ex.Message); }
    }

    void Load()
    {
        try
        {
            if (!File.Exists(_storagePath)) return;

            var loaded = JsonSerializer.Deserialize(File.ReadAllText(_storagePath), AppJsonContext.Default.DisabledDevicesState);
            if (loaded is null) return;

            _lock.EnterWriteLock();
            try   { _state = loaded; }
            finally { _lock.ExitWriteLock(); }

            EventEmitter.EmitInfo($"disabled-devices loaded - {_state.Devices.Count} device(s)");
        }
        catch (Exception ex) { EventEmitter.EmitError("disabled_devices_load", $"{ex.Message} - starting empty"); }
    }
}
