namespace DlpEndpointMonitor.AlertContracts;

/// <summary>
/// The one shared named-pipe identifier used to deliver alerts to the interactive session's
/// AlertHost singleton - defined once here so DlpEndpointMonitor (client) and AlertHost
/// (server) never hand-type the same string twice and risk it drifting apart.
/// </summary>
public static class AlertPipe
{
    public const string Name = "DlpEndpointMonitor.Alerts";
}
