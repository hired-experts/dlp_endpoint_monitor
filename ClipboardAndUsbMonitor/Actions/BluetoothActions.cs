using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using ClipboardUsbMonitor.Core;
using ClipboardUsbMonitor.Win32;

namespace ClipboardUsbMonitor.Actions;

static class BluetoothActions
{
    // Matches the 12-hex-char BT address in BTHENUM paths, e.g. "...&0&AABBCCDDEEFF_C00000000"
    static readonly Regex _macInPath = new(
        @"[\\&]([0-9A-Fa-f]{12})_C[0-9A-Fa-f]{8}",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Matches the interface GUID suffix "#{ ... }" at the end of a device path
    static readonly Regex _ifaceGuid = new(
        @"#\{([0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12})\}",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // ── Path parsing ──────────────────────────────────────────────────────────

    /// <summary>
    /// Extracts the Bluetooth device address from a BTHENUM device path.
    /// Returns a canonical "AA:BB:CC:DD:EE:FF" string or null if not found.
    /// </summary>
    public static string? ParseMacFromPath(string path)
    {
        var m = _macInPath.Match(path);
        if (!m.Success) return null;
        return FormatHexMac(m.Groups[1].Value);
    }

    /// <summary>
    /// Extracts the interface class GUID from a device path and resolves it to a DeviceKind.
    /// </summary>
    public static DeviceKind ParseKindFromPath(string path)
    {
        var m = _ifaceGuid.Match(path);
        if (!m.Success) return DeviceKind.Unknown;
        string guid = "{" + m.Groups[1].Value.ToUpperInvariant() + "}";
        return DeviceKindResolver.Resolve(guid, out _);
    }

    // ── Address formatting ────────────────────────────────────────────────────

    /// <summary>Converts a BLUETOOTH_ADDRESS.ullLong to "AA:BB:CC:DD:EE:FF".</summary>
    public static string FormatAddress(ulong ullLong)
    {
        // ullLong stores bytes little-endian: byte[0] = LSB = rgBytes[0]
        // Display order is MSB first: AA:BB:CC:DD:EE:FF → ullLong = 0x0000AABBCCDDEEFF
        var bytes = new byte[6];
        for (int i = 0; i < 6; i++)
            bytes[i] = (byte)((ullLong >> ((5 - i) * 8)) & 0xFF);
        return string.Join(":", bytes.Select(b => b.ToString("X2")));
    }

    /// <summary>Converts a CoD (Class of Device) major class to a DeviceKind.</summary>
    public static DeviceKind GetKindFromCoD(uint cod)
    {
        uint major = (cod >> 8) & 0x1F;
        return major switch
        {
            0x04 => DeviceKind.Audio,    // Audio/Video: headset, speaker, headphone
            0x05 => DeviceKind.Hid,      // Peripheral: keyboard, mouse, joystick
            0x06 => DeviceKind.Camera,   // Imaging: printer, scanner, camera
            0x03 => DeviceKind.Network,  // LAN/Network Access Point
            _    => DeviceKind.Unknown,
        };
    }

    // ── Remove pairing ────────────────────────────────────────────────────────

    /// <summary>
    /// Removes the Bluetooth pairing for the given MAC address.
    /// This disconnects the device and prevents it from reconnecting
    /// without going through the full pairing flow again.
    /// </summary>
    public static (bool ok, string? error) RemovePairing(string mac)
    {
        try
        {
            ulong address = ParseMacToUllLong(mac);
            uint cr = NativeMethods.BluetoothRemoveDevice(ref address);
            return cr == 0
                ? (true, null)
                : (false, $"BluetoothRemoveDevice failed: 0x{cr:X8}");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    // ── Startup enumeration ───────────────────────────────────────────────────

    public record BtDevice(string Mac, DeviceKind Kind, string Name);

    /// <summary>Enumerates all currently connected Bluetooth devices.</summary>
    public static IEnumerable<BtDevice> EnumerateConnected()
    {
        var searchParams = new BLUETOOTH_DEVICE_SEARCH_PARAMS
        {
            dwSize               = (uint)Marshal.SizeOf<BLUETOOTH_DEVICE_SEARCH_PARAMS>(),
            fReturnAuthenticated = false,
            fReturnRemembered    = false,
            fReturnUnknown       = false,
            fReturnConnected     = true,
            fIssueInquiry        = false,
            cTimeoutMultiplier   = 0,
            hRadio               = IntPtr.Zero,
        };

        var deviceInfo = new BLUETOOTH_DEVICE_INFO
        {
            dwSize = (uint)Marshal.SizeOf<BLUETOOTH_DEVICE_INFO>(),
        };

        IntPtr handle = NativeMethods.BluetoothFindFirstDevice(ref searchParams, ref deviceInfo);
        if (handle == IntPtr.Zero || handle == NativeMethods.INVALID_HANDLE_VALUE)
            yield break;

        try
        {
            do
            {
                string mac  = FormatAddress(deviceInfo.Address);
                DeviceKind kind = GetKindFromCoD(deviceInfo.ulClassofDevice);
                yield return new BtDevice(mac, kind, deviceInfo.szName ?? string.Empty);
            }
            while (NativeMethods.BluetoothFindNextDevice(handle, ref deviceInfo));
        }
        finally
        {
            NativeMethods.BluetoothFindDeviceClose(handle);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    static string FormatHexMac(string rawHex)
    {
        // rawHex = "AABBCCDDEEFF" (big-endian, MSB first, same as display order)
        return string.Join(":",
            Enumerable.Range(0, 6).Select(i => rawHex.Substring(i * 2, 2).ToUpperInvariant()));
    }

    static ulong ParseMacToUllLong(string mac)
    {
        // "AA:BB:CC:DD:EE:FF" → ullLong = 0x0000AABBCCDDEEFF
        byte[] bytes = mac.Split(':').Select(h => Convert.ToByte(h, 16)).ToArray();
        ulong result = 0;
        for (int i = 0; i < 6; i++)
            result |= ((ulong)bytes[i]) << ((5 - i) * 8);
        return result;
    }
}
