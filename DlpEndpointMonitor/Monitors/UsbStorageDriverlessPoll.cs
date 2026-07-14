using System.Runtime.InteropServices;
using DlpEndpointMonitor.Actions;
using DlpEndpointMonitor.Core;
using DlpEndpointMonitor.Win32;

namespace DlpEndpointMonitor.Monitors;

/// <summary>
/// Fills a detection gap in <c>usb_storage_blocked</c>: a plain, single-function USB mass-storage
/// stick connecting while the usb_disable_storage kill switch is on never registers a device
/// INTERFACE at all (USBSTOR.sys is the only candidate driver for the whole devnode, and it never
/// binds), so <see cref="UsbMonitor"/>'s interface-arrival-based detection never sees it - not
/// just the event, the device is completely invisible to this process. This class is a separate,
/// additive detection path: it polls by USB BUS ENUMERATOR (<c>SetupDiGetClassDevsByEnumerator</c>
/// with Enumerator="USB"), which only requires the USB bus driver to have enumerated the devnode
/// at all - true the instant a device is physically present, independent of Setup Class
/// assignment, interface registration, or any bound function driver.
///
/// Lifecycle is tied to the kill switch, not free-running: <see cref="Start"/> is called from
/// Program.cs at boot if the switch is already on, and from
/// WindowsUsbStorageHandler.Handle(UsbDisableStorageCmd) after a successful registry write;
/// <see cref="Stop"/> from Handle(UsbEnableStorageCmd). Both are idempotent no-ops when called
/// while already in the target state. See AGENTS.md section 10 for the full incident writeup.
/// </summary>
sealed class UsbStorageDriverlessPoll : IDisposable
{
    // "A few seconds" - not finalized to a more precise value; chosen to match this codebase's
    // other debounce/poll timers' general order of magnitude (DisplayMonitor's 800ms
    // WM_DISPLAYCHANGE debounce, the companion's whitelistReload poll).
    const int DefaultPollIntervalMs = 3000;

