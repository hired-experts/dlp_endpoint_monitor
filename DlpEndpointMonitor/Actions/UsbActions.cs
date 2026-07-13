using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using DlpEndpointMonitor.Core;
using DlpEndpointMonitor.Win32;
using Microsoft.Win32;


namespace DlpEndpointMonitor.Actions;

public record ParsedDevice(string Vid, string Pid, string? Serial, string? ClassGuid, int? UsbClass, DeviceKind Kind, string? GroupId, string InstanceId, string RawPath);

static class UsbActions
{
    // Standard USB paths:  VID_046D&PID_B020
    static readonly Regex _vidPid = new(
        @"VID_(?<vid>[0-9A-Fa-f]{4})&PID_(?<pid>[0-9A-Fa-f]{4})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Bluetooth HID paths: VID&02046D_PID&B020  (first two hex digits = vendor-ID source byte)
    static readonly Regex _btVidPid = new(
        @"VID&[0-9A-Fa-f]{2}(?<vid>[0-9A-Fa-f]{4})_PID&(?<pid>[0-9A-Fa-f]{4})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Recovers the PnP device instance ID from a device-interface symbolic path.
    /// The path looks like "\\?\&lt;instance-id&gt;#{&lt;interface-class-guid&gt;}\&lt;reference&gt;"
    /// (e.g. "...#{65e8773d-...}\global", "...#{65e8773d-...}\wavemicin"). Everything
    /// from the FINAL interface GUID onward must be dropped - including the "\reference" tail -
    /// then the '#' separators turned back into '\'. The previous logic only stripped a
    /// trailing "#{guid}" and left the "\reference" tail in place, producing an invalid
    /// instance ID that CM_Locate_DevNodeW rejects with CR_INVALID_DEVICE_ID (0x1E).
    ///
    /// Must search for the LAST "#{", not the first: a Bluetooth-LE HID device's own instance
    /// ID embeds a GATT service UUID right after the enumerator name (e.g.
    /// "hid#{00001812-...}_Dev_VID&...&Col01#9&.../#{378de44c-...}" - the trailing interface-
    /// class GUID). Using the first "#{" match truncated at that embedded GATT UUID instead,
    /// collapsing the instance ID down to just "hid" - a real production bug: a live device
    /// arrival (WM_DEVICECHANGE, the only caller of this function with no SetupDi fallback -
    /// see EnumerateConnected's own instance-id lookup) then failed every subsequent
    /// CM_Locate_DevNodeW call for that device, making IsProtectedInternal/HasBluetoothAncestor
    /// silently fall through to "no ancestor found -> internal", refusing to block a genuinely
    /// external Bluetooth mouse that should have been blocked.
    /// </summary>
    static string ToInstanceId(string interfacePath)
    {
        string working = interfacePath.StartsWith(@"\\?\") ? interfacePath[4..] : interfacePath;
        int guidIdx = working.LastIndexOf("#{", StringComparison.Ordinal);
        if (guidIdx >= 0) working = working[..guidIdx];
        return working.Replace('#', '\\');
    }

    public static ParsedDevice? ParseDevicePath(string rawPath)
    {
        var match = _vidPid.Match(rawPath);
        if (!match.Success)
            match = _btVidPid.Match(rawPath);
        if (!match.Success)
            return null;

        string vid = match.Groups["vid"].Value.ToUpperInvariant();
        string pid = match.Groups["pid"].Value.ToUpperInvariant();

        string working = ToInstanceId(rawPath);

        // Last segment of the instance ID is the serial number when it contains no '&'.
        // Positional IDs assigned by Windows look like "7&3A4B1C2D&0&1" and always contain '&'.
        int lastSlash = working.LastIndexOf('\\');
        string? serial = lastSlash >= 0 ? working[(lastSlash + 1)..] : null;
        if (serial?.Contains('&') == true) serial = null;

        // ClassGuid/UsbClass/Kind/GroupId are not in the device path — the caller sets them via `with { ... }`
        return new ParsedDevice(vid, pid, serial, null, null, DeviceKind.Unknown, null, working, rawPath);
    }

    /// <summary>
    /// Extracts only the instance ID from a device path that has no VID/PID
    /// (e.g. USBSTOR, ACPI). Returns a <see cref="ParsedDevice"/> with empty
    /// Vid/Pid so kind-only policy entries can still match.
    /// An Unknown kind is deliberately still returned (not null): an unclassifiable device
    /// must still reach policy evaluation so whitelist mode fails closed on it (it will not
    /// match any specific entry, so it ends up blocked) instead of silently connecting.
    /// Blacklist mode is unaffected - it already default-allows anything that does not match
    /// a specific entry, so this is a pure win for whitelist with no blacklist regression.
    /// </summary>
    public static ParsedDevice? ParsePartialDevice(string rawPath, DeviceKind kind)
    {
        string working = ToInstanceId(rawPath);
        return working.Length > 0
            ? new ParsedDevice("", "", null, null, null, kind, null, working, rawPath)
            : null;
    }

    // ── Startup enumeration ───────────────────────────────────────────────────

    /// <summary>
    /// Yields a <see cref="ParsedDevice"/> for every USB device interface currently
    /// connected to the system, across all known interface class GUIDs.
    /// A composite device with N interfaces will appear N times, same as at runtime.
    /// </summary>
    public static IEnumerable<ParsedDevice> EnumerateConnected()
    {
        // cbSize of the FIXED part of SP_DEVICE_INTERFACE_DETAIL_DATA_W:
        // DWORD(4) + WCHAR[1](2) padded to DWORD alignment = 8 on both 32/64-bit.
        const uint DetailCbSize    = 8;
        uint       detailBufSize   = (uint)Marshal.SizeOf<SP_DEVICE_INTERFACE_DETAIL_DATA_W>();

        foreach (Guid ifaceGuid in DeviceKindResolver.KnownInterfaceGuids)
        {
            Guid guid = ifaceGuid; // local copy — ref parameter must be writable
            IntPtr devInfo = NativeMethods.SetupDiGetClassDevs(
                ref guid, IntPtr.Zero, IntPtr.Zero,
                NativeMethods.DIGCF_PRESENT | NativeMethods.DIGCF_DEVICEINTERFACE);

            if (devInfo == NativeMethods.INVALID_HANDLE_VALUE) continue;

            try
            {
                for (uint idx = 0; ; idx++)
                {
                    var ifaceData = new SP_DEVICE_INTERFACE_DATA();
                    ifaceData.cbSize = (uint)Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>();

                    if (!NativeMethods.SetupDiEnumDeviceInterfaces(
                            devInfo, IntPtr.Zero, ref guid, idx, ref ifaceData))
                        break; // ERROR_NO_MORE_ITEMS

                    var detail = new SP_DEVICE_INTERFACE_DETAIL_DATA_W();
                    detail.cbSize = DetailCbSize;

                    var devInfoData = new SP_DEVINFO_DATA();
                    devInfoData.cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>();

                    if (!NativeMethods.SetupDiGetDeviceInterfaceDetail(
                            devInfo, ref ifaceData, ref detail,
                            detailBufSize, out _, ref devInfoData))
                        continue;

                    string     rawPath   = detail.DevicePath;
                    string     classGuid = ifaceGuid.ToString("B");
                    DeviceKind kind      = DeviceKindResolver.Resolve(classGuid, out int? usbClass);

                    var parsed = ParseDevicePath(rawPath)
                                ?? ParsePartialDevice(rawPath, kind);
                    if (parsed is null) continue;

                    // Use the true instance ID from SetupAPI. Deriving it from the interface path
                    // (ToInstanceId) breaks for some HID paths - it collapsed to "hid" for a
                    // Bluetooth-LE HID mouse, so CM_Locate_DevNodeW/DisableDevice silently failed
                    // and the mouse was matched as kind=mouse but never actually disabled.
                    string instanceId = GetDeviceInstanceId(devInfo, ref devInfoData) ?? parsed.InstanceId;

                    string? groupId = GetGroupId(instanceId);
                    yield return parsed with
                    {
                        InstanceId = instanceId,
                        ClassGuid  = classGuid,
                        UsbClass   = usbClass,
                        Kind       = kind,
                        GroupId    = groupId,
                    };
                }
            }
            finally
            {
                NativeMethods.SetupDiDestroyDeviceInfoList(devInfo);
            }
        }
    }

    /// <summary>
    /// Yields every currently-connected interface that shares the given USB composite group
    /// ID - i.e. every live sibling interface of the same physical device. A disabled devnode
    /// (leaf or composite parent) has no active interface, so it never appears here; callers
    /// that need to reason about a currently-disabled device must fall back to other means
    /// (see <c>UsbMonitor.RestoreCompliant</c>).
    /// </summary>
    public static IEnumerable<ParsedDevice> EnumerateGroupSiblings(string groupId) =>
        EnumerateConnected().Where(p => groupId.Equals(p.GroupId, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Reads the true device instance ID of a devnode via SetupDiGetDeviceInstanceId.
    /// Returns null on failure, in which case the caller falls back to the path-derived id.
    /// Internal (not private) so <see cref="BluetoothActions"/> can reuse it rather than
    /// duplicating the same SetupDiGetDeviceInstanceId call.
    /// </summary>
    internal static string? GetDeviceInstanceId(IntPtr devInfo, ref SP_DEVINFO_DATA devInfoData)
    {
        var sb = new StringBuilder(512);
        return NativeMethods.SetupDiGetDeviceInstanceId(devInfo, ref devInfoData, sb, (uint)sb.Capacity, out _)
            ? sb.ToString()
            : null;
    }

    // ── Device tree — group ID ────────────────────────────────────────────────

    /// <summary>
    /// Walks up the device tree to find the root USB device node and returns its
    /// instance ID as an opaque group key. All interface notifications from the
    /// same physical device share this ID.
    /// Uses CM_LOCATE_DEVNODE_PHANTOM so it works for both arrival and removal events.
    /// Returns null if the device is not locatable, has no USB ancestor, or is
    /// Bluetooth-backed (see the BTH check below) - never the shared Bluetooth radio's ID.
    /// </summary>
    public static string? GetGroupId(string instanceId)
    {
        uint cr = NativeMethods.CM_Locate_DevNodeW(
            out uint current, instanceId, NativeMethods.CM_LOCATE_DEVNODE_PHANTOM);
        if (cr != NativeMethods.CR_SUCCESS) return null;

        for (int depth = 0; depth < 10; depth++)
        {
            var sb = new StringBuilder((int)NativeMethods.MAX_DEVICE_ID_LEN + 1);
            cr = NativeMethods.CM_Get_Device_IDW(current, sb, NativeMethods.MAX_DEVICE_ID_LEN + 1, 0);
            if (cr != NativeMethods.CR_SUCCESS) break;

            string id = sb.ToString();

            // A Bluetooth-backed HID device's ancestor walk (BTHLEDEVICE -> BTHLE -> BTH\MS_BTHLE)
            // eventually reaches the shared Bluetooth radio's OWN "USB\..." node, since the radio
            // itself commonly sits on the USB bus. Returning that as this device's "group" would
            // silently merge its block/allow decision with the radio's own (harmless, never
            // blacklisted) USB interface - stop here instead, same as HasBluetoothAncestor's own
            // walk, so a Bluetooth peripheral never inherits a group from an unrelated device that
            // happens to share the same physical radio. Real production bug: a kind=mouse blacklist
            // entry never matched a BLE mouse because its group resolved to the radio's own
            // kind=Bluetooth interface, which is never blacklisted.
            if (id.StartsWith("BTH", StringComparison.OrdinalIgnoreCase))
                return null;

            // The physical USB device node always starts with "USB\"
            if (id.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase))
                return id;

            cr = NativeMethods.CM_Get_Parent(out uint parent, current, 0);
            if (cr != NativeMethods.CR_SUCCESS) break;
            current = parent;
        }

        return null;
    }

    // ── Disable a specific device instance ───────────────────────────────────

    /// <summary>
    /// Disables the device so Windows will not load its driver.
    /// Persists across replug — device stays disabled until re-enabled.
    /// Requires administrator privileges.
    /// </summary>
    public static (bool ok, string? error) DisableDevice(string instanceId)
    {
        uint cr = NativeMethods.CM_Locate_DevNodeW(
            out uint devNode, instanceId, NativeMethods.CM_LOCATE_DEVNODE_NORMAL);

        if (cr != NativeMethods.CR_SUCCESS)
        {
            return (false, $"CM_Locate_DevNodeW failed: 0x{cr:X}");
        }

        cr = NativeMethods.CM_Disable_DevNode(devNode, NativeMethods.CM_DISABLE_UI_NOT_OK);

        return cr == NativeMethods.CR_SUCCESS
            ? (true, null)
            : (false, $"CM_Disable_DevNode failed: 0x{cr:X}");
    }

    // ── Enable a specific device instance ────────────────────────────────────

    /// <summary>Re-enables a previously disabled device.</summary>
    public static (bool ok, string? error) EnableDevice(string instanceId)
    {
        uint cr = NativeMethods.CM_Locate_DevNodeW(
            out uint devNode, instanceId, NativeMethods.CM_LOCATE_DEVNODE_NORMAL);

        if (cr != NativeMethods.CR_SUCCESS)
        {
            return (false, $"CM_Locate_DevNodeW failed: 0x{cr:X}");
        }

        cr = NativeMethods.CM_Enable_DevNode(devNode, 0);

        return cr == NativeMethods.CR_SUCCESS
            ? (true, null)
            : (false, $"CM_Enable_DevNode failed: 0x{cr:X}");
    }

    // ── Internal / strict-device protection ──────────────────────────────────

    // The machine's own essential devices on a laptop. A built-in one of these must never be
    // disabled/ejected: keyboard/mouse/hid = local input; hub = disabling an internal/root hub
    // takes down everything downstream (including the built-in keyboard). Camera/Video are
    // deliberately NOT here - a built-in webcam is allowed to be blocked.
    static readonly HashSet<DeviceKind> StrictInputKinds =
        [DeviceKind.Keyboard, DeviceKind.Mouse, DeviceKind.Hid, DeviceKind.Hub];

    /// <summary>
    /// Whether a device can be physically removed. A built-in / soldered device reports
    /// CM_REMOVAL_POLICY_EXPECT_NO_REMOVAL. On ANY read failure this FAILS SAFE and returns
    /// false (treat as internal), so an undeterminable device is never blocked as if external.
    /// </summary>
    public static bool IsRemovable(string instanceId)
    {
        uint cr = NativeMethods.CM_Locate_DevNodeW(out uint devNode, instanceId, NativeMethods.CM_LOCATE_DEVNODE_NORMAL);
        if (cr != NativeMethods.CR_SUCCESS) return false;

        uint len = sizeof(uint);
        cr = NativeMethods.CM_Get_DevNode_Registry_PropertyW(
            devNode, NativeMethods.CM_DRP_REMOVAL_POLICY, out _, out uint policy, ref len, 0);
        if (cr != NativeMethods.CR_SUCCESS) return false;

        return policy != NativeMethods.CM_REMOVAL_POLICY_EXPECT_NO_REMOVAL;
    }

    /// <summary>
    /// Pure string check: does this Compatible IDs list indicate USB mass-storage class (0x08)?
    /// Windows-generated compatible IDs for mass storage look like "USB\Class_08&SubClass_06&Prot_50",
    /// "USB\Class_08&SubClass_06", "USB\Class_08" (decreasing specificity) - a case-insensitive
    /// substring check for "Class_08" is sufficient and matches this file's existing preference
    /// for simple, robust string checks over brittle exact-format parsing. No Win32 involved, so
    /// this is fully unit-testable (unlike <see cref="IsMassStorageDevice"/> below).
    /// </summary>
    public static bool CompatibleIdsIndicateMassStorage(IEnumerable<string?>? compatibleIds)
    {
        if (compatibleIds is null) return false;

        foreach (string? id in compatibleIds)
        {
            if (!string.IsNullOrEmpty(id) && id.Contains("Class_08", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Purely observational check: is this devnode USB mass-storage class, regardless of whether
    /// its storage driver actually bound? Used to report a device that arrived while the USBSTOR
    /// kill switch is off (<see cref="IsUsbStorageEnabled"/>) - see <c>UsbMonitor.HandleArrival</c>.
    /// Reads Compatible IDs directly off the devnode rather than relying on Kind/ClassGuid, since a
    /// mass-storage device connecting with USBSTOR disabled never binds a storage driver and so
    /// never exposes GUID_DEVINTERFACE_DISK/CDROM - it resolves to DeviceKind.Unknown via the
    /// generic GUID_DEVINTERFACE_USB_DEVICE interface instead (see Core/UsbKind.cs), which carries
    /// no class information of its own.
    /// On ANY failure (device not locatable, registry read fails) this returns false - fails closed
    /// toward "don't claim it's storage", the opposite direction from IsRemovable's fail-toward-
    /// "internal" default, because this check is purely informational (no enforcement decision rides
    /// on it) rather than a safety gate. Must never throw: a devnode registry read failing here must
    /// not take down the message thread that called it.
    /// </summary>
    public static bool IsMassStorageDevice(string instanceId)
    {
        try
        {
            uint cr = NativeMethods.CM_Locate_DevNodeW(out uint devNode, instanceId, NativeMethods.CM_LOCATE_DEVNODE_NORMAL);
            if (cr != NativeMethods.CR_SUCCESS) return false;

            var buffer = new byte[4096];
            uint len = (uint)buffer.Length;
            cr = NativeMethods.CM_Get_DevNode_Registry_PropertyW_MultiSz(
                devNode, NativeMethods.CM_DRP_COMPATIBLEIDS, out _, buffer, ref len, 0);
            if (cr != NativeMethods.CR_SUCCESS || len == 0) return false;

            // REG_MULTI_SZ: consecutive null-terminated UTF-16 strings, ending in an extra
            // trailing null - split on '\0' and drop the empty entries that produces.
            string raw = Encoding.Unicode.GetString(buffer, 0, (int)len);
            string[] ids = raw.Split('\0', StringSplitOptions.RemoveEmptyEntries);

            return CompatibleIdsIndicateMassStorage(ids);
        }
        catch {
            return false;
        }
    }

    // True if any ancestor devnode is on a Bluetooth enumerator (BTHENUM\, BTHLE\, BTHLEDEVICE\).
    // A wireless HID device's own instance id (e.g. HID\{00001812-...}) does not say "BTH" - the
    // Bluetooth origin is on a parent node - so we walk the tree.
    public static bool HasBluetoothAncestor(string instanceId)
    {
        // PHANTOM (not NORMAL) like GetGroupId: BLE peripherals route through an intermediate
        // BTHLEDEVICE/GATT-service node that can be phantom/not-currently-present at query time.
        // NORMAL mode fails to even locate such a node, silently making this report "no ancestor"
        // for exactly the wireless devices it most needs to protect from an internal-USB misclassify.
        uint cr = NativeMethods.CM_Locate_DevNodeW(out uint current, instanceId, NativeMethods.CM_LOCATE_DEVNODE_PHANTOM);
        if (cr != NativeMethods.CR_SUCCESS) return false;

        for (int depth = 0; depth < 12; depth++)
        {
            var sb = new StringBuilder((int)NativeMethods.MAX_DEVICE_ID_LEN + 1);
            if (NativeMethods.CM_Get_Device_IDW(current, sb, NativeMethods.MAX_DEVICE_ID_LEN + 1, 0) != NativeMethods.CR_SUCCESS) break;
            if (sb.ToString().StartsWith("BTH", StringComparison.OrdinalIgnoreCase)) return true;
            if (NativeMethods.CM_Get_Parent(out uint parent, current, 0) != NativeMethods.CR_SUCCESS) break;
            current = parent;
        }
        return false;
    }

    /// <summary>
    /// For a Bluetooth-backed HID device, returns the instance ID of its Bluetooth DEVICE node
    /// - BTHENUM\ (classic) or, for BLE, the true top-level peripheral under BTHLE\ (NOT
    /// BTHLEDEVICE\, which is one level too shallow - a GATT-service child of the peripheral,
    /// not the peripheral itself; verified live: BTHLE\DEV_&lt;mac&gt;\... is what Device Manager
    /// shows as the device's own friendly name, e.g. "MX Vertical", while BTHLEDEVICE\...
    /// entries underneath it are individual GATT services such as "GATT compliant HID device").
    /// Disabling THAT node (rather than the HID leaf) stops the device robustly: vendor
    /// software (e.g. Logitech Options) re-enables the HID leaf, and the device's "USB
    /// ancestor" is the shared Bluetooth radio, which must never be disabled. Scoped to this
    /// one peripheral, reversible via CM_Enable, and does NOT unpair. Returns null when the
    /// device is not behind Bluetooth.
    /// </summary>
    public static string? GetBluetoothDeviceNode(string instanceId)
    {
        // PHANTOM (not NORMAL) like GetGroupId: BLE peripherals route through an intermediate
        // BTHLEDEVICE/GATT-service node that can be phantom/not-currently-present at query time.
        // NORMAL mode fails to even locate such a node, silently making this return null and
        // dropping the Bluetooth-node disable path for exactly the devices that most need it.
        uint cr = NativeMethods.CM_Locate_DevNodeW(out uint current, instanceId, NativeMethods.CM_LOCATE_DEVNODE_PHANTOM);
        if (cr != NativeMethods.CR_SUCCESS) return null;

        for (int depth = 0; depth < 12; depth++)
        {
            var sb = new StringBuilder((int)NativeMethods.MAX_DEVICE_ID_LEN + 1);
            if (NativeMethods.CM_Get_Device_IDW(current, sb, NativeMethods.MAX_DEVICE_ID_LEN + 1, 0) != NativeMethods.CR_SUCCESS) break;
            string id = sb.ToString();
            // Classic Bluetooth's own device node - stop here, do NOT ascend to the USB radio above it.
            if (id.StartsWith("BTHENUM\\", StringComparison.OrdinalIgnoreCase))
                return id;
            // BLE's true peripheral node (one level above BTHLEDEVICE\'s GATT-service children) -
            // stop here, do NOT ascend to BTH\MS_BTHLE\ (the enumerator driver) above it.
            if (id.StartsWith("BTHLE\\", StringComparison.OrdinalIgnoreCase))
                return id;
            // BTHLEDEVICE\ itself is a GATT-service child, not the peripheral - keep walking up.
            if (NativeMethods.CM_Get_Parent(out uint parent, current, 0) != NativeMethods.CR_SUCCESS) break;
            current = parent;
        }
        return null;
    }

    /// <summary>
    /// Returns every Bluetooth device-node instance ID worth trying as a pairing-removal
    /// candidate for the peripheral currently resolving to <paramref name="instanceId"/>:
    /// <see cref="GetBluetoothDeviceNode"/>'s own resolution first (the primary, most likely
    /// candidate), followed by any SIBLING BTHENUM\ or BTHLE\ node under the same immediate
    /// Bluetooth-enumerator parent (e.g. BTH\MS_BTHPORT\ for classic, BTH\MS_BTHLE\ for BLE).
    /// Returns an empty list when the device is not Bluetooth-backed at all (mirrors
    /// GetBluetoothDeviceNode returning null).
    ///
    /// Exists because a BLE peripheral is permitted to regenerate its Resolvable Private /
    /// Static Random address across power cycles (observed live: the same physical mouse's
    /// PnP instance ID carried a different trailing MAC-derived hex suffix on each of three
    /// reconnects in one session), so the address recovered from the CURRENT arrival's devnode
    /// is not guaranteed to be the one Windows' classic "remembered devices" store - what
    /// BluetoothRemoveDevice actually checks - still has on file. A PRIOR connection cycle's
    /// now-phantom node for the SAME physical peripheral is exactly the kind of sibling this
    /// walk picks up: CM_LOCATE_DEVNODE_PHANTOM keeps a devnode locatable/enumerable as a child
    /// even once its hardware is no longer present, so an older sibling can still be found and
    /// tried here. Trying every candidate is safe regardless of whether this theory is correct
    /// for any given failure: BluetoothRemoveDevice against a non-matching address is a
    /// harmless no-op returning ERROR_NOT_FOUND (0x490), never a destructive operation on the
    /// wrong device. Order is preserved: primary node first, then siblings in
    /// CM_Get_Child/CM_Get_Sibling enumeration order, so a normal (non-rotated) case still
    /// tries the obviously-correct address first and stops there.
    /// </summary>
    public static IReadOnlyList<string> GetBluetoothPairingCandidates(string instanceId)
    {
        string? primary = GetBluetoothDeviceNode(instanceId);
        if (primary is null) return Array.Empty<string>();

        var result = new List<string> { primary };

        uint cr = NativeMethods.CM_Locate_DevNodeW(out uint primaryNode, primary, NativeMethods.CM_LOCATE_DEVNODE_PHANTOM);
        if (cr != NativeMethods.CR_SUCCESS) return result;

        cr = NativeMethods.CM_Get_Parent(out uint parent, primaryNode, 0);
        if (cr != NativeMethods.CR_SUCCESS) return result;

        cr = NativeMethods.CM_Get_Child(out uint child, parent, 0);
        while (cr == NativeMethods.CR_SUCCESS)
        {
            var sb = new StringBuilder((int)NativeMethods.MAX_DEVICE_ID_LEN + 1);
            if (NativeMethods.CM_Get_Device_IDW(child, sb, NativeMethods.MAX_DEVICE_ID_LEN + 1, 0) == NativeMethods.CR_SUCCESS)
            {
                string id = sb.ToString();
                bool isBtDeviceNode = id.StartsWith("BTHENUM\\", StringComparison.OrdinalIgnoreCase)
                                   || id.StartsWith("BTHLE\\", StringComparison.OrdinalIgnoreCase);
                if (isBtDeviceNode && !result.Contains(id, StringComparer.OrdinalIgnoreCase))
                    result.Add(id);
            }

            cr = NativeMethods.CM_Get_Sibling(out uint next, child, 0);
            child = next;
        }

        return result;
    }

    /// <summary>
    /// Bus-ancestry decision shared by <see cref="IsProtectedInternal"/> (strict input kinds)
    /// and <see cref="NetworkMonitor"/>'s own built-in-adapter guard - the machine's only
    /// network adapter is exactly as essential as its built-in keyboard: disabling it can
    /// strand a managed endpoint with no connectivity to report the disconnection back to
    /// the sibling dlp_v2 agent. Internal (no removable USB ancestor) -> never block.
    /// External/removable (USB WiFi/Ethernet dongle) -> still blockable, this is a real DLP
    /// use case (blocking a rogue USB network adapter used to bypass monitoring).
    /// </summary>
    public static bool IsBuiltIn(string instanceId)
    {
        // Check Bluetooth FIRST. A wireless BT/BLE mouse is external and blockable, but the
        // Bluetooth radio itself is frequently a USB device (e.g. USB\VID_0BDA...), which sits
        // ABOVE the BTH nodes in the tree - so a USB-ancestor check would find the radio and
        // wrongly treat the wireless input as built-in USB.
        if (HasBluetoothAncestor(instanceId)) return false; // BT is always external

        // On the USB bus: external unless the physical USB node is non-removable (rare internal-USB device).
        string? usbAncestor = GetGroupId(instanceId);
        if (usbAncestor is not null)
            return !IsRemovable(usbAncestor);

        return true; // no bus ancestor at all -> internal
    }

    /// <summary>
    /// SAFETY (criterion 5): a built-in keyboard/touchpad must never be blocked (it would brick
    /// local input on a laptop). "Built-in" is decided by TRANSPORT BUS, not removability - see
    /// <see cref="IsBuiltIn"/> for the bus-ancestry walk this delegates to.
    /// Camera/Video are not strict kinds, so a built-in webcam stays blockable.
    ///
    /// Kind == Unknown is ALSO routed through this same bus-ancestry check (not just
    /// StrictInputKinds). This is a safety net for whitelist mode's fail-closed behavior
    /// (ParsePartialDevice/ParseMonitorPath no longer skip unclassifiable devices): an
    /// internal, essential device that happens to expose only an unrecognized interface GUID
    /// was previously never reachable here at all (silently skipped upstream), so it was never
    /// at risk of being disabled; now that it can reach this far, it must get the same
    /// built-in protection as a strict input kind, not be treated as blindly blockable just
    /// because we don't know what it is.
    /// </summary>
    public static bool IsProtectedInternal(DeviceKind kind, string instanceId)
    {
        if (!StrictInputKinds.Contains(kind) && kind != DeviceKind.Unknown) return false;

        return IsBuiltIn(instanceId);
    }

    /// <summary>
    /// Requests the OS to eject the device via the PnP safe-removal path.
    /// Unlike <see cref="DisableDevice"/>, this succeeds for HID input devices
    /// (keyboards, mice) where CM_Disable_DevNode is rejected by Windows to
    /// prevent keyboard lockout.
    /// </summary>
    public static (bool ok, string? error) RequestEject(string instanceId)
    {
        uint cr = NativeMethods.CM_Locate_DevNodeW(
            out uint devNode, instanceId, NativeMethods.CM_LOCATE_DEVNODE_NORMAL);

        if (cr != NativeMethods.CR_SUCCESS)
            return (false, $"CM_Locate_DevNodeW failed: 0x{cr:X}");

        cr = NativeMethods.CM_Request_Device_EjectW(devNode, out uint vetoType, IntPtr.Zero, 0, 0);

        if (cr == NativeMethods.CR_SUCCESS && vetoType == 0)
            return (true, null);

        string reason = vetoType != 0
            ? $"CM_Request_Device_EjectW vetoed (vetoType=0x{vetoType:X})"
            : $"CM_Request_Device_EjectW failed: 0x{cr:X}";
        return (false, reason);
    }

    // ── Eject a volume by drive letter ────────────────────────────────────────
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CreateFile(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition,
        uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(
        IntPtr hDevice, uint dwIoControlCode,
        IntPtr lpInBuffer,  uint nInBufferSize,
        IntPtr lpOutBuffer, uint nOutBufferSize,
        out uint lpBytesReturned, IntPtr lpOverlapped);

    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr hObject);

    const uint GENERIC_READ              = 0x80000000;
    const uint GENERIC_WRITE             = 0x40000000;
    const uint FILE_SHARE_READ           = 0x00000001;
    const uint FILE_SHARE_WRITE          = 0x00000002;
    const uint OPEN_EXISTING             = 3;
    const uint IOCTL_STORAGE_EJECT_MEDIA = 0x2D4808;

    static readonly IntPtr INVALID_HANDLE = new(-1);

    public static (bool ok, string? error) EjectDrive(string driveLetter)
    {
        char letter = char.ToUpper(driveLetter.TrimEnd('\\', ':')[0]);
        string path = $"\\\\.\\{letter}:";

        IntPtr handle = CreateFile(
            path,
            GENERIC_READ | GENERIC_WRITE,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);

        if (handle == INVALID_HANDLE)
        {
            return (false, $"Cannot open drive {letter}: error {Marshal.GetLastWin32Error()}");
        }

        try
        {
            bool ok = DeviceIoControl(
                handle, IOCTL_STORAGE_EJECT_MEDIA,
                IntPtr.Zero, 0, IntPtr.Zero, 0,
                out _, IntPtr.Zero);

            return ok
                ? (true, null)
                : (false, $"Eject failed: error {Marshal.GetLastWin32Error()}");
        }
        finally {
            CloseHandle(handle);
        }
    }

    // ── Global USB storage block via registry ─────────────────────────────────

    const string UsbStorKey = @"SYSTEM\CurrentControlSet\Services\USBSTOR";

    public static (bool ok, string? error) SetUsbStorageEnabled(bool enable)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(UsbStorKey, writable: true);
            if (key is null)
            {
                return (false, "USBSTOR registry key not found");
            }

            key.SetValue("Start", enable ? 3 : 4, RegistryValueKind.DWord);

            return (true, null);
        }
        catch (Exception ex) {
            return (false, ex.Message);
        }
    }

    public static bool IsUsbStorageEnabled()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(UsbStorKey);
            return key?.GetValue("Start") is int v && v != 4;
        }
        catch {
            return true;
        }
    }
}
