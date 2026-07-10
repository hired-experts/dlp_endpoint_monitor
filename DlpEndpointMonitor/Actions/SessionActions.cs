using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using DlpEndpointMonitor.Win32;

namespace DlpEndpointMonitor.Actions;

/// <summary>
/// Stateless Win32 wrapper for the interactive-session machinery a headless (Session 0)
/// process needs to reach the logged-on user's desktop: which session owns the console, whether
/// THIS process already lives there, and launching a child process into it via
/// WTSQueryUserToken + CreateProcessAsUser. No policy logic; every expected failure path returns
/// (false, "&lt;reason&gt;"), matching every other Actions/* method in this codebase.
/// </summary>
static class SessionActions
{
    /// <summary>
    /// The session id currently attached to the physical console, or null when there is none
    /// (WTSGetActiveConsoleSessionId returns 0xFFFFFFFF when no user is logged on / at a fast-user-
    /// switch transition). Returning null keeps the raw sentinel out of every caller.
    /// </summary>
    public static uint? GetActiveConsoleSessionId()
    {
        uint sessionId = NativeMethods.WTSGetActiveConsoleSessionId();
        return sessionId == unchecked((uint)-1) ? null : sessionId;
    }

    /// <summary>
    /// True when this process is already running in <paramref name="sessionId"/> - in which case
    /// no token-duplication dance is needed and a child can be launched directly.
    /// </summary>
    public static bool IsRunningInSession(uint sessionId)
        => (uint)Process.GetCurrentProcess().SessionId == sessionId;

    /// <summary>
    /// Launches <paramref name="exePath"/> into a session DIFFERENT from the one this process is
    /// running in - the real deployment shape, where the main binary runs as LocalSystem in
    /// Session 0 and must reach the logged-on user's desktop in another session. Every acquired
    /// handle is released in the finally block below, regardless of which step failed.
    /// </summary>
    public static (bool ok, string? error) LaunchIntoSession(uint sessionId, string exePath, string args)
    {
        IntPtr userToken = IntPtr.Zero;
        IntPtr primaryToken = IntPtr.Zero;
        IntPtr environment = IntPtr.Zero;
        PROCESS_INFORMATION procInfo = default;

        try
        {
            if (!NativeMethods.WTSQueryUserToken(sessionId, out userToken))
                return (false, $"WTSQueryUserToken failed: 0x{Marshal.GetLastWin32Error():X}");

            if (!NativeMethods.DuplicateTokenEx(
                    userToken, NativeMethods.MAXIMUM_ALLOWED, IntPtr.Zero,
                    NativeMethods.SECURITY_IMPERSONATION_LEVEL, NativeMethods.TOKEN_TYPE_PRIMARY,
                    out primaryToken))
                return (false, $"DuplicateTokenEx failed: 0x{Marshal.GetLastWin32Error():X}");

            if (!NativeMethods.CreateEnvironmentBlock(out environment, primaryToken, false))
                return (false, $"CreateEnvironmentBlock failed: 0x{Marshal.GetLastWin32Error():X}");

            var startupInfo = new STARTUPINFO
            {
                cb = Marshal.SizeOf<STARTUPINFO>(),
                // Without this, CreateProcessAsUser creates the process on a non-interactive
                // window station and its window is never visible to the user - the single most
                // common real-world bug in this exact pattern.
                lpDesktop = "winsta0\\default",
            };

            // CreateProcessW may write into this buffer, so it must be a mutable StringBuilder,
            // never a plain immutable string - a real, documented Win32 marshaling hazard.
            var commandLine = new StringBuilder($"\"{exePath}\" {args}");

            bool created = NativeMethods.CreateProcessAsUser(
                primaryToken, null, commandLine, IntPtr.Zero, IntPtr.Zero, false,
                NativeMethods.CREATE_UNICODE_ENVIRONMENT, environment, null,
                ref startupInfo, out procInfo);

            return created
                ? (true, null)
                : (false, $"CreateProcessAsUser failed: 0x{Marshal.GetLastWin32Error():X}");
        }
        finally
        {
            if (procInfo.hThread != IntPtr.Zero) NativeMethods.CloseHandle(procInfo.hThread);
            if (procInfo.hProcess != IntPtr.Zero) NativeMethods.CloseHandle(procInfo.hProcess);
            if (environment != IntPtr.Zero) NativeMethods.DestroyEnvironmentBlock(environment);
            if (primaryToken != IntPtr.Zero) NativeMethods.CloseHandle(primaryToken);
            if (userToken != IntPtr.Zero) NativeMethods.CloseHandle(userToken);
        }
    }
}
