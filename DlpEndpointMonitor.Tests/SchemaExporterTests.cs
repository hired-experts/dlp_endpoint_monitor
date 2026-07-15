using System.Reflection;
using System.Text.Json;
using DlpEndpointMonitor.Commands;
using DlpEndpointMonitor.Core;
using Xunit;

namespace DlpEndpointMonitor.Tests;

public class SchemaExporterTests
{
    // Runs the real export once per test and hands back the parsed root object,
    // since every case here inspects a different facet of the same output.
    static JsonDocument RunExport()
    {
        var writer = new StringWriter();
        SchemaExporter.Export(writer);
        return JsonDocument.Parse(writer.ToString());
    }

    // Wire-format string for an enum member, via the same attribute the production
    // code uses to derive discriminant values (JsonStringEnumMemberName, falling back
    // to the member name) - avoids hand-duplicating the naming policy.
    static string WireName(Enum value)
    {
        var member = value.GetType().GetField(value.ToString())!;
        var attr = member.GetCustomAttribute<System.Text.Json.Serialization.JsonStringEnumMemberNameAttribute>();
        return attr?.Name ?? value.ToString();
    }

    // T-SCHEMA-01: Export output is valid JSON.
    [Fact]
    public void Export_ProducesValidJson()
    {
        using var doc = RunExport();
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
        Assert.True(doc.RootElement.TryGetProperty("$defs", out _));
        Assert.True(doc.RootElement.TryGetProperty("cmdReply", out _));
    }

    // Collects, for every $defs entry, the const value of a given discriminant field
    // (e.g. "cmd" or "type") if that entry's schema declares one.
    static HashSet<string> DiscriminantValuesInDefs(JsonElement defs, string field)
    {
        var found = new HashSet<string>();
        foreach (var def in defs.EnumerateObject())
        {
            if (def.Value.TryGetProperty("properties", out var props) &&
                props.TryGetProperty(field, out var fieldSchema) &&
                fieldSchema.TryGetProperty("const", out var constVal) &&
                constVal.ValueKind == JsonValueKind.String)
            {
                found.Add(constVal.GetString()!);
            }
        }
        return found;
    }

    // T-SCHEMA-02: every CommandType member has a $defs entry reachable from some
    // ICommand type, i.e. some $defs schema declares "cmd" discriminated to that value.
    [Fact]
    public void Export_EveryCommandTypeMember_HasReachableDefsEntry()
    {
        using var doc = RunExport();
        var defs = doc.RootElement.GetProperty("$defs");
        var covered = DiscriminantValuesInDefs(defs, "cmd");

        var expected = Enum.GetValues<CommandType>().Select(v => WireName(v));
        foreach (var wireName in expected)
            Assert.Contains(wireName, covered);
    }

    // T-SCHEMA-03: every EventType member has a $defs entry reachable from some
    // IEvent type ("type" discriminant), including values only reachable via an
    // inherited discriminant (e.g. ClipboardChange on the abstract base, exposed
    // through concrete ClipboardTextEvent/ClipboardFilesEvent/etc.).
    [Fact]
    public void Export_EveryEventTypeMember_HasReachableDefsEntry()
    {
        using var doc = RunExport();
        var defs = doc.RootElement.GetProperty("$defs");
        var covered = DiscriminantValuesInDefs(defs, "type");

        var expected = Enum.GetValues<EventType>().Select(v => WireName(v));
        foreach (var wireName in expected)
            Assert.Contains(wireName, covered);
    }

