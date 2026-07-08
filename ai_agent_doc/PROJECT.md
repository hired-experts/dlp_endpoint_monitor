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

`DeviceEntryDto(vid?, pid?, serial?, mac?, kind?, label?)` is the shared shape used inside
every `*_set` command's `entries` array.

`device_disable`/`device_enable` act on an arbitrary PnP instance ID directly - they are
**not** policy-aware (no whitelist/blacklist involved), unlike everything under
"protection". Use them for one-off manual control; use whitelist/blacklist commands for
persisted policy.

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
| `usb_drive_connected` / `usb_drive_disconnected` | `UsbDriveConnectedEvent` / `DisconnectedEvent` | `drives: string[], ts` | `DBT_DEVTYP_VOLUME` arrival/removal |
| `usb_device_detected` | `UsbDeviceDetectedEvent` | `vid?, pid?, serial?, devicePath, usbClass?, kind, nativeClass?, groupId?, ts` | Arrival where VID/PID could not be parsed |
| `usb_device_connected` / `usb_device_disconnected` | `UsbDeviceConnectedEvent` / `DisconnectedEvent` | `vid, pid, serial?, usbClass?, kind, nativeClass?, groupId?, devicePath, allowed(only on connect), ts` | USB device-interface arrival/removal |
| `usb_device_blocked` / `usb_device_block_failed` | `UsbDeviceBlockedEvent` / `BlockFailedEvent` | `..., instanceId, error?(failed only), ts` | After a block attempt |
| `usb_device_unblocked` / `usb_device_unblock_failed` | `UsbDeviceUnblockedEvent` / `UnblockFailedEvent` | `vid?, pid?, serial?, kind, instanceId, error?(failed only), ts` | During `RestoreCompliant` |
| `usb_storage_status` | `UsbStorageStatusEvent` | `id?, ok, enabled` | Reply to `usb_storage_status` |
| `device_protection_status` | `DeviceProtectionStatusEvent` | `id?, ok, mode, error?` | Reply to `device_protection_status`, also usable standalone |
| `device_whitelist_get` / `device_blacklist_get` | `DeviceWhitelistGetEvent` / `DeviceBlacklistGetEvent` | `id?, ok, enabled, entries: WhitelistEntryDto[]` | Reply to the `_get` commands |
| `monitor_connected` / `monitor_disconnected` | `MonitorConnectedEvent` / `DisconnectedEvent` | `vid?, pid?, devicePath, ts` | External monitor interface arrival/removal (`vid`/`pid` = EDID manufacturer/product code) |
| `monitor_blocked` / `monitor_block_failed` | `MonitorBlockedEvent` / `BlockFailedEvent` | `vid?, pid?, devicePath, error?(failed only), ts` | After `DisableExternalDisplays` |
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

