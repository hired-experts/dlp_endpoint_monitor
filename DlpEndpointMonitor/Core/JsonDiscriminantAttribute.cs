using System.Reflection;
using System.Text.Json.Serialization;

namespace DlpEndpointMonitor.Core;

[AttributeUsage(AttributeTargets.Class, Inherited = true, AllowMultiple = true)]
public sealed class JsonDiscriminantAttribute : Attribute
{
    // Maps each enum type to the JSON field it discriminates.
    static readonly Dictionary<Type, string> FieldByEnumType = new()
    {
        [typeof(EventType)]    = "type",
        [typeof(CommandType)]  = "cmd",
        [typeof(ClipboardKind)] = "kind",
    };

    public string Field { get; }
    public string Value { get; }

    public JsonDiscriminantAttribute(object enumValue)
    {
        var enumType = enumValue.GetType();
        if (!FieldByEnumType.TryGetValue(enumType, out var field))
            throw new ArgumentException($"No field mapping for enum type {enumType.Name}");
        Field = field;

        var memberName = Enum.GetName(enumType, enumValue)
            ?? throw new ArgumentException($"Invalid enum value: {enumValue}");
        var member = enumType.GetField(memberName)!;
        Value = member.GetCustomAttribute<JsonStringEnumMemberNameAttribute>()?.Name ?? memberName;
    }
}