    // T-SCHEMA-04: cmdReply contains an entry for every command carrying [EmitsEvent],
    // and no others. List read directly from Commands/Commands.cs: ClipboardReadCmd,
    // UsbStorageStatusCmd, DeviceProtectionStatusCmd, DeviceWhitelistGetCmd, DeviceBlacklistGetCmd,
    // plus (added alongside clipboard protection) ClipboardProtectionStatusCmd,
    // ClipboardWhitelistGetCmd, ClipboardBlacklistGetCmd, plus (added alongside screenshot-block
    // protection) ScreenshotBlockStatusCmd, plus (added alongside the session-user event)
    // SessionUserGetCmd.
    [Fact]
    public void Export_CmdReply_MatchesEmitsEventAttributeUsageExactly()
    {
        using var doc = RunExport();
        var cmdReply = doc.RootElement.GetProperty("cmdReply");

        var actualKeys = cmdReply.EnumerateObject().Select(p => p.Name).ToHashSet();

        var expectedKeys = new HashSet<string>
        {
            WireName(CommandType.ClipboardRead),
            WireName(CommandType.UsbStorageStatus),
            WireName(CommandType.DeviceProtectionStatus),
            WireName(CommandType.DeviceWhitelistGet),
            WireName(CommandType.DeviceBlacklistGet),
            WireName(CommandType.ClipboardProtectionStatus),
            WireName(CommandType.ClipboardWhitelistGet),
            WireName(CommandType.ClipboardBlacklistGet),
            WireName(CommandType.ScreenshotBlockStatus),
            WireName(CommandType.SessionUserGet),
        };

        Assert.Equal(expectedKeys, actualKeys);

        // Spot-check the mapped event names line up with the commands' [EmitsEvent] argument.
        Assert.Equal(WireName(EventType.ClipboardRead), cmdReply.GetProperty(WireName(CommandType.ClipboardRead)).GetString());
        Assert.Equal(WireName(EventType.UsbStorageStatus), cmdReply.GetProperty(WireName(CommandType.UsbStorageStatus)).GetString());
        Assert.Equal(WireName(EventType.DeviceProtectionStatus), cmdReply.GetProperty(WireName(CommandType.DeviceProtectionStatus)).GetString());
        Assert.Equal(WireName(EventType.DeviceWhitelistGet), cmdReply.GetProperty(WireName(CommandType.DeviceWhitelistGet)).GetString());
        Assert.Equal(WireName(EventType.DeviceBlacklistGet), cmdReply.GetProperty(WireName(CommandType.DeviceBlacklistGet)).GetString());
        Assert.Equal(WireName(EventType.ClipboardProtectionStatus), cmdReply.GetProperty(WireName(CommandType.ClipboardProtectionStatus)).GetString());
        Assert.Equal(WireName(EventType.ClipboardWhitelistGet), cmdReply.GetProperty(WireName(CommandType.ClipboardWhitelistGet)).GetString());
        Assert.Equal(WireName(EventType.ClipboardBlacklistGet), cmdReply.GetProperty(WireName(CommandType.ClipboardBlacklistGet)).GetString());
        Assert.Equal(WireName(EventType.ScreenshotBlockStatus), cmdReply.GetProperty(WireName(CommandType.ScreenshotBlockStatus)).GetString());
        Assert.Equal(WireName(EventType.SessionUserChanged), cmdReply.GetProperty(WireName(CommandType.SessionUserGet)).GetString());
    }

    // T-SCHEMA-04 (regression half): confirm every command WITHOUT [EmitsEvent] really
    // is absent from cmdReply, guarding against the attribute being added without this
    // test's expected set being updated (or vice versa).
    [Fact]
    public void Export_CmdReply_OmitsCommandsWithoutEmitsEvent()
    {
        using var doc = RunExport();
        var cmdReply = doc.RootElement.GetProperty("cmdReply");
        var actualKeys = cmdReply.EnumerateObject().Select(p => p.Name).ToHashSet();

        var commandsWithoutEmitsEvent = typeof(ICommand).Assembly.GetTypes()
            .Where(t => typeof(ICommand).IsAssignableFrom(t) && t.IsClass && !t.IsAbstract)
            .Where(t => t.GetCustomAttribute<EmitsEventAttribute>() is null)
            .Select(t => t.GetCustomAttribute<JsonDiscriminantAttribute>()?.Value)
            .OfType<string>();

        foreach (var wireName in commandsWithoutEmitsEvent)
            Assert.DoesNotContain(wireName, actualKeys);
    }

    // T-SCHEMA-05: every discriminated record's schema marks its discriminant field(s)
    // required, with the matching const value - drives TransformSchemaNode's behavior.
    [Fact]
    public void Export_DiscriminatedDefs_HaveRequiredConstDiscriminantFields()
    {
        using var doc = RunExport();
        var defs = doc.RootElement.GetProperty("$defs");

        var discriminatedTypes = typeof(ICommand).Assembly.GetTypes()
            .Where(t => (typeof(ICommand).IsAssignableFrom(t) || typeof(IEvent).IsAssignableFrom(t))
                        && t.IsClass && !t.IsAbstract)
            .Where(t => t.GetCustomAttributes<JsonDiscriminantAttribute>(inherit: true).Any());

        bool sawAny = false;
        foreach (var type in discriminatedTypes)
        {
            if (!defs.TryGetProperty(type.Name, out var schema)) continue; // shared/base types not in $defs under their own name
            sawAny = true;

            var required = schema.TryGetProperty("required", out var reqArr)
                ? reqArr.EnumerateArray().Select(e => e.GetString()).ToHashSet()
                : [];
            var props = schema.GetProperty("properties");

            foreach (var attr in type.GetCustomAttributes<JsonDiscriminantAttribute>(inherit: true))
            {
                Assert.Contains(attr.Field, required);
                Assert.Equal(attr.Value, props.GetProperty(attr.Field).GetProperty("const").GetString());
            }
        }

        Assert.True(sawAny, "Expected at least one discriminated type to be checked.");
    }
}
