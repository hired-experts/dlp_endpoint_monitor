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

    readonly Dictionary<string, string> _groupAnchors = new(StringComparer.OrdinalIgnoreCase);
    readonly Lock _groupAnchorLock = new();

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

            // Bucket by composite-group so every sibling interface is judged together, not
            // one at a time - this is what BlockDevice's own group check also does, done here
            // once up front so BlockNonCompliant's own "allowed" logging/counting reflects it
            // too, and so BlockDevice does not need to re-enumerate per device below.
            // Devices with a Bluetooth ancestor are excluded from grouping: GetGroupId can
            // resolve a Bluetooth-backed peripheral's "USB group" to the shared BT radio's own
            // instance ID (the radio commonly sits above BTHENUM/BTHLEDEVICE in the tree), so
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

                // blacklisted mirrors "allowed"'s own group-vs-single-device branching, computed
                // alongside it (not re-derived later) so a group-blocked verdict is credited to
                // whichever sibling actually matched, never re-derived from parsed alone.
                bool allowed, blacklisted;
                if (siblings is not null)
                {
                    allowed = IsGroupCompliant(siblings, out bool anyBlocked) && !anyBlocked;
                    blacklisted = anyBlocked;
                }
                else
                {
                    blacklisted = _blacklist.IsBlocked(parsed.Vid, parsed.Pid, parsed.Serial, parsed.Kind);
                    allowed = _whitelist.IsAllowed(parsed.Vid, parsed.Pid, parsed.Serial, parsed.Kind) && !blacklisted;
                }

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
    /// Retroactively enforces the usb_disable_storage kill switch against mass-storage devices
    /// that were already connected and mounted BEFORE the switch flipped - the USBSTOR registry
    /// Start=4 write only prevents a FUTURE driver load, it has no effect on a device whose
    /// driver is already bound and running. Skips Volume-interface siblings (Fix 1 - not an
    /// independently disableable devnode; the enumeration filter below already excludes them, so
    /// this is filtering, not a redundant re-check) and defers to IsProtectedInternal (Fix 2) for
    /// each candidate before attempting to disable it - defense in depth alongside BlockDevice's
    /// own gate for the normal policy path, not relying on either alone.
    ///
    /// Deliberately does NOT run the whitelist/blacklist group-compliance check BlockDevice runs -
    /// the kill switch is a blanket, policy-independent block on ALL mass storage, not a
    /// per-device whitelist/blacklist decision, so there is no "a sibling interface satisfies
    /// whitelist" escape hatch here by design.
    ///
    /// Emits UsbStorageDeviceBlockedEvent/UsbStorageDeviceBlockFailedEvent - see their own doc
    /// comments in EventEmitter.cs for why this is a third, distinct event pair rather than a
    /// reuse of UsbDeviceBlockedEvent/UsbStorageBlockedEvent. Call on a ThreadPool thread after
    /// usb_disable_storage's registry write succeeds.
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
    /// Decides whether a composite USB device, as a whole, satisfies policy by checking ALL of
    /// its currently-known sibling interfaces rather than just one. A group is allowed if ANY
    /// sibling individually matches an active whitelist rule (or whitelist is disabled); it is
    /// blocked if ANY sibling individually matches an active blacklist rule. This exists
    /// because Windows enumerates one physical composite device (e.g. a keyboard) as several
    /// independent interfaces, each possibly resolving to a different DeviceKind - without
    /// this, a whitelist entry for one kind (e.g. "keyboard") would not protect a sibling
    /// interface of the SAME physical device that resolves to a different kind (e.g. "hid"),
    /// and blocking that sibling can escalate to disabling the shared composite parent,
    /// taking the whole physical device down including the interface that WAS allowed.
    /// </summary>
    bool IsGroupCompliant(IEnumerable<ParsedDevice> siblings, out bool anyBlocked)
    {
        anyBlocked = siblings.Any(s => _blacklist.IsBlocked(s.Vid, s.Pid, s.Serial, s.Kind));
        return siblings.Any(s => _whitelist.IsAllowed(s.Vid, s.Pid, s.Serial, s.Kind));
    }

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
        // gates or changes `allowed`/`reason`, and fires regardless of what Kind resolved to.
        // A mass-storage device connecting while USBSTOR is disabled never binds a storage
        // driver, so it resolves as DeviceKind.Unknown here (see Core/UsbKind.cs) rather than
        // DeviceKind.Storage - IsMassStorageDevice reads Compatible IDs directly off the devnode
        // to detect this case, since Kind alone cannot. See PROJECT.md section 5.7.
        if (!UsbActions.IsUsbStorageEnabled() && UsbActions.IsMassStorageDevice(parsed.InstanceId))
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

        // GROUP COMPLIANCE: a physical composite device (e.g. a keyboard) enumerates as
        // several independent interfaces, each possibly resolving to a different DeviceKind.
        // Before blocking THIS interface, check whether any sibling interface of the SAME
        // physical device already satisfies policy - if so, the device as a whole is allowed
        // and must not be touched, even though this one interface's own kind does not match.
        // Without this, blocking a non-matching sibling can escalate to disabling the shared
        // composite parent below, taking the whole physical device down with it. Skipped for
        // Bluetooth-backed devices - their "group" (if any) would be the shared radio, not a
        // meaningful sibling set.
        if (parsed.GroupId is not null && !hasBtAncestor)
        {
            var siblings = knownGroupSiblings ?? UsbActions.EnumerateGroupSiblings(parsed.GroupId).ToList();
            if (!siblings.Any(s => s.InstanceId.Equals(parsed.InstanceId, StringComparison.OrdinalIgnoreCase)))
                siblings = [.. siblings, parsed]; // the arriving/current interface itself must vote too

            if (IsGroupCompliant(siblings, out bool groupBlocked) && !groupBlocked)
            {
                EventEmitter.EmitInfo(
                    $"usb_group_allowed: {parsed.InstanceId} kind={parsed.Kind} groupId={parsed.GroupId} " +
                    "- a sibling interface satisfies whitelist, skipping block");
                return;
            }

            // This live re-check is more authoritative than whatever reason the caller passed in
            // (which may be stale by the time this Task.Run body actually executes) - same
            // never-trust-a-single-interface principle as the compliance check just above.
            reason = groupBlocked ? "blacklist_match" : "whitelist_gate";
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
    /// The actual disable/escalate mechanics shared by <see cref="BlockDevice"/> (policy-triggered
    /// block) and <see cref="BlockAlreadyConnectedStorage"/> (usb_disable_storage kill-switch
    /// retroactive block) - factored out so both call the same Win32 sequence instead of
    /// duplicating it. Callers run their own gates (IsProtectedInternal, IsVolumeInterface, group
    /// compliance) and persist/emit for their own trigger BEFORE/AFTER calling this - it does
    /// neither itself.
    ///
    /// Bluetooth-backed HID (BLE/classic): disables the device's own primary node
    /// (GetBluetoothDeviceNode - the BTHLE\/BTHENUM\ node, never the HID leaf, since vendor
    /// software like Logitech Options re-enables a disabled leaf on its own) instead of
    /// unpairing it.
    ///
    /// Unpair (BluetoothActions.RemovePairing/RemovePairingAny via
    /// UsbActions.GetBluetoothPairingCandidates) is intentionally NOT called here anymore -
    /// a real production incident (see AGENTS.md "sharp edges") showed candidate resolution
    /// walking up to the shared BTH\MS_BTHLE\ enumerator, which every Bluetooth device on the
    /// machine sits under, not just rotated addresses of the SAME physical device, and
    /// issuing a real BluetoothRemoveDevice call against an unrelated mouse's live pairing
    /// while blocking an unrelated keyboard - the mouse never reconnected again, even after
    /// the policy was removed and the user tried re-pairing manually through Windows Settings.
    /// GetBluetoothPairingCandidates/RemovePairingAny/ResolveBluetoothBlock are left fully
    /// intact and still unit-tested (UsbActionsParsingTests, BluetoothActionsParsingTests,
    /// UsbMonitorTests) for a future fix that scopes candidates to the SAME device (e.g.
    /// matching Vid/Pid) instead of every sibling under the shared enumerator - do not delete
    /// them just because this call site stopped using them.
    ///
    /// Trade-off accepted: Windows Settings' Connected/Paired indicator reads the pairing
    /// store, not devnode state, so a disable-only block still shows the device as
    /// "Connected" there, and since the pairing itself survives, Windows may keep
    /// re-establishing the underlying link whenever the device is in range - each reconnect
    /// re-arrives a devnode and this method just re-disables it. Accepted in exchange for
    /// never again touching a device other than the one policy actually matched:
    /// GetBluetoothDeviceNode walks only THIS device's own ancestor chain (no
    /// CM_Get_Child/CM_Get_Sibling fan-out to other devices), so it cannot repeat the
    /// incident above by construction.
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
    /// Decides whether a persisted <see cref="DisabledDeviceRecord"/> is now compliant with
    /// current policy - i.e. whether <see cref="RestoreCompliant"/> should re-enable it.
    ///
    /// The record's own stored Vid/Pid/Kind is the identity of whichever INTERFACE originally
    /// triggered the block, which for a composite device escalated to the shared parent (e.g.
    /// a keyboard's generic-HID sibling interface) may be the WRONG identity to re-check - it
    /// will never match a whitelist entry scoped to the interface that was actually meant to
    /// be allowed (e.g. "keyboard"), leaving the whole physical device stuck disabled forever
    /// even after policy is fixed. To avoid that:
    /// - If other sibling interfaces of the same USB group are currently live (only a single
    ///   leaf was disabled, not the whole composite parent), compliance is derived from THEIR
    ///   current state via <see cref="IsGroupCompliant"/> - the record's own stale identity is
    ///   not used at all.
    /// - If the disabled instance IS the composite parent itself and no sibling is enumerable
    ///   right now (the whole physical device is torn down), this returns true unconditionally
    ///   so the caller re-enables it; Windows then re-enumerates every child interface, each
    ///   firing a fresh arrival that re-evaluates group compliance with live data via
    ///   <see cref="BlockDevice"/>, which re-disables it (at the correct granularity) if it is
    ///   genuinely still non-compliant. Re-enabling is reversible (unlike eject), so this is a
    ///   safe default even though it briefly makes a possibly-non-compliant device live again -
    ///   the same kind of live-then-evaluate window every fresh plug-in already goes through.
    /// - Otherwise (a plain leaf disable of a non-composite device, or no group info at all),
    ///   falls back to the record's own stored identity, same as before this method existed.
    /// Bluetooth-backed disabled peripherals skip all of this - there is no composite-group
    /// concept for them; they are judged by their own stored identity unconditionally.
    /// </summary>
    bool IsRecordCompliant(DisabledDeviceRecord d)
    {
        if (UsbActions.HasBluetoothAncestor(d.InstanceId))
            return _whitelist.IsAllowed(d.Vid, d.Pid, d.Serial, d.Kind)
                && !_blacklist.IsBlocked(d.Vid, d.Pid, d.Serial, d.Kind);

        string? groupId = UsbActions.GetGroupId(d.InstanceId);
        var siblings = groupId is not null ? UsbActions.EnumerateGroupSiblings(groupId).ToList() : [];

        if (siblings.Count > 0)
            return IsGroupCompliant(siblings, out bool anyBlocked) && !anyBlocked;

        if (groupId is not null && groupId.Equals(d.InstanceId, StringComparison.OrdinalIgnoreCase))
            return true; // torn-down composite parent - re-enable and let fresh arrivals re-evaluate

        return _whitelist.IsAllowed(d.Vid, d.Pid, d.Serial, d.Kind)
            && !_blacklist.IsBlocked(d.Vid, d.Pid, d.Serial, d.Kind);
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
            // Mac is not null -> a Bluetooth-blocked record, owned exclusively by
            // BluetoothMonitor.RestoreCompliant; Kind==Network -> owned exclusively by
            // NetworkMonitor.RestoreCompliant. Without this filter, a shared record could be
            // re-enabled by two monitors racing on the same devnode, each emitting its own
            // "unblocked" event for one restore action (a second, independently-discovered bug
            // bundled into this change - see AGENTS.md/PROJECT.md bug history).
            // BlockedBy==StorageKillSwitchBlockedBy is owned exclusively by RestoreStorageDisabled,
            // same reasoning: without this exclusion, an UNRELATED whitelist/blacklist change that
            // fires this same restore reconciliation (e.g. DeviceWhitelistDisableCmd) would
            // re-enable a device the kill switch disabled while usb_disable_storage is still in
            // effect, since IsRecordCompliant only ever consults whitelist/blacklist state, never
            // IsUsbStorageEnabled - the kill switch must only ever be undone by usb_enable_storage.
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
    /// Symmetric restore for <see cref="BlockAlreadyConnectedStorage"/>: re-enables every
    /// persisted record that sweep disabled that current whitelist/blacklist policy does not
    /// ALSO want blocked, reusing <see cref="IsRecordCompliant"/> - the same compliance check
    /// <see cref="RestoreCompliant"/> already runs - so a device blocked by both the kill switch
    /// and an independent blacklist entry correctly stays blocked after usb_enable_storage alone
    /// (the two enforcement paths never fight each other). Reuses UsbDeviceUnblockedEvent/
    /// UsbDeviceUnblockFailedEvent, not a new event type - unlike blocking, un-blocking is the
    /// same "give the device back" outcome RestoreCompliant already reports for every other
    /// trigger, not a distinct kind of action. Call on a ThreadPool thread after
    /// usb_enable_storage's registry write succeeds.
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
