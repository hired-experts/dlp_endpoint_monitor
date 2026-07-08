using DlpEndpointMonitor.Actions;
using System.Text.Json;


namespace DlpEndpointMonitor.Core;

static class EventEmitter
{
    static readonly Lock _lock = new();

    public static void Emit(IEvent payload)
    {
        var typeInfo = AppJsonContext.Default.GetTypeInfo(payload.GetType());

        string json = typeInfo == null
            ? JsonSerializer.Serialize(new ErrorEvent("event_emit", $"Type {payload.GetType().Name} is not registered in AppJsonContext.", Ts()), AppJsonContext.Default.ErrorEvent)
            : JsonSerializer.Serialize(payload, typeInfo);

        lock (_lock)
        {
            Console.WriteLine(json);
            Console.Out.Flush();
        }
    }

    public static void EmitError(string source, string message) =>
        Emit(new ErrorEvent(source, message, Ts()));

    public static void EmitInfo(string message) =>
        Emit(new InfoEvent(message, Ts()));

    public static long Ts() =>
        DateTimeOffset.UtcNow.ToUnixTimeSeconds();
}


public interface IEvent { }

#region Replies
[JsonDiscriminant(EventType.Error)]
public record ErrorEvent(string Source, string Message, long Ts) : IEvent
{
    public EventType Type => EventType.Error;
}

[JsonDiscriminant(EventType.Info)]
public record InfoEvent(string Message, long Ts) : IEvent
{
    public EventType Type => EventType.Info;
}

[JsonDiscriminant(EventType.Reply)]
public record ReplyEvent(string? Id, bool Ok, string? Error = null) : IEvent
{
    public EventType Type => EventType.Reply;
}
#endregion

#region Clipboard
[JsonDiscriminant(EventType.ClipboardRead)]
public record ClipboardReadEvent(string? Id, bool Ok, ClipboardContent? Content) : IEvent
{
    public EventType Type => EventType.ClipboardRead;
}

[JsonDiscriminant(EventType.ClipboardChange)]
public abstract record ClipboardChangeEvent(string Operation, ClipboardKind Kind, long Ts) : IEvent
{
    public EventType Type => EventType.ClipboardChange;
}

[JsonDiscriminant(ClipboardKind.Text)]
public record ClipboardTextEvent(string Operation, string? Content, long Ts)
    : ClipboardChangeEvent(Operation, ClipboardKind.Text, Ts);

[JsonDiscriminant(ClipboardKind.Files)]
public record ClipboardFilesEvent(string Operation, string[]? Files, long Ts)
    : ClipboardChangeEvent(Operation, ClipboardKind.Files, Ts);

[JsonDiscriminant(ClipboardKind.Image)]
public record ClipboardImageEvent(long Ts)
    : ClipboardChangeEvent("copy", ClipboardKind.Image, Ts);

[JsonDiscriminant(ClipboardKind.Unknown)]
public record ClipboardUnknownEvent(long Ts)
    : ClipboardChangeEvent("copy", ClipboardKind.Unknown, Ts);

// Deliberately no ProtectionMode/Conflict field here, unlike DeviceProtectionStatusEvent -
// both clipboard lists being enabled at once is a valid state, not a conflict to guard against.
[JsonDiscriminant(EventType.ClipboardProtectionStatus)]
public record ClipboardProtectionStatusEvent(string? Id, bool Ok, bool WhitelistEnabled, bool BlacklistEnabled) : IEvent
{
    public EventType Type => EventType.ClipboardProtectionStatus;
}

public record ClipboardRuleEntryDto(string Pattern, ClipboardKind? Kind, string? Label);

[JsonDiscriminant(EventType.ClipboardWhitelistGet)]
public record ClipboardWhitelistGetEvent(string? Id, bool Ok, bool Enabled, IEnumerable<ClipboardRuleEntryDto> Entries) : IEvent
{
    public EventType Type => EventType.ClipboardWhitelistGet;
}

[JsonDiscriminant(EventType.ClipboardBlacklistGet)]
public record ClipboardBlacklistGetEvent(string? Id, bool Ok, bool Enabled, IEnumerable<ClipboardRuleEntryDto> Entries) : IEvent
{
    public EventType Type => EventType.ClipboardBlacklistGet;
}

// Reason is "blacklist_match" (MatchedPattern set to the specific pattern that matched) or
// "whitelist_gate" (whitelist enabled, nothing matched it — MatchedPattern null, since there is
// no single pattern responsible for an absence of a match).
[JsonDiscriminant(EventType.ClipboardContentBlocked)]
public record ClipboardContentBlockedEvent(string Operation, ClipboardKind Kind, string Reason, string? MatchedPattern, long Ts) : IEvent
{
    public EventType Type => EventType.ClipboardContentBlocked;
}

