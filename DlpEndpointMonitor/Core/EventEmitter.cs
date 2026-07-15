using DlpEndpointMonitor.Actions;
using System.Text.Json;


namespace DlpEndpointMonitor.Core;

static class EventEmitter
{
    static readonly Lock _lock = new();

    // Null by default - the primary process never sets this, so its behavior is unchanged.
    // A --session-companion instance sets it to also forward every event this process emits
    // into its own companion relay transport, without a second stdout writer.
    public static Action<string>? RawLineSink;

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

        // Outside the lock so a misbehaving sink implementation that re-enters Emit/EmitError
        // can never deadlock against itself.
        RawLineSink?.Invoke(json);
    }

    /// <summary>
    /// Writes an already-serialized JSON line straight to stdout, verbatim - no
    /// deserialize/reserialize round trip. The only other place besides <see cref="Emit"/>
    /// allowed to write to <see cref="Console.Out"/>: a companion process instance's own
    /// EventEmitter.Emit call already produced valid JSON, and relaying it through this
    /// process's stdout must not risk a second (potentially lossy) serialization pass.
    /// </summary>
    public static void EmitRawLine(string alreadySerializedJson)
    {
        lock (_lock)
        {
            Console.WriteLine(alreadySerializedJson);
            Console.Out.Flush();
        }
    }

    public static void EmitError(string source, string message) =>
        Emit(new ErrorEvent(source, message, Ts()));

    public static void EmitInfo(string message) =>
        Emit(new InfoEvent(message, Ts()));

    public static long Ts() =>
        DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    public static string NewEventId() =>
        Convert.ToBase64String(Guid.NewGuid().ToByteArray()).TrimEnd('=');
}


// EventId is a convention every event record already follows (a body-initialized
// `= EventEmitter.NewEventId()` property), promoted to a real interface member so callers can
// read another event's own EventId back polymorphically (e.g. ClipboardContentBlockedEvent's
// SourceEventId, correlating it to the ClipboardTextEvent/ClipboardFilesEvent reported moments
// earlier) without a type-switch over every concrete record. Every existing IEvent already
// declares a matching get-only `string EventId` property, so this is source-compatible.
public interface IEvent
{
    string EventId { get; }
}

#region Replies
[JsonDiscriminant(EventType.Error)]
public record ErrorEvent(string Source, string Message, long Ts) : IEvent
{
    public EventType Type => EventType.Error;
    public string EventId { get; } = EventEmitter.NewEventId();
}

[JsonDiscriminant(EventType.Info)]
public record InfoEvent(string Message, long Ts) : IEvent
{
    public EventType Type => EventType.Info;
    public string EventId { get; } = EventEmitter.NewEventId();
}

[JsonDiscriminant(EventType.Reply)]
public record ReplyEvent(string? Id, bool Ok, string? Error = null) : IEvent
{
    public EventType Type => EventType.Reply;
    public string EventId { get; } = EventEmitter.NewEventId();
    public long Ts { get; } = EventEmitter.Ts();
}
#endregion

#region Clipboard
[JsonDiscriminant(EventType.ClipboardRead)]
public record ClipboardReadEvent(string? Id, bool Ok, ClipboardContent? Content) : IEvent
{
    public EventType Type => EventType.ClipboardRead;
    public string EventId { get; } = EventEmitter.NewEventId();
    public long Ts { get; } = EventEmitter.Ts();
}

[JsonDiscriminant(EventType.ClipboardChange)]
public abstract record ClipboardChangeEvent(string Operation, ClipboardKind Kind, long Ts) : IEvent
{
    public EventType Type => EventType.ClipboardChange;
    public string EventId { get; } = EventEmitter.NewEventId();
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
    public string EventId { get; } = EventEmitter.NewEventId();
    public long Ts { get; } = EventEmitter.Ts();
}

public record ClipboardRuleEntryDto(string Pattern, ClipboardKind? Kind, string? Label);

