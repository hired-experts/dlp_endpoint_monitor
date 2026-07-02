using System.Runtime.InteropServices;
using DlpEndpointMonitor.Actions;
using DlpEndpointMonitor.Core;
using DlpEndpointMonitor.Win32;

namespace DlpEndpointMonitor.Monitors;

sealed class UsbMonitor : IDisposable
{
    readonly MessageWindow _window;
    readonly DeviceWhitelist  _whitelist;
    readonly DeviceBlacklist  _blacklist;
    readonly DisabledDevices  _disabled;

    public UsbMonitor(MessageWindow window, DeviceWhitelist whitelist, DeviceBlacklist blacklist, DisabledDevices disabled)
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

            if (!arrived && !removed)
            {
                return;
            }

            // ── Drive event — carries drive letter(s) ─────────────────────────

            if (hdr.dbch_devicetype == NativeMethods.DBT_DEVTYP_VOLUME)
            {
                var vol = Marshal.PtrToStructure<DEV_BROADCAST_VOLUME>(lParam);
                string[] drives = DriveLettersFromMask(vol.dbcv_unitmask);

                EventEmitter.Emit(arrived
                    ? new UsbDriveConnectedEvent(drives, EventEmitter.Ts())
                    : new UsbDriveDisconnectedEvent(drives, EventEmitter.Ts()));

                return;
            }

            // ── Device event — carries VID, PID, device path ──────────────────

