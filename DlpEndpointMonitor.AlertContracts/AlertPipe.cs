namespace DlpEndpointMonitor.AlertContracts;

/// <summary>
/// The shared named-pipe identifier used to deliver alerts to an interactive session's AlertHost
/// singleton - defined once here so DlpEndpointMonitor (client) and AlertHost (server) never
/// hand-type the same string twice and risk it drifting apart. Must be scoped per session id,
/// not a single machine-wide name: unlike the "Global\" vs unprefixed Mutex-name convention that
/// correctly isolates AlertHost's own singleton mutex per session, named pipes have no such
/// session isolation - they all live in one machine-wide kernel namespace (\Device\NamedPipe\).
/// A fixed, unscoped name let a stale AlertHost left running in a disconnected session (e.g.
/// after Fast User Switching) silently claim the pipe and swallow alerts meant for the session
/// that is actually active now, so the caller's send-to-owner attempt "succeeded" against a
/// window nobody could ever see.
/// </summary>
public static class AlertPipe
{
    const string BaseName = "DlpEndpointMonitor.Alerts";

    public static string NameFor(uint sessionId) => $"{BaseName}.{sessionId}";
}
