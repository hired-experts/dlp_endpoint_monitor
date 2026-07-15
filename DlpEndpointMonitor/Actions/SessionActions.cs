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
    /// The single function every caller should use to answer "who is on the console right now" -
    /// the session_user_get command calls this fresh on every request. Program.cs's proactive
    /// session-change emit uses <see cref="ResolveSessionUser"/> directly instead (it already has
    /// the session id resolved and must not re-query it a second time - see that overload's doc).
    /// Ok is false only when the WTS query itself genuinely failed (e.g. a session torn down
    /// between resolving the active session and querying it) - distinct from "a session exists but
    /// no interactive user is resolved yet" (logon screen), which is Ok:true, Username:null.
    /// </summary>
    public static (bool Ok, uint? SessionId, string? Username) GetCurrentSessionUser()
        => ResolveSessionUser(GetActiveConsoleSessionId());

    /// <summary>
    /// Resolves the username owning <paramref name="sessionId"/> without re-deriving the active
    /// console session itself - callers that already have a session id in hand (Program.cs's
    /// EnsureCompanionForActiveSession) must pass it in here rather than calling
    /// <see cref="GetCurrentSessionUser"/>, so the reported SessionId can never disagree with the
    /// session id the rest of that caller's logic is acting on. <paramref name="sessionId"/> null
    /// means nobody is logged on at all (see <see cref="GetActiveConsoleSessionId"/>) - not a
    /// failure, so Ok is true. Username is formatted "DOMAIN\User" when a domain is present, bare
    /// "User" otherwise - the normal Windows display convention (see <see cref="FormatSessionUser"/>).
    /// </summary>
    public static (bool Ok, uint? SessionId, string? Username) ResolveSessionUser(uint? sessionId)
    {
        if (sessionId is null) return (true, null, null);

        // WTSUserName is the query whose success/failure Ok reflects - a genuine WTS failure
        // (session torn down mid-query) returns null here, distinct from a session that resolved
        // fine but has no interactive user yet (logon screen), which returns "" (empty, not null).
        string? username = QuerySessionInfoString(sessionId.Value, NativeMethods.WTSUserName);
        if (username is null) return (false, sessionId, null);

        // A domain-lookup failure is a much lower-stakes degradation (lose the "DOMAIN\" prefix,
        // not the whole answer) - Ok still reflects only the primary username query above.
        string? domain = QuerySessionInfoString(sessionId.Value, NativeMethods.WTSDomainName);
        return (true, sessionId, FormatSessionUser(domain, username));
    }

    /// <summary>
    /// Pure formatting rule, factored out for direct unit testing (matches this codebase's
    /// existing precedent of extracting a pure decision core - e.g. UsbMonitor.DecideGroupBlock -
    /// out of a Win32-calling method): empty username means "no interactive user resolved yet",
    /// reported as null regardless of what domain came back; a present username is combined with
    /// its domain when there is one, or reported bare otherwise.
    /// </summary>
    internal static string? FormatSessionUser(string? domain, string? username)
    {
        if (string.IsNullOrEmpty(username)) return null;
        return string.IsNullOrEmpty(domain) ? username : $"{domain}\\{username}";
    }

    // CharSet.Unicode is required here, not optional: WTSQuerySessionInformation has no
    // undecorated exported symbol, so an unmarked [DllImport] defaults to CharSet.Ansi and binds
    // to WTSQuerySessionInformationA - which returns an ANSI (single-byte) buffer that
    // Marshal.PtrToStringUni (below) would then misread as UTF-16, corrupting every username. See
    // NativeMethods.cs's own WTSQuerySessionInformation declaration - this must stay in sync with it.
    static string? QuerySessionInfoString(uint sessionId, int wtsInfoClass)
    {
        if (!NativeMethods.WTSQuerySessionInformation(IntPtr.Zero, sessionId, wtsInfoClass, out IntPtr buffer, out _))
            return null;

        try
        {
            return Marshal.PtrToStringUni(buffer);
        }
        finally
        {
            NativeMethods.WTSFreeMemory(buffer);
        }
    }

    /// <summary>
    /// Kills any process at <paramref name="exePath"/> already running in
    /// <paramref name="sessionId"/> (excluding this process itself). Called both just before
    /// launching a fresh companion (so a restart replaces rather than accumulates) and from this
    /// primary's own shutdown paths (so an uninstall/service-stop/`shutdown` command doesn't leave
    /// today's companion behind) - a lingering companion otherwise keeps independently enforcing
    /// whatever policy was current at ITS OWN startup (see AGENTS.md section 10). Best-effort by
    /// design: a failure to enumerate or kill any one process must never block the caller's next
    /// step.
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
    /// Kills any <c>DlpEndpointMonitor.AlertHost.exe</c> already running in
    /// <paramref name="sessionId"/> - the same class of gap <see cref="TerminateCompanionProcesses"/>
    /// closes for the companion, extended to the other spawned child this binary never previously
    /// reaped (see AGENTS.md section 10). AlertHost's exe path is a fixed constant (it always lives
    /// alongside the primary), unlike the companion's caller-supplied <paramref name="exePath"/>
    /// above, so this just resolves that path and delegates the kill loop to
    /// <see cref="TerminateCompanionProcesses"/> rather than re-implementing it.
    /// </summary>
    public static int TerminateStaleAlertHost(uint sessionId)
    {
        string exePath = Path.Combine(AppContext.BaseDirectory, "DlpEndpointMonitor.AlertHost.exe");
        return TerminateCompanionProcesses(sessionId, exePath);
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
