using System.Text.Json.Serialization;

namespace DlpEndpointMonitor.AlertContracts;

// Public (unlike AppJsonContext/CommandsJsonContext, which are internal) - this context is
// resolved from both DlpEndpointMonitor and DlpEndpointMonitor.AlertHost, two separate
// assemblies, so it must be visible outside this one. Source-gen only, same as every other
// JsonSerializerContext in this repo - keeps this project reflection-free like the rest of
// the trimmed/self-contained build.
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AlertRequest))]
[JsonSerializable(typeof(AlertType))]
[JsonSerializable(typeof(AlertSeverity))]
public partial class AlertJsonContext : JsonSerializerContext { }
