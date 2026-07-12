# DlpEndpointMonitor - Project Reference

This is the deep reference. `AGENTS.md` in this same folder is the short version - read
that first. Come here for exact protocol shapes, the device-blocking algorithm, and the
engineering principles behind this codebase.

---

## 1. Architecture

### 1.1 Process model

One process, one project (`DlpEndpointMonitor/DlpEndpointMonitor.csproj`), `net10.0`,
published `win-x64`, self-contained, single-file, trimmed
(`Properties/PublishProfiles/FolderProfile.pubxml` -> `bin\Release\net10.0\win-x64\publish\`).
**Zero NuGet package references** - everything is BCL + Windows P/Invoke
(`System.Text.Json` source generation, `Microsoft.Win32.Registry`,
`System.Runtime.InteropServices`).

Two logical threads matter:
- **The STA "message loop" thread**, started in `Program.cs`. It owns a hidden `MessageWindow`
  and every Win32-message-driven component: `UsbMonitor`, `BluetoothMonitor`,
  `DisplayMonitor`, `ClipboardMonitor`, `KeyboardHook`. All of these **must** live on this one
  thread because clipboard listening (`AddClipboardFormatListener`), device-change
  notifications (`RegisterDeviceNotification`), and the low-level keyboard hook
  (`SetWindowsHookEx(WH_KEYBOARD_LL, ...)`) are all thread-affine to whichever thread
  pumps messages, and Windows requires that thread to be STA.
  Caveat for the two clipboard components: `ClipboardMonitor` and `KeyboardHook` only run on
  *this* thread when the process is already in the interactive session. Under the real
  LocalSystem-service deployment the process is in Session 0 (no desktop), so `Program.cs`
  instead launches a `--clipboard-companion` copy of this exe into the logged-on user's session
  (via `Actions/SessionActions.cs`), which runs its own STA thread hosting only
  `MessageWindow` + `ClipboardMonitor` + `KeyboardHook` and relays its events back to the
  primary's stdout over `Core/ClipboardCompanionRelay`'s named pipe. In that case the primary
  builds neither locally. See AGENTS.md section 10's Session-0 companion bullet.
- **The main thread**, which waits for the message window to be ready, then builds the
  `CommandDispatcher` and awaits `RunAsync()` - the stdin read loop.

Startup enumeration (`EnumerateExisting`, `BlockNonCompliant`) for all three device
monitors is deliberately kicked off via `Task.Run` on the **ThreadPool**, not called
inline on the message thread, so a slow first enumeration never delays the message pump
from being ready to receive events.

### 1.2 Wiring (no DI container)

`Program.cs` hand-constructs everything:

```
whitelist = DeviceWhitelist()      # persisted, shared
blacklist = DeviceBlacklist()      # persisted, shared
disabled  = DisabledDevices()      # persisted, shared

# on the STA thread:
window            = MessageWindow()
usbMonitor        = UsbMonitor(window, whitelist, blacklist, disabled)
bluetoothMonitor  = BluetoothMonitor(window, whitelist, blacklist)
displayMonitor    = DisplayMonitor(window, whitelist, blacklist)
clipboardMonitor  = ClipboardMonitor(window)
keyboardHook      = KeyboardHook()

# on the main thread, after windowReady:
dispatcher = CommandDispatcher(
  clipboard:     WindowsClipboardHandler(),
  usbStorage:    WindowsUsbStorageHandler(),
  usbDevice:     WindowsUsbDeviceHandler(),
  usbProtection: WindowsUsbProtectionHandler(whitelist, blacklist,
                   applyPolicy:    () => { usbMonitor.BlockNonCompliant(); bluetoothMonitor.BlockNonCompliant(); displayMonitor.BlockNonCompliant(); },
                   restoreDevices: () => { usbMonitor.RestoreCompliant(); displayMonitor.RestoreCompliant(); }),
  control:       WindowsControlHandler())
```

The two `Action` delegates (`applyPolicy`, `restoreDevices`) are how a protection-list
mutation (on the main thread, inside a command handler) reaches back into the three
monitors (created on the other thread) without a circular constructor dependency. Note
Bluetooth has **no** restore path - removing a pairing is not something `RestoreCompliant`
can undo, so `restoreDevices` only touches USB and display.

### 1.3 The stdin/stdout protocol loop

`Core/CommandDispatcher.RunAsync()`:

```
while not cancelled:
    line = await Task.Run(Console.ReadLine, token)
    if line is null:
        emit info "stdin closed - monitoring continues"
        await Delay(Infinite)   # keep the process alive; monitors keep running
        break
    Dispatch(line)
```

`Dispatch(line)`:
1. Parse as a `JsonDocument`; read `id` and `cmd` as raw strings first (so an error reply
   can echo them even if the rest of the payload is malformed).
2. Deserialize `cmd` through the source-generated `CommandsJsonContext.Default.CommandType`
   to get a `CommandType?`. If that fails or is null, reply `{ok:false, error:"unknown
   command: <cmd>"}` and stop.
3. `switch` on `CommandType`, deserialize the full line into the matching command record
   (via the source-gen `JsonTypeInfo<T>`, never a reflection-based `JsonSerializer.Deserialize<T>()`
   call), and call the matching handler's `Handle(...)` overload.
4. Any exception anywhere in this path is caught and turned into `{ok:false, error:
   ex.Message}` - the dispatch loop itself never throws, so one bad line never kills stdin
   processing for the next line.

Every stdout line is written exclusively through `EventEmitter.Emit`, under a `lock`, one
full `Console.WriteLine` + `Flush()` per call - this is what guarantees concurrent monitor
threads (message thread + ThreadPool block/restore tasks) never interleave two JSON
objects into one malformed line.

---

## 2. Full protocol reference

### 2.1 Commands (stdin, one JSON object per line)

Every command has an optional `id` (opaque, echoed back). Table columns: wire `cmd` value,
C# record (`Commands/Commands.cs`), extra fields, what it replies with.

| `cmd` | Record | Extra fields | Replies with |
|---|---|---|---|
| `clipboard_read` | `ClipboardReadCmd` | - | `ClipboardReadEvent` (`[EmitsEvent]`) |
| `clipboard_set` | `ClipboardSetCmd` | `content: string` | `ReplyEvent` |
| `clipboard_clear` | `ClipboardClearCmd` | - | `ReplyEvent` |
| `usb_eject` | `UsbEjectCmd` | `drive: string` (e.g. `"E:\\"`) | `ReplyEvent` |
| `usb_disable_storage` | `UsbDisableStorageCmd` | - | `ReplyEvent` |
| `usb_enable_storage` | `UsbEnableStorageCmd` | - | `ReplyEvent` |
| `usb_storage_status` | `UsbStorageStatusCmd` | - | `UsbStorageStatusEvent` (`[EmitsEvent]`) |
| `device_disable` | `DeviceDisableCmd` | `instanceId: string` | `ReplyEvent` |
| `device_enable` | `DeviceEnableCmd` | `instanceId: string` | `ReplyEvent` |
| `device_protection_status` | `DeviceProtectionStatusCmd` | - | `DeviceProtectionStatusEvent` (`[EmitsEvent]`) |
| `device_whitelist_enable` | `DeviceWhitelistEnableCmd` | - | `ReplyEvent` |
| `device_whitelist_disable` | `DeviceWhitelistDisableCmd` | - | `ReplyEvent` |
| `device_whitelist_get` | `DeviceWhitelistGetCmd` | - | `DeviceWhitelistGetEvent` (`[EmitsEvent]`) |
| `device_whitelist_clear` | `DeviceWhitelistClearCmd` | - | `ReplyEvent` |
| `device_whitelist_add` | `DeviceWhitelistAddCmd` | `vid?, pid?, serial?, mac?, kind?, label?` | `ReplyEvent` |
| `device_whitelist_remove` | `DeviceWhitelistRemoveCmd` | `vid?, pid?, serial?, mac?, kind?` | `ReplyEvent` |
| `device_whitelist_set` | `DeviceWhitelistSetCmd` | `entries: DeviceEntryDto[]` | `ReplyEvent` |
| `device_blacklist_enable` | `DeviceBlacklistEnableCmd` | - | `ReplyEvent` |
| `device_blacklist_disable` | `DeviceBlacklistDisableCmd` | - | `ReplyEvent` |
| `device_blacklist_get` | `DeviceBlacklistGetCmd` | - | `DeviceBlacklistGetEvent` (`[EmitsEvent]`) |
| `device_blacklist_clear` | `DeviceBlacklistClearCmd` | - | `ReplyEvent` |
| `device_blacklist_add` | `DeviceBlacklistAddCmd` | `vid?, pid?, serial?, mac?, kind?, label?` | `ReplyEvent` |
| `device_blacklist_remove` | `DeviceBlacklistRemoveCmd` | `vid?, pid?, serial?, mac?, kind?` | `ReplyEvent` |
| `device_blacklist_set` | `DeviceBlacklistSetCmd` | `entries: DeviceEntryDto[]` | `ReplyEvent` |
| `ping` | `PingCmd` | - | `ReplyEvent` |
| `shutdown` | `ShutdownCmd` | - | `ReplyEvent`, then `Environment.Exit(0)` |
| `reset_all_policy` | `ResetAllPolicyCmd` | - | `ReplyEvent` |

`DeviceEntryDto(vid?, pid?, serial?, mac?, kind?, label?)` is the shared shape used inside
every `*_set` command's `entries` array.

`device_disable`/`device_enable` act on an arbitrary PnP instance ID directly - they are
**not** policy-aware (no whitelist/blacklist involved), unlike everything under
"protection". Use them for one-off manual control; use whitelist/blacklist commands for
persisted policy.

`reset_all_policy` clears all four lists (device whitelist, device blacklist, clipboard
whitelist, clipboard blacklist) in one call, handled by `WindowsControlHandler` (not by
`WindowsUsbProtectionHandler`/`WindowsClipboardProtectionHandler`, since no single existing
handler owns both device and clipboard state). Each list ends up in exactly the state its own
individual `*_clear` command would leave it in - device whitelist also disables itself
(matching `device_whitelist_clear`'s factory-reset semantics: an enabled-but-empty whitelist is
deny-all), the other three only empty (already the loosest state for a blacklist, and for
clipboard's independently-toggleable model). It is **additive, not a replacement** - every
individual `*_clear` command keeps working unchanged on its own; this exists purely so a caller
that wants everything cleared doesn't need four separate round trips. See
`WindowsControlHandler.Handle(ResetAllPolicyCmd)` for the reconcile delegates it fires
(`restoreDevices` covering all four device monitors, plus clipboard's own `reevaluate`). A
standing rule (AGENTS.md "Policy list completeness") requires every future policy list to be
wired in here too, mechanically checked by
`DlpEndpointMonitor.Tests/WindowsControlHandlerTests.cs`'s
`ResetAllPolicyCmd_HandlerConstructorCoversEveryPolicyListType`.

### 2.2 Events (stdout, one JSON object per line)

Every event has a `type` field (the JSON discriminant). Table: `type` value, C# record
(`Core/EventEmitter.cs`), fields, when it fires.

| `type` | Record | Fields | Fires when |
|---|---|---|---|
| `error` | `ErrorEvent` | `source, message, ts` | Any caught exception anywhere; also unregistered-type serialization guard |
| `info` | `InfoEvent` | `message, ts` | Startup/shutdown milestones, policy-apply/restore summaries |
| `reply` | `ReplyEvent` | `id?, ok, error?` | Default ack for most commands |
| `clipboard_read` | `ClipboardReadEvent` | `id?, ok, content?` | Reply to `clipboard_read` |
| `clipboard_change` (kind `text`/`files`/`image`/`unknown`) | `ClipboardTextEvent` / `ClipboardFilesEvent` / `ClipboardImageEvent` / `ClipboardUnknownEvent` | `operation` ("copy"/"cut"/"paste"), content, `ts` | Unsolicited, on every `WM_CLIPBOARDUPDATE` |
| `clipboard_content_blocked` / `clipboard_content_block_failed` | `ClipboardContentBlockedEvent` / `ClipboardContentBlockFailedEvent` | `operation, kind, reason` ("blacklist_match"/"whitelist_gate"), `matchedPattern?, sourceEventId?, error?(failed only), ts` | `sourceEventId` is the `eventId` of the `clipboard_change` reported moments earlier for the same content - this event carries no content of its own, join on that id to see what was actually blocked (section 10) |
| `usb_drive_connected` / `usb_drive_disconnected` | `UsbDriveConnectedEvent` / `DisconnectedEvent` | `drives: string[], ts` | `DBT_DEVTYP_VOLUME` arrival/removal |
| `usb_device_detected` | `UsbDeviceDetectedEvent` | `vid?, pid?, serial?, devicePath, usbClass?, kind, nativeClass?, groupId?, sourceEventId?, ts` | Arrival where VID/PID could not be parsed |
| `usb_device_connected` / `usb_device_disconnected` | `UsbDeviceConnectedEvent` / `DisconnectedEvent` | `vid, pid, serial?, usbClass?, kind, nativeClass?, groupId?, devicePath, allowed(only on connect), sourceEventId?, ts` | USB device-interface arrival/removal. `sourceEventId` (composite devices only) is the anchor `eventId` shared by every event for this device's `groupId` in the current connect episode (section 10) |
| `usb_device_blocked` / `usb_device_block_failed` | `UsbDeviceBlockedEvent` / `BlockFailedEvent` | `..., instanceId, sourceEventId?, error?(failed only), ts` | After a block attempt |
| `usb_device_unblocked` / `usb_device_unblock_failed` | `UsbDeviceUnblockedEvent` / `UnblockFailedEvent` | `vid?, pid?, serial?, kind, instanceId, sourceEventId?, error?(failed only), ts` | During `RestoreCompliant` |
| `usb_storage_status` | `UsbStorageStatusEvent` | `id?, ok, enabled` | Reply to `usb_storage_status` |
| `device_protection_status` | `DeviceProtectionStatusEvent` | `id?, ok, mode, error?` | Reply to `device_protection_status`, also usable standalone |
| `device_whitelist_get` / `device_blacklist_get` | `DeviceWhitelistGetEvent` / `DeviceBlacklistGetEvent` | `id?, ok, enabled, entries: WhitelistEntryDto[]` | Reply to the `_get` commands |
| `monitor_connected` / `monitor_disconnected` | `MonitorConnectedEvent` / `DisconnectedEvent` | `vid?, pid?, devicePath, ts` | External monitor interface arrival/removal (`vid`/`pid` = EDID manufacturer/product code) |
| `monitor_blocked` / `monitor_block_failed` | `MonitorBlockedEvent` / `BlockFailedEvent` | `vid?, pid?, devicePath, error?(failed only), ts` | After `DisableExternalDisplays` - emitted both on a fresh non-compliant arrival AND per non-compliant monitor found during `BlockNonCompliant()`'s re-check (policy change or a projection-mode switch relayed via `DisplayChangeRelay`), not just arrival (section 10) |
| `monitor_projection_changed` | `MonitorProjectionChangedEvent` | `kind ("internal"|"clone"|"extend"|"external"|"unknown"), ts` | `WM_DISPLAYCHANGE` settles (800ms debounce) - which Win+P mode is now active, read via `DisplayActions.GetCurrentTopology` |
| `keyboard_shortcut` | `KeyboardShortcutEvent` | `action ("copy"|"cut"|"paste"|"undo"), ts` | Ctrl+C/X/V/Z detected (reporting only, never blocks the key) |
| `bluetooth_device_connected` / `_disconnected` | `BluetoothDeviceConnectedEvent` / `DisconnectedEvent` | `mac, kind, name, allowed(connect only), ts` | Paired BT device arrival/removal |
| `bluetooth_device_blocked` / `_block_failed` | `BluetoothDeviceBlockedEvent` / `BlockFailedEvent` | `mac, kind, name, error?(failed only), ts` | After `RemovePairing` |

**`info`/`error` share the same stdout stream as everything else** - there is no separate
log channel. A consumer must treat `type: "info"`/`"error"` as log lines interleaved with
protocol events, not filter them out as non-events.

### 2.3 `--schema`: the machine-readable version of this section

`dotnet run --project DlpEndpointMonitor -- --schema` runs `Core/SchemaExporter.Export`
and exits before touching stdout for anything else. It reflects over every `IEvent`/
`ICommand` type in the assembly, emits a JSON Schema `$defs` map (one schema per record,
with the `[JsonDiscriminant]` field/value injected as a `const`-valued required property),
plus a top-level `cmdReply` map from each command's discriminant string to the event-type
string it replies with (only for commands carrying `[EmitsEvent]` - a command without it
implicitly replies `reply`). **This is very likely what a consumer (the sibling Node.js
agent) generates its TypeScript types from** - if you add or change a command/event shape,
re-run `--schema` and diff the output; that is the actual contract check available today
(there is no separate contract test).

This is the **only** reflection-based path in the whole binary, which is why it is guarded
with `[RequiresUnreferencedCode]` and only reachable through the `--schema` flag checked
before any other startup work - it must never run as part of the trimmed, normal-operation
path.

---

## 3. Device kind vocabulary and resolution

`DeviceKind` (`Core/Enums.cs`): `unknown, audio, biometric, bluetooth, camera, hid, hub,
keyboard, monitor, mouse, mtp, network, printer, sensor, smartcard, storage, vendor, video`.

Three independent resolvers feed this enum, one per bus:

- **USB / display** (`Core/UsbKind.cs`, `DeviceKindResolver`): a lookup table maps ~25
  known Windows device-interface-class GUIDs to standard USB class codes
  (`bDeviceClass`/`bInterfaceClass`), then a second table maps USB class code to
  `DeviceKind`. A third table, checked first, overrides specific GUIDs that share a USB
  class with a broader category but deserve their own kind (keyboard/mouse/biometric/
  sensor are all HID class `0x03`; MTP phones/tablets share class `0x06` with WIA
  scanners/cameras).
- **Bluetooth** (`Actions/BluetoothActions.GetKindFromCoD`): decodes the Class-of-Device
  major/minor class bitfield reported by the Bluetooth API. Peripheral major class
  (`0x05`) is further split by minor class into mouse/keyboard/combo/generic-HID, because a
  generic `hid` kind would never match a `{kind: "mouse"}` policy entry against a real
  Bluetooth mouse.
- **Display** (`Actions/DisplayActions.ParseMonitorPath`): not a "kind" resolution at all -
  monitors always resolve to `DeviceKind.Monitor`; `vid`/`pid` are repurposed to carry the
  3-letter EDID manufacturer code and 4-hex-digit product code instead of a USB vendor/
  product ID.

**Sync obligation**: this vocabulary is mirrored by a `device-kind.ts` union in the sibling
Node.js agent repository. If you add a `DeviceKind` value here, the corresponding change
must land there too, or a policy entry referencing the new kind will fail to round-trip
through the agent/hub/dashboard.

One deliberate non-mapping worth knowing before you "fix" it: `KSCATEGORY_CAPTURE`
(`{65E8773D-...}`) is **not** mapped to `video`, even though it sounds like it should be.
It is an audio+video capture category that HD-audio codecs also register their mic/
speaker/HDMI-audio topology under (`wavemicin`, `wavespeaker`, `ehdmiouttopo`). Mapping it
to video previously made a "block all video devices" policy sweep up and try to disable
the sound card. Only `KSCATEGORY_VIDEO` (`{6994AD05-...}`) is video-specific and is what
real UVC webcams register under.

---

## 4. Policy state model

### 4.1 Protection mode is derived, never stored

`machine_policies`-equivalent state here is just two independent booleans,
`whitelist.IsEnabled` and `blacklist.IsEnabled` (each persisted in its own list's JSON
file). The "protection mode" is computed fresh every time, in
`WindowsUsbProtectionHandler.Handle(DeviceProtectionStatusCmd)`:

| whitelist enabled | blacklist enabled | mode |
|---|---|---|
| false | false | `none` |
| true | false | `whitelist` |
| false | true | `blacklist` |
| true | true | `conflict` (`error` set; treat as no enforcement until resolved) |

`conflict` should be structurally unreachable through normal command flow - every
"enable X" handler force-disables the other list first
(`WindowsUsbProtectionHandler.Handle(DeviceWhitelistEnableCmd)` calls
`_blacklist.SetEnabled(false)` before `_whitelist.SetEnabled(true)`, and vice versa). The
only way to actually reach `conflict` is a direct edit of `whitelist.json`/`blacklist.json`
on disk, which is exactly why `Program.cs` checks for it at startup and force-disables
both if found, emitting a `startup_conflict` error event rather than silently picking one.

### 4.2 Matching semantics (`Core/UsbDeviceList.cs`)

A device list holds an `Enabled` flag and a flat list of `UsbDeviceEntry(Vid?, Pid?,
Serial?, Mac?, Kind?, Label?)`. Two independent match predicates exist on the base class,
used by the two subclasses with opposite polarity:

- `MatchesAnyUsb(vid, pid, serial, kind)` - only entries with **no `Mac`** participate; a
  null field on the entry is a wildcard for that field; an entry with every identity field
  null (only `Kind` set, or nothing at all) matches broadly by kind (or matches everything,
  for a kind-less wildcard entry).
- `MatchesAnyBt(mac, kind)` - only entries with **no Vid/Pid/Serial** participate; same
  wildcard rule.

`DeviceWhitelist.IsAllowed(...)` returns `true` unconditionally when disabled (nothing is
restricted); when enabled, a device is allowed only if it matches an entry.
`DeviceBlacklist.IsBlocked(...)` returns `false` unconditionally when disabled; when
enabled, a device is blocked if it matches an entry. Both have USB and Bluetooth overloads
mapping onto the two match predicates above.

### 4.3 Duplicate rejection

Two entries are the **same device** when `Vid`, `Pid`, `Serial`, `Mac`, and `Kind` all
match case-insensitively (`UsbDeviceList.SameDevice`) - `Label` is cosmetic and
deliberately excluded, so relabeling an existing entry does not create a duplicate.
`Add` silently no-ops if the device is already present; `Set` dedupes the incoming array
against itself while building the replacement list. This is enforced at exactly one layer
here (there is no separate API/UI boundary in this repo) - a sibling repo may duplicate
this check at its own boundary, but this is the ultimate source of truth for what the
persisted list actually contains.

### 4.4 Mutation -> enforcement side effects

Every mutating command in `WindowsUsbProtectionHandler` fires `Task.Run(_applyPolicy)`
and/or `Task.Run(_restoreDevices)` (never synchronously, so the command reply is not
blocked on a full device re-sweep). Exactly which one(s) fire is not uniform - get this
right when adding a new mutation:

| Command | Fires |
|---|---|
| `whitelist_enable` | `applyPolicy` (newly-non-allowed devices may need blocking) |
| `whitelist_disable` | `restoreDevices` (everything becomes allowed again) |
| `whitelist_clear` | disable the list, then `restoreDevices` (see below - "factory reset") |
| `whitelist_add` | `restoreDevices` (a newly-allowed device may need unblocking) |
| `whitelist_remove` | *(no automatic side effect - removing an allow-entry can only newly disallow; nothing to restore, and blocking is not immediate)* |
| `whitelist_set` | `restoreDevices` THEN `applyPolicy` (a bulk replace can both loosen and tighten at once; the two calls act on disjoint device sets, so the order is safe) |
| `blacklist_enable` | `applyPolicy` |
| `blacklist_disable` | `restoreDevices` |
| `blacklist_clear` | `restoreDevices` (clearing a blacklist can only allow more) |
| `blacklist_add` | `applyPolicy` (a newly-blocked device may need blocking now) |
| `blacklist_remove` | `restoreDevices` (removing a block-entry can only newly allow) |
| `blacklist_set` | `restoreDevices` THEN `applyPolicy` |

**`whitelist_clear` is a "factory reset", not a bare clear**: it also force-disables the
whitelist before clearing. An enabled-but-empty whitelist is deny-all (`MatchesAnyUsb`/
`MatchesAnyBt` finds nothing), so a bare `Clear()` would leave every currently-blocked
device blocked forever (`RestoreCompliant` checks `IsAllowed`, which would still be false).
Disabling first makes `IsAllowed` return `true` unconditionally, so the subsequent
`restoreDevices` actually re-enables everything.

---

## 5. Device blocking algorithm (the enforcement core)

This is the most safety-critical logic in the repo. Read this section fully before
touching `UsbMonitor.BlockDevice`, `UsbActions`, or `IsProtectedInternal`.

### 5.1 The safety gate: never block a built-in input device

`UsbActions.IsProtectedInternal(kind, instanceId)` is the **single choke point** called
from every block path (live arrival, policy-apply sweep, startup enumeration). It applies
to `StrictInputKinds = {Keyboard, Mouse, Hid, Hub}` **and** to `DeviceKind.Unknown` - camera/
video are deliberately excluded, so a built-in webcam remains blockable. The `Unknown`
extension exists because of section 5.8 below: an unclassifiable device is now reachable by
`BlockDevice` at all (it used to be silently skipped upstream), so it needs the same
bus-ancestry protection as a strict input kind, or an internal-but-unrecognized device could
be disabled blind.

Decision order matters and is non-obvious:
1. **Check Bluetooth ancestry first.** `HasBluetoothAncestor` walks up the device tree
   looking for a `BTH*`-prefixed node. If found, the device is a wireless BT/BLE
   peripheral and is **always** treated as external/blockable - even though Windows
   reports Bluetooth input devices as "non-removable" via `CM_DRP_REMOVAL_POLICY" (because
   the *radio* is what's non-removable, not the peripheral). Checking this first also
   avoids a trap: the Bluetooth radio itself is frequently a USB device sitting *above*
   the BTH nodes in the tree, so a USB-ancestor check run first would find the radio and
   wrongly classify a wireless keyboard as built-in USB hardware.
2. **Otherwise, check the USB ancestor's removal policy.** `GetGroupId` walks up to find
   the physical `USB\...` node; `IsRemovable` reads its `CM_DRP_REMOVAL_POLICY` and returns
   `false` (protect it) only for `CM_REMOVAL_POLICY_EXPECT_NO_REMOVAL`. **Fails safe**: any
   API read failure returns `false` from `IsRemovable`, i.e. `IsProtectedInternal` returns
   `true` - an undeterminable device is never blocked as if it were external.
3. **No USB and no Bluetooth ancestor at all** -> an internal bus (ACPI, I2C, PS/2) ->
   always protected.

### 5.2 Group compliance for composite USB devices (`UsbMonitor.IsGroupCompliant`)

Windows enumerates one physical composite USB device (a keyboard, most commonly) as
**several independent device interfaces**, each notified and evaluated separately - and
each can resolve to a **different** `DeviceKind` (e.g. one interface resolves to `Keyboard`
via the legacy keyboard-class GUID, a sibling resolves to `Hid` via the generic HID
interface GUID). A whitelist entry scoped to one kind only matches the interface of that
kind; without a group-aware check, blocking the non-matching sibling can escalate to
disabling the shared composite parent (section 5.3 below), taking the *entire physical
device* down - including the interface that was correctly whitelisted. This was a real,
shipped bug: enabling whitelist with only `{kind: keyboard}` disabled the keyboard itself,
not just an unrelated mouse.

`UsbMonitor.IsGroupCompliant(siblings, out anyBlocked)` decides compliance for the **group**
(the USB composite parent's `GroupId`), not a single interface: the group is allowed if
**any** sibling interface individually satisfies the whitelist, and blocked if **any**
sibling individually matches the blacklist. `UsbActions.EnumerateGroupSiblings(groupId)` is
the stateless enumeration this reads from (filters `EnumerateConnected()` by matching
`GroupId`) - a disabled devnode has no active interface, so it never appears here, which is
why restore (5.4) needs its own fallback rather than reusing this helper directly.

This check runs inside `BlockDevice`, immediately after the `IsProtectedInternal` gate and
before any disable attempt: if the arriving/current interface's group is confirmed allowed,
`BlockDevice` returns immediately (emitting an `info` `usb_group_allowed` line) and nothing
about the physical device is touched - not even the interface that *would* otherwise have
been blocked on its own. If the group is not confirmed allowed, the existing per-interface
escalation ladder (5.3) proceeds exactly as before for the current interface only - this
check only ever *prevents* a wrongful block, it never changes behavior when a block is
actually warranted. **Skipped entirely for Bluetooth-backed devices** (`HasBluetoothAncestor`
true) - their `GroupId`, if any, can resolve to the shared BT radio's own USB instance, not a
meaningful sibling set; grouping there would incorrectly conflate unrelated devices hanging
off the same radio. `BlockNonCompliant` (the full policy-change sweep) buckets by `GroupId`
in one pass up front for the same reason, with the same Bluetooth-ancestor exclusion.

### 5.3 The USB/HID block escalation ladder (`UsbMonitor.BlockDevice`)

For a non-Bluetooth-backed device, in order, stopping at the first success:
1. `CM_Disable_DevNode` on the device's own instance ID.
2. If that failed AND the device has a USB group ancestor (`GroupId` is set) AND it has
   **no** Bluetooth ancestor: escalate to disabling the whole composite-device group node
   instead of just the one interface.
3. If that still failed: `CM_Request_Device_EjectW` on the group node. Windows vetoes
   `CM_Disable_DevNode` for some input devices specifically to prevent a hard keyboard
   lockout, so eject exists as the last resort. **Eject is not reversible by software** -
   there is no `CM_Enable` that undoes an eject; the user must physically replug the
   device. This is fundamentally different from disable/enable and must not be treated as
   equivalent when reasoning about "will `RestoreCompliant` bring this back."

For a Bluetooth-backed HID device (`GetBluetoothDeviceNode` returns non-null): resolve that
node's own MAC via `BluetoothActions.ParseMacFromPath` and unpair via
`BluetoothActions.RemovePairing(mac)`, full stop - no group/eject escalation, no
`DisabledDevices` record, because "the group" for a BT peripheral would be the shared
Bluetooth radio (which must never be disabled/unpaired - it would take out every paired
Bluetooth device at once) and there is nothing to restore once unpaired (see 5.5). If the
resolved node's MAC can't be parsed (not normally reachable), this falls back to
`UsbActions.DisableDevice` on that node, tracked in `DisabledDevices` like any other
disable, purely so blocking never silently no-ops for that one edge case.

For the non-Bluetooth path, whichever instance ID actually got disabled (`disabledId`) is
recorded in `DisabledDevices`, keyed by that exact ID - not by VID/PID/kind - because that ID
is what `CM_Enable_DevNode` needs later, and a disabled device is invisible to interface
enumeration (it has no active interface), so there is no other way to rediscover it. Note
this recorded identity is whichever interface actually triggered the escalation (e.g. the
`Hid` sibling, not the `Keyboard` one) - section 5.4 explains why `RestoreCompliant` does
not trust this identity at face value for a composite parent.

### 5.4 Restore (`UsbMonitor.RestoreCompliant`, `IsRecordCompliant`)

Iterates the **persisted** `DisabledDevices` list (not a live re-enumeration - a disabled
device has no active interface, so nothing would be found). For each recorded device,
compliance is now decided by `UsbMonitor.IsRecordCompliant`, not by trusting the record's
own stored Vid/Pid/Kind directly:
- **Bluetooth-backed** disabled peripheral: judged by its own stored identity
  unconditionally - no composite-group concept applies.
- **Other sibling interfaces of the same `GroupId` are currently live** (only one leaf was
  disabled, not the whole composite parent): compliance is derived from *their* current
  state via `IsGroupCompliant` (5.2) - the record's own stale identity is not used at all.
  This is the fix for the compounding half of the bug in 5.2: a wrongly-escalated composite
  parent's `DisabledDeviceRecord` carries the WRONG interface's kind (e.g. `Hid`), which
  would never match a keyboard-only whitelist if trusted directly.
- **The disabled instance IS the composite parent itself, and no sibling is enumerable**
  (the whole physical device is torn down): returns compliant **unconditionally**. Restore
  then calls `CM_Enable_DevNode` on it; Windows re-enumerates every child interface, each
  firing a fresh arrival that re-evaluates group compliance with **live** data through the
  normal `BlockDevice` path - if the device is genuinely still non-compliant, it gets
  re-disabled there, this time at the correct granularity. Re-enabling is reversible (unlike
  eject), so this is judged an acceptable trade: a brief live-then-evaluate window, bounded
  the same way any fresh plug-in's live-then-evaluate window already is.
- **No group info at all** (plain leaf disable of a non-composite device, or the physical
  device is now fully unplugged): falls back to the record's own stored identity, same as
  before this fix existed.

Outcome handling per record is unchanged from before:
- If current policy now allows it: `CM_Enable_DevNode` on the recorded instance ID.
  - Success -> remove from `DisabledDevices`, emit `usb_device_unblocked`.
  - Failure containing `"Locate"` (the device is physically absent / unplugged) -> forget
    the record anyway; a future replug will be re-evaluated fresh as an arrival, so nothing
    is lost by dropping it now.
  - Any other failure (device present but re-enable genuinely failed) -> **keep** the
    record so a future restore retries it, and emit `usb_device_unblock_failed`. Do not
    let a still-disabled device fall out of tracking.
- If still not allowed: leave it disabled, leave the record, count it as `stillBlocked`.

This whole mechanism is what makes "unblock re-enables the device" correct even when the
process restarted between the block and the unblock - the record survives on disk.

### 5.5 Bluetooth blocking (`BluetoothMonitor`, unpair as the primary and only action)

Blocking a Bluetooth device removes its pairing (`BluetoothActions.RemovePairing`,
`BluetoothRemoveDevice`) rather than disabling its PnP node. This used to be the other way
around (disable-primary, unpair only as a fallback), but Windows Settings' Bluetooth
Connected/Paired indicator reads the Bluetooth pairing/link-state store, **not** PnP devnode
enabled/disabled state - `CM_Disable_DevNode` correctly stops HID input reaching any app, but
can never change what Settings displays, because Settings reads a different data source
entirely. Only an actual unpair does. The trade this makes: a device blocked this way can no
longer be auto-restored by `BluetoothMonitor.RestoreCompliant` when policy loosens - the user
must manually re-pair it (see AGENTS.md section 7's carve-out).

`BluetoothMonitor.BlockDevice(mac, kind, name)` already has the MAC address (from
`EnumerateConnected`'s classic Bluetooth API or from a parsed device-change path) and calls
`RemovePairing` directly - no PnP node lookup needed.

`UsbMonitor.BlockDevice`'s Bluetooth branch is different: it arrives via a HID *interface*
event, which only gives an instance ID, not a MAC, so it still needs
`UsbActions.GetBluetoothPairingCandidates(parsed.InstanceId)` to find one or more MAC
candidates for the peripheral, then `BluetoothActions.ParseMacFromPath` to extract each
candidate node's MAC before it can call `RemovePairing`. In the (not normally reachable) case
where NONE of the resolved candidate nodes' MACs can be parsed, it falls back to
`UsbActions.DisableDevice` on the primary node so blocking never silently no-ops - this is a
parsing-safety fallback, not a policy choice, and does not get tracked in `DisabledDevices` in
the normal (unpair) path.

**Why more than one candidate address was tried, and what live testing actually found (real
production bug):** a real session's event log showed the SAME physical Bluetooth LE mouse (a
Logitech MX Vertical) reconnect with a DIFFERENT trailing MAC-derived hex suffix on each of
three reconnects in one session (`...BE7`, then `...BE8`, then `...BE9`), and a block attempt
using the newest (`...BE9`) address failed with `BluetoothRemoveDevice failed: 0x00000490`
(`ERROR_NOT_FOUND`). The initial hypothesis was that the pairing store simply didn't recognize
that specific address (BLE peripherals are permitted to regenerate their Resolvable
Private/Static Random address across power cycles) - `UsbActions.GetBluetoothPairingCandidates`
was built to try every sibling `BTHENUM\`/`BTHLE\` node still enumerable (a prior connection
cycle's now-phantom node included, via the same `CM_Get_Parent` then
`CM_Get_Child`/`CM_Get_Sibling` walk `GetBluetoothPairingCandidates` uses), and
`BluetoothActions.RemovePairingAny` (thin wrapper over the pure, unit-tested
`TryCandidatesInOrder`) tries `RemovePairing` against each in order, stopping at the first
success - safe regardless of mechanism, since a non-matching address is a harmless no-op
(`ERROR_NOT_FOUND`), never a destructive operation on the wrong device.

**Live testing on the real hardware disproved the address-rotation theory as the cause of this
specific failure, and pinned down the real one.** `GetBluetoothPairingCandidates` found only the
single current address (no older sibling was still enumerable - Windows does not keep them
around) and it still failed. Directly inspecting
`HKLM\SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters\Devices` at the moment of failure
showed the exact failing address (`d15799812be9`) WAS present as a remembered device - so the
address was correct all along. Manually removing the same device through Windows Settings
("Bluetooth & devices" -> Remove device) **succeeded** against that identical address. The
conclusion: the legacy `BluetoothRemoveDevice` API (`bthprops.cpl`) itself cannot reliably unpair
this Bluetooth LE peripheral, independent of address correctness - Windows Settings uses a
different, almost certainly WinRT-based (`Windows.Devices.Enumeration`/
`DeviceInformation.Pairing.UnpairAsync()`) removal path this codebase has no equivalent to
without adding the `CsWinRT` NuGet package, which would violate this project's zero-NuGet-
dependency constraint - a tradeoff for a human decision, not something to add speculatively.
The candidate-retry logic is kept regardless (it is still correct and safe for the case it WAS
designed for - a genuinely stale/rotated address - and is kind-agnostic, applying the same way
to a Bluetooth mouse, keyboard, audio device, or generic HID/dongle), but it is not sufficient
by itself for hardware where the legacy API fails outright.

**Because of this, `UsbMonitor.BlockDevice`'s Bluetooth branch falls back to
`UsbActions.DisableDevice` on the primary node whenever `RemovePairingAny` fails for ANY
reason** - not only the narrow "no MAC could be parsed" case this fallback originally existed
for. Confirmed live: leaving a device fully connected and functional just because the legacy
unpair API doesn't support it is a real DLP enforcement gap, not an acceptable degraded mode.
The decision (unpair first for the cosmetically-correct Settings result when it works, disable
as a guaranteed fallback when it doesn't) is factored into
`UsbMonitor.ResolveBluetoothBlock(tryUnpair, tryDisable, disableNodeId)` - pure logic, unit
tested independent of the real Win32 calls (`DlpEndpointMonitor.Tests/UsbMonitorTests.cs`). A
device blocked via the disable fallback IS tracked in `DisabledDevices` and CAN be
auto-restored by `RestoreCompliant` (unlike a successful unpair) - same as any other
disable-based block.

**Verified BLE device tree is three levels deep, one more than classic Bluetooth** (confirmed
live via `Get-PnpDevice`/`Get-PnpDeviceProperty` against real hardware):
```
BTH\MS_BTHLE\...                                  (enumerator driver - never touch)
  -> BTHLE\DEV_<MAC>\...                           (true peripheral node, e.g. "MX Vertical")
       -> BTHLEDEVICE\{service-guid}_..._<MAC>\... (one GATT service child, e.g. the HID service)
            -> HID\{...}_..._COL0N\...             (the actual input-delivering leaf)
```
`BTHLEDEVICE\` is **not** the peripheral - it is a GATT-service child, one level below the
true `BTHLE\` node. `UsbActions.GetBluetoothDeviceNode` (the mechanism `UsbMonitor` uses for
Bluetooth-backed HID devices arriving via their own HID interface) walks all the way up to
`BTHLE\`, never stopping at `BTHLEDEVICE\` - stopping there would resolve the MAC of a GATT
service instead of the peripheral. Classic Bluetooth (`BTHENUM\`) has no such extra layer -
its own node is where the walk stops directly.

`DisabledDeviceRecord` still carries an optional `Mac` field (mirroring `UsbDeviceEntry`'s
existing USB/Bluetooth side-by-side identity pattern), but nothing new populates it going
forward - it only still matters for `BluetoothMonitor.RestoreCompliant` to drain any
pre-existing record left over from a machine that was running the old disable-primary binary
before this change shipped. A device blocked via unpair has no record and cannot be restored
in software - re-pairing is manual.

### 5.6 Display/monitor blocking (`DisplayMonitor`, `DisplayActions`)

Monitors are blocked as a **group**, not per-device: `DisableExternalDisplays` uses the
Connecting-and-Configuring-Displays (CCD) API (`QueryDisplayConfig` /
`SetDisplayConfigPaths`) to deactivate every path whose `outputTechnology` is not
`DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INTERNAL`, compacts the mode array, and applies with
`SDC_APPLY | SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_SAVE_TO_DATABASE | SDC_ALLOW_CHANGES` so
the setting survives a monitor re-enumeration. A "second-screen-only" edge case (no
internal path currently active) cannot use `SDC_USE_SUPPLIED_DISPLAY_CONFIG` (it requires
at least one active path), so it falls back to `SDC_TOPOLOGY_INTERNAL`, which forces the
topology back to the laptop panel regardless of current state.

`RestoreCompliant` re-enables extended desktop (`SDC_TOPOLOGY_EXTEND`) only if **no**
currently-connected monitor is non-compliant - if even one blocked monitor is still
plugged in, it stays blocked (logged, not restored). `EnableExternalDisplays` returns the
real `SetDisplayConfig` result (`ok = result == 0`) - it used to hardcode `ok = true`
regardless of outcome, so a failure was logged as an error but `RestoreCompliant` still
claimed "external displays re-enabled" right after. If a user reports the external display
still not coming back after this fix, the next step is a symmetric redesign - explicitly
re-query and reactivate the specific external path(s) instead of the blunt
`SDC_TOPOLOGY_EXTEND` auto-topology switch, mirroring `DisableExternalDisplays` in reverse -
not implemented, since it's unverifiable without a real external monitor to test against.

`WM_DISPLAYCHANGE` is debounced 800ms (`DisplayMonitor.OnDisplayChanged`) because Windows
fires it repeatedly while HDMI-audio interfaces cycle during a topology switch. The
debounce token must be replaced atomically: cancel-and-dispose the previous
`CancellationTokenSource` *before* installing the new one, in the same synchronous block -
see section 10 for the crash this fixed.

**`WM_DISPLAYCHANGE` is session/desktop-scoped, unlike `WM_DEVICECHANGE` (systemwide/PnP) -
a headless Session-0 primary never receives it on its own message window.** This was a real
production report: plugging in a non-compliant monitor got blocked correctly, but switching
projection mode (Win+P) on a monitor that was *already* connected did not, because no new
`WM_DEVICECHANGE` fires for a pure topology switch - only `WM_DISPLAYCHANGE`, which a Session-0
window structurally cannot receive (Session 0 has no desktop at all). `DisplayCompanionRelay`
(section 11's action-execution relay) did not fix this - it only carries the *outbound*
"disable"/"enable" command, not the *inbound* change notification. `Core/DisplayChangeRelay.cs`
adds the missing leg, same direction as `ClipboardCompanionRelay` (companion -> primary,
fire-and-forget): the companion's own `MessageWindow` lives in the real interactive session and
genuinely gets `WM_DISPLAYCHANGE`, so it forwards a bare notification to the primary, which calls
`DisplayMonitor.NotifyExternalDisplayChange()` - a public wrapper around the exact same private
`OnDisplayChanged()` debounce path a local broadcast would have used, so the debounce/
re-check logic itself is not duplicated anywhere.

**Every settled `WM_DISPLAYCHANGE` also emits `monitor_projection_changed` with which Win+P mode
is now active** (`DisplayMonitor.EmitProjectionChanged`, called right before `BlockNonCompliant()`
in the same debounce continuation - both local and relayed). The `kind` (`internal`/`clone`/
`extend`/`external`/`unknown`) is read via `DisplayActions.GetCurrentTopology`, which calls
`QueryDisplayConfig(QDC_DATABASE_CURRENT)` for its `currentTopologyId` out-param - the same
identifier Windows itself uses to decide which Win+P tile is highlighted, so this is not a
heuristic derived from path/monitor counts. This is a separate, informational event from
`monitor_blocked`/`monitor_block_failed` - a projection change is worth reporting even when
nothing ends up non-compliant, since the agent/dashboard otherwise has no way to learn what the
topology changed *to*. `DisplayActions.MapTopologyId(uint)` is the pure bit-value-to-enum mapping,
factored out for unit testing (`DlpEndpointMonitor.Tests/DisplayActionsParsingTests.cs`) - the
Win32 `QueryDisplayConfig` call itself is not (and cannot be) unit tested, same as every other
real-hardware CCD call in this file.

**`ParseMonitorPath` no longer drops unparseable monitors.** If a monitor's interface path
doesn't match the `DISPLAY#XXX0000` EDID pattern, it used to return `null` and the monitor
was silently excluded from `EnumerateConnected()` - invisible to `BlockNonCompliant`'s sweep,
so it could connect under whitelist without ever being evaluated. It now falls back to
`Vid=Pid=""` (the same identity-less sentinel `UsbActions.ParsePartialDevice` already uses),
so the monitor still participates in the compliance sweep and fails closed under whitelist
(see 5.8). Only the fully-degenerate case (no instance id survives parsing at all) still
returns `null`.

### 5.7 Global USB storage kill switch

Independent of the per-device whitelist/blacklist mechanism entirely:
`UsbActions.SetUsbStorageEnabled(bool)` toggles
`HKLM\SYSTEM\CurrentControlSet\Services\USBSTOR!Start` between `3` (enabled) and `4`
(disabled) - this is an all-USB-mass-storage on/off switch, driven by the
`usb_disable_storage`/`usb_enable_storage`/`usb_storage_status` commands, not by the
protection-status commands.

### 5.8 Unclassifiable devices fail closed under whitelist

A device whose interface class GUID isn't recognized (`DeviceKindResolver` resolves it to
`DeviceKind.Unknown`) or whose path doesn't regex-parse a VID/PID used to be **skipped
entirely** by `UsbActions.ParsePartialDevice` ("not policy-relevant") and by
`DisplayActions.ParseMonitorPath` (returned `null`) - meaning it never reached
`HandleArrival`/whitelist-blacklist evaluation at all and simply connected, regardless of
whitelist state. This was the root cause of "HDMI and other devices not in the whitelist
could connect": an unrecognized or unparseable device was invisible to policy, not merely
allowed by it.

Both functions now always produce a `ParsedDevice` (using the `""` empty-string sentinel for
missing Vid/Pid, consistent with the pattern already used elsewhere in this file) instead of
returning `null` for these cases, so the device always reaches `IsAllowed`/`IsBlocked`. No
change was needed to `Core/UsbWhitelist.cs`/`UsbBlacklist.cs` for this to behave correctly:
- **Whitelist enabled**: an unclassifiable device will not match any specific entry (unless
  the operator deliberately added a kind-less wildcard or `{kind: unknown}` entry), so
  `IsAllowed` returns `false` -> blocked. This is the intended fail-closed behavior - a
  whitelist means "only allow what's listed."
- **Blacklist enabled**: same reasoning in reverse - `IsBlocked` returns `false` unless an
  explicit wildcard/`{kind: unknown}` entry exists, so blacklist's default-allow semantics
  are unaffected. No special-casing needed.

`IsProtectedInternal`'s extension to cover `Kind == Unknown` (5.1) is the safety net this
relies on: an internal, essential device that happens to expose only an unrecognized
interface GUID must not be blocked just because it can't be classified - a device with no
removable USB ancestor and no Bluetooth ancestor is still protected regardless of kind.

**Accepted residual gap, not fixed by this change**: `UsbActions.EnumerateConnected()` (used
by both `EnumerateExisting()` and `BlockNonCompliant()`) only iterates
`DeviceKindResolver.KnownInterfaceGuids` (~25 hardcoded GUIDs) - a device whose interface
exposes *only* a class GUID outside that table is never even attempted by
`SetupDiGetClassDevs`, so it's still invisible to the startup/sweep enumeration entirely
(separate from the parse-failure case fixed here, which only helped devices that DO get
enumerated but then failed identity parsing). Fixing that would mean enumerating without an
interface-class filter at all - a materially bigger, separate change.

### 5.9 Network blocking (`NetworkMonitor`, own monitor, own protection default)

`DeviceKind.Network` (`GUID_DEVINTERFACE_NET`) is exposed by any NDIS adapter - a built-in
PCIe WiFi/Ethernet card exactly as much as a USB dongle - so it cannot be run through
`UsbMonitor`'s generic per-interface / composite-group pipeline: that pipeline was designed
for HID-style peripherals, has no concept that fits "the machine's only network path," and
disabling a NIC is not reversible-by-replug the way an input device is (there is no fallback
eject/escalation ladder that makes sense for a NIC). Instead, `Monitors/NetworkMonitor.cs`
owns every `Kind == Network` device exclusively, structurally mirroring `BluetoothMonitor`
(no composite-group concept, one whitelist/blacklist check per device, its own
`RestoreCompliant`) rather than `UsbMonitor`:

- `UsbMonitor.OnDeviceChanged`/`EnumerateExisting`/`BlockNonCompliant` all explicitly exclude
  `Kind == Network` (including from the `byGroup` bucketing Where-clause) - both monitors
  observe the identical `window.DeviceChanged` event stream and partition purely by the
  `DeviceKind` that `DeviceKindResolver.Resolve` returns, so a network interface is never
  double-handled.
- **Safety default**: `NetworkMonitor.BlockDevice` calls `UsbActions.IsBuiltIn(instanceId)`
  *before* any `DisableDevice` call - if the adapter is internal (no Bluetooth ancestor, and
  either no USB ancestor at all or a non-removable USB ancestor), the block is refused and a
  `NetworkDeviceBlockFailedEvent` is emitted with `error: "protected internal network adapter
  - refused to block"`. This is the single most important line in the whole feature: it is
  what stops a "block network kind" blacklist rule from disabling the machine's own
  WiFi/Ethernet and stranding it with no way to report back to the sibling dlp_v2 agent.
- `UsbActions.IsBuiltIn` is a new public helper factored out of the bus-ancestry walk that
  `IsProtectedInternal` already did inline (Bluetooth-ancestor check first, since a Bluetooth
  radio is frequently itself a USB device sitting above the BTH nodes in the tree; otherwise
  fall back to USB-group non-removability; no bus ancestor at all -> internal). `Network` is
  deliberately **not** added to `IsProtectedInternal`'s `StrictInputKinds` set - it gets its
  own call site in `NetworkMonitor` instead, so `IsProtectedInternal`'s existing behavior for
  Keyboard/Mouse/Hid/Hub/Unknown is completely unchanged.
- External/removable network adapters (a USB WiFi/Ethernet dongle) remain fully blockable -
  this is a real DLP use case (blocking a rogue USB network adapter used to bypass
  monitoring/exfiltrate data over an unmanaged network path).
- No composite-group concept, no eject fallback: `BlockDevice` is a single `DisableDevice`
  call, tracked in `DisabledDevices` the same shape as a plain USB record (`Mac` stays null).
  `RestoreCompliant` filters to `d.Mac is null && d.Kind == DeviceKind.Network` so it only
  ever touches records it owns - see the bug this shares its root cause with, below.
- New wire events, additive to the existing `usb_device_*`/`bluetooth_device_*` precedent:
  `network_device_connected`, `network_device_disconnected`, `network_device_blocked`,
  `network_device_block_failed`, `network_device_unblocked`, `network_device_unblock_failed`.

### 5.10 Clipboard content policy (separate from device blocking - `ClipboardMonitor`, `KeyboardHook`)

Clipboard content policy is a structurally separate feature from everything else in this
section - it does not decide whether to disable/eject a *device*, it decides whether to let
copied/cut/pasted *content* through, and its persistence, combination, and enforcement rules are
all deliberately different from 4.1-4.4/5.1-5.9 above. Read this subsection before touching
`Core/ClipboardRuleList.cs`, `Monitors/ClipboardMonitor.cs`, or `Monitors/KeyboardHook.cs`.

**Persisted lists.** `ClipboardWhitelist`/`ClipboardBlacklist` (`Core/ClipboardRuleList.cs`) mirror
`UsbDeviceList`'s `ReaderWriterLockSlim` + atomic-temp-file-write persistence shape (same
`StorageLocation.Default`, i.e. `%ProgramData%\DlpEndpointMonitor\`, as `clipboard-whitelist.json`/
`clipboard-blacklist.json`), but hold a structurally different entry,
`ClipboardRuleEntry(Pattern, Kind?, Label?)` - a regex pattern optionally scoped to a
`ClipboardKind`, not a device identity - and match by `Regex.IsMatch`, not vid/pid/mac
wildcarding. Two entries are the same rule when `Pattern` (ordinal - regex patterns are literal
strings, not case-insensitive text) and `Kind` both match (`SameRule`, the `ClipboardRuleEntry`
analogue of `UsbDeviceList.SameDevice`); `Label` is cosmetic and ignored for identity, same as
`UsbDeviceEntry.Label`. Every entry's `Regex` is compiled exactly once, whenever the list loads
or mutates (`RebuildCompiled`, which publishes a brand-new immutable list so a reader taking the
read lock never sees a partially-rebuilt cache) - never re-parsed on the hot path, because that
hot path includes the keyboard hook's per-keystroke Ctrl+V check. Every `Regex` is constructed
with an explicit 250ms timeout; a `RegexMatchTimeoutException` is caught and treated as "did not
match" (logged via `EventEmitter.EmitError`), and a pattern that fails to even *construct*
(`ArgumentException`, e.g. an unbalanced group) is kept in the list - still reported by
`Get`/`GetAll` - paired with a `null` compiled `Regex`, so it degrades to permanently inert rather
than crashing `Add`/`Load` or losing the rest of the list. This all exists because an
operator-supplied catastrophic-backtracking pattern is evaluated synchronously inside a global
low-level keyboard hook - without the timeout, one bad pattern could stall every keystroke on the
machine.

**The AND-combination formula, and why both lists enabled at once is fine.**
`ClipboardMonitor.EvaluatePolicy` (internal, not private, specifically so `KeyboardHook` reuses it
instead of carrying a second, possibly-diverging copy) computes: `allowed = (whitelist disabled OR
ANY candidate matches a whitelist pattern) AND (blacklist disabled OR NO candidate matches a
blacklist pattern)` - the exact same formula every device monitor already applies
(`whitelist.IsAllowed(...) && !blacklist.IsBlocked(...)`). The divergence from device policy is in
what's allowed to *reach* that formula: `WindowsUsbProtectionHandler` force-disables the other
list on every `*WhitelistEnableCmd`/`*BlacklistEnableCmd` (4.1), making simultaneous enablement an
unreachable `conflict` state guarded at startup. `WindowsClipboardProtectionHandler.Handle
(ClipboardWhitelistEnableCmd)`/`Handle(ClipboardBlacklistEnableCmd)` deliberately do **not** touch
the other list's `Enabled` flag - both enabled at once is a valid, intended combination (the
whitelist gates what's allowed through at all; the blacklist additionally forbids specific
patterns even within whatever the whitelist allows), not a conflict. There is no
`ProtectionMode`-equivalent concept and no startup conflict-guard for clipboard - do not add one.

**Two-layer enforcement.**
1. **Copy/cut** - `ClipboardMonitor.OnClipboardChanged` evaluates new content the instant
   `WM_CLIPBOARDUPDATE` fires (via `EvaluateAndEnforce`); a violation calls
   `ClipboardActions.Clear()` (the existing `EmptyClipboard()` wrapper, unchanged) and emits
   `ClipboardContentBlockedEvent` (`Reason: "blacklist_match"` with the specific `MatchedPattern`,
   or `"whitelist_gate"` with `MatchedPattern: null` when the whitelist is enabled and nothing
   satisfied it) - or `ClipboardContentBlockFailedEvent` if `Clear()` itself fails. The existing
   `clipboard_change` event (`ClipboardTextEvent`/`ClipboardFilesEvent`/`ClipboardImageEvent`/
   `ClipboardUnknownEvent`) is still emitted first, regardless of verdict, exactly as before this
   feature - "what was copied/cut" is always reported. A public `ApplyPolicy()` re-reads and
   re-evaluates whatever is CURRENTLY on the clipboard; `WindowsClipboardProtectionHandler` calls
   it (via `Task.Run`) after every whitelist/blacklist mutation, because clipboard content has no
   persisted "this was blocked" record the way a device does (every clipboard read is live and
   transient) - a newly-tightened policy must clear already-present non-compliant content
   immediately, not wait for the next copy.
2. **Paste** - `KeyboardHook`'s existing Ctrl+V keydown detection now also live-evaluates
   whatever is CURRENTLY on the clipboard, in the hook callback, before the keystroke reaches any
   application (`ShouldBlockPaste`). A violation returns a non-zero value **without** calling
   `CallNextHookEx`, so the keystroke never reaches any application; the existing
   `KeyboardShortcutEvent("paste", ...)` reporting emission is unchanged and still fires
   regardless of verdict. This closes the race window where a very fast paste immediately after a
   copy could beat the copy-time clearing in layer 1.

**The paste layer fails OPEN, not closed - the opposite default from device blocking.**
`KeyboardHook`'s hook procedure is a global, system-wide `WH_KEYBOARD_LL` hook (unlike a
per-device enforcement decision). `ShouldBlockPaste`'s entire body is wrapped in try/catch; on
ANY exception (malformed regex, a Win32 clipboard-read failure, anything) it returns `false` (do
not block), falling through to the ordinary `CallNextHookEx` call. A bug that swallows Ctrl+V
without meaning to breaks paste for every application on the machine, silently, until the process
restarts - a far worse failure mode than occasionally letting through content that should have
been blocked. This is the exact opposite of `IsProtectedInternal`/`IsRemovable`'s fail-**safe**
(fail-closed) defaults in section 5.1 - an undeterminable device is protected there, but
undeterminable clipboard content is let through here. Know which direction applies before
reusing either pattern elsewhere.

**Content-scope limitation.** Only `ClipboardKind.Text` (the full copied/pasted string) and
`ClipboardKind.Files` (each file's full path individually) are ever evaluated against these
rules. For Files, aggregation mirrors `UsbMonitor.IsGroupCompliant`'s "any sibling matches"
`.Any()` style exactly: ANY single path matching a blacklist pattern blocks the whole clipboard
operation; ANY single path matching a whitelist pattern satisfies the whitelist gate for the
whole operation. `ClipboardKind.Image` and `Unknown` clipboard content have no text to test and
always pass through untouched regardless of policy state - this is a deliberate scope limit
(no OCR, no file-content scanning), not a gap this feature is meant to close.

**What this does NOT cover.** Only a Ctrl+V keydown is interceptable this way. Right-click
"Paste", Shift+Insert, and an application's own Paste button/API all bypass the low-level
keyboard hook entirely and are not blocked by this feature - see the bug-history entry below.

---

## 6. Persistence

Three JSON files under `%ProgramData%\DlpEndpointMonitor\`
(`Environment.SpecialFolder.CommonApplicationData`): `whitelist.json`, `blacklist.json`,
`disabled-devices.json`. Machine-wide and user-agnostic on purpose - this process runs
elevated and may be launched under a different effective user context (e.g. a service/SYSTEM
account) than the interactive user, in which case a per-user profile folder
(`Environment.SpecialFolder.UserProfile`, i.e. `~`) would resolve to the wrong place entirely.
(This was briefly moved to `~/.dlp` to mirror the sibling Node.js agent's own `~/.dlp/agent.db`/
`machine.json` convention, then moved back here for exactly the reason above - no migration
was needed either time since there were no real deployments yet.)

**The storage directory is an optional constructor parameter, not a hard-coded path.**
`UsbDeviceList`'s constructor and `DisabledDevices`'s constructor both accept an optional
`storageDir` (defaulting to `null`, which resolves to the exact `%ProgramData%\DlpEndpointMonitor\`
computation above); `DeviceWhitelist`/`DeviceBlacklist` thread the same optional parameter
through to their base constructor. Every production call site (`Program.cs`) uses the
parameterless form and is unaffected - this exists purely so
`DlpEndpointMonitor.Tests/UsbDeviceListTests.cs` can point each test at its own throwaway temp
directory instead of touching the real storage location (see `ai_agent_doc/TEST-PLAN.md` section
2.5.1). Do not add a second, competing way to override the directory (e.g. an environment
variable) - this constructor parameter is the one seam.

All three share the same pattern (`Core/UsbDeviceList.cs`, `Core/DisabledDevices.cs`):
- A `ReaderWriterLockSlim` guards in-memory state, because it is read from the STA message
  thread (every arrival/removal event) and written from the async command-dispatch thread
  (every whitelist/blacklist mutation) concurrently.
- Every write is atomic: serialize to `<path>.tmp`, then `File.Move(tmp, path,
  overwrite: true)` - never a direct in-place write, so a crash mid-write cannot corrupt
  the on-disk policy state that the next startup depends on.
- Serialization goes through the source-generated `AppJsonContext` - no reflection-based
  `JsonSerializer.Serialize<T>()` calls.
- Load failures are non-fatal: caught, logged via `EventEmitter.EmitError`, and the process
  starts with empty state rather than refusing to start.

---

## 7. JSON wire format architecture

Three pieces work together to make every command/event both strongly typed in C# and
trim/AOT-safe (no reflection in the hot path):

1. **`[JsonDiscriminant(enumValue)]`** (`Core/JsonDiscriminantAttribute.cs`) - applied to a
   command or event record. At construction time it looks up which JSON field a given enum
   *type* discriminates on (`EventType` -> `"type"`, `CommandType` -> `"cmd"`,
   `ClipboardKind` -> `"kind"`), then resolves the enum member's wire string via its
   `[JsonStringEnumMemberName]`. `SchemaExporter`'s `TransformSchemaNode` reads this
   attribute to inject a `const`-valued required property into the generated JSON Schema -
   this is metadata for schema generation, not something `System.Text.Json` enforces at
   (de)serialization time; the actual field must still be present on the record (usually
   via a `Type => EventType.X` computed property, or embedded in the primary constructor
   for commands, whose discriminant is read separately by `CommandDispatcher` before
   choosing which type to deserialize into).
2. **`[EmitsEvent(EventType)]`** (`Core/EmitsEventAttribute.cs`) - documents, for a command
   record, which event type its handler replies with when that is richer than a bare
   `reply`. Purely descriptive metadata consumed by `SchemaExporter` to build the
   `cmdReply` map; `CommandDispatcher` does not read it (each handler just calls
   `EventEmitter.Emit` with whatever it wants).
3. **Source-generated `JsonSerializerContext`** - `AppJsonContext.cs` (events + persisted
   list/disabled-device state) and `Commands/CommandsJsonContext.cs` (commands). Every
   `EventEmitter.Emit` / `CommandDispatcher.Deserialize<T>` / `UsbDeviceList`/
   `DisabledDevices` save-load call resolves its `JsonTypeInfo` from one of these two
   contexts, never from ad-hoc reflection. `EventEmitter.Emit` explicitly checks for a
   missing `TypeInfo` and falls back to emitting an `ErrorEvent` naming the unregistered
   type, rather than throwing - a new event record that forgets to get added to
   `AppJsonContext` fails loudly but safely instead of crashing the process or silently
   dropping the event.

Adding a new command or event means: add the record (with `[JsonDiscriminant]`, and
`[EmitsEvent]` if applicable) + register it in the relevant `JsonSerializerContext`
partial class + add a `CommandType`/`EventType` enum member + (for a command) a `case` in
`CommandDispatcher.Dispatch` + a `Handle` overload on the relevant handler interface and
its Windows implementation. Never bypass this chain with a raw `JsonSerializer.Serialize`.

---

## 8. Engineering principles

These are written as concrete rules for this codebase, not generic definitions.

### Single Responsibility
One class, one job, and the four folders enforce it structurally:
- `Monitors/*` decide **whether** a device is allowed (ask whitelist/blacklist) and call
  into `Actions/*` to act - no P/Invoke directly in a monitor.
- `Actions/*` are stateless Win32 wrappers - no policy decisions, no `EventEmitter` calls;
  they return `(bool ok, string? error)` and let the caller decide what to emit.
- `Handlers/Windows/*` translate one `ICommand` into `Actions/*` calls and emit exactly one
  reply/result event - no enumeration/monitoring logic.
- `Core/UsbDeviceList` and subclasses do persistence and matching only - no Win32 calls at
  all.

### Open/Closed
- A new device category is added by extending `DeviceKindResolver`'s GUID tables (`Core/UsbKind.cs`)
  or `BluetoothActions.GetKindFromCoD` - existing resolution logic is not rewritten.
- A new command is added by adding a record + enum member + dispatcher `case` + handler
  method - the dispatch loop itself never changes shape.

### DRY (Don't Repeat Yourself)
- `Core/StorageLocation.cs` holds the single `%ProgramData%\DlpEndpointMonitor` computation
  used by both `UsbDeviceList` (whitelist/blacklist) and `DisabledDevices` - it used to be
  copy-pasted identically into both files when `DisabledDevices` was added. That duplication
  was a real, concrete cost: the storage location has moved twice in this project's history
  (`%ProgramData%\DlpEndpointMonitor\` -> `~/.dlp` -> back to
  `%ProgramData%\DlpEndpointMonitor\`, section 6), and each move had to be applied in two
  places instead of one, with no compiler check to catch a missed spot if they'd drifted.
  Extracting the shared constant removes that risk for the next move.
- The general rule: a third occurrence of the same literal value, constant, or block of logic
  is the signal to extract a shared helper into the correct layer (`Actions/*` for Win32
  wrappers, `Core/*` for shared state/constants) - not to keep copy-pasting. Two coincidentally
  similar lines are not yet a violation; three that must change together are.

### Interface Segregation
`Handlers/IHandlers.cs` splits into six narrow interfaces (`IClipboardHandler`,
`IUsbStorageHandler`, `IUsbDeviceHandler`, `IUsbProtectionHandler`,
`IClipboardProtectionHandler`, `IControlHandler`) rather than one god-interface. Two
exceptions are deliberate, for opposite reasons: whitelist and blacklist share
`IUsbProtectionHandler` because their mutual-exclusivity logic (enabling one disables the
other) genuinely needs both lists in the same place; clipboard whitelist/blacklist share
`IClipboardProtectionHandler` for the mirror-image reason - they do NOT have mutual-exclusivity
logic (both can be enabled at once, section 5.10), but they do share one `reevaluate` delegate,
so keeping them together avoids two near-identical interfaces.

### Dependency Inversion
`CommandDispatcher` depends on the six handler *interfaces*, not the concrete
`Handlers.Windows.*` classes - `Program.cs` is the one place concrete types are
constructed and injected. If this binary ever targets a non-Windows platform, only
`Handlers/Windows/*` and `Actions/*`/`Win32/*` would need replacing.

### Command-Query Separation
Every `Handle(...)` method is `void` - it performs a side effect and emits an event; it
never returns a value the caller inspects. `UsbDeviceList.GetAll()` returns data and
changes nothing; `Add`/`Remove`/`Set`/`Clear`/`SetEnabled` change state and return nothing.

### Fail Fast, Fail Safe
- `Program.cs` checks for the whitelist/blacklist conflict state at startup, before
  accepting any command.
- `UsbActions.IsRemovable` and `IsProtectedInternal` fail **safe**, not fast: an
  undeterminable device is treated as protected/internal rather than risking a bricked
  input device. This is the one place "fail fast" is deliberately inverted in favor of a
  conservative default - know the difference before applying "fail fast" as a blanket rule
  here.
- Nullable reference types are enabled and meant to be respected - do not silence a
  nullable warning with `!` without having actually proven non-null at that point.

### YAGNI
- There is no configuration system, feature flag mechanism, or plugin architecture - one
  binary, one behavior set, controlled entirely by the whitelist/blacklist files and the
  command protocol.
- No abstraction exists for "future device buses" beyond USB/Bluetooth/Display - if a
  fourth bus is ever needed, add it the same way the third one was added (a new
  `Monitors/*` + `Actions/*` pair), not by generalizing ahead of need.

---

## 9. Technology choices

- **`.NET 10`, `win-x64`, self-contained, single-file, trimmed** - the binary must run on
  a managed endpoint with no assumption of a pre-installed .NET runtime, and must start
  fast as a child process of the sibling Node.js agent.
- **Zero external NuGet dependencies in the shipped binary** - every capability comes from
  the BCL or direct Win32/SetupAPI/CfgMgr32/Bluetooth P/Invoke. This keeps the trimmed/AOT-
  friendly surface entirely under this repo's control. `DlpEndpointMonitor.Tests/` (xUnit) is
  the one exception, and is deliberately scoped to a separate, dev-only, non-published
  project - the `dotnet publish` artifact remains dependency-free.
- **`System.Text.Json` source generation** (not the reflection-based serializer) for every
  command/event/persisted-state shape - required for trimming to be safe; the only
  reflection in the whole binary is `SchemaExporter`, deliberately confined to `--schema` mode.
- **Result-tuple error handling** (`(bool ok, string? error)`) over exceptions for every
  Win32-facing operation - Win32/CfgMgr failures (device busy, access denied, not found)
  are expected control flow here, not exceptional.
- **JSON-lines over stdin/stdout** as the entire integration surface - no HTTP server, no
  named pipes, no sockets. This process assumes nothing about who is on the other end of
  its stdio.

---

## 10. Known pending issues / bug history

These are documented in code comments; repeated here because they explain *why* certain
lines look the way they do, so a future change does not accidentally reintroduce them.

- **[fixed] `ObjectDisposedException` crash in `DisplayMonitor.OnDisplayChanged`.** The
  800ms debounce continuation disposed the previous `CancellationTokenSource` but the
  field could still point at it; a later `WM_DISPLAYCHANGE` calling `.Cancel()` on the
  already-disposed source threw unhandled **on the message-loop thread**, which killed the
  entire process (a Win32 callback that throws back into unmanaged code is fatal). Fixed
  by owning the CTS lifecycle in one place: null out the field, cancel + dispose the
  *previous* token synchronously, then install the new one - never in a continuation that
  races a fresh `WM_DISPLAYCHANGE`.
- **[fixed] Instance ID mis-derivation for some HID paths.** `UsbActions.ToInstanceId`
  originally stripped only a trailing `#{guid}` and left a `\reference` tail
  (e.g. `\wavemicin`) in the resulting "instance ID", which `CM_Locate_DevNodeW` rejected
  with `CR_INVALID_DEVICE_ID (0x1E)`. Fixed by dropping everything from the interface GUID
  onward, not just the guid token itself. Startup enumeration additionally now prefers the
  real instance ID from `SetupDiGetDeviceInstanceId` over any path-derived one, because a
  Bluetooth-LE HID mouse's path collapsed to the bare string `"hid"` under the old parse -
  matched correctly as `kind=mouse` for reporting, but silently never actually disabled.
- **[fixed] `KSCATEGORY_CAPTURE` mis-mapped to `video`.** See section 3 - this GUID also
  carries onboard audio (mic/speaker/HDMI-audio) topology, so a "block video" sweep
  disabled sound hardware. Only `KSCATEGORY_VIDEO` is video-specific.
- **[fixed] Bluetooth HID devices blocked at the wrong node.** Disabling the raw HID leaf
  node let vendor software (Logitech Options, etc.) silently re-enable it; disabling the
  Bluetooth radio would have taken out every paired device. Fixed by walking up to the
  peripheral's own `BTHLEDEVICE\`/`BTHENUM\` node (`GetBluetoothDeviceNode`) and disabling
  exactly that.
- **[fixed] Built-in-input detection wrongly protected wireless BT/BLE input.** Windows
  reports Bluetooth input devices as "non-removable" via `CM_DRP_REMOVAL_POLICY`, which
  made an early removal-policy-only check treat a wireless mouse as built-in. Fixed by
  checking Bluetooth ancestry *first* and always treating a BT/BLE-backed input device as
  external, falling back to the USB removal-policy check only when there is no Bluetooth
  ancestor.
- **[fixed] Duplicate whitelist/blacklist entries.** `UsbDeviceList` now dedupes on
  `Add`/`Set` via `SameDevice` (case-insensitive vid/pid/serial/mac/kind match, label
  ignored).
- **[fixed] Unblock never worked across a restart / did not exist at all.** Before
  `DisabledDevices` was introduced, there was no persisted record of what this process had
  disabled, so a disabled device (which has no active interface to re-discover by
  enumeration) could not be reliably re-enabled once policy loosened, especially across a
  process restart. Fixed by recording the exact disabled instance ID and reconciling
  against that persisted list in `RestoreCompliant`, plus adding the
  `usb_device_unblocked`/`usb_device_unblock_failed` events so a consumer can see the
  outcome.
- **[fixed] Whitelist with a single-kind entry (e.g. `{kind: keyboard}`) collateral-blocked
  the very device it was meant to allow.** A composite USB keyboard enumerates as multiple
  device interfaces resolving to different `DeviceKind`s (one `Keyboard`, a sibling `Hid`);
  the sibling didn't match the whitelist entry, and blocking it escalated to disabling the
  shared composite parent - killing the whole physical keyboard. Worse, the persisted
  `DisabledDeviceRecord` carried the sibling's (wrong) kind, so `RestoreCompliant` could
  never recognize the device as compliant again even after fixing policy - only a manual
  `device_enable` (or unplugging entirely) recovered it, and a physical replug never helped
  (disable persists across replug by design). Fixed by evaluating compliance at the
  composite-device **group** level (section 5.2, `IsGroupCompliant`) rather than per
  interface, and by having restore re-derive compliance from live sibling state (or
  unconditionally re-enable and let a fresh arrival re-evaluate, section 5.4) instead of
  trusting the record's stale per-interface identity.
- **[fixed] Unclassifiable devices (parse failure or unrecognized interface GUID) were
  invisible to policy, not merely allowed by it** - "HDMI and other devices not in the
  whitelist could connect." See section 5.8 for the full fix (`ParsePartialDevice`/
  `ParseMonitorPath` no longer return `null` for these cases) and the `IsProtectedInternal`
  extension (section 5.1) added as its safety net.
- **[fixed, then superseded] Disabling/clearing whitelist didn't restore Bluetooth or
  external displays.** Two unrelated causes: (1) `EnableExternalDisplays` hardcoded
  `ok = true` regardless of the actual `SetDisplayConfig` result, so a real failure was
  silently reported as success (see section 5.6); (2) blocking a Bluetooth device unpaired it
  outright, and Windows has no API to undo an unpair - there was no restore path to have a bug
  in, by design. Fixed at the time by switching Bluetooth blocking to a reversible
  device-node disable, with a fallback to unpair only when MAC-to-instance-ID resolution
  couldn't find a matching node. **This was later superseded**: Bluetooth blocking is now
  unpair-based again, deliberately - see section 5.5 for why (Windows Settings' Connected/
  Paired indicator reads the pairing store, not devnode state, so a disable-based primary
  action never made Settings agree with reality). The non-restorability this reintroduces is
  an accepted trade, not a regression of this fix.
- **[fixed] `UsbActions.GetBluetoothDeviceNode` stopped one level too shallow for BLE
  devices.** It treated `BTHENUM\` (classic) and `BTHLEDEVICE\` (BLE) as equivalent "the
  peripheral's own node," but live verification showed `BTHLEDEVICE\` is actually a
  GATT-service *child* of the true peripheral node, which lives one level up under `BTHLE\`
  (see section 5.5's verified device tree). Disabling a parent does cascade to disable its
  children in Windows, so this likely still worked in practice, but landed on the wrong node
  conceptually. Fixed to walk up to `BTHLE\` for BLE, matching the new `BluetoothMonitor`
  resolution path exactly.
- **[fixed] `IsProtectedInternal`'s first-line gate silently skipped `DeviceKind.Network`,
  so a "block network kind" policy disabled the machine's own built-in WiFi/Ethernet
  adapter with no protection at all.** Root cause of the real user report ("previously it
  disable the wifi driver, the network wifi adapter") that the still-open item below was
  blocked on. `GUID_DEVINTERFACE_NET` is exposed by any NDIS adapter, built-in or USB, so
  `UsbMonitor`'s generic per-interface/composite-group pipeline (designed for HID-style
  peripherals, not NIC semantics) reached `BlockDevice` for it - `IsProtectedInternal(kind,
  instanceId)` gated on `StrictInputKinds.Contains(kind) || kind == DeviceKind.Unknown` as
  its first line, and `Network` is in neither set, so it returned `false` immediately
  without ever reaching the bus-ancestry walk. Fixed by (1) extracting that bus-ancestry
  walk into a new reusable `UsbActions.IsBuiltIn(instanceId)` (Bluetooth-ancestor check
  first, then USB-ancestor removability, then "no bus ancestor -> internal"), with
  `IsProtectedInternal` now just gating on kind and delegating to it - behavior for
  Keyboard/Mouse/Hid/Hub/Unknown is unchanged; and (2) giving `DeviceKind.Network` its own
  `NetworkMonitor` (section 3, mirrors `BluetoothMonitor`'s shape: no composite-group
  concept, per-device whitelist/blacklist, own `RestoreCompliant`) whose `BlockDevice` calls
  `IsBuiltIn` directly, before any `DisableDevice` call, and refuses to block when it
  returns `true`. `UsbMonitor` now explicitly excludes `Kind == Network` from
  `OnDeviceChanged`/`EnumerateExisting`/`BlockNonCompliant` so the two monitors partition
  the same `window.DeviceChanged` event stream purely by resolved kind. New wire events:
  `network_device_connected/disconnected/blocked/block_failed/unblocked/unblock_failed` -
  additive, mirrors the `bluetooth_device_*` precedent.
- **[fixed] `UsbMonitor.RestoreCompliant` had no ownership filter at all, so it raced
  `BluetoothMonitor.RestoreCompliant` to re-enable the same Bluetooth devnode.** Independently
  discovered while designing the `Network` fix above (same shared-`DisabledDevices`-list
  ownership problem): `BluetoothMonitor.RestoreCompliant` already filtered to `d.Mac is not
  null`, but `UsbMonitor.RestoreCompliant` iterated `_disabled.GetAll()` unfiltered, so a
  Bluetooth-blocked record (`Mac` set) was processed by both monitors, each independently
  re-enabling the same devnode and emitting its own unblocked event
  (`UsbDeviceUnblockedEvent` and `BluetoothDeviceUnblockedEvent`) for one restore action.
  Fixed by filtering `UsbMonitor.RestoreCompliant`'s foreach to
  `d.Mac is null && d.Kind != DeviceKind.Network`, so each monitor owns a disjoint slice of
  the persisted list (plain USB records only), matching `BluetoothMonitor`'s and the new
  `NetworkMonitor`'s own filters.
- **[added] Clipboard content policy: regex whitelist/blacklist rules over copy/cut/paste,
  enforced at two independent layers.** New capability, not a bug fix - see section 5.10 for the
  full model. New persisted lists (`ClipboardWhitelist`/`ClipboardBlacklist`,
  `Core/ClipboardRuleList.cs`), commands/events (`clipboard_whitelist_*`/`clipboard_blacklist_*`/
  `clipboard_protection_status`, `ClipboardContentBlockedEvent`/`ClipboardContentBlockFailedEvent`),
  and `WindowsClipboardProtectionHandler`. Two deliberate divergences from every existing device
  policy in this doc: (1) both lists can be enabled simultaneously - no `ProtectionMode`/conflict
  concept, no startup conflict-guard, for clipboard; (2) the paste-interception path
  (`KeyboardHook.ShouldBlockPaste`) fails OPEN, not closed, on any internal error, since it is a
  global `WH_KEYBOARD_LL` hook and swallowing a keystroke by accident breaks paste for every
  application on the machine, not just this feature's target content. **Explicitly does NOT
  cover**: right-click "Paste", Shift+Insert, and an application's own Paste button/API all bypass
  the low-level keyboard hook entirely and are not interceptable this way - only a Ctrl+V keydown
  is. Also out of scope: Image and Unknown clipboard content (no text to evaluate a regex
  against) always pass through untouched regardless of policy.
- **[fixed] `DisplayCompanionRelay`/`BluetoothCompanionRelay`'s request/reply pipes threw
  `ObjectDisposedException("Cannot access a closed pipe.")` on a real machine, repeatedly.**
  Both relays wrap ONE `PipeDirection.InOut` pipe with a `StreamReader` AND a `StreamWriter` per
  request; both were constructed with the default `leaveOpen: false`, so whichever disposed
  first (they dispose in reverse declaration order at the end of every request) closed the
  shared pipe out from under the other - `StreamWriter.Dispose` always calls the underlying
  stream's `Flush`, even with nothing buffered, so the second `Dispose` threw. Observed live in
  the user's own `agent.db` event log under both `bluetooth_companion_relay` and
  `display_companion_relay`/`monitor_policy_restore` sources. Fixed by constructing both the
  reader and the writer with an explicit `leaveOpen: true` (a shared `NoBomUtf8` encoding field
  preserves the previous default no-BOM behavior) at all four call sites across both files - the
  existing explicit `pipe.Dispose()` at each call site remains the single owner that actually
  closes the pipe. `ClipboardCompanionRelay.cs` does NOT have this shape (one-way, one reader OR
  one writer per pipe, never both) and needed no change. Regression-covered by
  `DlpEndpointMonitor.Tests/CompanionRelayPipeTests.cs`, which drives a real Server/Client pair
  over the actual named-pipe transport through a 50-iteration loop (a single request/reply did
  not reliably reproduce the bug) and asserts no `error` event is ever emitted.
- **[fixed, root cause confirmed live] `UsbMonitor.BlockDevice`'s Bluetooth branch could leave a
  BLE peripheral fully connected and functional when unpairing it failed.** See section 5.5's
  subsection for the full writeup. Initially suspected as an address-rotation problem
  (`GetBluetoothPairingCandidates`/`RemovePairingAny`/`TryCandidatesInOrder` were added to try
  multiple candidate addresses) - live testing on the real hardware that surfaced this bug
  disproved that theory (the failing address WAS present in Windows' own remembered-devices
  registry) and confirmed the real cause instead: the legacy `BluetoothRemoveDevice` API cannot
  reliably unpair this Bluetooth LE peripheral at all, even given a correct address (Windows
  Settings' own "Remove device" succeeded against the identical address where this API did not).
  The actual fix is `UsbMonitor.BlockDevice` now falling back to `UsbActions.DisableDevice` on
  the primary node whenever `RemovePairingAny` fails for ANY reason (previously only a
  can't-parse-a-MAC edge case triggered this fallback) - decision logic factored into
  `UsbMonitor.ResolveBluetoothBlock`, unit tested in `DlpEndpointMonitor.Tests/UsbMonitorTests.cs`.
  A device blocked via this fallback IS tracked in `DisabledDevices` and can be auto-restored,
  unlike a successful unpair.
- **[fixed] A `--session-companion` process was never killed either before launching a fresh one
  OR when the primary that owns it shuts down, so it accumulated across restarts and outlived
  uninstalls.** Confirmed live twice, from both ends: (1) a clipboard-blacklist rule that had been
  disabled kept getting enforced, traced to an orphaned companion (its own primary long exited)
  still running with the OLD policy loaded at its own startup; (2) after uninstalling the MSI, the
  companion was left running with no primary process at all. Investigated across both repos:
  neither the MSI's `ServiceControl` uninstall action, nor the agent service host's stop/
  process-tree-kill handling, nor this binary's own shutdown paths, ever targeted the companion -
  it lives in a different Windows session via `CreateProcessAsUser`, not a normal process-tree
  child a tree-kill would reach. Windows allows multiple processes to each independently register
  `WM_CLIPBOARDUPDATE`/`WH_KEYBOARD_LL`, so an orphan keeps hooking and enforcing whatever policy
  it started with, right alongside (or after) the primary that owned it is gone, with no error or
  visible sign anything is wrong. Fixed with one helper used at both ends:
  `SessionActions.TerminateCompanionProcesses(sessionId, exePath)` finds any other process at the
  same exe path running in the target session (excluding this process itself) and kills it -
  best-effort (a failure to enumerate/kill one leftover must never block the caller's next step).
  `Program.cs` calls it immediately before `LaunchIntoSession` (so a restart replaces rather than
  accumulates), and a `stopCompanion` delegate - set when the companion is launched, closing over
  that exact session/exePath - is called from BOTH this process's own shutdown routes: the
  Ctrl+Break/cancellation `finally` block, and `WindowsControlHandler.Handle(ShutdownCmd)` before
  `Environment.Exit(0)`. Does not cover a hard kill/crash of the primary bypassing both routes -
  only the two shutdown paths this binary itself controls.
- **[fixed] Introducing `DisplayChangeRelay.Client` on the companion's entry thread starved the
  two companion-hosted relay servers, breaking Bluetooth enumeration and display-topology actions.**
  Confirmed live in the very release that added `DisplayChangeRelay`: `bluetooth_companion_relay` and
  `monitor_policy_apply` (`display companion relay`) both logged "could not connect to companion
  pipe", while `clipboard companion ready` logged fine. Root cause: the first cut of the wiring
  constructed `displayChangeRelayClient` on `Program.cs`'s companion-branch entry thread BEFORE
  `displayRelayServer`/`bluetoothRelayServer` and before `companionThread.Start()` - its constructor
  blocks up to ~2s on a bounded connect retry (same shape as `ClipboardCompanionRelay.Client`), which
  delayed those two companion-hosted servers from starting to listen. The PRIMARY's own first
  connection attempts to them - fired almost immediately at startup via
  `Task.Run(displayMonitor.BlockNonCompliant)`/`Task.Run(bluetoothMonitor.EnumerateExisting)`, each
  with their OWN ~2s connect-retry budget - lost that race on a slower/cold-starting machine.
  Clipboard was unaffected because it doesn't depend on any companion-hosted server. Fixed by moving
  the `DisplayChangeRelay.Client` construction and `window.DisplayChanged` subscription into a
  `Task.Run` inside the companion thread's own message-pump body (after `companionReady.Set()`,
  before `MessageWindow.RunMessageLoop()`), fully decoupling it from both the entry thread's
  sequential setup and the message-pump startup itself. See AGENTS.md section 10 for the "lesson
  for any future companion-side relay client" this leaves behind.

### Not implemented - worth flagging before relying on it

- **No CI** (no `.github/workflows`) - `dotnet build`/`dotnet test` are run by hand (AGENTS.md
  section 8.1).
- **Automated tests cover only the hardware-independent pure-logic layer.**
  `DlpEndpointMonitor.Tests/` (xUnit) implements `ai_agent_doc/TEST-PLAN.md` section 2 in full: 104
  tests covering `DeviceKindResolver`, the USB/Bluetooth/Display path-parsing functions,
  whitelist/blacklist matching+dedup (via a storage-directory constructor overload on
  `UsbDeviceList`/`DeviceWhitelist`/`DeviceBlacklist`/`DisabledDevices` added specifically to
  make this safe to test - defaults to the exact prior `%ProgramData%\DlpEndpointMonitor\`
  behavior when omitted, so every production call site is unaffected), clipboard
  whitelist/blacklist matching+dedup+AND-combination (`ClipboardRuleListTests.cs`, same
  storage-directory-seam pattern), `CommandDispatcher`, `EventEmitter`, and
  `SchemaExporter`. **Device-blocking and display-topology logic itself is still not
  automated** - `Monitors/*` and `Handlers/Windows/*` call `Actions/*` as static methods with
  no substitution point, so that layer depends on real Win32/hardware/OS state that cannot
  be faked without introducing a seam (`ai_agent_doc/TEST-PLAN.md` section 4 sketches what that would
  look like, not implemented). Today's "test" for that part is a careful code read plus
  manual verification on a real machine (`ai_agent_doc/TEST-PLAN.md` section 3's manual matrix: plug/
  unplug the relevant device class, toggle whitelist/blacklist, watch the event stream).
  `ai_agent_doc/TEST-PLAN.md` section 1 has the full feasibility breakdown per file/function.
- **[resolved as a different bug than originally suspected] Network/WiFi adapter
  restore/blocking.** Originally filed as a restore-path mystery ("`RestoreCompliant`/
  `IsRecordCompliant` appear correct by inspection"), but the real root cause was upstream
  of restore entirely: `IsProtectedInternal` never protected `DeviceKind.Network` in the
  first place, so the built-in adapter was disabled with no safety net and the *block* side,
  not the restore side, was the bug - see the bug-history entry above (`IsBuiltIn` +
  `NetworkMonitor`) for the fix.
- **Classic Bluetooth (`BTHENUM\`) device-tree assumptions are not live-verified**, unlike
  BLE (section 5.5) - no classic-paired device was available to test against this session.
  `UsbActions.GetBluetoothDeviceNode` assumes `BTHENUM\` is a flat, 2-level hierarchy
  (peripheral -> HID child) with no intermediate layer analogous to BLE's `BTHLEDEVICE\`.
  Verify with `Get-PnpDevice | Where-Object { $_.InstanceId -match '^BTHENUM' }` against a
  real classic-paired device before trusting this path blind.
- **Display restore may still need a symmetric-reactivation redesign.** The confirmed
  `EnableExternalDisplays` bug (section 5.6/10) is fixed, but if `SDC_TOPOLOGY_EXTEND` itself
  turns out not to reliably reactivate a path `DisableExternalDisplays` deliberately dropped
  from the CCD database, the next step is re-querying and explicitly reactivating the specific
  external path(s) instead of the blunt auto-topology switch - not implemented, unverifiable
  without a real external monitor.
- **No packaging/installer.** There is no MSI/installer project, no service-registration
  script, and no documented uninstall procedure in this repo. Whatever runs this binary
  as a persistent Windows service, and whatever cleans up OS-persistent state on removal
  (the global USB storage registry toggle, any per-device disables, any saved display
  topology) is presumed to live in the sibling agent repository or a future packaging
  effort here - do not assume it exists.
- **No `.editorconfig` / analyzer ruleset.** Style consistency today is "match what's
  already there," not an enforced rule. If you add one, keep it additive (do not
  reformat the whole codebase in the same change).
- **Confirmed live: the legacy `BluetoothRemoveDevice` API cannot unpair this Logitech MX
  Vertical Bluetooth LE mouse, independent of address correctness** (section 5.5). The disable
  fallback means the device is now actually blocked either way, but Windows Settings will only
  show it as correctly unpaired/removed when the legacy API happens to work (classic devices,
  or BLE devices this API does support) - for hardware like this one, Settings will keep showing
  the device as paired even though it is functionally disabled. The only confirmed way to close
  that cosmetic gap is the WinRT `Windows.Devices.Enumeration`/
  `DeviceInformation.Pairing.UnpairAsync()` API, which requires adding the `CsWinRT` NuGet
  package - a deliberate, explicit exception to this project's zero-NuGet-dependency constraint,
  not something to add without a human decision to accept that tradeoff.

---

## 11. Alert Host architecture (standalone capability, not yet wired into any block path)

**NOT YET WIRED INTO ANY BLOCK PATH.** Everything in this section is a standalone, callable,
independently-testable capability - `Actions/AlertActions.ShowAlert(...)` exists and works, but
no `Monitors/*` (`ClipboardMonitor`, `UsbMonitor`, `BluetoothMonitor`, `DisplayMonitor`,
`NetworkMonitor`) calls it yet. Wiring a block/violation event to actually pop an alert is
deliberately left as a follow-up, separate change - this section documents the delivery
*mechanism* only.

### 11.1 Why a second process is required (Session 0 isolation)

This binary is designed to run as a Windows service under `LocalSystem`, in Session 0 (see the
sibling `dlp_v2/agent` repo's packaging - `Account="LocalSystem"` in its WiX product definition
- for how it is actually deployed; nothing in *this* repo changes that deployment). Session 0 is
a non-interactive session with no desktop - a WPF `Window` created there is never visible to
anyone, no matter how correctly it is coded. The only way to show a window to the logged-on user
is a **separate process running in that user's own interactive session**. That process is
`DlpEndpointMonitor.AlertHost.exe` (`DlpEndpointMonitor.AlertHost/`, a `net10.0-windows` WPF app),
and `Actions/AlertActions.cs` is the one place the main binary knows how to reach it - it never
touches WPF, System.Windows, or any UI type itself, keeping the shipped binary's trimmed,
`win-x64`, zero-WPF-dependency build exactly as it was (its only new reference is
`DlpEndpointMonitor.AlertContracts`, which is plain records/enums, not WPF).

### 11.2 Delivery mechanism: mutex-guarded singleton owning a named pipe

`AlertHost.exe` may be launched many times over a machine's uptime (once per alert, in the
naive case) but at most one instance per interactive session may ever own a visible window
queue - otherwise two alerts could show at once, or two instances could race to answer the same
request. Ownership is decided by a **session-scoped named `Mutex`**,
`"DlpEndpointMonitor.AlertHost.Singleton"` (deliberately no `"Global\"` prefix: a `Global\` mutex
is one instance for the *whole machine*; this needs one owner *per session*, since a machine can
have multiple interactive sessions - fast user switching, RDP - each needing its own alert
delivery). `App.OnStartup` (`AlertHost/App.xaml.cs`) creates the mutex with
`initiallyOwned: true`:

- If `createdNew` is true, this instance **is** the owner: it starts an `AlertQueue`
  (section 11.3) and a `PipeTransport.Server` (`AlertHost/PipeTransport.cs`) listening on a
  named pipe, `NamedPipeServerStream`, whose name is `AlertPipe.Name` - a single constant
  defined once in `DlpEndpointMonitor.AlertContracts/AlertPipe.cs` and referenced by both sides,
  never hand-typed as a literal in more than one place.
- If `createdNew` is false, an owner already exists: this instance is a **one-shot client** -
  it opens a `NamedPipeClientStream` to `AlertPipe.Name`, writes exactly one newline-terminated
  JSON `AlertRequest` line (mirroring the newline-delimited-JSON convention this repo already
  uses between the main binary and its stdin/stdout), and exits immediately via `Shutdown(0)`.

The owner's `PipeTransport.Server` runs an accept loop that reads newline-delimited JSON lines
from whichever client is currently connected and forwards each parsed `AlertRequest` into the
owner's `AlertQueue.Enqueue(...)`. A malformed line, or a client disconnecting mid-read, is
logged and does not take down the accept loop - the same "one bad input must not kill the
monitor thread" discipline this repo already applies elsewhere (section 8's error rules).

**Bootstrapping the very first alert** in a session needs care: launching a brand-new
`AlertHost.exe` and immediately trying to pipe-connect to it is a race (the new process has not
created its pipe server yet). Instead, `AlertActions.ShowAlert` encodes that first
`AlertRequest` as base64 JSON and passes it as a `--initial-alert=<base64>` command-line
argument; the new instance decodes it in `App.OnStartup` and enqueues it the moment it wins the
mutex, with no reconnect race at all. It still starts its own pipe server afterward for any
further requests that arrive during its lifetime.

### 11.3 Session-crossing launch (`Actions/AlertActions.cs`)

`ShowAlert(AlertRequest)` first tries the pipe (`TrySendToRunningOwner`, a client-only,
minimal re-implementation of the same newline-JSON protocol - it cannot literally share code
with `AlertHost.PipeTransport` because `AlertContracts`, the one project both sides may
reference, is deliberately kept dependency-free, and the main binary must not take a project
reference on a WPF app just to reuse a few lines of pipe-client code). If nobody answers within
a short timeout, it launches a new `AlertHost.exe`:

- **Same session** (the common case in manual/dev testing, where this binary is not actually
  running as a service): `Process.GetCurrentProcess().SessionId` already equals
  `WTSGetActiveConsoleSessionId()`, so a plain `Process.Start` is enough - no token work needed.
- **Different session** (the real deployment - `LocalSystem` in Session 0, interactive user in
  another session): `WTSQueryUserToken` gets that session's user token, `DuplicateTokenEx`
  turns it into a primary token suitable for `CreateProcessAsUser`, `CreateEnvironmentBlock`
  builds that user's environment block, and `CreateProcessAsUser` launches the exe with
  `STARTUPINFO.lpDesktop` set to `"winsta0\\default"` - omitting that last part is *the* classic
  way this exact pattern silently fails (the process starts, but on a non-interactive window
  station, so its window is never seen - no exception, no error code). Every handle acquired
  along the way (`userToken`, `primaryToken`, `environment`, `procInfo.hProcess`/`hThread`) is
  released in a `finally` block regardless of which step failed. Every failure path returns
  `(false, "<reason>")`, matching every other `Actions/*` method in this codebase - nothing here
  throws for an expected Win32 failure (no one logged in yet at boot, a duplicated token
  rejected, `CreateProcessAsUser` itself failing).

### 11.4 Queueing policy (`AlertHost/AlertQueue.cs`)

The owner's `AlertQueue` is a single in-memory producer/consumer (a lock-protected queue +
`SemaphoreSlim`, not literally `System.Threading.Channels.Channel<T>`, so the coalesce lookup
below has somewhere to live) with one dispatcher loop that shows at most one alert window at a
time, applying two policies as requests are enqueued:

- **COALESCE** - while an entry for the same `(Type, Severity)` pair is still pending (enqueued
  but not yet dequeued for display), a new request with that same pair does not open a second
  window; it increments a running count on the existing pending entry, folded into the shown
  message's title as `" (+N more)"` once it is finally displayed. A request that arrives *after*
  the matching entry has already been dequeued for display starts a fresh pending entry instead
  of trying to fold into a window already on screen.
- **CAP** - at most 5 distinct-key pending entries are held at once; anything beyond that is
  dropped, not silently - the drop is logged to `Console.Error` (`AlertHost` has no
  `EventEmitter`; it is a separate process/project) so a dropped alert is never invisible.

The dispatcher loop calls its `show` callback synchronously and blocks until the callback
returns - `App.ShowAlertWindow` hops onto the WPF dispatcher thread via `Dispatcher.Invoke` (not
`InvokeAsync`/`BeginInvoke`, specifically so the call blocks) and calls `Window.ShowDialog()`,
which itself blocks until the window is dismissed (timer elapsed or clicked). That is the entire
mechanism behind "never two windows visible at once" - no separate visibility-tracking state is
needed.

### 11.5 Visual design and where the color palette came from

Both `AlertType` values (`Toast`, `FullScreen`) share one visual shape - a single reusable
control, `AlertHost/Controls/AlertBox.xaml(.cs)` - a rounded box (20px corner radius) with a
colored header band across the top (`Title`, white foreground) and a plain white body below
(`Message`, run through `RichTextParser`, section 11.6). They differ only in size, placement, and
backdrop:

- **Toast** - small window anchored to the bottom-right screen corner (common OS toast
  convention), auto-closes after `DurationSeconds` (default 5) or on a click anywhere on it.
  `ShowInTaskbar="False"` - transient, should not clutter the taskbar.
- **FullScreen** - covers the entire screen edge-to-edge, filled solid with the severity color as
  the backdrop, with the same rounded `AlertBox` centered on top in white, so the visual reads as
  one continuous severity color from the screen edge through the header band, breaking to white
  only for the body. Auto-closes the same way as Toast. `ShowInTaskbar="False"`. Also the fail-safe
  fallback in `App.ShowAlertWindow`'s switch for any future/unmapped `AlertType`, since it is the
  hardest of the two to miss.

Both are `Topmost="True"` - the entire point of an alert is that it is not hidden behind another
window.

Severity maps to color (`AlertHost/Resources/Colors.xaml`, consumed everywhere through
`Controls/SeverityBrushes.cs` so there is exactly one place mapping `AlertSeverity` to a brush):
`Info` -> `#FF1B7FA9` (brand blue), `Warning` -> `#FFEB980A` (amber), `Blocked` -> `#FFEE3A3A`
(red), all with a white (`#FFFFFFFF`) header foreground. Surface/background/body tokens
(`#FFFFFFFF` card white, `#FFF4F7F8` page background, `#FF151B1F` body text, `#FFDAE2E5` border)
round out the palette. **These exact hex values were computed directly from the sibling
`dlp_v2/controlcenter` web dashboard's HSL design tokens
(`dlp_v2/controlcenter/app/globals.css`)** - they are not this project's own invention, and
should not be recomputed or approximated differently if the dashboard's tokens ever change;
re-derive from that file, the same way these were.

### 11.6 Rich text in `Message` (`AlertHost/RichTextParser.cs`)

`AlertRequest.Message` stays a plain string on the wire (no HTML type in `AlertContracts`), but
may contain a small, **closed** allowlist of inline tags: `<strong>`/`<b>` (bold), `<em>`/`<i>`
(italic), `<br>` (line break) - case-insensitive. `RichTextParser.Parse` converts such a string
into a sequence of WPF `Inline` objects (`Run` with `FontWeight`/`FontStyle` set, or
`LineBreak`) for a `TextBlock.Inlines` collection. This is deliberately **not** a general HTML
parser - no XML/HTML parsing library, no WPF `WebBrowser` control - and it must **never throw**:
any tag outside the allowlist is silently stripped (the surrounding text still renders as plain
text), and an unclosed/malformed tag simply never matches the tag regex and falls through as
plain text rather than being treated as an error. A top-level `try/catch` around the whole parse
is a last-resort fallback to one plain-text `Run` of the original message, in case something
still misbehaves on a truly pathological input.

### 11.7 Contracts (`DlpEndpointMonitor.AlertContracts/`)

Referenced by both the main binary and `AlertHost`, and deliberately kept to plain
records/enums + one `System.Text.Json` source-gen `JsonSerializerContext`
(`AlertJsonContext.cs`) - the same reasoning already documented in section 7/`SchemaExporter`'s
one deliberate reflection exception applies here: introducing a second reflection-based JSON
path anywhere in this dependency chain would compromise the main binary's trimmed,
self-contained build. `AlertRequest(Type, Title, Message, Id, Severity = Info, DurationSeconds = 5)` -
`Id` is a required correlation field for every alert type (Toast, FullScreen), not
optional; both `AlertActions.ShowAlert` and `AlertHost.AlertQueue.Enqueue` reject a
null/blank `Id` rather than showing an uncorrelatable alert, since a JSON-deserialized request
can carry a blank string despite the compile-time non-nullable signature.
`AlertType { Toast, FullScreen }`, `AlertSeverity { Info, Warning, Blocked }`,
`AlertPipe.Name` (the one shared pipe-name constant, section 11.2).

### 11.8 What is deliberately out of scope here

- **Wiring `ShowAlert` into any block path** - no `Monitors/*` calls it yet; this is a
  standalone capability today, wiring is an explicit follow-up.
- **An installer/packaging entry for `AlertHost.exe`** - that belongs in the sibling `dlp_v2`
  repo's own packaging, same as this binary's own service registration (section 10's "No
  packaging/installer" entry already covers this binary; `AlertHost.exe` inherits the same gap).
- **A persisted/DB-backed alert history** - `AlertQueue` is purely in-memory, owned entirely by
  the current session's owner process; nothing about a shown or dropped alert survives that
  process exiting.

---

## 12. Where to look for the truth

| To answer... | Open this file |
|---|---|
| Every command's exact shape | `Commands/Commands.cs` |
| Every event's exact shape | `Core/EventEmitter.cs` |
| The command/event enum vocabularies | `Core/Enums.cs` |
| How a stdin line becomes a handler call | `Core/CommandDispatcher.cs` |
| Whitelist/blacklist enable/disable/mutate interplay | `Handlers/Windows/WindowsUsbProtectionHandler.cs` |
| The USB block/restore escalation ladder | `Monitors/UsbMonitor.cs`, `Actions/UsbActions.cs` |
| Group compliance for composite USB devices | `Monitors/UsbMonitor.cs` (`IsGroupCompliant`, `IsRecordCompliant`), `Actions/UsbActions.cs` (`EnumerateGroupSiblings`) |
| The built-in-input safety gate | `Actions/UsbActions.cs` (`IsProtectedInternal`) |
| Why unclassifiable devices are blocked under whitelist | `Actions/UsbActions.cs` (`ParsePartialDevice`), `Actions/DisplayActions.cs` (`ParseMonitorPath`) |
| Bluetooth device matching/blocking/restore | `Monitors/BluetoothMonitor.cs`, `Actions/BluetoothActions.cs` (`RemovePairing`) |
| Why `BTHLEDEVICE\` is not the Bluetooth peripheral's own node | `Actions/UsbActions.cs` (`GetBluetoothDeviceNode`), PROJECT.md section 5.5 |
| Why a BLE unpair can need more than one candidate address, why unpair can still fail regardless, and the disable fallback | `Actions/UsbActions.cs` (`GetBluetoothPairingCandidates`), `Actions/BluetoothActions.cs` (`RemovePairingAny`, `TryCandidatesInOrder`), `Monitors/UsbMonitor.cs` (`ResolveBluetoothBlock`), PROJECT.md section 5.5 |
| The companion-relay double-dispose fix (leaveOpen:true) | `Core/DisplayCompanionRelay.cs`, `Core/BluetoothCompanionRelay.cs`, `DlpEndpointMonitor.Tests/CompanionRelayPipeTests.cs` |
| Display topology blocking/restore | `Monitors/DisplayMonitor.cs`, `Actions/DisplayActions.cs` |
| Why a headless primary missed pure Win+P projection switches, and the relay that fixes it | `Core/DisplayChangeRelay.cs`, `Monitors/DisplayMonitor.cs` (`NotifyExternalDisplayChange`), section 5.6 |
| GUID/CoD -> DeviceKind resolution | `Core/UsbKind.cs`, `Actions/BluetoothActions.cs` |
| The Win32 message pump / STA requirement | `Core/MessageWindow.cs`, `Program.cs` |
| Every P/Invoke signature and struct | `Win32/NativeMethods.cs` |
| The `--schema` JSON-Schema export | `Core/SchemaExporter.cs` |
| Persisted state file format/location | `Core/UsbDeviceList.cs`, `Core/DisabledDevices.cs` |
| The Alert Host delivery mechanism (session-crossing launch, mutex/pipe singleton, queueing, visual design) | section 11, `Actions/AlertActions.cs`, `DlpEndpointMonitor.AlertHost/` |
| The short operating guide | `ai_agent_doc/AGENTS.md` |
