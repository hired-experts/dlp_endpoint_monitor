using System.Text.Json.Serialization;

namespace DlpEndpointMonitor.AlertContracts;

[JsonConverter(typeof(JsonStringEnumConverter<AlertSeverity>))]
public enum AlertSeverity
{
    [JsonStringEnumMemberName("info")]    Info,
    [JsonStringEnumMemberName("warning")] Warning,
    [JsonStringEnumMemberName("blocked")] Blocked
}
