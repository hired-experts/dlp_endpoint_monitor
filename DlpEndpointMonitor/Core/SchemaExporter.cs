using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using DlpEndpointMonitor.Commands;

namespace DlpEndpointMonitor.Core;

static class SchemaExporter
{
    // Enums and shared DTOs not discoverable via IEvent/ICommand — rarely changes.
    static readonly Type[] SharedEventTypes =
    [
        typeof(EventType), typeof(DeviceKind), typeof(ClipboardKind), typeof(ProtectionMode),
        typeof(WhitelistEntryDto),
    ];

    static readonly Type[] SharedCommandTypes =
    [
        typeof(CommandType),
        typeof(DeviceEntryDto),
    ];

    static readonly JsonSchemaExporterOptions ExporterOptions = new()
    {
        TreatNullObliviousAsNonNullable = true,
        TransformSchemaNode = (context, schema) =>
        {
            var attrs = context.TypeInfo.Type
                .GetCustomAttributes<JsonDiscriminantAttribute>(inherit: true)
                .ToList();

            if (attrs.Count == 0 || schema is not JsonObject obj) return schema;

            if (obj["properties"] is not JsonObject props)
            {
                props = new JsonObject();
                obj["properties"] = props;
            }

            var required = obj["required"] as JsonArray ?? new JsonArray();
            obj["required"] = required;

            foreach (var attr in attrs)
            {
                props[attr.Field] = new JsonObject { ["const"] = attr.Value };
                if (!required.Any(n => n?.GetValue<string>() == attr.Field))
                    required.Add((JsonNode)attr.Field);
            }

            return schema;
        },
    };

    static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    [System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode("SchemaExporter uses reflection to discover IEvent/ICommand types. Only run via --schema flag, never in trimmed builds.")]
    public static void Export(TextWriter output)
    {
        var assembly = Assembly.GetExecutingAssembly();

        var eventTypes = assembly.GetTypes()
            .Where(t => typeof(IEvent).IsAssignableFrom(t) && t.IsClass && !t.IsAbstract)
            .OrderBy(t => t.Name);

        var commandTypes = assembly.GetTypes()
            .Where(t => typeof(ICommand).IsAssignableFrom(t) && t.IsClass && !t.IsAbstract)
            .OrderBy(t => t.Name);

        var defs = new JsonObject();
        var seen = new HashSet<string>();

        void Add(JsonSerializerOptions options, Type type)
        {
            if (!seen.Add(type.Name)) return;
            defs[type.Name] = JsonSchemaExporter.GetJsonSchemaAsNode(options, type, ExporterOptions);
        }

        foreach (var type in SharedEventTypes)   Add(AppJsonContext.Default.Options,      type);
        foreach (var type in eventTypes)          Add(AppJsonContext.Default.Options,      type);
        foreach (var type in SharedCommandTypes)  Add(CommandsJsonContext.Default.Options, type);
        foreach (var type in commandTypes)        Add(CommandsJsonContext.Default.Options, type);

        // cmd → event type mapping for commands that reply with something richer than ReplyEvent.
        // Commands without [EmitsEvent] are omitted — they implicitly return 'reply'.
        var cmdReply = new JsonObject();
        foreach (var type in commandTypes)
        {
            var discriminant = type.GetCustomAttribute<JsonDiscriminantAttribute>();
            var emits        = type.GetCustomAttribute<EmitsEventAttribute>();
            if (discriminant is not null && emits is not null)
                cmdReply[discriminant.Value] = emits.EventTypeName;
        }

        var root = new JsonObject
        {
            ["$schema"]  = "http://json-schema.org/draft-07/schema#",
            ["$defs"]    = defs,
            ["cmdReply"] = cmdReply,
        };

        output.WriteLine(root.ToJsonString(WriteOptions));
    }
}