[JsonDiscriminant(EventType.ClipboardWhitelistGet)]
public record ClipboardWhitelistGetEvent(string? Id, bool Ok, bool Enabled, IEnumerable<ClipboardRuleEntryDto> Entries) : IEvent
{
    public EventType Type => EventType.ClipboardWhitelistGet;
    public string EventId { get; } = EventEmitter.NewEventId();
    public long Ts { get; } = EventEmitter.Ts();
}

[JsonDiscriminant(EventType.ClipboardBlacklistGet)]
public record ClipboardBlacklistGetEvent(string? Id, bool Ok, bool Enabled, IEnumerable<ClipboardRuleEntryDto> Entries) : IEvent
{
    public EventType Type => EventType.ClipboardBlacklistGet;
    public string EventId { get; } = EventEmitter.NewEventId();
    public long Ts { get; } = EventEmitter.Ts();
}

// Reason is "blacklist_match" (MatchedPattern set to the specific pattern that matched) or
// "whitelist_gate" (whitelist enabled, nothing matched it — MatchedPattern null, since there is
// no single pattern responsible for an absence of a match). Deliberately carries no content of
// its own - SourceEventId is the EventId of the ClipboardTextEvent/ClipboardFilesEvent reported
// moments earlier for the same clipboard change (ClipboardMonitor.EvaluateAndEnforce/
// KeyboardHook.ShouldBlockPaste always emit that event first, unconditionally), so a consumer
// wanting to know what was actually blocked joins on that id instead of this event duplicating
// the same text/files a second time.
[JsonDiscriminant(EventType.ClipboardContentBlocked)]
public record ClipboardContentBlockedEvent(string Operation, ClipboardKind Kind, string Reason, string? MatchedPattern, string? SourceEventId, long Ts) : IEvent
{
    public EventType Type => EventType.ClipboardContentBlocked;
    public string EventId { get; } = EventEmitter.NewEventId();
}

// Emitted when the remediation action itself fails (e.g. ClipboardActions.Clear() returns false).
// Reason/MatchedPattern - see ClipboardContentBlockedEvent's own doc comment; Error is orthogonal,
// it explains why the CLEAR failed, not which policy triggered the block attempt.
// SourceEventId - see ClipboardContentBlockedEvent's own doc comment.
[JsonDiscriminant(EventType.ClipboardContentBlockFailed)]
public record ClipboardContentBlockFailedEvent(string Operation, ClipboardKind Kind, string Reason, string? MatchedPattern, string? Error, string? SourceEventId, long Ts) : IEvent
{
    public EventType Type => EventType.ClipboardContentBlockFailed;
    public string EventId { get; } = EventEmitter.NewEventId();
}
#endregion

#region USB
public abstract record UsbDriveEvent(EventType Type, string[] Drives, long Ts) : IEvent
{
    public string EventId { get; } = EventEmitter.NewEventId();
}

[JsonDiscriminant(EventType.UsbDriveConnected)]
public record UsbDriveConnectedEvent(string[] Drives, long Ts)
    : UsbDriveEvent(EventType.UsbDriveConnected, Drives, Ts);

[JsonDiscriminant(EventType.UsbDriveDisconnected)]
public record UsbDriveDisconnectedEvent(string[] Drives, long Ts)
    : UsbDriveEvent(EventType.UsbDriveDisconnected, Drives, Ts);

[JsonDiscriminant(EventType.UsbDeviceDetected)]
public record UsbDeviceDetectedEvent(string? Vid, string? Pid, string? Serial, string DevicePath, int? UsbClass, DeviceKind Kind, string? NativeClass, string? GroupId, string? SourceEventId, long Ts) : IEvent
{
    public EventType Type => EventType.UsbDeviceDetected;
    public string EventId { get; } = EventEmitter.NewEventId();
}

[JsonDiscriminant(EventType.UsbDeviceConnected)]
public record UsbDeviceConnectedEvent(string Vid, string Pid, string? Serial, int? UsbClass, DeviceKind Kind, string? NativeClass, string? GroupId, string InstanceId, string DevicePath, bool Allowed, string? SourceEventId, long Ts) : IEvent
{
    public EventType Type => EventType.UsbDeviceConnected;
    public string EventId { get; } = EventEmitter.NewEventId();
}

