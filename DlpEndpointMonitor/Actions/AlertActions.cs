using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using DlpEndpointMonitor.AlertContracts;
using DlpEndpointMonitor.Win32;

namespace DlpEndpointMonitor.Actions;

/// <summary>
/// The one stateless entry point this binary uses to show a UI alert. This process may be
/// running as a LocalSystem service in Session 0 (no desktop of its own), so a WPF window is
/// always shown by a separate companion process, AlertHost.exe, running in the interactive
/// user's session - this method only gets a request there, it never touches WPF itself.
/// Every failure path returns (false, "&lt;reason&gt;"), matching every other Actions/* method
/// in this codebase; nothing here throws for an expected Win32 failure.
/// </summary>
static class AlertActions
{
    const string AlertHostExeName    = "DlpEndpointMonitor.AlertHost.exe";
    const int    PipeConnectTimeoutMs = 300;
    const string InitialAlertArgPrefix = "--initial-alert=";

    /// <summary>
    /// Delivers <paramref name="request"/> to the current interactive session's AlertHost.
    /// Tries the named pipe first (an owner may already be running there); if nobody answers,
    /// launches a new AlertHost.exe with the request embedded as its first alert - directly if
    /// this process already lives in the target session, or via WTSQueryUserToken +
    /// CreateProcessAsUser if it is elevated in a different one (the real LocalSystem/Session-0
    /// deployment).
    /// </summary>
    public static (bool ok, string? error) ShowAlert(AlertRequest request)
    {
        // Id is a required correlation field (AlertRequest.Id's doc comment) - the compile-time
        // `string Id` signature does not stop a caller from passing "" or whitespace, so guard
        // here too rather than letting an uncorrelatable alert reach AlertHost.
        if (string.IsNullOrWhiteSpace(request.Id))
            return (false, "AlertRequest.Id is required and cannot be blank");

        if (TrySendToRunningOwner(request))
            return (true, null);

        string exePath = Path.Combine(AppContext.BaseDirectory, AlertHostExeName);
        if (!File.Exists(exePath))
            return (false, $"AlertHost executable not found at '{exePath}'");

        string json = JsonSerializer.Serialize(request, AlertJsonContext.Default.AlertRequest);
        string args = InitialAlertArgPrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(json));

        uint targetSession = NativeMethods.WTSGetActiveConsoleSessionId();
        if (targetSession == unchecked((uint)-1))
            return (false, "WTSGetActiveConsoleSessionId: no interactive session attached to the console");

        // Same-session fast path — the normal case when running interactively (e.g. manual
        // testing); no token duplication needed since we are already IN the target session.
        if ((uint)Process.GetCurrentProcess().SessionId == targetSession)
            return LaunchDirect(exePath, args);

        return LaunchIntoSession(targetSession, exePath, args);
    }

    // A minimal, client-only version of AlertHost's own PipeTransport.TrySendToOwner. It cannot
    // be shared directly: AlertContracts (the one project both sides may reference) is
    // deliberately kept dependency-free plain records/enums, and this binary must not take a
    // project reference on AlertHost (a WPF app) just to reuse a few lines of pipe-client code.
    static bool TrySendToRunningOwner(AlertRequest request)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", AlertPipe.Name, PipeDirection.Out);
            client.Connect(PipeConnectTimeoutMs);
            using var writer = new StreamWriter(client) { AutoFlush = true };
            writer.WriteLine(JsonSerializer.Serialize(request, AlertJsonContext.Default.AlertRequest));
            return true;
        }
        catch
        {
            // No owner currently listening on this session's pipe - not an error, it just means
            // a new AlertHost.exe needs to be launched to become the owner.
            return false;
        }
    }

    static (bool ok, string? error) LaunchDirect(string exePath, string args)
    {
        try
        {
            Process.Start(new ProcessStartInfo(exePath, args) { UseShellExecute = false });
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, $"Process.Start failed: {ex.Message}");
        }
    }

    // Launches AlertHost.exe into a session DIFFERENT from the one this process is running in -
    // the real deployment shape, where the main binary runs as LocalSystem in Session 0 and must
    // reach the logged-on user's desktop in another session. Every acquired handle is released
    // in the finally block below, regardless of which step failed.
    static (bool ok, string? error) LaunchIntoSession(uint sessionId, string exePath, string args)
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
