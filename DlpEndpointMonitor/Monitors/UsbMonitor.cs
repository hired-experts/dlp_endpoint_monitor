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
    readonly UsbStorageDriverlessPoll _storagePoll;

    readonly Dictionary<string, string> _groupAnchors = new(StringComparer.OrdinalIgnoreCase);
    readonly Lock _groupAnchorLock = new();

    public UsbMonitor(MessageWindow window, DeviceWhitelist whitelist, DeviceBlacklist blacklist, DisabledDevices disabled, UsbStorageDriverlessPoll storagePoll)
    {
        _window      = window;
        _whitelist   = whitelist;
        _blacklist   = blacklist;
        _disabled    = disabled;
        _storagePoll = storagePoll;
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

                // Network devices (NICs) are handled exclusively by NetworkMonitor - a NIC is not
                // composite and disabling one is catastrophic, so it does not fit this monitor's
                // per-interface / composite-group pipeline.
                if (kind == DeviceKind.Network) return;

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
                        null, null, null, devicePath, usbClass, kind, classGuid, null, null, EventEmitter.Ts()));
                else
                {
                    string? groupId = parsed?.GroupId;
                    var disconnectedEvent = new UsbDeviceDisconnectedEvent(
                        parsed?.Vid is "" ? null : parsed?.Vid,
                        parsed?.Pid is "" ? null : parsed?.Pid,
                        parsed?.Serial,
                        devicePath,
                        usbClass,
                        kind,
                        classGuid,
                        groupId,
                        parsed?.InstanceId,
                        null,
                        EventEmitter.Ts());

                    string? anchor = ResolveGroupAnchor(groupId, disconnectedEvent.EventId);
                    if (anchor is not null)
                        disconnectedEvent = disconnectedEvent with { SourceEventId = anchor };

                    EventEmitter.Emit(disconnectedEvent);
                    ReleaseGroupAnchorIfLastSibling(groupId);
                }
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
            // Network devices (NICs) are NetworkMonitor's exclusive territory - see OnDeviceChanged.
            foreach (var parsed in UsbActions.EnumerateConnected().Where(p => p.Kind != DeviceKind.Network))
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
            // Network devices (NICs) are NetworkMonitor's exclusive territory - see OnDeviceChanged.
            var all = UsbActions.EnumerateConnected().Where(p => p.Kind != DeviceKind.Network).ToList();

            // Bucket by composite-group so every sibling interface is judged together (same check
            // BlockDevice itself does), done once up front so this method's own counting/logging
            // reflects it too. Devices with a Bluetooth ancestor are excluded from grouping:
            // GetGroupId can resolve a Bluetooth peripheral's "USB group" to the shared BT radio's
            // own instance ID (the radio often sits above BTHENUM/BTHLEDEVICE in the tree), so
            // grouping by raw GroupId there would wrongly lump unrelated devices together.
            var byGroup = all
                .Where(d => d.GroupId is not null && !UsbActions.HasBluetoothAncestor(d.InstanceId) && d.Kind != DeviceKind.Network)
                .GroupBy(d => d.GroupId!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => (IReadOnlyList<ParsedDevice>)g.ToList(), StringComparer.OrdinalIgnoreCase);

            int checked_ = 0, blocked = 0;
            foreach (var parsed in all)
            {
                checked_++;
                IReadOnlyList<ParsedDevice>? siblings =
                    parsed.GroupId is not null && byGroup.TryGetValue(parsed.GroupId, out var s) ? s : null;

                // Compliance is always this interface's own identity now - never "any sibling
                // compliant" (see Finding A fix). siblings is still passed through to BlockDevice,
                // which does its own individual-vs-leaf-only-vs-escalate decision.
                bool blacklisted = _blacklist.IsBlocked(parsed.Vid, parsed.Pid, parsed.Serial, parsed.Kind);
                bool allowed = _whitelist.IsAllowed(parsed.Vid, parsed.Pid, parsed.Serial, parsed.Kind) && !blacklisted;

                if (!allowed)
                {
                    blocked++;
                    string reason = blacklisted ? "blacklist_match" : "whitelist_gate";
                    EventEmitter.EmitInfo($"usb_policy_apply: {parsed.Vid}/{parsed.Pid} kind={parsed.Kind} allowed={allowed}");
                    Task.Run(() => BlockDevice(parsed, reason, siblings));
                }
            }
            EventEmitter.EmitInfo($"usb_policy_apply: checked {checked_} device(s), blocking {blocked}");
        }
        catch (Exception ex)
        {
            EventEmitter.EmitError("usb_policy_apply", ex.Message);
        }
    }

    // Tag stamped onto a DisabledDeviceRecord created by BlockAlreadyConnectedStorage, so
    // RestoreStorageDisabled can find and restore exactly the devices IT disabled without
    // touching an independent blacklist-disabled device sharing the same instance ID space.
    internal const string StorageKillSwitchBlockedBy = "usb_storage_disabled";

    /// <summary>
    /// Retroactively enforces the usb_disable_storage kill switch on mass-storage devices already
    /// connected before the switch flipped - the USBSTOR registry write only blocks a FUTURE driver
    /// load, not an already-bound one. Skips Volume-interface siblings (not independently
    /// disableable) and re-checks IsProtectedInternal per candidate (defense-in-depth alongside
    /// BlockDevice's own gate). Deliberately skips BlockDevice's whitelist/blacklist group check -
    /// this is a blanket block, not a per-device policy decision. Emits its own
    /// UsbStorageDeviceBlocked(Failed)Event pair (see EventEmitter.cs) rather than reusing
    /// UsbDeviceBlockedEvent. See AGENTS.md section 10 for the incident this closes.
    /// </summary>
    public void BlockAlreadyConnectedStorage()
    {
        try
        {
            var targets = UsbActions.EnumerateConnected()
                .Where(p => p.Kind == DeviceKind.Storage && !UsbActions.IsVolumeInterface(p.ClassGuid))
                .ToList();

            int blocked = 0, failed = 0;
            foreach (var parsed in targets)
            {
                if (UsbActions.IsProtectedInternal(parsed.Kind, parsed.InstanceId))
                {
                    failed++;
                    var protectedFailedEvent = new UsbStorageDeviceBlockFailedEvent(
                        parsed.Vid, parsed.Pid, parsed.Serial, parsed.InstanceId, parsed.GroupId,
                        "protected internal device (built-in storage) - refused to block",
                        null, EventEmitter.Ts());
                    string? protectedAnchor = ResolveGroupAnchor(parsed.GroupId, protectedFailedEvent.EventId);
                    if (protectedAnchor is not null) protectedFailedEvent = protectedFailedEvent with { SourceEventId = protectedAnchor };
                    EventEmitter.Emit(protectedFailedEvent);
                    continue;
                }

                bool hasBtAncestor = UsbActions.HasBluetoothAncestor(parsed.InstanceId);
                var (ok, error, disabledId) = DisableDeviceWithEscalation(parsed, hasBtAncestor);

                if (disabledId is not null)
                    _disabled.Add(new DisabledDeviceRecord(disabledId, parsed.Vid, parsed.Pid, parsed.Serial, parsed.Kind, GroupId: parsed.GroupId, BlockedBy: StorageKillSwitchBlockedBy));

                if (ok)
                {
                    blocked++;
                    var blockedEvent = new UsbStorageDeviceBlockedEvent(parsed.Vid, parsed.Pid, parsed.Serial, parsed.InstanceId, parsed.GroupId, null, EventEmitter.Ts());
                    string? anchor = ResolveGroupAnchor(parsed.GroupId, blockedEvent.EventId);
                    if (anchor is not null) blockedEvent = blockedEvent with { SourceEventId = anchor };
                    EventEmitter.Emit(blockedEvent);
                }
                else
                {
                    failed++;
                    var failedEvent = new UsbStorageDeviceBlockFailedEvent(parsed.Vid, parsed.Pid, parsed.Serial, parsed.InstanceId, parsed.GroupId, error, null, EventEmitter.Ts());
                    string? anchor = ResolveGroupAnchor(parsed.GroupId, failedEvent.EventId);
                    if (anchor is not null) failedEvent = failedEvent with { SourceEventId = anchor };
                    EventEmitter.Emit(failedEvent);
                }
            }
            EventEmitter.EmitInfo($"usb_storage_kill_switch_apply: checked {targets.Count} device(s), blocking {blocked}, failed {failed}");
        }
        catch (Exception ex)
        {
            EventEmitter.EmitError("usb_storage_kill_switch_apply", ex.Message);
        }
    }

    /// <summary>
    /// Whether a single device's own identity satisfies policy on its own - the sole compliance
    /// check now used everywhere a device (or one interface of a composite device) is judged. A
    /// sibling's own compliance is never a reason to allow a different, non-compliant interface -
    /// see <see cref="DecideGroupBlock"/> and AGENTS.md section 10 (Finding A fix).
    /// </summary>
    bool IsIndividuallyCompliant(string vid, string pid, string? serial, DeviceKind kind) =>
        _whitelist.IsAllowed(vid, pid, serial, kind) && !_blacklist.IsBlocked(vid, pid, serial, kind);

    /// <summary>
    /// Pure decision for how far a composite-group block should go, factored out the same way
    /// <see cref="ResolveBluetoothBlock"/> is - no Win32, just the three-way outcome so it is
    /// unit-testable. <paramref name="parsedCompliant"/> is whether the interface actually being
    /// judged is itself individually compliant; <paramref name="anyOtherSiblingCompliant"/> is
    /// whether any OTHER interface sharing its composite parent is. A compliant sibling only ever
    /// downgrades an escalating block to a leaf-only one (to avoid collaterally disabling that
    /// sibling) - it never allows the non-compliant interface outright.
    /// </summary>
    internal enum GroupBlockDecision { Allow, LeafOnly, FullEscalation }

    internal static GroupBlockDecision DecideGroupBlock(bool parsedCompliant, bool anyOtherSiblingCompliant) =>
        parsedCompliant ? GroupBlockDecision.Allow
        : anyOtherSiblingCompliant ? GroupBlockDecision.LeafOnly
        : GroupBlockDecision.FullEscalation;

    /// <summary>
    /// Get-or-establish the group-anchor EventId for <paramref name="groupId"/>: returns the
    /// SourceEventId this caller's event should carry. Null groupId (non-composite, or
    /// Bluetooth-backed - no anchor concept) always returns null. First sighting of a groupId
    /// establishes <paramref name="candidateEventId"/> as its anchor and returns null (this event
    /// IS the anchor - nothing came before it); every later sighting returns the EXISTING anchor,
    /// ignoring candidateEventId entirely. Locked wrapper over <see cref="ResolveGroupAnchorCore"/>,
    /// which holds the actual pure decision so it is unit-testable without a real UsbMonitor - see
    /// DlpEndpointMonitor.Tests/UsbMonitorTests.cs.
    /// </summary>
    string? ResolveGroupAnchor(string? groupId, string candidateEventId)
    {
        lock (_groupAnchorLock)
        {
            return ResolveGroupAnchorCore(_groupAnchors, groupId, candidateEventId);
        }
    }

    /// <summary>
    /// Pure get-or-create decision for a group anchor, factored out of <see cref="ResolveGroupAnchor"/>
    /// the same way <see cref="ResolveBluetoothBlock"/> is factored out of BlockDevice - no locking,
    /// no Win32, just the dictionary logic, so tests can drive it against a plain Dictionary they
    /// construct themselves. Null groupId (non-composite, or Bluetooth-backed - no anchor concept)
    /// always returns null without touching the dictionary.
    /// </summary>
    internal static string? ResolveGroupAnchorCore(Dictionary<string, string> anchors, string? groupId, string candidateEventId)
    {
        if (groupId is null) return null;
        if (anchors.TryGetValue(groupId, out var existing)) return existing;
        anchors[groupId] = candidateEventId;
        return null;
    }

    /// <summary>
    /// Forgets a group's anchor once every sibling interface has disconnected, so a later,
    /// unrelated device that happens to reuse a Windows-generated GroupId does not inherit a
    /// stale anchor. MANDATORY cleanup, not optional - without it this dictionary grows
    /// unboundedly over the process's multi-week/month uptime as different physical devices
    /// connect and disconnect over time. Call after emitting a disconnect event.
    /// </summary>
    void ReleaseGroupAnchorIfLastSibling(string? groupId)
    {
        if (groupId is null) return;

        bool anySiblingStillConnected = UsbActions.EnumerateGroupSiblings(groupId).Any();
        if (anySiblingStillConnected) return;

        lock (_groupAnchorLock)
        {
            _groupAnchors.Remove(groupId);
        }
    }

    void HandleArrival(ParsedDevice parsed)
    {
        bool blacklisted = _blacklist.IsBlocked(parsed.Vid, parsed.Pid, parsed.Serial, parsed.Kind);
        bool allowed = _whitelist.IsAllowed(parsed.Vid, parsed.Pid, parsed.Serial, parsed.Kind) && !blacklisted;
        string reason = blacklisted ? "blacklist_match" : "whitelist_gate";

        var connectedEvent = new UsbDeviceConnectedEvent(
            string.IsNullOrEmpty(parsed.Vid) ? null : parsed.Vid,
            string.IsNullOrEmpty(parsed.Pid) ? null : parsed.Pid,
            parsed.Serial,
            parsed.UsbClass,
            parsed.Kind,
            parsed.ClassGuid,
            parsed.GroupId,
            parsed.InstanceId,
            parsed.RawPath,
            allowed,
            null,
            EventEmitter.Ts());

        // Resolve/establish the anchor using this event's own real EventId as the candidate, then
        // fold the result back in via `with` - see the disconnect branch in OnDeviceChanged for why
        // this two-step shape (not passed in at construction) is required to keep the anchor value
        // equal to an EventId a consumer can actually see in the stream.
        string? anchor = ResolveGroupAnchor(parsed.GroupId, connectedEvent.EventId);
        if (anchor is not null)
            connectedEvent = connectedEvent with { SourceEventId = anchor };

        EventEmitter.Emit(connectedEvent);

        // Purely observational, independent of the whitelist/blacklist decision above - never
        // gates or changes `allowed`/`reason`. A driverless mass-storage device resolves as
        // DeviceKind.Unknown here, not DeviceKind.Storage, so IsMassStorageDevice reads Compatible
        // IDs directly off the devnode instead (see PROJECT.md section 5.7). TryClaimNewArrival
        // dedupes against UsbStorageDriverlessPoll's own polling cycle independently noticing the
        // same device (e.g. a composite device whose parent still gets a real interface arrival via
        // usbccgp.sys even with its storage function driverless) - see AGENTS.md section 10.
        if (!UsbActions.IsUsbStorageEnabled() && UsbActions.IsMassStorageDevice(parsed.InstanceId)
            && _storagePoll.TryClaimNewArrival(parsed.InstanceId))
        {
            EventEmitter.Emit(new UsbStorageBlockedEvent(
                string.IsNullOrEmpty(parsed.Vid) ? null : parsed.Vid,
                string.IsNullOrEmpty(parsed.Pid) ? null : parsed.Pid,
                parsed.Serial,
                parsed.InstanceId,
                EventEmitter.Ts()));
        }

        if (!allowed)
        {
            Task.Run(() => BlockDevice(parsed, reason));
        }
    }

    void BlockDevice(ParsedDevice parsed, string reason, IReadOnlyList<ParsedDevice>? knownGroupSiblings = null)
    {
        // SAFETY (criterion 5): never block a built-in keyboard / touchpad / pointing device -
        // disabling or ejecting it would brick local input on a laptop, and an eject is
        // unrecoverable by software. External input and camera/video remain blockable. This is
        // the single choke point for every block path (arrival, apply, startup enum).
        if (UsbActions.IsProtectedInternal(parsed.Kind, parsed.InstanceId))
        {
            string protectedReason = parsed.Kind == DeviceKind.Storage
                ? "protected internal device (built-in storage) - refused to block"
                : "protected internal input device (built-in keyboard/touchpad) - refused to block";

            var protectedFailedEvent = new UsbDeviceBlockFailedEvent(
                parsed.Vid, parsed.Pid, parsed.Serial, parsed.UsbClass, parsed.Kind,
                parsed.ClassGuid, parsed.GroupId, parsed.InstanceId, reason,
                protectedReason,
                null, EventEmitter.Ts());

            string? protectedAnchor = ResolveGroupAnchor(parsed.GroupId, protectedFailedEvent.EventId);
            if (protectedAnchor is not null)
                protectedFailedEvent = protectedFailedEvent with { SourceEventId = protectedAnchor };

            EventEmitter.Emit(protectedFailedEvent);
            return;
        }

        // A STORAGE\Volume\{guid}\... path is a volume identity, not a PnP device-instance ID -
        // CM_Locate_DevNodeW can never resolve it, so DisableDevice on it is a guaranteed,
        // deterministic failure. The sibling disk-function interface (GUID_DEVINTERFACE_DISK/
        // CDROM/TAPE) is the real devnode and its own independent BlockDevice call is what
        // actually blocks the device - this only skips a call that was never going to succeed.
        if (UsbActions.IsVolumeInterface(parsed.ClassGuid))
        {
            EventEmitter.EmitInfo($"usb_block_skipped: {parsed.InstanceId} is a volume interface, not an independently-disableable devnode - the sibling disk-function interface handles the real block");
            return;
        }

        // Computed once, reused both for the group-compliance check below and for the
        // usbEscalate guard further down (was two separate device-tree walks before).
        bool hasBtAncestor = UsbActions.HasBluetoothAncestor(parsed.InstanceId);

        // GROUP COMPLIANCE: a composite device's siblings can resolve to different DeviceKinds.
        // This interface's OWN identity must itself be compliant to be allowed - a compliant
        // sibling is never a reason to allow a different, non-compliant interface (that was the
        // Finding A bypass: a whitelisted keyboard interface allowed an unrelated storage sibling
        // to keep working). A compliant sibling IS still a reason to decline ESCALATING a block to
        // the shared composite parent (which would collaterally disable that compliant sibling) -
        // in that case only a leaf-scoped disable of this interface is attempted. Skipped for
        // Bluetooth-backed devices - their "group" would be the shared radio, not a meaningful
        // sibling set.
        if (parsed.GroupId is not null && !hasBtAncestor)
        {
            var siblings = knownGroupSiblings ?? UsbActions.EnumerateGroupSiblings(parsed.GroupId).ToList();
            if (!siblings.Any(s => s.InstanceId.Equals(parsed.InstanceId, StringComparison.OrdinalIgnoreCase)))
                siblings = [.. siblings, parsed]; // the arriving/current interface itself must vote too

            bool parsedCompliant = IsIndividuallyCompliant(parsed.Vid, parsed.Pid, parsed.Serial, parsed.Kind);
            bool anyOtherCompliant = siblings.Any(s =>
                !s.InstanceId.Equals(parsed.InstanceId, StringComparison.OrdinalIgnoreCase) &&
                IsIndividuallyCompliant(s.Vid, s.Pid, s.Serial, s.Kind));

            switch (DecideGroupBlock(parsedCompliant, anyOtherCompliant))
            {
                case GroupBlockDecision.Allow:
                    EventEmitter.EmitInfo($"usb_interface_allowed: {parsed.InstanceId} kind={parsed.Kind} - individually satisfies policy");
                    return;

                case GroupBlockDecision.LeafOnly:
                    // A sibling on the SAME composite parent is itself compliant - escalating
                    // (composite parent disable, or eject) would collaterally take it down too.
                    // Attempt a LEAF-ONLY disable; never escalate here.
                    var (leafOk, leafErr) = UsbActions.DisableDevice(parsed.InstanceId);
                    string leafReason = _blacklist.IsBlocked(parsed.Vid, parsed.Pid, parsed.Serial, parsed.Kind)
                        ? "blacklist_match" : "whitelist_gate";

                    if (leafOk)
                    {
                        _disabled.Add(new DisabledDeviceRecord(parsed.InstanceId, parsed.Vid, parsed.Pid, parsed.Serial, parsed.Kind, GroupId: parsed.GroupId));
                        var blockedEvent = new UsbDeviceBlockedEvent(parsed.Vid, parsed.Pid, parsed.Serial, parsed.UsbClass, parsed.Kind, parsed.ClassGuid, parsed.GroupId, parsed.InstanceId, leafReason, null, EventEmitter.Ts());
                        string? anchor = ResolveGroupAnchor(parsed.GroupId, blockedEvent.EventId);
                        if (anchor is not null) blockedEvent = blockedEvent with { SourceEventId = anchor };
                        EventEmitter.Emit(blockedEvent);
                    }
                    else
                    {
                        // Honest residual gap: this interface cannot be disabled without also risking the
                        // compliant sibling, and escalation is refused. Surface loudly - never silently
                        // "usb_group_allowed" this case again.
                        var failedEvent = new UsbDeviceBlockFailedEvent(parsed.Vid, parsed.Pid, parsed.Serial, parsed.UsbClass, parsed.Kind, parsed.ClassGuid, parsed.GroupId, parsed.InstanceId, leafReason,
                            $"leaf disable failed ({leafErr}); escalation suppressed to protect a compliant sibling interface on the same composite device", null, EventEmitter.Ts());
                        string? anchor = ResolveGroupAnchor(parsed.GroupId, failedEvent.EventId);
                        if (anchor is not null) failedEvent = failedEvent with { SourceEventId = anchor };
                        EventEmitter.Emit(failedEvent);
                    }
                    return;

                case GroupBlockDecision.FullEscalation:
                    reason = _blacklist.IsBlocked(parsed.Vid, parsed.Pid, parsed.Serial, parsed.Kind) ||
                             siblings.Any(s => _blacklist.IsBlocked(s.Vid, s.Pid, s.Serial, s.Kind))
                        ? "blacklist_match" : "whitelist_gate";
                    break; // fall through to the existing DisableDeviceWithEscalation call below, unchanged
            }
        }

        var (ok, error, disabledId) = DisableDeviceWithEscalation(parsed, hasBtAncestor);

        if (disabledId is not null)
            _disabled.Add(new DisabledDeviceRecord(disabledId, parsed.Vid, parsed.Pid, parsed.Serial, parsed.Kind, GroupId: parsed.GroupId));

        // Two separate branches (not a shared IEvent ternary) so each concrete record's own
        // `with` can fold in the resolved anchor - `with` isn't available through the IEvent
        // interface, and each type needs its own real EventId as ResolveGroupAnchor's candidate.
        if (ok)
        {
            var blockedEvent = new UsbDeviceBlockedEvent(parsed.Vid, parsed.Pid, parsed.Serial, parsed.UsbClass, parsed.Kind, parsed.ClassGuid, parsed.GroupId, parsed.InstanceId, reason, null, EventEmitter.Ts());
            string? anchor = ResolveGroupAnchor(parsed.GroupId, blockedEvent.EventId);
            if (anchor is not null) blockedEvent = blockedEvent with { SourceEventId = anchor };
            EventEmitter.Emit(blockedEvent);
        }
        else
        {
            var failedEvent = new UsbDeviceBlockFailedEvent(parsed.Vid, parsed.Pid, parsed.Serial, parsed.UsbClass, parsed.Kind, parsed.ClassGuid, parsed.GroupId, parsed.InstanceId, reason, error, null, EventEmitter.Ts());
            string? anchor = ResolveGroupAnchor(parsed.GroupId, failedEvent.EventId);
            if (anchor is not null) failedEvent = failedEvent with { SourceEventId = anchor };
            EventEmitter.Emit(failedEvent);
        }
    }

    /// <summary>
    /// The actual disable/escalate mechanics shared by <see cref="BlockDevice"/> and
    /// <see cref="BlockAlreadyConnectedStorage"/> - callers run their own gates (IsProtectedInternal,
    /// IsVolumeInterface, group compliance) and persist/emit around this call; it does neither
    /// itself.
    ///
    /// Bluetooth-backed HID disables the device's own primary node (GetBluetoothDeviceNode - never
    /// the HID leaf, since vendor software like Logitech Options re-enables a disabled leaf on its
    /// own) instead of unpairing it - unpairing via GetBluetoothPairingCandidates/RemovePairingAny
    /// was removed from this call site after a real incident where candidate resolution reached an
    /// unrelated device's live pairing instead of a stale sibling of the same device (see AGENTS.md
    /// section 10); those helpers remain intact and unit-tested for a future Vid/Pid-scoped fix.
    /// Trade-off: a disable-only block still shows as "Connected" in Windows Settings and may keep
    /// reconnecting/re-disabling, but GetBluetoothDeviceNode only ever walks THIS device's own
    /// ancestor chain, so it cannot repeat that incident by construction.
    /// </summary>
    static (bool ok, string? error, string? disabledId) DisableDeviceWithEscalation(ParsedDevice parsed, bool hasBtAncestor)
    {
        string? disabledId = null;
        bool    ok;
        string? error;

        string? btNode = UsbActions.GetBluetoothDeviceNode(parsed.InstanceId);
        if (btNode is not null)
        {
            (ok, error) = UsbActions.DisableDevice(btNode);
            if (ok) disabledId = btNode;
            // No USB-group / eject fallback for Bluetooth - both would target the radio.
        }
        else
        {
            (ok, error) = UsbActions.DisableDevice(parsed.InstanceId);
            if (ok) disabledId = parsed.InstanceId;

            // Escalate to the USB composite parent / eject ONLY for genuine USB devices - never
            // when there is any Bluetooth ancestor (its "group" is the shared radio).
            bool usbEscalate = parsed.GroupId is not null && !hasBtAncestor;

            // If HID-level disable was rejected, try at the USB composite device level - reversible.
            if (!ok && usbEscalate)
            {
                var (groupOk, groupErr) = UsbActions.DisableDevice(parsed.GroupId!);
                if (groupOk) { ok = true; error = null; disabledId = parsed.GroupId; }
                else error ??= groupErr;
            }

            // Last resort: physically eject via CM_Request_Device_EjectW. Windows rejects
            // CM_Disable_DevNode for input devices (keyboards, mice) to prevent lockout.
            // Eject disconnects the device from the USB bus - manual replug required to restore.
            // (An eject is not a CM_Disable, so there is nothing to persist/re-enable.)
            if (!ok && usbEscalate)
            {
                var (ejectOk, ejectErr) = UsbActions.RequestEject(parsed.GroupId!);
                if (ejectOk) { ok = true; error = null; }
                else error ??= ejectErr;
            }
        }

        return (ok, error, disabledId);
    }

    /// <summary>
    /// Unpair-then-disable-fallback decision for a Bluetooth-backed block, factored out as pure
    /// logic (no Win32 calls of its own) so it is unit-testable independent of the real
    /// RemovePairing/DisableDevice P/Invoke calls - see
    /// DlpEndpointMonitor.Tests/UsbMonitorTests.cs. <paramref name="disableNodeId"/> is returned
    /// as the disabledId only when the disable fallback is the one that actually succeeded -
    /// a successful unpair has nothing to restore, same as before this fallback existed.
    /// </summary>
    internal static (bool ok, string? error, string? disabledId) ResolveBluetoothBlock(
        Func<(bool ok, string? error)> tryUnpair,
        Func<(bool ok, string? error)> tryDisable,
        string disableNodeId)
    {
        var (unpairOk, unpairError) = tryUnpair();
        if (unpairOk) return (true, null, null);

        var (disableOk, disableError) = tryDisable();
        if (disableOk) return (true, null, disableNodeId);

        return (false, $"unpair failed ({unpairError}); disable fallback also failed ({disableError})", null);
    }

    /// <summary>
    /// Decides whether a persisted <see cref="DisabledDeviceRecord"/> is now compliant with current
    /// policy - i.e. whether <see cref="RestoreCompliant"/> should re-enable it. The record's OWN
    /// identity must itself satisfy policy - a sibling's compliance never authorizes restoring a
    /// DIFFERENT function sharing its composite parent (mirrors the <see cref="BlockDevice"/> fix:
    /// a whitelisted keyboard interface must not also restore a still-forbidden storage interface
    /// on the same physical device). The one remaining legitimate reason to restore a
    /// non-compliant record is if its InstanceId denotes the composite PARENT node itself (not a
    /// leaf) and it has been completely torn down (no sibling interfaces left at all) -
    /// re-enabling a fully torn-down parent is reversible, and Windows re-evaluates every child
    /// interface fresh via <see cref="BlockDevice"/> the moment it re-arrives. Bluetooth-backed
    /// records skip all of this - no composite-group concept applies to them.
    /// </summary>
    bool IsRecordCompliant(DisabledDeviceRecord d)
    {
        if (UsbActions.HasBluetoothAncestor(d.InstanceId))
            return IsIndividuallyCompliant(d.Vid, d.Pid, d.Serial, d.Kind);

        if (IsIndividuallyCompliant(d.Vid, d.Pid, d.Serial, d.Kind))
            return true;

        string? groupId = UsbActions.GetGroupId(d.InstanceId);
        var siblings = groupId is not null ? UsbActions.EnumerateGroupSiblings(groupId).ToList() : [];

        // The record's own identity is non-compliant. The ONLY remaining legitimate reason to
        // restore it is if `d.InstanceId` denotes the composite PARENT node itself (not a leaf) and
        // it has been completely torn down (no sibling interfaces left at all) - re-enabling a fully
        // torn-down parent is reversible, and Windows re-evaluates every child interface fresh via
        // BlockDevice the moment it re-arrives. This is unchanged from prior behavior for that
        // specific case - not a group-compliance shortcut, just "nothing is left here to keep blocked."
        if (siblings.Count == 0 && groupId is not null && groupId.Equals(d.InstanceId, StringComparison.OrdinalIgnoreCase))
            return true;

        return false; // a non-compliant leaf sharing a composite parent with live siblings stays blocked
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
            // Mac is not null -> Bluetooth-blocked record (owned by BluetoothMonitor.RestoreCompliant);
            // Kind==Network -> owned by NetworkMonitor.RestoreCompliant. Excluding them here prevents
            // two monitors racing to re-enable the same devnode and double-emitting "unblocked" (see
            // AGENTS.md/PROJECT.md bug history). BlockedBy==StorageKillSwitchBlockedBy is owned by
            // RestoreStorageDisabled - IsRecordCompliant only ever consults whitelist/blacklist state,
            // never IsUsbStorageEnabled, so an unrelated policy change must never re-enable a
            // kill-switch-disabled device; only usb_enable_storage may undo that.
            foreach (var d in _disabled.GetAll().Where(d =>
                d.Mac is null && d.Kind != DeviceKind.Network && d.BlockedBy != StorageKillSwitchBlockedBy))
            {
                bool allowed = IsRecordCompliant(d);
                if (!allowed) { stillBlocked++; continue; }

                var (ok, error) = UsbActions.EnableDevice(d.InstanceId);
                if (ok)
                {
                    _disabled.Remove(d.InstanceId);
                    restored++;

                    // No anchor may exist here (in-memory only - a process restart since the
                    // anchor was established, or an eject-while-disabled, both lose it); resolving
                    // establishes a fresh one in that case, an accepted degradation (this event's
                    // own SourceEventId is then null, same as any first-sighting establisher).
                    var unblockedEvent = new UsbDeviceUnblockedEvent(d.Vid, d.Pid, d.Serial, d.Kind, d.GroupId, d.InstanceId, null, EventEmitter.Ts());
                    string? anchor = ResolveGroupAnchor(d.GroupId, unblockedEvent.EventId);
                    if (anchor is not null) unblockedEvent = unblockedEvent with { SourceEventId = anchor };
                    EventEmitter.Emit(unblockedEvent);
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

                    var unblockFailedEvent = new UsbDeviceUnblockFailedEvent(d.Vid, d.Pid, d.Serial, d.Kind, d.GroupId, d.InstanceId, error, null, EventEmitter.Ts());
                    string? anchor = ResolveGroupAnchor(d.GroupId, unblockFailedEvent.EventId);
                    if (anchor is not null) unblockFailedEvent = unblockFailedEvent with { SourceEventId = anchor };
                    EventEmitter.Emit(unblockFailedEvent);
                }
            }
            EventEmitter.EmitInfo($"usb_policy_restore: restored {restored}, still-blocked {stillBlocked}");
        }
        catch (Exception ex)
        {
            EventEmitter.EmitError("usb_policy_restore", ex.Message);
        }
    }

    /// <summary>
    /// Pure filter over a persisted disabled-devices set: which records were disabled by the
    /// usb_disable_storage kill switch's retroactive sweep (<see cref="BlockAlreadyConnectedStorage"/>),
    /// as opposed to a normal whitelist/blacklist-triggered disable (BlockedBy null) or a
    /// Bluetooth/Network monitor's own record. Factored out as a static, no-Win32 decision so it
    /// is unit-testable against a plain list of records - see
    /// DlpEndpointMonitor.Tests/UsbMonitorTests.cs.
    /// </summary>
    internal static IEnumerable<DisabledDeviceRecord> FilterStorageKillSwitchRecords(IEnumerable<DisabledDeviceRecord> records) =>
        records.Where(d => d.BlockedBy == StorageKillSwitchBlockedBy);

    /// <summary>
    /// Symmetric restore for <see cref="BlockAlreadyConnectedStorage"/>: re-enables every persisted
    /// record that sweep disabled that current whitelist/blacklist policy does not ALSO want
    /// blocked, reusing <see cref="IsRecordCompliant"/> - so a device blocked by both the kill
    /// switch and an independent blacklist entry stays blocked after usb_enable_storage alone (the
    /// two enforcement paths never fight). Reuses UsbDeviceUnblockedEvent/UsbDeviceUnblockFailedEvent
    /// rather than a new event type - un-blocking is the same outcome RestoreCompliant already
    /// reports for every other trigger. Call on a ThreadPool thread after usb_enable_storage's
    /// registry write succeeds.
    /// </summary>
    public void RestoreStorageDisabled()
    {
        try
        {
            int restored = 0, stillBlocked = 0;
            foreach (var d in FilterStorageKillSwitchRecords(_disabled.GetAll()))
            {
                bool allowed = IsRecordCompliant(d);
                if (!allowed) { stillBlocked++; continue; }

                var (ok, error) = UsbActions.EnableDevice(d.InstanceId);
                if (ok)
                {
                    _disabled.Remove(d.InstanceId);
                    restored++;

                    var unblockedEvent = new UsbDeviceUnblockedEvent(d.Vid, d.Pid, d.Serial, d.Kind, d.GroupId, d.InstanceId, null, EventEmitter.Ts());
                    string? anchor = ResolveGroupAnchor(d.GroupId, unblockedEvent.EventId);
                    if (anchor is not null) unblockedEvent = unblockedEvent with { SourceEventId = anchor };
                    EventEmitter.Emit(unblockedEvent);
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

                    var unblockFailedEvent = new UsbDeviceUnblockFailedEvent(d.Vid, d.Pid, d.Serial, d.Kind, d.GroupId, d.InstanceId, error, null, EventEmitter.Ts());
                    string? anchor = ResolveGroupAnchor(d.GroupId, unblockFailedEvent.EventId);
                    if (anchor is not null) unblockFailedEvent = unblockFailedEvent with { SourceEventId = anchor };
                    EventEmitter.Emit(unblockFailedEvent);
                }
            }
            EventEmitter.EmitInfo($"usb_storage_kill_switch_restore: restored {restored}, still-blocked {stillBlocked}");
        }
        catch (Exception ex)
        {
            EventEmitter.EmitError("usb_storage_kill_switch_restore", ex.Message);
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