[JsonDiscriminant(EventType.UsbDeviceDisconnected)]
public record UsbDeviceDisconnectedEvent(string? Vid, string? Pid, string? Serial, string DevicePath, int? UsbClass, DeviceKind Kind, string? NativeClass, string? GroupId, string? InstanceId, string? SourceEventId, long Ts) : IEvent
{
    public EventType Type => EventType.UsbDeviceDisconnected;
    public string EventId { get; } = EventEmitter.NewEventId();
}

// Reason is exactly "blacklist_match" or "whitelist_gate" - which policy list caused this block,
// same convention as ClipboardContentBlockedEvent. Orthogonal to Error on the Failed sibling below,
// which explains why the enforcement ACTION failed, not which policy triggered the attempt.
[JsonDiscriminant(EventType.UsbDeviceBlocked)]
public record UsbDeviceBlockedEvent(string Vid, string Pid, string? Serial, int? UsbClass, DeviceKind Kind, string? NativeClass, string? GroupId, string InstanceId, string Reason, string? SourceEventId, long Ts) : IEvent
{
    public EventType Type => EventType.UsbDeviceBlocked;
    public string EventId { get; } = EventEmitter.NewEventId();
}

[JsonDiscriminant(EventType.UsbDeviceBlockFailed)]
public record UsbDeviceBlockFailedEvent(string Vid, string Pid, string? Serial, int? UsbClass, DeviceKind Kind, string? NativeClass, string? GroupId, string InstanceId, string Reason, string? Error, string? SourceEventId, long Ts) : IEvent
{
    public EventType Type => EventType.UsbDeviceBlockFailed;
    public string EventId { get; } = EventEmitter.NewEventId();
}

[JsonDiscriminant(EventType.UsbDeviceUnblocked)]
public record UsbDeviceUnblockedEvent(string? Vid, string? Pid, string? Serial, DeviceKind Kind, string? GroupId, string InstanceId, string? SourceEventId, long Ts) : IEvent
{
    public EventType Type => EventType.UsbDeviceUnblocked;
    public string EventId { get; } = EventEmitter.NewEventId();
}

[JsonDiscriminant(EventType.UsbDeviceUnblockFailed)]
public record UsbDeviceUnblockFailedEvent(string? Vid, string? Pid, string? Serial, DeviceKind Kind, string? GroupId, string InstanceId, string? Error, string? SourceEventId, long Ts) : IEvent
{
    public EventType Type => EventType.UsbDeviceUnblockFailed;
    public string EventId { get; } = EventEmitter.NewEventId();
}

[JsonDiscriminant(EventType.UsbStorageStatus)]
public record UsbStorageStatusEvent(string? Id, bool Ok, bool Enabled) : IEvent
{
    public EventType Type => EventType.UsbStorageStatus;
    public string EventId { get; } = EventEmitter.NewEventId();
    public long Ts { get; } = EventEmitter.Ts();
}

// Purely OBSERVATIONAL, not an enforcement action - there is no CM_Disable_DevNode call behind
// this event. Windows itself already refused to bind a storage driver before this process had
// any say (the USBSTOR service's Start value was 4 at the time this device's driver would have
// loaded), so there is no "block failed" counterpart possible - same reasoning as
// ScreenshotBlockedEvent's deliberate lack of a *_block_failed sibling. Fired by UsbMonitor as an
// independent, additive check alongside (never instead of) the normal whitelist/blacklist arrival
// evaluation - see UsbMonitor.HandleArrival and PROJECT.md section 5.7.
[JsonDiscriminant(EventType.UsbStorageBlocked)]
public record UsbStorageBlockedEvent(string? Vid, string? Pid, string? Serial, string InstanceId, long Ts) : IEvent
{
    public EventType Type => EventType.UsbStorageBlocked;
    public string EventId { get; } = EventEmitter.NewEventId();
}