// Emitted when the remediation action itself fails (e.g. ClipboardActions.Clear() returns false).
[JsonDiscriminant(EventType.ClipboardContentBlockFailed)]
public record ClipboardContentBlockFailedEvent(string Operation, ClipboardKind Kind, string? Error, long Ts) : IEvent
{
    public EventType Type => EventType.ClipboardContentBlockFailed;
}
#endregion

#region USB
public abstract record UsbDriveEvent(EventType Type, string[] Drives, long Ts) : IEvent;

[JsonDiscriminant(EventType.UsbDriveConnected)]
public record UsbDriveConnectedEvent(string[] Drives, long Ts)
    : UsbDriveEvent(EventType.UsbDriveConnected, Drives, Ts);

[JsonDiscriminant(EventType.UsbDriveDisconnected)]
public record UsbDriveDisconnectedEvent(string[] Drives, long Ts)
    : UsbDriveEvent(EventType.UsbDriveDisconnected, Drives, Ts);

[JsonDiscriminant(EventType.UsbDeviceDetected)]
public record UsbDeviceDetectedEvent(string? Vid, string? Pid, string? Serial, string DevicePath, int? UsbClass, DeviceKind Kind, string? NativeClass, string? GroupId, long Ts) : IEvent
{
    public EventType Type => EventType.UsbDeviceDetected;
}

[JsonDiscriminant(EventType.UsbDeviceConnected)]
public record UsbDeviceConnectedEvent(string Vid, string Pid, string? Serial, int? UsbClass, DeviceKind Kind, string? NativeClass, string? GroupId, string DevicePath, bool Allowed, long Ts) : IEvent
{
    public EventType Type => EventType.UsbDeviceConnected;
}

[JsonDiscriminant(EventType.UsbDeviceDisconnected)]
public record UsbDeviceDisconnectedEvent(string? Vid, string? Pid, string? Serial, string DevicePath, int? UsbClass, DeviceKind Kind, string? NativeClass, string? GroupId, long Ts) : IEvent
{
    public EventType Type => EventType.UsbDeviceDisconnected;
}

[JsonDiscriminant(EventType.UsbDeviceBlocked)]
public record UsbDeviceBlockedEvent(string Vid, string Pid, string? Serial, int? UsbClass, DeviceKind Kind, string? NativeClass, string? GroupId, string InstanceId, long Ts) : IEvent
{
    public EventType Type => EventType.UsbDeviceBlocked;
}

[JsonDiscriminant(EventType.UsbDeviceBlockFailed)]
public record UsbDeviceBlockFailedEvent(string Vid, string Pid, string? Serial, int? UsbClass, DeviceKind Kind, string? NativeClass, string? GroupId, string InstanceId, string? Error, long Ts) : IEvent
{
    public EventType Type => EventType.UsbDeviceBlockFailed;
}

[JsonDiscriminant(EventType.UsbDeviceUnblocked)]
public record UsbDeviceUnblockedEvent(string? Vid, string? Pid, string? Serial, DeviceKind Kind, string InstanceId, long Ts) : IEvent
{
    public EventType Type => EventType.UsbDeviceUnblocked;
}

[JsonDiscriminant(EventType.UsbDeviceUnblockFailed)]
public record UsbDeviceUnblockFailedEvent(string? Vid, string? Pid, string? Serial, DeviceKind Kind, string InstanceId, string? Error, long Ts) : IEvent
{
    public EventType Type => EventType.UsbDeviceUnblockFailed;
}

[JsonDiscriminant(EventType.UsbStorageStatus)]
public record UsbStorageStatusEvent(string? Id, bool Ok, bool Enabled) : IEvent
{
    public EventType Type => EventType.UsbStorageStatus;
}

[JsonDiscriminant(EventType.DeviceProtectionStatus)]
public record DeviceProtectionStatusEvent(string? Id, bool Ok, ProtectionMode Mode, string? Error = null) : IEvent
{
    public EventType Type => EventType.DeviceProtectionStatus;
}

public record WhitelistEntryDto(string? Vid, string? Pid, string? Serial, string? Mac, DeviceKind? Kind, string? Label);

[JsonDiscriminant(EventType.DeviceWhitelistGet)]
public record DeviceWhitelistGetEvent(string? Id, bool Ok, bool Enabled, IEnumerable<WhitelistEntryDto> Entries) : IEvent
{
    public EventType Type => EventType.DeviceWhitelistGet;
}

[JsonDiscriminant(EventType.DeviceBlacklistGet)]
public record DeviceBlacklistGetEvent(string? Id, bool Ok, bool Enabled, IEnumerable<WhitelistEntryDto> Entries) : IEvent
{
    public EventType Type => EventType.DeviceBlacklistGet;
}
#endregion

#region Display / Monitor
// Vid = EDID manufacturer code (e.g. "SAM"), Pid = EDID product code (e.g. "0F91")
[JsonDiscriminant(EventType.MonitorConnected)]
public record MonitorConnectedEvent(string? Vid, string? Pid, string DevicePath, long Ts) : IEvent
{
    public EventType Type => EventType.MonitorConnected;
}