For a Bluetooth-backed HID device (`GetBluetoothDeviceNode` returns non-null): disable
*that* node (the `BTHLEDEVICE\`/`BTHENUM\` peripheral node), full stop - no group/eject
escalation, because "the group" for a BT peripheral would be the shared Bluetooth radio,
which must never be disabled (it would take out every paired Bluetooth device at once).
This path exists because disabling the raw HID leaf node is unreliable: vendor software
(e.g. Logitech Options) silently re-enables it.

Whichever instance ID actually got disabled (`disabledId`) is recorded in
`DisabledDevices`, keyed by that exact ID - not by VID/PID/kind - because that ID is what
`CM_Enable_DevNode` needs later, and a disabled device is invisible to interface
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

### 5.5 Bluetooth blocking (`BluetoothMonitor`, reversible disable with unpair fallback)

Blocking a Bluetooth device disables its own PnP node (`UsbActions.DisableDevice`, the same
mechanism USB devices use) rather than unpairing it - the device stays paired but
non-functional, and `BluetoothMonitor.RestoreCompliant` can bring it back automatically once
policy allows it again, symmetric with USB. `BluetoothActions.FindInstanceIdByMac(mac)`
resolves the MAC address the classic Bluetooth API gives us (`BluetoothFindFirstDevice`/
`BluetoothFindNextDevice`, used by `EnumerateConnected`) to a PnP instance ID, since that API
never exposes one directly - it walks the `BTHENUM` (classic) and `BTHLE` (BLE) PnP enumerator
branches directly via `SetupDiGetClassDevsByEnumerator`/`SetupDiEnumDeviceInfo` (device
*nodes*, not interfaces) and matches by MAC embedded in each node's own instance ID.

**Verified BLE device tree is three levels deep, one more than classic Bluetooth** (confirmed
live via `Get-PnpDevice`/`Get-PnpDeviceProperty` against real hardware):
```
BTH\MS_BTHLE\...                                  (enumerator driver - never touch)
  -> BTHLE\DEV_<MAC>\...                           (true peripheral node, e.g. "MX Vertical")
       -> BTHLEDEVICE\{service-guid}_..._<MAC>\... (one GATT service child, e.g. the HID service)
            -> HID\{...}_..._COL0N\...             (the actual input-delivering leaf)
```
`BTHLEDEVICE\` is **not** the peripheral - it is a GATT-service child, one level below the
true `BTHLE\` node. Both `BluetoothActions.FindInstanceIdByMac` and
`UsbActions.GetBluetoothDeviceNode` (the sibling mechanism `UsbMonitor` uses for
Bluetooth-backed HID devices arriving via their own HID interface) walk all the way up to
`BTHLE\`, never stopping at `BTHLEDEVICE\`. Classic Bluetooth (`BTHENUM\`) has no such extra
layer - its own node is where both functions stop directly.

**Fallback, not a guarantee**: if `FindInstanceIdByMac` can't find a matching node (an
unusual device, a profile this hasn't been verified against), `BlockDevice` falls back to the
old `RemovePairing` unconditionally - the device is still blocked, just via the irreversible
path for that one device, rather than silently leaving a non-compliant device connected. A
disable that genuinely *fails* after the node *was* found does not fall back to unpair - that
would mask a real failure as an unresolvable-device case.

`DisabledDeviceRecord` carries an optional `Mac` (mirroring `UsbDeviceEntry`'s existing
USB/Bluetooth side-by-side identity pattern) so `RestoreCompliant` can tell a
Bluetooth-disabled record apart from a USB one in the same persisted list. A device blocked
via the unpair fallback has no record and cannot be restored in software - re-pairing is
manual, same as before this change.

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
directory instead of touching the real storage location (see `docs/TEST-PLAN.md` section
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
`Handlers/IHandlers.cs` splits into five narrow interfaces (`IClipboardHandler`,
`IUsbStorageHandler`, `IUsbDeviceHandler`, `IUsbProtectionHandler`, `IControlHandler`)
rather than one god-interface. The one exception is deliberate: whitelist and blacklist
share `IUsbProtectionHandler` because their mutual-exclusivity logic (enabling one disables
the other) genuinely needs both lists in the same place.

### Dependency Inversion
`CommandDispatcher` depends on the five handler *interfaces*, not the concrete
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
- **[fixed] Disabling/clearing whitelist didn't restore Bluetooth or external displays.**
  Two unrelated causes: (1) `EnableExternalDisplays` hardcoded `ok = true` regardless of the
  actual `SetDisplayConfig` result, so a real failure was silently reported as success (see
  section 5.6); (2) blocking a Bluetooth device unpaired it outright, and Windows has no API
  to undo an unpair - there was no restore path to have a bug in, by design. Fixed by
  switching Bluetooth blocking to a reversible device-node disable (section 5.5), with a
  fallback to the old unpair only when the new MAC-to-instance-ID resolution can't find a
  matching node, so blocking never silently stops working for an unresolvable device.
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

### Not implemented - worth flagging before relying on it

- **No CI** (no `.github/workflows`) - `dotnet build`/`dotnet test` are run by hand (AGENTS.md
  section 8.1).
- **Automated tests cover only the hardware-independent pure-logic layer.**
  `DlpEndpointMonitor.Tests/` (xUnit) implements `docs/TEST-PLAN.md` section 2 in full: 78
  tests covering `DeviceKindResolver`, the USB/Bluetooth/Display path-parsing functions,
  whitelist/blacklist matching+dedup (via a storage-directory constructor overload on
  `UsbDeviceList`/`DeviceWhitelist`/`DeviceBlacklist`/`DisabledDevices` added specifically to
  make this safe to test - defaults to the exact prior `%ProgramData%\DlpEndpointMonitor\`
  behavior when omitted, so every production call site is unaffected), `CommandDispatcher`,
  `EventEmitter`, and
  `SchemaExporter`. **Device-blocking and display-topology logic itself is still not
  automated** - `Monitors/*` and `Handlers/Windows/*` call `Actions/*` as static methods with
  no substitution point, so that layer depends on real Win32/hardware/OS state that cannot
  be faked without introducing a seam (`docs/TEST-PLAN.md` section 4 sketches what that would
  look like, not implemented). Today's "test" for that part is a careful code read plus
  manual verification on a real machine (`docs/TEST-PLAN.md` section 3's manual matrix: plug/
  unplug the relevant device class, toggle whitelist/blacklist, watch the event stream).
  `docs/TEST-PLAN.md` section 1 has the full feasibility breakdown per file/function.
- **[resolved as a different bug than originally suspected] Network/WiFi adapter
  restore/blocking.** Originally filed as a restore-path mystery ("`RestoreCompliant`/
  `IsRecordCompliant` appear correct by inspection"), but the real root cause was upstream
  of restore entirely: `IsProtectedInternal` never protected `DeviceKind.Network` in the
  first place, so the built-in adapter was disabled with no safety net and the *block* side,
  not the restore side, was the bug - see the bug-history entry above (`IsBuiltIn` +
  `NetworkMonitor`) for the fix.
- **Classic Bluetooth (`BTHENUM\`) device-tree assumptions are not live-verified**, unlike
  BLE (section 5.5) - no classic-paired device was available to test against this session.
  `BluetoothActions.FindInstanceIdByMac`/`UsbActions.GetBluetoothDeviceNode` both assume
  `BTHENUM\` is a flat, 2-level hierarchy (peripheral -> HID child) with no intermediate layer
  analogous to BLE's `BTHLEDEVICE\`. Verify with `Get-PnpDevice | Where-Object { $_.InstanceId
  -match '^BTHENUM' }` against a real classic-paired device before trusting this path blind.
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

---

## 11. Where to look for the truth

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
| Bluetooth device matching/blocking/restore | `Monitors/BluetoothMonitor.cs`, `Actions/BluetoothActions.cs` (`FindInstanceIdByMac`) |
| Why `BTHLEDEVICE\` is not the Bluetooth peripheral's own node | `Actions/UsbActions.cs` (`GetBluetoothDeviceNode`), `Actions/BluetoothActions.cs` (`FindInstanceIdByMac`), PROJECT.md section 5.5 |
| Display topology blocking/restore | `Monitors/DisplayMonitor.cs`, `Actions/DisplayActions.cs` |
| GUID/CoD -> DeviceKind resolution | `Core/UsbKind.cs`, `Actions/BluetoothActions.cs` |
| The Win32 message pump / STA requirement | `Core/MessageWindow.cs`, `Program.cs` |
| Every P/Invoke signature and struct | `Win32/NativeMethods.cs` |
| The `--schema` JSON-Schema export | `Core/SchemaExporter.cs` |
| Persisted state file format/location | `Core/UsbDeviceList.cs`, `Core/DisabledDevices.cs` |
| The short operating guide | `ai_agent_doc/AGENTS.md` |
