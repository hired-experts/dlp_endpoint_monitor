using System.IO;
using System.Text.Json;
using Xunit;

namespace DlpEndpointMonitor.AlertHost.Tests;

// Scaled-down counterpart to DlpEndpointMonitor.Tests/SchemaExporterTests.cs - no discriminant/
// cmdReply machinery applies here since AlertRequest is the only wire shape (see
// DlpEndpointMonitor.AlertHost/SchemaExporter.cs).
public class SchemaExporterTests
{
    static JsonDocument RunExport()
    {
        var writer = new StringWriter();
        SchemaExporter.Export(writer);
        return JsonDocument.Parse(writer.ToString());
    }

    // T-SCHEMA-01: Export output is valid JSON with a $defs property.
    [Fact]
    public void Export_ProducesValidJson()
    {
        using var doc = RunExport();
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
        Assert.True(doc.RootElement.TryGetProperty("$defs", out _));
    }

    // T-SCHEMA-02: $defs carries all three wire types this project actually serializes.
    [Fact]
    public void Export_Defs_ContainsAllThreeWireTypes()
    {
        using var doc = RunExport();
        var defs = doc.RootElement.GetProperty("$defs");

        Assert.True(defs.TryGetProperty("AlertRequest", out _));
        Assert.True(defs.TryGetProperty("AlertType", out _));
        Assert.True(defs.TryGetProperty("AlertSeverity", out _));
    }

    // T-SCHEMA-03: AlertRequest's exported property names are camelCase - confirms
    // AlertJsonContext's PropertyNamingPolicy actually reaches the exported schema, not
    // just runtime serialization.
    [Fact]
    public void Export_AlertRequestSchema_HasCamelCasePropertyNames()
    {
        using var doc = RunExport();
        var props = doc.RootElement.GetProperty("$defs").GetProperty("AlertRequest").GetProperty("properties");

        foreach (var name in new[] { "type", "title", "message", "id", "severity", "durationSeconds" })
            Assert.True(props.TryGetProperty(name, out _), $"expected property '{name}' in AlertRequest schema");
    }
}