[JsonDiscriminant(EventType.MonitorDisconnected)]
public record MonitorDisconnectedEvent(string? Vid, string? Pid, string DevicePath, long Ts) : IEvent
{
    public EventType Type => EventType.MonitorDisconnected;
}

[JsonDiscriminant(EventType.MonitorBlocked)]
public record MonitorBlockedEvent(string? Vid, string? Pid, string DevicePath, long Ts) : IEvent
{
    public EventType Type => EventType.MonitorBlocked;
}

[JsonDiscriminant(EventType.MonitorBlockFailed)]
public record MonitorBlockFailedEvent(string? Vid, string? Pid, string DevicePath, string? Error, long Ts) : IEvent
{
    public EventType Type => EventType.MonitorBlockFailed;
}
#endregion

#region Keyboard
[JsonDiscriminant(EventType.KeyboardShortcut)]
public record KeyboardShortcutEvent(string Action, long Ts) : IEvent
{
    public EventType Type => EventType.KeyboardShortcut;
}
#endregion

#region Bluetooth
[JsonDiscriminant(EventType.BluetoothDeviceConnected)]
public record BluetoothDeviceConnectedEvent(string Mac, DeviceKind Kind, string Name, bool Allowed, long Ts) : IEvent
{
    public EventType Type => EventType.BluetoothDeviceConnected;
}

[JsonDiscriminant(EventType.BluetoothDeviceDisconnected)]
public record BluetoothDeviceDisconnectedEvent(string Mac, DeviceKind Kind, string Name, long Ts) : IEvent
{
    public EventType Type => EventType.BluetoothDeviceDisconnected;
}

[JsonDiscriminant(EventType.BluetoothDeviceBlocked)]
public record BluetoothDeviceBlockedEvent(string Mac, DeviceKind Kind, string Name, long Ts) : IEvent
{
    public EventType Type => EventType.BluetoothDeviceBlocked;
}

[JsonDiscriminant(EventType.BluetoothDeviceBlockFailed)]
public record BluetoothDeviceBlockFailedEvent(string Mac, DeviceKind Kind, string Name, string? Error, long Ts) : IEvent
{
    public EventType Type => EventType.BluetoothDeviceBlockFailed;
}

[JsonDiscriminant(EventType.BluetoothDeviceUnblocked)]
public record BluetoothDeviceUnblockedEvent(string Mac, DeviceKind Kind, long Ts) : IEvent
{
    public EventType Type => EventType.BluetoothDeviceUnblocked;
}

[JsonDiscriminant(EventType.BluetoothDeviceUnblockFailed)]
public record BluetoothDeviceUnblockFailedEvent(string Mac, DeviceKind Kind, string? Error, long Ts) : IEvent
{
    public EventType Type => EventType.BluetoothDeviceUnblockFailed;
}
#endregion

#region Network
[JsonDiscriminant(EventType.NetworkDeviceConnected)]
public record NetworkDeviceConnectedEvent(string? Vid, string? Pid, string? Serial, int? UsbClass, string? NativeClass, string DevicePath, bool Allowed, long Ts) : IEvent
{
    public EventType Type => EventType.NetworkDeviceConnected;
}

[JsonDiscriminant(EventType.NetworkDeviceDisconnected)]
public record NetworkDeviceDisconnectedEvent(string? Vid, string? Pid, string? Serial, string DevicePath, int? UsbClass, string? NativeClass, long Ts) : IEvent
{
    public EventType Type => EventType.NetworkDeviceDisconnected;
}

[JsonDiscriminant(EventType.NetworkDeviceBlocked)]
public record NetworkDeviceBlockedEvent(string? Vid, string? Pid, string? Serial, string InstanceId, long Ts) : IEvent
{
    public EventType Type => EventType.NetworkDeviceBlocked;
}

[JsonDiscriminant(EventType.NetworkDeviceBlockFailed)]
public record NetworkDeviceBlockFailedEvent(string? Vid, string? Pid, string? Serial, string InstanceId, string? Error, long Ts) : IEvent
{
    public EventType Type => EventType.NetworkDeviceBlockFailed;
}

[JsonDiscriminant(EventType.NetworkDeviceUnblocked)]
public record NetworkDeviceUnblockedEvent(string? Vid, string? Pid, string? Serial, string InstanceId, long Ts) : IEvent
{
    public EventType Type => EventType.NetworkDeviceUnblocked;
}

[JsonDiscriminant(EventType.NetworkDeviceUnblockFailed)]
public record NetworkDeviceUnblockFailedEvent(string? Vid, string? Pid, string? Serial, string InstanceId, string? Error, long Ts) : IEvent
{
    public EventType Type => EventType.NetworkDeviceUnblockFailed;
}
#endregion
