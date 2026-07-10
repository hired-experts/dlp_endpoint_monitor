using System.Diagnostics;
using System.IO;
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
    /// Kills any process at <paramref name="exePath"/> already running in
    /// <paramref name="sessionId"/> (excluding this process itself). Used at TWO points in the
    /// companion lifecycle, both closing the same gap: nothing previously stopped a companion from
    /// lingering once orphaned, whether that happened because a PRIOR primary generation exited
    /// without cleaning up after itself (called again just before launching a fresh companion, so
    /// restarts replace rather than accumulate), or because THIS primary is shutting down right now
    /// (called from Program.cs's shutdown path and WindowsControlHandler's ShutdownCmd, so an
    /// uninstall/service-stop/`shutdown` command doesn't leave today's companion behind for the
    /// next start to clean up). Either way, a lingering companion still independently hooks the
    /// clipboard/keyboard with whatever policy was current at ITS OWN startup, silently enforcing
    /// stale rules alongside (or after) the primary that owned it is gone - confirmed live: a
    /// disabled clipboard-blacklist rule kept being enforced by an orphaned companion, and after an
    /// MSI uninstall the companion was left running with no primary at all. Best-effort by design:
    /// a failure to enumerate or kill any one process must never block the caller's own next step
    /// (launching a new companion, or exiting) - partial cleanup beats none.
    /// </summary>
    public static int TerminateCompanionProcesses(uint sessionId, string exePath)
    {
        int killed = 0;
        string exeName = Path.GetFileNameWithoutExtension(exePath);

        foreach (var process in Process.GetProcessesByName(exeName))
        {
            try
            {
                using (process)
                {
                    if (process.Id == Environment.ProcessId) continue;
                    if ((uint)process.SessionId != sessionId) continue;

                    // Best-effort path confirmation - a companion always shares this exe's own
                    // path, but querying another session's MainModule can itself fail with
                    // access-denied even when TerminateProcess would succeed. Only skip when the
                    // path is POSITIVELY a different executable; an inconclusive query still
                    // falls through to the kill attempt, since name + session already matched.
                    try
                    {
                        if (!string.Equals(process.MainModule?.FileName, exePath, StringComparison.OrdinalIgnoreCase))
                            continue;
                    }
                    catch { /* inconclusive - fall through to the kill attempt below */ }

                    process.Kill();
                    process.WaitForExit(2000);
                    killed++;
                }
            }
            catch
            {
                // Best-effort - a process that already exited, or one we genuinely can't
                // terminate, must not block launching the new companion.
            }
        }

        return killed;
    }

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