// A third, distinct trigger for a USB block/block-failed pair - deliberately NOT a reuse of
// either existing type. Not UsbDeviceBlockedEvent/UsbDeviceBlockFailedEvent: those records'
// Reason field is contractually exactly "blacklist_match" or "whitelist_gate" (see their own
// doc comment above), never a third value - this fires from the usb_disable_storage kill
// switch, not a whitelist/blacklist decision. Not UsbStorageBlockedEvent (just above): that
// event is documented as purely OBSERVATIONAL with no possible failure (Windows itself refused
// to bind a driver before this process had any say) - this trigger genuinely calls
// CM_Disable_DevNode/eject against an already-mounted device and can genuinely fail, which
// would contradict that event's own contract. See UsbMonitor.BlockAlreadyConnectedStorage.
[JsonDiscriminant(EventType.UsbStorageDeviceBlocked)]
public record UsbStorageDeviceBlockedEvent(string? Vid, string? Pid, string? Serial, string InstanceId, string? GroupId, string? SourceEventId, long Ts) : IEvent
{
    public EventType Type => EventType.UsbStorageDeviceBlocked;
    public string EventId { get; } = EventEmitter.NewEventId();
}

[JsonDiscriminant(EventType.UsbStorageDeviceBlockFailed)]
public record UsbStorageDeviceBlockFailedEvent(string? Vid, string? Pid, string? Serial, string InstanceId, string? GroupId, string? Error, string? SourceEventId, long Ts) : IEvent
{
    public EventType Type => EventType.UsbStorageDeviceBlockFailed;
    public string EventId { get; } = EventEmitter.NewEventId();
}

[JsonDiscriminant(EventType.DeviceProtectionStatus)]
public record DeviceProtectionStatusEvent(string? Id, bool Ok, ProtectionMode Mode, string? Error = null) : IEvent
{
    public EventType Type => EventType.DeviceProtectionStatus;
    public string EventId { get; } = EventEmitter.NewEventId();
    public long Ts { get; } = EventEmitter.Ts();
}

public record WhitelistEntryDto(string? Vid, string? Pid, string? Serial, string? Mac, DeviceKind? Kind, string? Label);

[JsonDiscriminant(EventType.DeviceWhitelistGet)]
public record DeviceWhitelistGetEvent(string? Id, bool Ok, bool Enabled, IEnumerable<WhitelistEntryDto> Entries) : IEvent
{
    public EventType Type => EventType.DeviceWhitelistGet;
    public string EventId { get; } = EventEmitter.NewEventId();
    public long Ts { get; } = EventEmitter.Ts();
}

[JsonDiscriminant(EventType.DeviceBlacklistGet)]
public record DeviceBlacklistGetEvent(string? Id, bool Ok, bool Enabled, IEnumerable<WhitelistEntryDto> Entries) : IEvent
{
    public EventType Type => EventType.DeviceBlacklistGet;
    public string EventId { get; } = EventEmitter.NewEventId();
    public long Ts { get; } = EventEmitter.Ts();
}
#endregion

#region Display / Monitor
// Vid = EDID manufacturer code (e.g. "SAM"), Pid = EDID product code (e.g. "0F91")
[JsonDiscriminant(EventType.MonitorConnected)]
public record MonitorConnectedEvent(string? Vid, string? Pid, string DevicePath, long Ts) : IEvent
{
    public EventType Type => EventType.MonitorConnected;
    public string EventId { get; } = EventEmitter.NewEventId();
}

[JsonDiscriminant(EventType.MonitorDisconnected)]
public record MonitorDisconnectedEvent(string? Vid, string? Pid, string DevicePath, long Ts) : IEvent
{
    public EventType Type => EventType.MonitorDisconnected;
    public string EventId { get; } = EventEmitter.NewEventId();
}

// Reason - see UsbDeviceBlockedEvent's doc comment; same "blacklist_match"/"whitelist_gate" convention.
[JsonDiscriminant(EventType.MonitorBlocked)]
public record MonitorBlockedEvent(string? Vid, string? Pid, string DevicePath, string Reason, long Ts) : IEvent
{
    public EventType Type => EventType.MonitorBlocked;
    public string EventId { get; } = EventEmitter.NewEventId();
}

