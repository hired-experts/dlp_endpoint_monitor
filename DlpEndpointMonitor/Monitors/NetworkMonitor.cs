using System.Runtime.InteropServices;
using DlpEndpointMonitor.Actions;
using DlpEndpointMonitor.Core;
using DlpEndpointMonitor.Win32;

namespace DlpEndpointMonitor.Monitors;

/// <summary>
/// Owns every DeviceKind.Network device exclusively - UsbMonitor's generic per-interface /
/// composite-group pipeline was designed for HID-style peripherals and does not fit NIC
/// semantics (a NIC is not composite, and disabling one is catastrophic if it is the machine's
/// only adapter). Structurally mirrors BluetoothMonitor: no composite-group concept, per-device
/// whitelist/blacklist check, own RestoreCompliant. See UsbActions.IsBuiltIn for the safety
/// gate that refuses to disable the machine's own built-in WiFi/Ethernet adapter.
/// </summary>
sealed class NetworkMonitor : IDisposable
{
    readonly MessageWindow _window;
    readonly DeviceWhitelist  _whitelist;
    readonly DeviceBlacklist  _blacklist;
    readonly DisabledDevices  _disabled;

    public NetworkMonitor(MessageWindow window, DeviceWhitelist whitelist, DeviceBlacklist blacklist, DisabledDevices disabled)
    {
        _window    = window;
        _whitelist = whitelist;
        _blacklist = blacklist;
        _disabled  = disabled;
        _window.DeviceChanged += OnDeviceChanged;
    }

    void OnDeviceChanged(int wParam, IntPtr lParam)
    {
        if (lParam == IntPtr.Zero) return;

        try
        {
            var hdr = Marshal.PtrToStructure<DEV_BROADCAST_HDR>(lParam);

            bool arrived = wParam == NativeMethods.DBT_DEVICEARRIVAL;
            bool removed = wParam == NativeMethods.DBT_DEVICEREMOVECOMPLETE;
            if (!arrived && !removed) return;

            if (hdr.dbch_devicetype != NativeMethods.DBT_DEVTYP_DEVICEINTERFACE) return;

            var iface = Marshal.PtrToStructure<DEV_BROADCAST_DEVICEINTERFACE>(lParam);
            var namePtr = lParam + 28;
            string devicePath = Marshal.PtrToStringAnsi(namePtr) ?? iface.dbcc_name;

            // Bluetooth paired devices are BluetoothMonitor's territory (mirrors UsbMonitor's own guard).
            if (devicePath.Contains("BTHENUM", StringComparison.OrdinalIgnoreCase)
                || devicePath.Contains("BTHLE", StringComparison.OrdinalIgnoreCase)) return;

            string     classGuid = iface.dbcc_classguid.ToString("B");
            DeviceKind kind      = DeviceKindResolver.Resolve(classGuid, out int? usbClass);

            // Complement of UsbMonitor's new Network exclusion - both monitors see the SAME
            // window.DeviceChanged event and partition purely by resolved Kind.
            if (kind != DeviceKind.Network) return;

            var parsed = UsbActions.ParseDevicePath(devicePath)
                      ?? UsbActions.ParsePartialDevice(devicePath, kind);
            if (parsed is not null)
            {
                string? groupId = UsbActions.GetGroupId(parsed.InstanceId);
                parsed = parsed with { ClassGuid = classGuid, UsbClass = usbClass, Kind = kind, GroupId = groupId };
            }

            if (arrived && parsed is not null)
                HandleArrival(parsed);
            else if (!arrived)
                EventEmitter.Emit(new NetworkDeviceDisconnectedEvent(
                    parsed?.Vid is "" ? null : parsed?.Vid,
                    parsed?.Pid is "" ? null : parsed?.Pid,
                    parsed?.Serial,
                    devicePath,
                    usbClass,
                    classGuid,
                    parsed?.InstanceId,
                    EventEmitter.Ts()));
        }
        catch (Exception ex)
        {
            EventEmitter.EmitError("network_monitor", ex.Message);
        }
    }

    /// <summary>
    /// Enumerates all currently connected network-kind devices and runs each through arrival
    /// logic. Call once after the message loop is running, on a ThreadPool thread.
    /// </summary>
    public void EnumerateExisting()
    {
        try
        {
            foreach (var parsed in UsbActions.EnumerateConnected().Where(p => p.Kind == DeviceKind.Network))
                HandleArrival(parsed);
        }
        catch (Exception ex)
        {
            EventEmitter.EmitError("network_startup_enum", ex.Message);
        }
    }

    /// <summary>
    /// Re-checks all currently connected network devices against the active policy and blocks
    /// any that are no longer allowed. Call on a ThreadPool thread after policy rules change.
    /// </summary>
    public void BlockNonCompliant()
    {
        try
        {
            int checked_ = 0, blocked = 0;
            foreach (var parsed in UsbActions.EnumerateConnected().Where(p => p.Kind == DeviceKind.Network))
            {
                checked_++;
                bool allowed = _whitelist.IsAllowed(parsed.Vid, parsed.Pid, parsed.Serial, parsed.Kind)
                            && !_blacklist.IsBlocked(parsed.Vid, parsed.Pid, parsed.Serial, parsed.Kind);
                if (!allowed)
                {
                    blocked++;
                    EventEmitter.EmitInfo($"net_policy_apply: {parsed.Vid}/{parsed.Pid} kind={parsed.Kind} allowed={allowed}");
                    Task.Run(() => BlockDevice(parsed));
                }
            }
            EventEmitter.EmitInfo($"net_policy_apply: checked {checked_} device(s), blocking {blocked}");
        }
        catch (Exception ex)
        {
            EventEmitter.EmitError("net_policy_apply", ex.Message);
        }
    }

