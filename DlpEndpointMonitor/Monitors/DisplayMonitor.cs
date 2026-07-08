using System.Runtime.InteropServices;
using DlpEndpointMonitor.Actions;
using DlpEndpointMonitor.Core;
using DlpEndpointMonitor.Win32;

namespace DlpEndpointMonitor.Monitors;

sealed class DisplayMonitor : IDisposable
{
    readonly MessageWindow _window;
    readonly DeviceWhitelist  _whitelist;
    readonly DeviceBlacklist  _blacklist;

    // Debounce token for WM_DISPLAYCHANGE — prevents back-to-back calls while
    // HDMI audio interfaces cycle after a SetDisplayConfig topology switch.
    CancellationTokenSource? _displayChangeCts;

    public DisplayMonitor(MessageWindow window, DeviceWhitelist whitelist, DeviceBlacklist blacklist)
    {
        _window    = window;
        _whitelist = whitelist;
        _blacklist = blacklist;
        _window.DeviceChanged  += OnDeviceChanged;
        _window.DisplayChanged += OnDisplayChanged;
    }

    void OnDeviceChanged(int wParam, IntPtr lParam)
    {
        if (lParam == IntPtr.Zero) return;

        try
        {
            var hdr = Marshal.PtrToStructure<DEV_BROADCAST_HDR>(lParam);
            if (hdr.dbch_devicetype != NativeMethods.DBT_DEVTYP_DEVICEINTERFACE) return;

            var iface = Marshal.PtrToStructure<DEV_BROADCAST_DEVICEINTERFACE>(lParam);
            if (iface.dbcc_classguid != DisplayActions.MonitorGuid) return;

            var    namePtr = lParam + 28;
            string rawPath = Marshal.PtrToStringAnsi(namePtr) ?? iface.dbcc_name;
            bool   arrived = wParam == NativeMethods.DBT_DEVICEARRIVAL;
            var    parsed  = DisplayActions.ParseMonitorPath(rawPath);

            if (arrived && parsed is not null)
                HandleArrival(parsed);
            else if (arrived)
                EventEmitter.Emit(new MonitorConnectedEvent(null, null, rawPath, EventEmitter.Ts()));
            else
                EventEmitter.Emit(new MonitorDisconnectedEvent(
                    parsed?.Vid is "" ? null : parsed?.Vid,
                    parsed?.Pid is "" ? null : parsed?.Pid,
                    rawPath, EventEmitter.Ts()));
        }
        catch (Exception ex)
        {
            EventEmitter.EmitError("display_monitor", ex.Message);
        }
    }

    // Windows sends WM_DISPLAYCHANGE whenever the active display topology changes —
    // including when it auto-activates an external monitor after a cable is connected.
    // We debounce 800 ms to let the topology settle (HDMI audio interfaces often cycle
    // during a SetDisplayConfig call) before re-checking compliance.
    void OnDisplayChanged()
    {
        try
        {
            // Replace the previous debounce token. Cancel + dispose the previous source
            // HERE (not in the continuation) so `_displayChangeCts` never dangles at an
            // already-disposed instance - that dangling reference was the cause of the
            // ObjectDisposedException crash when a later WM_DISPLAYCHANGE called Cancel()
            // on a source its own completed continuation had already disposed.
            var previous = _displayChangeCts;
            _displayChangeCts = null;
            if (previous is not null)
            {
                try { previous.Cancel(); } catch (ObjectDisposedException) { }
                previous.Dispose();
            }

            var cts = new CancellationTokenSource();
            _displayChangeCts = cts;

            Task.Delay(800, cts.Token).ContinueWith(t =>
            {
                if (!t.IsCanceled) BlockNonCompliant();
            }, TaskScheduler.Default);
        }
        catch (Exception ex)
        {
            // WM_DISPLAYCHANGE runs on the message-loop thread - never let this throw,
            // or the unhandled exception tears down the whole native process.
            EventEmitter.EmitError("display_monitor", ex.Message);
        }
    }