[JsonDiscriminant(EventType.MonitorBlockFailed)]
public record MonitorBlockFailedEvent(string? Vid, string? Pid, string DevicePath, string Reason, string? Error, long Ts) : IEvent
{
    public EventType Type => EventType.MonitorBlockFailed;
    public string EventId { get; } = EventEmitter.NewEventId();
}

// Emitted whenever WM_DISPLAYCHANGE fires and settles (a Win+P projection-mode switch, not just a
// physical plug/unplug) - Kind is which of the four topologies is now active, read via
// DisplayActions.GetCurrentTopology. Distinct from MonitorConnected/Disconnected, which report a
// single device's PnP arrival/removal, not the overall desktop topology.
[JsonDiscriminant(EventType.MonitorProjectionChanged)]
public record MonitorProjectionChangedEvent(DisplayTopology Kind, long Ts) : IEvent
{
    public EventType Type => EventType.MonitorProjectionChanged;
    public string EventId { get; } = EventEmitter.NewEventId();
}
#endregion

#region Keyboard
[JsonDiscriminant(EventType.KeyboardShortcut)]
public record KeyboardShortcutEvent(KeyboardShortcutAction Action, long Ts) : IEvent
{
    public EventType Type => EventType.KeyboardShortcut;
    public string EventId { get; } = EventEmitter.NewEventId();
}
#endregion

#region Screenshot
[JsonDiscriminant(EventType.ScreenshotBlockStatus)]
public record ScreenshotBlockStatusEvent(string? Id, bool Ok, bool Enabled) : IEvent
{
    public EventType Type => EventType.ScreenshotBlockStatus;
    public string EventId { get; } = EventEmitter.NewEventId();
    public long Ts { get; } = EventEmitter.Ts();
}

// Shortcut is one of "print_screen", "alt_print_screen", "win_alt_print_screen", "win_shift_s" -
// see KeyboardHook's screenshot-detection block. Deliberately NO ScreenshotBlockFailedEvent
// counterpart: unlike a real Win32 disable/unpair call, swallowing a keystroke means simply
// returning non-zero instead of calling CallNextHookEx - there is no failure mode for that to
// report, so there is nothing for a *_block_failed sibling to describe.
[JsonDiscriminant(EventType.ScreenshotBlocked)]
public record ScreenshotBlockedEvent(string Shortcut, long Ts) : IEvent
{
    public EventType Type => EventType.ScreenshotBlocked;
    public string EventId { get; } = EventEmitter.NewEventId();
}
#endregion

#region Bluetooth
[JsonDiscriminant(EventType.BluetoothDeviceConnected)]
public record BluetoothDeviceConnectedEvent(string Mac, DeviceKind Kind, string Name, bool Allowed, long Ts) : IEvent
{
    public EventType Type => EventType.BluetoothDeviceConnected;
    public string EventId { get; } = EventEmitter.NewEventId();
}

[JsonDiscriminant(EventType.BluetoothDeviceDisconnected)]
public record BluetoothDeviceDisconnectedEvent(string Mac, DeviceKind Kind, string Name, long Ts) : IEvent
{
    public EventType Type => EventType.BluetoothDeviceDisconnected;
    public string EventId { get; } = EventEmitter.NewEventId();
}

// Reason - see UsbDeviceBlockedEvent's doc comment; same "blacklist_match"/"whitelist_gate" convention.
[JsonDiscriminant(EventType.BluetoothDeviceBlocked)]
public record BluetoothDeviceBlockedEvent(string Mac, DeviceKind Kind, string Name, string Reason, long Ts) : IEvent
{
    public EventType Type => EventType.BluetoothDeviceBlocked;
    public string EventId { get; } = EventEmitter.NewEventId();
}

[JsonDiscriminant(EventType.BluetoothDeviceBlockFailed)]
public record BluetoothDeviceBlockFailedEvent(string Mac, DeviceKind Kind, string Name, string Reason, string? Error, long Ts) : IEvent
{
    public EventType Type => EventType.BluetoothDeviceBlockFailed;
    public string EventId { get; } = EventEmitter.NewEventId();
}

