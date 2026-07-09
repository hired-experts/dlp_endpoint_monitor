namespace DlpEndpointMonitor.AlertContracts;

/// <param name="Id">
/// Caller-supplied correlation ID (e.g. the originating DLP event's own ID), shown in a small
/// copyable row so an operator can reference/report the exact event. Required for every alert
/// type (Toast, FullScreen) - never fabricated by AlertHost itself, since a locally-generated ID
/// wouldn't correlate to anything in the broader system's logs/dashboard, so there is no safe
/// placeholder to fall back to. Callers without a real correlating event ID have no alert to
/// raise in the first place.
/// </param>
public sealed record AlertRequest(
    AlertType Type,
    string Title,
    string Message,
    string Id,
    AlertSeverity Severity = AlertSeverity.Info,
    int DurationSeconds = 5);
