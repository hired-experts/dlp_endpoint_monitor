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

    public BluetoothMonitor(MessageWindow window, DeviceWhitelist whitelist, DeviceBlacklist blacklist)
    {
        _window    = window;
        _whitelist = whitelist;
        _blacklist = blacklist;
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

            // Only handle paired BT device paths — ignore USB-level BT adapter events
            if (!path.Contains("BTHENUM", StringComparison.OrdinalIgnoreCase)) return;

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
            foreach (var device in BluetoothActions.EnumerateConnected())
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
            foreach (var device in BluetoothActions.EnumerateConnected())
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

    static void BlockDevice(string mac, DeviceKind kind, string name)
    {
        var (ok, error) = BluetoothActions.RemovePairing(mac);
        IEvent ev = ok
            ? new BluetoothDeviceBlockedEvent(mac, kind, name, EventEmitter.Ts())
            : new BluetoothDeviceBlockFailedEvent(mac, kind, name, error, EventEmitter.Ts());
        EventEmitter.Emit(ev);
    }

    public void Dispose() =>
        _window.DeviceChanged -= OnDeviceChanged;
}