[JsonDiscriminant(EventType.BluetoothDeviceUnblocked)]
public record BluetoothDeviceUnblockedEvent(string Mac, DeviceKind Kind, long Ts) : IEvent
{
    public EventType Type => EventType.BluetoothDeviceUnblocked;
    public string EventId { get; } = EventEmitter.NewEventId();
}

[JsonDiscriminant(EventType.BluetoothDeviceUnblockFailed)]
public record BluetoothDeviceUnblockFailedEvent(string Mac, DeviceKind Kind, string? Error, long Ts) : IEvent
{
    public EventType Type => EventType.BluetoothDeviceUnblockFailed;
    public string EventId { get; } = EventEmitter.NewEventId();
}
#endregion

#region Network
[JsonDiscriminant(EventType.NetworkDeviceConnected)]
public record NetworkDeviceConnectedEvent(string? Vid, string? Pid, string? Serial, int? UsbClass, string? NativeClass, string InstanceId, string DevicePath, bool Allowed, long Ts) : IEvent
{
    public EventType Type => EventType.NetworkDeviceConnected;
    public string EventId { get; } = EventEmitter.NewEventId();
}

[JsonDiscriminant(EventType.NetworkDeviceDisconnected)]
public record NetworkDeviceDisconnectedEvent(string? Vid, string? Pid, string? Serial, string DevicePath, int? UsbClass, string? NativeClass, string? InstanceId, long Ts) : IEvent
{
    public EventType Type => EventType.NetworkDeviceDisconnected;
    public string EventId { get; } = EventEmitter.NewEventId();
}

// Reason - see UsbDeviceBlockedEvent's doc comment; same "blacklist_match"/"whitelist_gate" convention.
[JsonDiscriminant(EventType.NetworkDeviceBlocked)]
public record NetworkDeviceBlockedEvent(string? Vid, string? Pid, string? Serial, string InstanceId, string Reason, long Ts) : IEvent
{
    public EventType Type => EventType.NetworkDeviceBlocked;
    public string EventId { get; } = EventEmitter.NewEventId();
}

[JsonDiscriminant(EventType.NetworkDeviceBlockFailed)]
public record NetworkDeviceBlockFailedEvent(string? Vid, string? Pid, string? Serial, string InstanceId, string Reason, string? Error, long Ts) : IEvent
{
    public EventType Type => EventType.NetworkDeviceBlockFailed;
    public string EventId { get; } = EventEmitter.NewEventId();
}

[JsonDiscriminant(EventType.NetworkDeviceUnblocked)]
public record NetworkDeviceUnblockedEvent(string? Vid, string? Pid, string? Serial, string InstanceId, long Ts) : IEvent
{
    public EventType Type => EventType.NetworkDeviceUnblocked;
    public string EventId { get; } = EventEmitter.NewEventId();
}

[JsonDiscriminant(EventType.NetworkDeviceUnblockFailed)]
public record NetworkDeviceUnblockFailedEvent(string? Vid, string? Pid, string? Serial, string InstanceId, string? Error, long Ts) : IEvent
{
    public EventType Type => EventType.NetworkDeviceUnblockFailed;
    public string EventId { get; } = EventEmitter.NewEventId();
}
#endregion

#region Session
// Id is null when emitted proactively (startup, or a WM_WTSSESSION_CHANGE-driven session switch -
// see Program.cs's EnsureCompanionForActiveSession) and populated when emitted in reply to a
// session_user_get command. SessionId/Username are both null when no one is logged into the
// console at all - see SessionActions.GetCurrentSessionUser for why that's never reported as
// "SYSTEM"/Session 0 (Session 0 is never attachable to the console, interactive or not).
[JsonDiscriminant(EventType.SessionUserChanged)]
public record SessionUserChangedEvent(string? Id, bool Ok, uint? SessionId, string? Username) : IEvent
{
    public EventType Type => EventType.SessionUserChanged;
    public string EventId { get; } = EventEmitter.NewEventId();
    public long Ts { get; } = EventEmitter.Ts();
}
#endregion
