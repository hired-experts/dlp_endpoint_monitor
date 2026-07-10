using System.Runtime.InteropServices;
using DlpEndpointMonitor.Actions;
using DlpEndpointMonitor.Core;
using DlpEndpointMonitor.Win32;

namespace DlpEndpointMonitor.Monitors;

sealed class BluetoothMonitor : IDisposable
{
    readonly MessageWindow _window;
    readonly DeviceWhitelist  _whitelist;
    readonly DeviceBlacklist  _blacklist;
    readonly DisabledDevices  _disabled;

    // How currently-connected Bluetooth devices are enumerated. In a real service
    // deployment this binary may run in Session 0, where BluetoothFindFirstDevice
    // yields nothing; when a companion process owns Bluetooth enumeration this delegate
    // routes through it instead of calling BluetoothActions.EnumerateConnected directly.
    // Program.cs decides which at construction time — there is no default, so the choice
    // is always explicit.
    readonly Func<IReadOnlyList<BluetoothActions.BtDevice>> _enumerateBluetoothDevices;

    public BluetoothMonitor(
        MessageWindow window, DeviceWhitelist whitelist, DeviceBlacklist blacklist,
        DisabledDevices disabled,
        Func<IReadOnlyList<BluetoothActions.BtDevice>> enumerateBluetoothDevices)
    {
        _window    = window;
        _whitelist = whitelist;
        _blacklist = blacklist;
        _disabled  = disabled;
        _enumerateBluetoothDevices = enumerateBluetoothDevices;
        _window.DeviceChanged += OnDeviceChanged;
    }

    void OnDeviceChanged(int wParam, IntPtr lParam)
    {
        if (lParam == IntPtr.Zero) return;

        bool arrived = wParam == NativeMethods.DBT_DEVICEARRIVAL;
        bool removed = wParam == NativeMethods.DBT_DEVICEREMOVECOMPLETE;
        if (!arrived && !removed) return;

        try
        {
            var hdr = Marshal.PtrToStructure<DEV_BROADCAST_HDR>(lParam);
            if (hdr.dbch_devicetype != NativeMethods.DBT_DEVTYP_DEVICEINTERFACE) return;

            var namePtr = lParam + 28;
            string path = Marshal.PtrToStringAnsi(namePtr) ?? string.Empty;

            // Only handle paired BT device paths — ignore USB-level BT adapter events.
            // "BTHLE" also matches "BTHLEDEVICE" paths (a GATT-service child, not the
            // peripheral) — harmless: ParseMacFromPath's BLE regex only matches the true
            // top-level "BTHLE...DEV_<mac>..." format, so a GATT-service child path simply
            // fails to yield a MAC below and is skipped, same as any other unparseable path.
            bool isBluetoothPath = path.Contains("BTHENUM", StringComparison.OrdinalIgnoreCase)
                                || path.Contains("BTHLE", StringComparison.OrdinalIgnoreCase);
            if (!isBluetoothPath) return;

            string?    mac  = BluetoothActions.ParseMacFromPath(path);
            DeviceKind kind = BluetoothActions.ParseKindFromPath(path);

            if (mac is null) return;

            if (arrived)
                HandleArrival(mac, kind, name: string.Empty);
            else
                EventEmitter.Emit(new BluetoothDeviceDisconnectedEvent(mac, kind, string.Empty, EventEmitter.Ts()));
        }
        catch (Exception ex)
        {
            EventEmitter.EmitError("bluetooth_monitor", ex.Message);
        }
    }

    /// <summary>
    /// Enumerates all currently connected BT devices and runs each through arrival logic.
    /// Call once after the message loop is running, on a ThreadPool thread.
    /// </summary>
    public void EnumerateExisting()
    {
        try
        {
            foreach (var device in _enumerateBluetoothDevices())
                HandleArrival(device.Mac, device.Kind, device.Name);
        }
        catch (Exception ex)
        {
            EventEmitter.EmitError("bluetooth_startup_enum", ex.Message);
        }
    }

    /// <summary>
    /// Re-checks all currently connected BT devices against the active policy
    /// and removes pairings for any that are no longer allowed.
    /// </summary>
    public void BlockNonCompliant()
    {
        try
        {
            int checked_ = 0, blocked = 0;
            foreach (var device in _enumerateBluetoothDevices())
            {
                checked_++;
                bool allowed = _whitelist.IsAllowed(device.Mac, device.Kind)
                            && !_blacklist.IsBlocked(device.Mac, device.Kind);
                EventEmitter.EmitInfo($"bt_policy_apply: {device.Mac} kind={device.Kind} allowed={allowed}");
                if (!allowed)
                {
                    blocked++;
                    string mac  = device.Mac;
                    DeviceKind kind = device.Kind;
                    string name = device.Name;
                    Task.Run(() => BlockDevice(mac, kind, name));
                }
            }
            EventEmitter.EmitInfo($"bt_policy_apply: checked {checked_} device(s), blocking {blocked}");
        }
        catch (Exception ex)
        {
            EventEmitter.EmitError("bt_policy_apply", ex.Message);
        }
    }