    void HandleArrival(ParsedDevice parsed)
    {
        bool allowed = _whitelist.IsAllowed(parsed.Vid, parsed.Pid, parsed.Serial, parsed.Kind)
                    && !_blacklist.IsBlocked(parsed.Vid, parsed.Pid, parsed.Serial, parsed.Kind);

        EventEmitter.Emit(new MonitorConnectedEvent(
            string.IsNullOrEmpty(parsed.Vid) ? null : parsed.Vid,
            string.IsNullOrEmpty(parsed.Pid) ? null : parsed.Pid,
            parsed.RawPath, EventEmitter.Ts()));

        if (!allowed)
            Task.Run(() => BlockAllExternal(parsed));
    }

    /// <summary>
    /// Re-checks all currently connected monitors and disables external displays if any are
    /// non-compliant. Call after policy rules change.
    /// </summary>
    public void BlockNonCompliant()
    {
        try
        {
            int checked_ = 0, blocked = 0;
            foreach (var parsed in DisplayActions.EnumerateConnected())
            {
                checked_++;
                bool allowed = _whitelist.IsAllowed(parsed.Vid, parsed.Pid, parsed.Serial, parsed.Kind)
                            && !_blacklist.IsBlocked(parsed.Vid, parsed.Pid, parsed.Serial, parsed.Kind);
                if (!allowed) blocked++;
            }

            if (blocked > 0)
            {
                var (ok, error) = DisplayActions.DisableExternalDisplays();
                if (!ok) EventEmitter.EmitError("monitor_policy_apply", error ?? "DisableExternalDisplays failed");
            }

            EventEmitter.EmitInfo($"monitor_policy_apply: checked {checked_} monitor(s), blocking {blocked}");
        }
        catch (Exception ex)
        {
            EventEmitter.EmitError("monitor_policy_apply", ex.Message);
        }
    }

    /// <summary>
    /// Re-enables external displays if all connected monitors are now compliant.
    /// Call after the whitelist or blacklist is disabled or cleared.
    /// </summary>
    public void RestoreCompliant()
    {
        try
        {
            bool anyBlocked = DisplayActions.EnumerateConnected().Any(p =>
                !(_whitelist.IsAllowed(p.Vid, p.Pid, p.Serial, p.Kind)
               && !_blacklist.IsBlocked(p.Vid, p.Pid, p.Serial, p.Kind)));

            if (!anyBlocked)
            {
                var (_, error) = DisplayActions.EnableExternalDisplays();
                if (error is not null)
                    EventEmitter.EmitError("monitor_policy_restore", error);
                EventEmitter.EmitInfo("monitor_policy_restore: external displays re-enabled");
            }
            else
            {
                EventEmitter.EmitInfo("monitor_policy_restore: non-compliant monitor still connected, staying blocked");
            }
        }
        catch (Exception ex)
        {
            EventEmitter.EmitError("monitor_policy_restore", ex.Message);
        }
    }

    static async Task BlockAllExternal(ParsedDevice parsed)
    {
        try
        {
            // DBT_DEVICEARRIVAL fires when Windows enumerates the PnP device, but the display
            // output is activated asynchronously afterwards. Wait 1 s so the external path
            // appears in QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS) before we try to block it.
            await Task.Delay(1000);

            var (ok, error) = DisplayActions.DisableExternalDisplays();

            string? vid = string.IsNullOrEmpty(parsed.Vid) ? null : parsed.Vid;
            string? pid = string.IsNullOrEmpty(parsed.Pid) ? null : parsed.Pid;

            IEvent ev = ok
                ? new MonitorBlockedEvent(vid, pid, parsed.RawPath, EventEmitter.Ts())
                : new MonitorBlockFailedEvent(vid, pid, parsed.RawPath, error, EventEmitter.Ts());

            EventEmitter.Emit(ev);
        }
        catch (Exception ex)
        {
            EventEmitter.Emit(new MonitorBlockFailedEvent(
                string.IsNullOrEmpty(parsed.Vid) ? null : parsed.Vid,
                string.IsNullOrEmpty(parsed.Pid) ? null : parsed.Pid,
                parsed.RawPath, ex.Message, EventEmitter.Ts()));
        }
    }

    public void Dispose()
    {
        _window.DeviceChanged  -= OnDeviceChanged;
        _window.DisplayChanged -= OnDisplayChanged;
        var cts = _displayChangeCts;
        _displayChangeCts = null;
        if (cts is not null)
        {
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
            cts.Dispose();
        }
    }
}
