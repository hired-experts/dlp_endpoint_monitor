using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using DlpEndpointMonitor.Core;
using DlpEndpointMonitor.Win32;

namespace DlpEndpointMonitor.Actions;

static class DisplayActions
{
    public const string MonitorClassGuid = "{E6F07B5F-EE97-4A90-B076-33F57BF4EAA7}";
    public static readonly Guid MonitorGuid = new(MonitorClassGuid);

    // EDID manufacturer code (3 letters) + product code (4 hex digits), e.g. "SAM0F91", "DELA0D2"
    static readonly Regex _monitorModel = new(
        @"DISPLAY#([A-Za-z]{3})([0-9A-Fa-f]{4})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    static readonly Regex _guidSuffix = new(
        @"#\{[0-9A-Fa-f\-]{36}\}$",
        RegexOptions.Compiled);

    /// <summary>
    /// Parses a GUID_DEVINTERFACE_MONITOR device path into a <see cref="ParsedDevice"/>.
    /// Vid = 3-char EDID manufacturer code (e.g. "SAM"), Pid = 4-char product code (e.g. "0F91").
    /// If the path does not match the DISPLAY#XXX0000 EDID pattern, Vid/Pid fall back to "" (the
    /// same identity-less sentinel <see cref="UsbActions.ParsePartialDevice"/> uses) rather than
    /// returning null - an unparseable monitor must still reach policy evaluation so whitelist
    /// mode fails closed on it instead of it being silently invisible to the compliance sweep.
    /// Only the fully-degenerate case (no instance id survives at all) still returns null.
    /// </summary>
    public static ParsedDevice? ParseMonitorPath(string rawPath)
    {
        var match = _monitorModel.Match(rawPath);

        string working = rawPath.StartsWith(@"\\?\") ? rawPath[4..] : rawPath;
        working = _guidSuffix.Replace(working, "").Replace('#', '\\');
        if (working.Length == 0) return null;

        if (!match.Success)
            return new ParsedDevice("", "", null, MonitorClassGuid, null, DeviceKind.Monitor, null, working, rawPath);

        string vid = match.Groups[1].Value.ToUpperInvariant();
        string pid = match.Groups[2].Value.ToUpperInvariant();

        return new ParsedDevice(vid, pid, null, MonitorClassGuid, null, DeviceKind.Monitor, null, working, rawPath);
    }

    /// <summary>
    /// Deactivates all non-internal display paths, compacts the mode array, and applies
    /// the configuration while saving it to the Windows display database so the setting
    /// persists when the monitor re-enumerates.
    /// </summary>
    public static (bool ok, string? error) DisableExternalDisplays()
    {
        int result = NativeMethods.GetDisplayConfigBufferSizes(
            NativeMethods.QDC_ONLY_ACTIVE_PATHS, out uint numPaths, out uint numModes);
        if (result != 0)
            return (false, $"GetDisplayConfigBufferSizes failed: 0x{result:X}");

        var paths = new DisplayConfigPathInfo[numPaths];
        var modes = new DisplayConfigModeInfo[numModes];

        result = NativeMethods.QueryDisplayConfig(
            NativeMethods.QDC_ONLY_ACTIVE_PATHS,
            ref numPaths, paths, ref numModes, modes, IntPtr.Zero);
        if (result != 0)
            return (false, $"QueryDisplayConfig failed: 0x{result:X}");

        // Deactivate every path whose target is not an embedded panel
        bool anyExternal        = false;
        bool anyInternalActive  = false;
        for (int i = 0; i < (int)numPaths; i++)
        {
            if (paths[i].targetInfo.outputTechnology == NativeMethods.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INTERNAL)
            {
                anyInternalActive = true;
            }
            else
            {
                paths[i].flags = 0;
                paths[i].sourceInfo.modeInfoIdx = NativeMethods.DISPLAYCONFIG_PATH_MODE_IDX_INVALID;
                paths[i].targetInfo.modeInfoIdx = NativeMethods.DISPLAYCONFIG_PATH_MODE_IDX_INVALID;
                anyExternal = true;
            }
        }
        if (!anyExternal) return (true, null);

        // "Second screen only" mode: no internal path is currently active.
        // SDC_USE_SUPPLIED_DISPLAY_CONFIG requires at least one active path, so fall back to
        // SDC_TOPOLOGY_INTERNAL which switches to the laptop screen regardless of current topology.
        if (!anyInternalActive)
        {
            result = NativeMethods.SetDisplayConfig(
                0, IntPtr.Zero, 0, IntPtr.Zero,
                NativeMethods.SDC_APPLY | NativeMethods.SDC_TOPOLOGY_INTERNAL);
            return result == 0
                ? (true, null)
                : (false, $"SetDisplayConfig(INTERNAL) failed: 0x{result:X}");
        }

        // Collect mode indices still referenced by active paths
        var usedIdx = new HashSet<uint>();
        for (int i = 0; i < (int)numPaths; i++)
        {
            if ((paths[i].flags & NativeMethods.DISPLAYCONFIG_PATH_ACTIVE) == 0) continue;
            var si = paths[i].sourceInfo.modeInfoIdx;
            var ti = paths[i].targetInfo.modeInfoIdx;
            if (si != NativeMethods.DISPLAYCONFIG_PATH_MODE_IDX_INVALID) usedIdx.Add(si);
            if (ti != NativeMethods.DISPLAYCONFIG_PATH_MODE_IDX_INVALID) usedIdx.Add(ti);
        }

        // Build compacted mode array and old→new index map
        var remap    = new Dictionary<uint, uint>();
        var newModes = new DisplayConfigModeInfo[usedIdx.Count];
        uint next = 0;
        foreach (uint old in usedIdx.OrderBy(x => x))
        {
            remap[old] = next;
            newModes[next++] = modes[old];
        }

        // Rewrite active paths to use compacted indices
        for (int i = 0; i < (int)numPaths; i++)
        {
            if ((paths[i].flags & NativeMethods.DISPLAYCONFIG_PATH_ACTIVE) == 0) continue;
            var si = paths[i].sourceInfo.modeInfoIdx;
            var ti = paths[i].targetInfo.modeInfoIdx;
            if (si != NativeMethods.DISPLAYCONFIG_PATH_MODE_IDX_INVALID)
                paths[i].sourceInfo.modeInfoIdx = remap[si];
            if (ti != NativeMethods.DISPLAYCONFIG_PATH_MODE_IDX_INVALID)
                paths[i].targetInfo.modeInfoIdx = remap[ti];
        }

        result = NativeMethods.SetDisplayConfigPaths(
            numPaths, paths, (uint)newModes.Length, newModes,
            NativeMethods.SDC_APPLY |
            NativeMethods.SDC_USE_SUPPLIED_DISPLAY_CONFIG |
            NativeMethods.SDC_SAVE_TO_DATABASE |
            NativeMethods.SDC_ALLOW_CHANGES);

        return result == 0
            ? (true, null)
            : (false, $"SetDisplayConfig failed: 0x{result:X}");
    }

    /// <summary>
    /// Restores extended desktop.
    /// </summary>
    public static (bool ok, string? error) EnableExternalDisplays()
    {
        int result = NativeMethods.SetDisplayConfig(
            0, IntPtr.Zero, 0, IntPtr.Zero,
            NativeMethods.SDC_APPLY | NativeMethods.SDC_TOPOLOGY_EXTEND);
        return result == 0
            ? (true, null)
            : (false, $"SetDisplayConfig(EXTEND) failed: 0x{result:X}");
    }

    /// <summary>
    /// Yields a <see cref="ParsedDevice"/> for every external monitor currently connected.
    /// </summary>
    public static IEnumerable<ParsedDevice> EnumerateConnected()
    {
        const uint DetailCbSize  = 8;
        uint       detailBufSize = (uint)Marshal.SizeOf<SP_DEVICE_INTERFACE_DETAIL_DATA_W>();
        Guid       guid          = MonitorGuid;

        IntPtr devInfo = NativeMethods.SetupDiGetClassDevs(
            ref guid, IntPtr.Zero, IntPtr.Zero,
            NativeMethods.DIGCF_PRESENT | NativeMethods.DIGCF_DEVICEINTERFACE);

        if (devInfo == NativeMethods.INVALID_HANDLE_VALUE) yield break;

        try
        {
            for (uint idx = 0; ; idx++)
            {
                var ifaceData = new SP_DEVICE_INTERFACE_DATA();
                ifaceData.cbSize = (uint)Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>();

                if (!NativeMethods.SetupDiEnumDeviceInterfaces(
                        devInfo, IntPtr.Zero, ref guid, idx, ref ifaceData))
                    break;

                var detail = new SP_DEVICE_INTERFACE_DETAIL_DATA_W();
                detail.cbSize = DetailCbSize;

                if (!NativeMethods.SetupDiGetDeviceInterfaceDetail(
                        devInfo, ref ifaceData, ref detail,
                        detailBufSize, out _, IntPtr.Zero))
                    continue;

                var parsed = ParseMonitorPath(detail.DevicePath);
                if (parsed is not null)
                    yield return parsed;
            }
        }
        finally
        {
            NativeMethods.SetupDiDestroyDeviceInfoList(devInfo);
        }
    }
}
