using System.Runtime.InteropServices;
using System.Windows;

namespace DlpEndpointMonitor.AlertHost;

/// <summary>
/// Enumerates every connected monitor so alert windows can be shown on all of them, not just the
/// primary one <see cref="SystemParameters"/> alone can see. Uses <c>EnumDisplayMonitors</c>/
/// <c>GetMonitorInfo</c> directly rather than <c>System.Windows.Forms.Screen.AllScreens</c> -
/// referencing WinForms just for that one API pulls in a global <c>System.Windows.Forms</c>
/// using that collides project-wide with WPF's own <c>Application</c>/<c>Brush</c>/
/// <c>UserControl</c> types (confirmed: it breaks the build with CS0104 ambiguous-reference
/// errors across unrelated files), so plain P/Invoke is the simpler, dependency-free choice here.
/// </summary>
static class MonitorHelper
{
    public readonly record struct MonitorInfo(Rect Bounds, Rect WorkArea);

    [StructLayout(LayoutKind.Sequential)]
    struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct POINT
    {
        public int X, Y;
    }

    delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll")]
    static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll")]
    static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    const uint MonitorInfoFPrimary = 0x1;
    const uint MonitorDefaultToPrimary = 0x1;

    /// <summary>
    /// Returns each monitor's full bounds (for FullScreenWindow) and work area excluding the
    /// taskbar (for ToastWindow), in WPF device-independent units. This app runs without
    /// per-monitor DPI awareness (same as its existing SystemParameters.Primary* usage), so WPF
    /// scales the whole app uniformly using the primary monitor's DPI - converting every
    /// monitor's raw pixel rects through that SAME single ratio (rather than querying each
    /// monitor's own DPI) is what actually matches how Windows will render these windows.
    /// </summary>
    public static IReadOnlyList<MonitorInfo> GetAll()
    {
        var raw = new List<(RECT Bounds, RECT Work, bool IsPrimary)>();

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr _, ref RECT _, IntPtr _) =>
        {
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(hMonitor, ref info))
                raw.Add((info.rcMonitor, info.rcWork, (info.dwFlags & MonitorInfoFPrimary) != 0));
            return true;
        }, IntPtr.Zero);

        if (raw.Count == 0)
        {
            // EnumDisplayMonitors itself failed - fall back to the single primary-screen rect
            // this app already knew how to show alerts on before multi-monitor support existed,
            // rather than showing no alert window anywhere.
            return
            [
                new MonitorInfo(
                    new Rect(0, 0, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight),
                    SystemParameters.WorkArea)
            ];
        }

        // Resolve the true primary monitor directly via MonitorFromPoint(MONITOR_DEFAULTTOPRIMARY)
        // rather than searching for an IsPrimary flag inside `raw` - that flag is only set for
        // monitors EnumDisplayMonitors AND GetMonitorInfo both succeeded for, so if the real
        // primary's own GetMonitorInfo call happened to fail (e.g. racing a hot-unplug/mode
        // change), the old search silently fell through to `raw[0]`, an arbitrary non-primary
        // monitor with no guaranteed size relationship to SystemParameters.PrimaryScreenWidth -
        // corrupting the one scale factor applied to every monitor's bounds below. This call
        // always returns a valid handle (MONITOR_DEFAULTTOPRIMARY never returns null), decoupling
        // the scale reference entirely from whether the enumeration loop above happened to
        // succeed for that specific handle.
        double scale = 1.0;
        IntPtr primaryHandle = MonitorFromPoint(default, MonitorDefaultToPrimary);
        var primaryInfo = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (primaryHandle != IntPtr.Zero && GetMonitorInfo(primaryHandle, ref primaryInfo))
        {
            int primaryWidth = primaryInfo.rcMonitor.Right - primaryInfo.rcMonitor.Left;
            if (primaryWidth != 0)
                scale = SystemParameters.PrimaryScreenWidth / primaryWidth;
        }

        var result = new List<MonitorInfo>(raw.Count);
        foreach ((RECT bounds, RECT work, bool _) in raw)
            result.Add(new MonitorInfo(Scale(bounds, scale), Scale(work, scale)));

        return result;
    }

    static Rect Scale(RECT r, double scale) => new(
        r.Left * scale, r.Top * scale, (r.Right - r.Left) * scale, (r.Bottom - r.Top) * scale);
}