    void HandleArrival(string mac, DeviceKind kind, string name)
    {
        bool allowed = _whitelist.IsAllowed(mac, kind) && !_blacklist.IsBlocked(mac, kind);
        EventEmitter.Emit(new BluetoothDeviceConnectedEvent(mac, kind, name, allowed, EventEmitter.Ts()));
        if (!allowed)
            Task.Run(() => BlockDevice(mac, kind, name));
    }

    /// <summary>
    /// Blocks a Bluetooth device by disabling its own PnP node (reversible via
    /// <see cref="RestoreCompliant"/>) rather than unpairing it outright. Falls back to the
    /// old unconditional unpair (<see cref="BluetoothActions.RemovePairing"/>) only when the
    /// node can't be found - this guarantees the device is always actually blocked, at the
    /// cost of losing reversibility for that one unresolvable device, rather than silently
    /// leaving a non-compliant device connected because the new lookup came up empty.
    /// </summary>
    void BlockDevice(string mac, DeviceKind kind, string name)
    {
        string? instanceId = BluetoothActions.FindInstanceIdByMac(mac);

        if (instanceId is null)
        {
            // Node not found - fall back to the old mechanism so blocking never silently no-ops.
            var (unpaired, unpairError) = BluetoothActions.RemovePairing(mac);
            IEvent fallbackEv = unpaired
                ? new BluetoothDeviceBlockedEvent(mac, kind, name, EventEmitter.Ts())
                : new BluetoothDeviceBlockFailedEvent(mac, kind, name, unpairError, EventEmitter.Ts());
            EventEmitter.Emit(fallbackEv);
            return;
        }

        var (ok, error) = UsbActions.DisableDevice(instanceId);
        if (ok)
            _disabled.Add(new DisabledDeviceRecord(instanceId, "", "", null, kind, mac));

        // Node WAS found - a disable failure here is a real failure, not an unresolvable-device
        // case, so it must NOT fall back to unpair (that would mask the real error).
        IEvent ev = ok
            ? new BluetoothDeviceBlockedEvent(mac, kind, name, EventEmitter.Ts())
            : new BluetoothDeviceBlockFailedEvent(mac, kind, name, error, EventEmitter.Ts());
        EventEmitter.Emit(ev);
    }

    /// <summary>
    /// Re-enables Bluetooth devices this process disabled (not unpaired) that the active
    /// policy no longer blocks, and forgets them. Structurally simpler than
    /// <see cref="UsbMonitor.RestoreCompliant"/> - no composite-group concept applies to
    /// Bluetooth peripherals. A device this process unpaired (the fallback path in
    /// <see cref="BlockDevice"/>) has no record here and cannot be restored in software -
    /// only entries with <see cref="DisabledDeviceRecord.Mac"/> set (Bluetooth-blocked via
    /// disable) are considered; USB records share the same persisted list and are skipped.
    /// Call after the whitelist or blacklist is disabled, cleared, or loosened.
    /// </summary>
    public void RestoreCompliant()
    {
        try
        {
            int restored = 0, stillBlocked = 0;
            foreach (var d in _disabled.GetAll().Where(d => d.Mac is not null))
            {
                bool allowed = _whitelist.IsAllowed(d.Mac!, d.Kind) && !_blacklist.IsBlocked(d.Mac!, d.Kind);
                if (!allowed) { stillBlocked++; continue; }

                var (ok, error) = UsbActions.EnableDevice(d.InstanceId);
                if (ok)
                {
                    _disabled.Remove(d.InstanceId);
                    restored++;
                    EventEmitter.Emit(new BluetoothDeviceUnblockedEvent(d.Mac!, d.Kind, EventEmitter.Ts()));
                }
                else if (error is not null && error.Contains("Locate", StringComparison.Ordinal))
                {
                    // Device is absent (out of range/powered off): can't re-enable it now, but
                    // policy allows it, so forget the record - a future reconnect will be
                    // re-evaluated fresh as an arrival.
                    _disabled.Remove(d.InstanceId);
                }
                else
                {
                    stillBlocked++;
                    EventEmitter.Emit(new BluetoothDeviceUnblockFailedEvent(d.Mac!, d.Kind, error, EventEmitter.Ts()));
                }
            }
            EventEmitter.EmitInfo($"bt_policy_restore: restored {restored}, still-blocked {stillBlocked}");
        }
        catch (Exception ex)
        {
            EventEmitter.EmitError("bt_policy_restore", ex.Message);
        }
    }

    public void Dispose() =>
        _window.DeviceChanged -= OnDeviceChanged;
}
