using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using DlpEndpointMonitor.AlertContracts;

namespace DlpEndpointMonitor.AlertHost;

// Flat counterpart to DlpEndpointMonitor/Core/SchemaExporter.cs - that one needs discriminant
// injection and a cmdReply map because it covers ~20+ discriminated command/event types; this
// project has exactly one wire shape (AlertRequest) with no discriminant field, so none of that
// machinery applies here.
public static class SchemaExporter
{
    static readonly JsonSchemaExporterOptions ExporterOptions = new()
    {
        TreatNullObliviousAsNonNullable = true,
    };

    static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    [System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode("SchemaExporter uses reflection-based JSON schema generation. Only run via --schema flag, never in trimmed builds.")]
    public static void Export(TextWriter output)
    {
        var defs = new JsonObject
        {
            [nameof(AlertRequest)]  = JsonSchemaExporter.GetJsonSchemaAsNode(AlertJsonContext.Default.Options, typeof(AlertRequest), ExporterOptions),
            [nameof(AlertType)]     = JsonSchemaExporter.GetJsonSchemaAsNode(AlertJsonContext.Default.Options, typeof(AlertType), ExporterOptions),
            [nameof(AlertSeverity)] = JsonSchemaExporter.GetJsonSchemaAsNode(AlertJsonContext.Default.Options, typeof(AlertSeverity), ExporterOptions),
        };

        var root = new JsonObject
        {
            ["$schema"] = "http://json-schema.org/draft-07/schema#",
            ["$defs"]   = defs,
        };

        output.WriteLine(root.ToJsonString(WriteOptions));
    }
}