    void HandleArrival(ParsedDevice parsed)
    {
        bool allowed = _whitelist.IsAllowed(parsed.Vid, parsed.Pid, parsed.Serial, parsed.Kind)
                    && !_blacklist.IsBlocked(parsed.Vid, parsed.Pid, parsed.Serial, parsed.Kind);

        EventEmitter.Emit(new NetworkDeviceConnectedEvent(
            string.IsNullOrEmpty(parsed.Vid) ? null : parsed.Vid,
            string.IsNullOrEmpty(parsed.Pid) ? null : parsed.Pid,
            parsed.Serial,
            parsed.UsbClass,
            parsed.ClassGuid,
            parsed.InstanceId,
            parsed.RawPath,
            allowed,
            EventEmitter.Ts()));

        if (!allowed)
            Task.Run(() => BlockDevice(parsed));
    }

    /// <summary>
    /// Blocks a network device by disabling its devnode - unlike UsbMonitor, there is no
    /// composite-group escalation and no eject fallback; neither HID-lockout workaround
    /// applies to a NIC. The IsBuiltIn check MUST run before any disable attempt: the
    /// machine's own WiFi/Ethernet adapter is exactly as essential as a built-in keyboard.
    /// </summary>
    void BlockDevice(ParsedDevice parsed)
    {
        if (UsbActions.IsBuiltIn(parsed.InstanceId))
        {
            EventEmitter.Emit(new NetworkDeviceBlockFailedEvent(
                parsed.Vid, parsed.Pid, parsed.Serial, parsed.InstanceId,
                "protected internal network adapter - refused to block",
                EventEmitter.Ts()));
            return;
        }

        var (ok, error) = UsbActions.DisableDevice(parsed.InstanceId);
        if (ok)
            _disabled.Add(new DisabledDeviceRecord(parsed.InstanceId, parsed.Vid, parsed.Pid, parsed.Serial, parsed.Kind));

        IEvent ev = ok
            ? new NetworkDeviceBlockedEvent(parsed.Vid, parsed.Pid, parsed.Serial, parsed.InstanceId, EventEmitter.Ts())
            : new NetworkDeviceBlockFailedEvent(parsed.Vid, parsed.Pid, parsed.Serial, parsed.InstanceId, error, EventEmitter.Ts());

        EventEmitter.Emit(ev);
    }

    /// <summary>
    /// Re-enables network devices this process disabled that the active policy no longer
    /// blocks, and forgets them. Only records with Mac unset AND Kind==Network are considered -
    /// USB and Bluetooth records share the same persisted list and are skipped (mirrors
    /// BluetoothMonitor.RestoreCompliant's own partition-by-ownership filter).
    /// </summary>
    public void RestoreCompliant()
    {
        try
        {
            int restored = 0, stillBlocked = 0;
            foreach (var d in _disabled.GetAll().Where(d => d.Mac is null && d.Kind == DeviceKind.Network))
            {
                bool allowed = _whitelist.IsAllowed(d.Vid, d.Pid, d.Serial, d.Kind)
                            && !_blacklist.IsBlocked(d.Vid, d.Pid, d.Serial, d.Kind);
                if (!allowed) { stillBlocked++; continue; }

                var (ok, error) = UsbActions.EnableDevice(d.InstanceId);
                if (ok)
                {
                    _disabled.Remove(d.InstanceId);
                    restored++;
                    EventEmitter.Emit(new NetworkDeviceUnblockedEvent(d.Vid, d.Pid, d.Serial, d.InstanceId, EventEmitter.Ts()));
                }
                else if (error is not null && error.Contains("Locate", StringComparison.Ordinal))
                {
                    // Device is absent (unplugged): can't re-enable it now, but policy allows it,
                    // so forget the record - a physical replug will re-evaluate it as allowed.
                    _disabled.Remove(d.InstanceId);
                }
                else
                {
                    // Present but re-enable FAILED: keep the record so the next restore retries
                    // it (do NOT orphan a still-disabled device), and surface the failure.
                    stillBlocked++;
                    EventEmitter.Emit(new NetworkDeviceUnblockFailedEvent(d.Vid, d.Pid, d.Serial, d.InstanceId, error, EventEmitter.Ts()));
                }
            }
            EventEmitter.EmitInfo($"net_policy_restore: restored {restored}, still-blocked {stillBlocked}");
        }
        catch (Exception ex)
        {
            EventEmitter.EmitError("net_policy_restore", ex.Message);
        }
    }

    public void Dispose() =>
        _window.DeviceChanged -= OnDeviceChanged;
}
