using System.Reflection;
using System.Text.Json.Serialization;

namespace DlpEndpointMonitor.Core;

[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class EmitsEventAttribute : Attribute
{
    public string EventTypeName { get; }

    public EmitsEventAttribute(EventType eventType)
    {
        var memberName = Enum.GetName(typeof(EventType), eventType)
            ?? throw new ArgumentException($"Invalid EventType: {eventType}");
        var member = typeof(EventType).GetField(memberName)!;
        EventTypeName = member.GetCustomAttribute<JsonStringEnumMemberNameAttribute>()?.Name ?? memberName;
    }
}
