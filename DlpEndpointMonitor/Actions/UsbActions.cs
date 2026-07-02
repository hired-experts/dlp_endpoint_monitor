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
    /// from the interface GUID onward must be dropped - including the "\reference" tail -
    /// then the '#' separators turned back into '\'. The previous logic only stripped a
    /// trailing "#{guid}" and left the "\reference" tail in place, producing an invalid
    /// instance ID that CM_Locate_DevNodeW rejects with CR_INVALID_DEVICE_ID (0x1E).
    /// </summary>
    static string ToInstanceId(string interfacePath)
    {
        string working = interfacePath.StartsWith(@"\\?\") ? interfacePath[4..] : interfacePath;
        int guidIdx = working.IndexOf("#{", StringComparison.Ordinal);
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
    /// Returns null for Unknown kind — those devices are not policy-relevant.
    /// </summary>
    public static ParsedDevice? ParsePartialDevice(string rawPath, DeviceKind kind)
    {
        if (kind == DeviceKind.Unknown) return null;

        string working = ToInstanceId(rawPath);
        return working.Length > 0
            ? new ParsedDevice("", "", null, null, null, DeviceKind.Unknown, null, working, rawPath)
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

                    if (!NativeMethods.SetupDiGetDeviceInterfaceDetail(
                            devInfo, ref ifaceData, ref detail,
                            detailBufSize, out _, IntPtr.Zero))
                        continue;

                    string     rawPath   = detail.DevicePath;
                    string     classGuid = ifaceGuid.ToString("B");
                    DeviceKind kind      = DeviceKindResolver.Resolve(classGuid, out int? usbClass);

                    var parsed = ParseDevicePath(rawPath)
                                ?? ParsePartialDevice(rawPath, kind);
                    if (parsed is null) continue;

                    string? groupId = GetGroupId(parsed.InstanceId);
                    yield return parsed with
                    {
                        ClassGuid = classGuid,
                        UsbClass  = usbClass,
                        Kind      = kind,
                        GroupId   = groupId,
                    };
                }
            }
            finally
            {
                NativeMethods.SetupDiDestroyDeviceInfoList(devInfo);
            }
        }
    }

    // ── Device tree — group ID ────────────────────────────────────────────────

    /// <summary>
    /// Walks up the device tree to find the root USB device node and returns its
    /// instance ID as an opaque group key. All interface notifications from the
    /// same physical device share this ID.
    /// Uses CM_LOCATE_DEVNODE_PHANTOM so it works for both arrival and removal events.
    /// Returns null if the device is not locatable or has no USB ancestor.
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