            if (hdr.dbch_devicetype == NativeMethods.DBT_DEVTYP_DEVICEINTERFACE)
            {
                var iface = Marshal.PtrToStructure<DEV_BROADCAST_DEVICEINTERFACE>(lParam);
                var namePtr = lParam + 28;
                string devicePath = Marshal.PtrToStringAnsi(namePtr) ?? iface.dbcc_name;

                // Bluetooth paired devices (BTHENUM paths) are handled exclusively by BluetoothMonitor
                if (devicePath.Contains("BTHENUM", StringComparison.OrdinalIgnoreCase)) return;
                string     classGuid = iface.dbcc_classguid.ToString("B"); // "B" = {xxxxxxxx-...} with braces
                DeviceKind kind      = DeviceKindResolver.Resolve(classGuid, out int? usbClass);

                // Try full parse (VID/PID). Fall back to partial parse (instance ID only,
                // no VID/PID) so kind-based policy entries still match USBSTOR devices.
                var parsed = UsbActions.ParseDevicePath(devicePath)
                          ?? UsbActions.ParsePartialDevice(devicePath, kind);
                if (parsed is not null)
                {
                    string? groupId = UsbActions.GetGroupId(parsed.InstanceId);
                    parsed = parsed with { ClassGuid = classGuid, UsbClass = usbClass, Kind = kind, GroupId = groupId };
                }

                if (arrived && parsed is not null)
                    HandleArrival(parsed);
                else if (arrived)
                    EventEmitter.Emit(new UsbDeviceDetectedEvent(
                        null, null, null, devicePath, usbClass, kind, classGuid, null, EventEmitter.Ts()));
                else
                    EventEmitter.Emit(new UsbDeviceDisconnectedEvent(
                        parsed?.Vid is "" ? null : parsed?.Vid,
                        parsed?.Pid is "" ? null : parsed?.Pid,
                        parsed?.Serial,
                        devicePath,
                        usbClass,
                        kind,
                        classGuid,
                        parsed?.GroupId,
                        EventEmitter.Ts()));
            }
        }
        catch (Exception ex)
        {
            EventEmitter.EmitError("usb_monitor", ex.Message);
        }
    }

    /// <summary>
    /// Enumerates all currently connected USB device interfaces and runs each
    /// through the same arrival logic as a live plug event (emit + block if needed).
    /// Call once after the message loop is running, on a ThreadPool thread.
    /// </summary>
    public void EnumerateExisting()
    {
        try
        {
            foreach (var parsed in UsbActions.EnumerateConnected())
                HandleArrival(parsed);
        }
        catch (Exception ex)
        {
            EventEmitter.EmitError("usb_startup_enum", ex.Message);
        }
    }

    /// <summary>
    /// Re-checks all currently connected devices against the active policy and
    /// blocks any that are no longer allowed. Does not re-emit connect events.
    /// Call on a ThreadPool thread after the whitelist/blacklist rules change.
    /// </summary>
    public void BlockNonCompliant()
    {
        try
        {
            int checked_ = 0, blocked = 0;
            foreach (var parsed in UsbActions.EnumerateConnected())
            {
                checked_++;
                bool allowed = _whitelist.IsAllowed(parsed.Vid, parsed.Pid, parsed.Serial, parsed.Kind)
                            && !_blacklist.IsBlocked(parsed.Vid, parsed.Pid, parsed.Serial, parsed.Kind);
                EventEmitter.EmitInfo($"usb_policy_apply: {parsed.Vid}/{parsed.Pid} kind={parsed.Kind} allowed={allowed}");
                if (!allowed)
                {
                    blocked++;
                    Task.Run(() => BlockDevice(parsed));
                }
            }
            EventEmitter.EmitInfo($"usb_policy_apply: checked {checked_} device(s), blocking {blocked}");
        }
        catch (Exception ex)
        {
            EventEmitter.EmitError("usb_policy_apply", ex.Message);
        }
    }

    void HandleArrival(ParsedDevice parsed)
    {
        bool allowed = _whitelist.IsAllowed(parsed.Vid, parsed.Pid, parsed.Serial, parsed.Kind)
                    && !_blacklist.IsBlocked(parsed.Vid, parsed.Pid, parsed.Serial, parsed.Kind);

        EventEmitter.Emit(new UsbDeviceConnectedEvent(
            string.IsNullOrEmpty(parsed.Vid) ? null : parsed.Vid,
            string.IsNullOrEmpty(parsed.Pid) ? null : parsed.Pid,
            parsed.Serial,
            parsed.UsbClass,
            parsed.Kind,
            parsed.ClassGuid,
            parsed.GroupId,
            parsed.RawPath,
            allowed,
            EventEmitter.Ts()));

        if (!allowed)
        {
            Task.Run(() => BlockDevice(parsed));
        }
    }

    void BlockDevice(ParsedDevice parsed)
    {
        // Track the exact devnode that ends up disabled so restore can re-enable it by
        // instance ID later (a disabled device has no interface to enumerate).
        string? disabledId = null;

        var (ok, error) = UsbActions.DisableDevice(parsed.InstanceId);
        if (ok) disabledId = parsed.InstanceId;

        // If HID-level disable was rejected, try at the USB composite device level — reversible.
        if (!ok && parsed.GroupId is not null)
        {
            var (groupOk, groupErr) = UsbActions.DisableDevice(parsed.GroupId);
            if (groupOk) { ok = true; error = null; disabledId = parsed.GroupId; }
            else error ??= groupErr;
        }

        // Last resort: physically eject via CM_Request_Device_EjectW. Windows rejects
        // CM_Disable_DevNode for input devices (keyboards, mice) to prevent lockout.
        // Eject disconnects the device from the USB bus — same UX as Bluetooth pairing
        // removal: device disappears immediately, manual replug required to restore.
        // (An eject is not a CM_Disable, so there is nothing to persist/re-enable.)
        if (!ok && parsed.GroupId is not null)
        {
            var (ejectOk, ejectErr) = UsbActions.RequestEject(parsed.GroupId);
            if (ejectOk) { ok = true; error = null; }
            else error ??= ejectErr;
        }

        if (disabledId is not null)
            _disabled.Add(new DisabledDeviceRecord(disabledId, parsed.Vid, parsed.Pid, parsed.Serial, parsed.Kind));

        IEvent ev = ok
            ? new UsbDeviceBlockedEvent(parsed.Vid, parsed.Pid, parsed.Serial, parsed.UsbClass, parsed.Kind, parsed.ClassGuid, parsed.GroupId, parsed.InstanceId, EventEmitter.Ts())
            : new UsbDeviceBlockFailedEvent(parsed.Vid, parsed.Pid, parsed.Serial, parsed.UsbClass, parsed.Kind, parsed.ClassGuid, parsed.GroupId, parsed.InstanceId, error, EventEmitter.Ts());

        EventEmitter.Emit(ev);
    }

    /// <summary>
    /// Re-enables the USB devices this process disabled that the active policy no longer
    /// blocks, and forgets them. Reconciles against the PERSISTED disabled set (not a live
    /// enumeration): a disabled device has no active device interface, so it is invisible to
    /// interface enumeration - the only way to reach it is by the exact instance ID we saved
    /// when we disabled it. This is what makes an unblock restore devices even when the
    /// process was restarted between the block and the unblock.
    /// Call after the whitelist or blacklist is disabled, cleared, or loosened.
    /// </summary>
    public void RestoreCompliant()
    {
        try
        {
            int restored = 0, stillBlocked = 0;
            foreach (var d in _disabled.GetAll())
            {
                bool allowed = _whitelist.IsAllowed(d.Vid, d.Pid, d.Serial, d.Kind)
                            && !_blacklist.IsBlocked(d.Vid, d.Pid, d.Serial, d.Kind);
                if (!allowed) { stillBlocked++; continue; }

                // Best-effort: an absent (unplugged) device simply cannot be located; we still
                // forget it, since policy no longer blocks it and a replug will re-evaluate it.
                UsbActions.EnableDevice(d.InstanceId);
                _disabled.Remove(d.InstanceId);
                restored++;
            }
            EventEmitter.EmitInfo($"usb_policy_restore: restored {restored}, still-blocked {stillBlocked}");
        }
        catch (Exception ex)
        {
            EventEmitter.EmitError("usb_policy_restore", ex.Message);
        }
    }

    // dbcv_unitmask is a bitmask: bit 0 = A:\, bit 1 = B:\, bit 2 = C:\, etc.
    static string[] DriveLettersFromMask(uint mask)
    {
        var result = new List<string>();

        for (int i = 0; i < 26; i++)
        {
            if ((mask & (1u << i)) != 0)
            {
                result.Add($"{(char)('A' + i)}:\\");
            }
        }

        return [.. result];
    }

    public void Dispose() =>
        _window.DeviceChanged -= OnDeviceChanged;
}