    readonly TimeSpan _interval;
    readonly Lock _lock = new();
    readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);

    Timer? _timer;
    bool _isFirstCycleSinceStart;

    public UsbStorageDriverlessPoll(int pollIntervalMs = DefaultPollIntervalMs)
    {
        _interval = TimeSpan.FromMilliseconds(pollIntervalMs);
    }

    /// <summary>
    /// Starts the poll - a no-op if already running, so a re-entrant usb_disable_storage never
    /// spawns a second, overlapping timer. Clears any leftover seen-set state from a previous
    /// on/off cycle and arms the first-cycle baseline flag fresh, so a later re-enable/re-disable's
    /// baseline cycle never inherits stale entries from a much earlier cycle.
    /// </summary>
    public void Start()
    {
        lock (_lock)
        {
            if (_timer is not null) return; // already running - idempotent no-op

            _seen.Clear();
            _isFirstCycleSinceStart = true;
            // due=0 fires the first cycle immediately; period=infinite so RunCycle's own
            // Reschedule() drives every subsequent tick - a slow cycle can never overlap the next.
            _timer = new Timer(_ => RunCycle(), null, TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// Stops the poll - a no-op if already stopped. Blocks until any in-flight cycle (including its
    /// own reschedule) has fully finished before returning - uses
    /// <see cref="Timer.Dispose(WaitHandle)"/>, not the bare parameterless overload, precisely so a
    /// fast re-toggle can never race a cycle still finishing from the previous Start(). Clears the
    /// seen-set so a later Start()'s baseline starts from nothing.
    /// </summary>
    public void Stop()
    {
        Timer? timerToStop;
        lock (_lock)
        {
            timerToStop = _timer;
            _timer = null; // cleared BEFORE Dispose so a concurrently-running cycle's own
                            // Reschedule() (which takes this same lock) sees null and no-ops
                            // instead of racing Dispose - see RunCycle/Reschedule.
        }

        if (timerToStop is null) return; // already stopped - idempotent no-op

        using var stopped = new ManualResetEvent(false);
        timerToStop.Dispose(stopped);
        stopped.WaitOne();

        lock (_lock)
        {
            _seen.Clear();
        }
    }

    public void Dispose() => Stop();

    /// <summary>
    /// Lets a caller OTHER than this poll's own cycles - specifically
    /// <see cref="UsbMonitor"/>.HandleArrival's inline usb_storage_blocked check for a device that
    /// got a normal interface arrival despite the kill switch being on (e.g. a composite device
    /// whose parent still enumerates via usbccgp.sys) - claim an instance ID before emitting. Guards
    /// the SAME seen-set under the SAME lock this poll's own cycles use, so a device both paths
    /// could independently notice is only ever reported once. Returns true the first time anyone
    /// claims this instance ID, false every time after.
    /// </summary>
    public bool TryClaimNewArrival(string instanceId)
    {
        lock (_lock)
        {
            return _seen.Add(instanceId);
        }
    }

    void RunCycle()
    {
        try
        {
            var present = EnumerateMassStorageInstanceIds();
            if (present is null)
            {
                // SetupDiGetClassDevsByEnumerator itself failed - distinct from "genuinely zero
                // devnodes present". Treating failure as an empty set would make ReconcileCycle
                // evict every currently-seen device as if it had all just unplugged, and the next
                // successful cycle would then misreport all of them as "new" - the same misleading
                // burst the first-cycle baseline exists to prevent, just triggered by a Win32 hiccup
                // instead of a timer restart. Skip reconciliation entirely this cycle and retry
                // fresh next cycle.
                EventEmitter.EmitError("usb_storage_driverless_poll", "device enumeration failed, skipping this cycle");
                return;
            }

            List<string> newlyAppeared;

            lock (_lock)
            {
                // Stop() already ran (and already cleared the seen-set) between the timer firing
                // and this callback acquiring the lock - nothing to reconcile or emit.
                if (_timer is null) return;

                bool wasFirstCycle = _isFirstCycleSinceStart;
                _isFirstCycleSinceStart = false;
                (newlyAppeared, _) = ReconcileCycle(_seen, present, wasFirstCycle);
            }

            foreach (var instanceId in newlyAppeared)
            {
                // Same Vid/Pid/Serial extraction UsbMonitor.HandleArrival's own inline emission of
                // this event already uses - ParseDevicePath matches "VID_xxxx&PID_xxxx" wherever it
                // appears in a raw instance-ID string, not just a device-interface path, so it works
                // directly against the instance IDs this poll enumerates. Null when the pattern isn't
                // present (e.g. a composite child interface with no VID/PID of its own) - the event
                // still carries the InstanceId either way.
                var parsed = UsbActions.ParseDevicePath(instanceId);
                EventEmitter.Emit(new UsbStorageBlockedEvent(parsed?.Vid, parsed?.Pid, parsed?.Serial, instanceId, EventEmitter.Ts()));
            }
        }
        catch (Exception ex)
        {
            EventEmitter.EmitError("usb_storage_driverless_poll", ex.Message);
        }
        finally
        {
            // MUST run unconditionally, even if the cycle body above threw - a single bad cycle
            // (a transient enumeration failure) must never permanently end this feature until the
            // next process restart.
            Reschedule();
        }
    }

    void Reschedule()
    {
        lock (_lock)
        {
            // Null when Stop() already ran - nothing to reschedule. Never disposed-out-from-under
            // us: Stop() nulls this field BEFORE calling Timer.Dispose, under this same lock, so a
            // non-null read here is always still a live, undisposed timer safe to Change().
            _timer?.Change(_interval, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// Enumerates every devnode the USB bus enumerator created (<c>SetupDiGetClassDevsByEnumerator</c>
    /// with Enumerator="USB", DIGCF_PRESENT|DIGCF_ALLCLASSES), regardless of Setup Class assignment
    /// or the total absence of one, and returns the instance IDs of those whose Compatible IDs
    /// indicate mass-storage class via the existing, already-unit-tested
    /// <see cref="UsbActions.IsMassStorageDevice"/>. Deliberately NOT the interface-based
    /// <see cref="UsbActions.EnumerateConnected"/> the rest of this codebase uses elsewhere - a
    /// driverless devnode never registers a device INTERFACE at all, so that call would never see it.
    ///
    /// Returns null (not an empty set) if the enumeration call itself fails, distinguishable from
    /// "genuinely zero mass-storage devnodes present" so the caller can skip reconciliation for the
    /// cycle instead of misreading a Win32 hiccup as every previously-seen device having just
    /// unplugged.
    /// </summary>
    static HashSet<string>? EnumerateMassStorageInstanceIds()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        IntPtr devInfo = NativeMethods.SetupDiGetClassDevsByEnumerator(
            IntPtr.Zero, "USB", IntPtr.Zero,
            NativeMethods.DIGCF_PRESENT | NativeMethods.DIGCF_ALLCLASSES);

        if (devInfo == NativeMethods.INVALID_HANDLE_VALUE) return null;

        try
        {
            for (uint idx = 0; ; idx++)
            {
                var devInfoData = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };

                if (!NativeMethods.SetupDiEnumDeviceInfo(devInfo, idx, ref devInfoData))
                    break; // ERROR_NO_MORE_ITEMS

                string? instanceId = UsbActions.GetDeviceInstanceId(devInfo, ref devInfoData);
                if (instanceId is null) continue;

                if (UsbActions.IsMassStorageDevice(instanceId))
                    result.Add(instanceId);
            }
        }
        finally
        {
            NativeMethods.SetupDiDestroyDeviceInfoList(devInfo);
        }

        return result;
    }

    /// <summary>
    /// Pure new/seen/evict decision for one poll cycle, factored out of <see cref="RunCycle"/> so it
    /// is unit-testable against a plain HashSet the test constructs itself (see
    /// DlpEndpointMonitor.Tests/UsbStorageDriverlessPollTests.cs). Mutates <paramref name="seen"/>
    /// in place (adds newly-appeared IDs, removes evicted ones) and returns which IDs fell into
    /// each bucket.
    ///
    /// <paramref name="isFirstCycle"/>: the very first cycle after a Start() silently baselines -
    /// every present ID is recorded as seen with nothing reported as new, so a timer (re)start
    /// never produces a "several new blocks just happened" burst for devices that were already
    /// there before the switch flipped. MUST be an explicit caller-supplied flag, never inferred
    /// from <paramref name="seen"/> being empty - the seen-set legitimately empties out on its own
    /// as devices disconnect, and re-arming baseline mode from that emptiness would silently
    /// swallow the next genuine new arrival instead of reporting it.
    /// </summary>
    internal static (List<string> newlyAppeared, List<string> evicted) ReconcileCycle(
        HashSet<string> seen, IReadOnlySet<string> currentlyPresent, bool isFirstCycle)
    {
        var newlyAppeared = new List<string>();
        var evicted = new List<string>();

        if (isFirstCycle)
        {
            foreach (var id in currentlyPresent)
                seen.Add(id);
            return (newlyAppeared, evicted);
        }

        foreach (var id in currentlyPresent)
        {
            if (seen.Add(id))
                newlyAppeared.Add(id);
        }

        // Evict anything seen previously that is no longer present, so a later replug (same
        // device, or a different one in the same port) is reported again instead of staying
        // silently suppressed forever.
        foreach (var id in seen.Where(id => !currentlyPresent.Contains(id)).ToList())
        {
            seen.Remove(id);
            evicted.Add(id);
        }

        return (newlyAppeared, evicted);
    }
}
