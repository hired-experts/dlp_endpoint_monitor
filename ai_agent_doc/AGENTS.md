# AGENTS.md - Guide for AI Agents Working in DlpEndpointMonitor

**Read this file first.** It explains what this project is, how the pieces fit
together, and the rules to follow when you change code.

This file and its companion live in `ai_agent_doc/` - the single source of the agent's
operating context. `CLAUDE.md` at the repo root loads this file via `@ai_agent_doc/AGENTS.md`.

Companion document:
- `ai_agent_doc/PROJECT.md` - the long, detailed reference (full command/event protocol,
  device-blocking algorithm, engineering principles). Read it when you need depth.

**One rule above all:** if this file disagrees with the actual code, the code is
correct. Update this file to match.

---

## 1. What this project does

DlpEndpointMonitor is the **native Windows monitor** for a larger Data Loss Prevention
(DLP) system. It is a self-contained C#/.NET **console binary** that runs on a managed
Windows endpoint, talks directly to Win32/SetupAPI/CfgMgr32/Bluetooth APIs, and:

- watches USB devices, Bluetooth devices, external monitors, and the clipboard for
  activity, emitting a JSON event line to **stdout** for each thing that happens;
- accepts JSON commands on **stdin** (one per line) to read the clipboard, eject/disable/
  enable a device, and manage a whitelist/blacklist of allowed or blocked devices;
- enforces that whitelist/blacklist itself - it is the enforcement point, not just a
  reporter. When a device is not allowed, this process disables it (or ejects it, or
  removes its Bluetooth pairing, or turns off the external display) without waiting for
  anyone else to tell it to.

This binary is **not** the whole DLP product. A sibling Node.js **agent** process
(in a separate repository) starts this binary as a child process, writes commands to its
stdin, reads events from its stdout, and relays both to a central hub/dashboard so an
operator can see device activity and change policy remotely. That relationship is
one-directional from this repo's point of view: this binary has no network code, no
database, and no knowledge that a dashboard exists. It only knows stdin/stdout JSON.

---

## 2. Where this fits in the bigger system

If you have also seen the sibling `dlp_v2` repository's docs, this is the piece described
there as:

> native binary | C# / .NET (lives in a separate repo, `DlpEndpointMonitor`) | on each
> Windows PC | Talks to Windows directly to watch and control devices. The agent controls
> it by sending JSON lines to its input and reading JSON lines from its output.

Two contracts matter across the repo boundary and must be kept in sync by hand (there is
no shared package):
- The **`DeviceKind`** vocabulary (`Core/Enums.cs`) mirrors a `device-kind.ts` union in the
  Node.js agent repo.
- The full **command/event JSON shapes** are consumed by the agent's generated
  `agent/native/types.ts`, produced by running this binary with `--schema` (section 8).

If you are only working in this repo, you do not need the other repository - just keep
the wire format additive and stable (see "No invention" rules in section 9).

---

## 3. Folder layout

Two projects. `DlpEndpointMonitor/DlpEndpointMonitor.csproj` is the shipped binary: target
`net10.0`, built as a trimmed, single-file, self-contained `win-x64` executable (no runtime
dependency on a shared .NET install), zero NuGet dependencies. `DlpEndpointMonitor.Tests/`
is a dev-only xUnit test project (the only place a NuGet dependency exists in this repo) -
see section 8.1 and `ai_agent_doc/TEST-PLAN.md`.

```
DlpEndpointMonitor/
  Program.cs                 entry point: starts the message-loop thread, wires up the
                              CommandDispatcher, runs until stdin closes or WM_QUIT
  Core/
    CommandDispatcher.cs      reads stdin lines, deserializes by `cmd`, dispatches to a handler
    Enums.cs                  EventType, CommandType, DeviceKind, ClipboardKind, ProtectionMode
    EventEmitter.cs           the ONLY place that writes to stdout; also declares every IEvent record
    UsbDeviceList.cs          abstract base: thread-safe, persisted list of device entries
    UsbWhitelist.cs           DeviceWhitelist : UsbDeviceList (IsAllowed)
    UsbBlacklist.cs           DeviceBlacklist : UsbDeviceList (IsBlocked)
    DisabledDevices.cs        persisted record of devices THIS process disabled, for restart-safe restore
    UsbKind.cs                DeviceKindResolver: Windows interface-class GUID -> DeviceKind
    ClipboardRuleList.cs      abstract base: thread-safe, persisted list of regex clipboard rules
                              (ClipboardRuleEntry(Pattern, Kind?, Label?)); compiles+caches each
                              entry's Regex (with a timeout) on load/mutate - structurally separate
                              from UsbDeviceList (different entry shape/matching), same
                              ReaderWriterLockSlim + atomic-temp-file-write persistence shape
    ClipboardWhitelist.cs     ClipboardWhitelist : ClipboardRuleList (IsAllowed)
    ClipboardBlacklist.cs     ClipboardBlacklist : ClipboardRuleList (IsBlocked, FindMatchedPattern)
    MessageWindow.cs          hidden Win32 window + message pump (clipboard/device/display events)
    JsonDiscriminantAttribute.cs   maps an enum value to the JSON field it discriminates on
    EmitsEventAttribute.cs    documents which event a command replies with (used by SchemaExporter)
    SchemaExporter.cs         `--schema` mode: dumps every command/event shape as JSON Schema
  Commands/
    Commands.cs               every ICommand record (the stdin wire format)
    CommandsJsonContext.cs     System.Text.Json source-gen context for commands
  Monitors/                   Win32-message-driven watchers; live on the STA message-loop thread
    UsbMonitor.cs             USB device arrival/removal, policy enforcement, restore
    BluetoothMonitor.cs       Bluetooth device arrival/removal, policy enforcement, restore
    DisplayMonitor.cs         external monitor arrival, WM_DISPLAYCHANGE debounce, policy enforcement
    NetworkMonitor.cs         NIC arrival/removal, policy enforcement, restore - own monitor (not
                              UsbMonitor's composite pipeline), guarded by UsbActions.IsBuiltIn
    ClipboardMonitor.cs       WM_CLIPBOARDUPDATE -> reads+emits clipboard content, evaluates
                              Text/Files content against whitelist/blacklist, clears the clipboard
                              on violation (copy/cut enforcement layer)
    KeyboardHook.cs           low-level keyboard hook: detects Ctrl+C/X/V/Z (reporting), and on
                              Ctrl+V live-evaluates current clipboard content and swallows the
                              keystroke if it violates policy (paste enforcement layer - fails
                              OPEN on any error, see section 10)
  Actions/                    stateless Win32 P/Invoke helpers, no policy logic
    UsbActions.cs             SetupAPI enumeration, CM_Disable/Enable/Eject, USBSTOR registry toggle
    BluetoothActions.cs       BluetoothFindFirst/NextDevice, RemoveDevice (pairing), MAC-to-PnP-node
                              resolution (FindInstanceIdByMac), path/CoD parsing
    DisplayActions.cs         QueryDisplayConfig/SetDisplayConfig(Paths) topology juggling
    ClipboardActions.cs       OpenClipboard/GetClipboardData/SetClipboardData
  Handlers/
    IHandlers.cs              one interface per command family (IClipboardHandler, IUsbStorageHandler,
                              IUsbDeviceHandler, IUsbProtectionHandler, IClipboardProtectionHandler,
                              IControlHandler)
    Windows/                  the concrete Windows implementation of each handler interface, incl.
                              WindowsClipboardProtectionHandler.cs (clipboard whitelist/blacklist
                              mutations - deliberately does NOT force-disable the other list)
  Win32/
    NativeMethods.cs          every P/Invoke signature and Win32 struct/constant used above
  AppJsonContext.cs           System.Text.Json source-gen context for events + persisted state
DlpEndpointMonitor.Tests/    xUnit project (ProjectReference to the binary above, InternalsVisibleTo
                             from AppJsonContext.cs). One test class per ai_agent_doc/TEST-PLAN.md section 2
                             subsection: DeviceKindResolverTests.cs, UsbActionsParsingTests.cs,
                             BluetoothActionsParsingTests.cs, DisplayActionsParsingTests.cs,
                             UsbDeviceListTests.cs, CommandDispatcherTests.cs, EventEmitterTests.cs,
                             SchemaExporterTests.cs. Covers the hardware-independent pure-logic
                             layer only - see ai_agent_doc/TEST-PLAN.md section 1 for what is and is not
                             unit-testable and why.
```

**Import rule:** there are no path aliases here (single project) - just use normal C#
namespaces (`DlpEndpointMonitor.Core`, `.Actions`, `.Monitors`, `.Handlers.Windows`, etc.).

---

## 4. How the process is put together (read `Program.cs`)

1. `--schema` is checked **first**, before anything touches stdout - if present, dump the
   JSON Schema for every command/event and exit (section 8). This must never run in a
   trimmed build's normal path because it uses reflection.
2. Three in-memory, disk-persisted lists are created: `DeviceWhitelist`, `DeviceBlacklist`,
   `DisabledDevices`. If both whitelist and blacklist were somehow left enabled on disk,
   both are force-disabled and a `startup_conflict` error event is emitted - a machine
   never starts in an ambiguous protection state.
3. A dedicated **STA thread** is started to own a hidden `MessageWindow` and every
   Win32-message-driven monitor (`UsbMonitor`, `BluetoothMonitor`, `DisplayMonitor`,
   `ClipboardMonitor`, `KeyboardHook`). **This is a hard requirement**: clipboard listening,
   device-change notifications, and the low-level keyboard hook all require a message pump
   on an STA thread, and they must all be the *same* thread's pump.
4. Once the window is ready, each monitor's `EnumerateExisting`/`BlockNonCompliant` runs
   once on a **ThreadPool** thread (not the message thread) to catch devices that were
   already connected before the process started.
5. The main thread builds a `CommandDispatcher` with one concrete handler per command
   family and calls `RunAsync()`, which loops reading stdin lines until EOF or
   cancellation. If stdin closes, monitoring keeps running (there is a supervising agent
   process expected to reconnect, but this binary does not assume that - it just keeps
   watching and blocking).
6. On shutdown (`Ctrl+C`, cancellation, or the `shutdown` command), the message window is
   asked to stop (`WM_DESTROY` -> `WM_QUIT` -> pump exits) and a final `shutdown` info
   event is emitted.

There is no dependency-injection container - `Program.cs` wires every handler and monitor
by hand with constructor parameters (including two `Action` delegates, `applyPolicy` and
`restoreDevices`, that let the protection handler trigger re-enforcement on the monitors
without a circular reference).

---

## 5. The protocol: stdin commands in, stdout events out

Every stdin line is one JSON object: `{"id": "<opaque>", "cmd": "<command_name>", ...args}`.
Every stdout line is one JSON object with a `"type"` field. **`EventEmitter.Emit` is the
only place allowed to write to stdout** - never call `Console.WriteLine` anywhere else, and
never write partial output; each `Emit` call is one complete, flushed line, guarded by a
lock so concurrent monitor threads never interleave two JSON objects on one line.

A command usually gets back one `ReplyEvent {id, ok, error?}`. Some commands are
annotated `[EmitsEvent(...)]` in `Commands/Commands.cs` and reply with something richer
instead (e.g. `device_whitelist_get` replies with `DeviceWhitelistGetEvent`, not a plain
reply) - `SchemaExporter` reads that attribute to document the `cmd -> event` mapping.

Full command list, full event list, and every payload shape are in **PROJECT.md section
2**. Do not hand-guess a shape here - the `Commands/Commands.cs` and `Core/EventEmitter.cs`
files are ground truth.

---

## 6. Device kind vocabulary (`DeviceKind`, `Core/Enums.cs`)

`unknown, audio, biometric, bluetooth, camera, hid, hub, keyboard, monitor, mouse, mtp,
network, printer, sensor, smartcard, storage, vendor, video`

This is the enum a whitelist/blacklist entry's `kind` field matches against, and it is
what the sibling Node.js agent's `device-kind.ts` must mirror exactly (see section 2).
`DeviceKindResolver` (`Core/UsbKind.cs`) is the only place that maps a Windows interface
class GUID (or a Bluetooth Class-of-Device) to a `DeviceKind` - if you need a new device
category, add the GUID mapping there, not inline in a monitor.

---

## 7. Policy state (see PROJECT.md section 5 for the full model)

- **Protection mode is derived, never stored as its own field**: `whitelist.IsEnabled` and
  `blacklist.IsEnabled` are two independent booleans; the mode (`none` / `whitelist` /
  `blacklist` / `conflict`) is computed from them every time (`DeviceProtectionStatusCmd`
  handler in `WindowsUsbProtectionHandler`). Enabling one always disables the other in the
  same handler call - a `conflict` should only ever be reachable via direct file edit,
  which is why startup checks for it too.
- **A blocked device is unblocked automatically when policy loosens** - every
  add/remove/set/clear/disable mutation on a list triggers `restoreDevices` and/or
  `applyPolicy` (both fire-and-forget via `Task.Run`) so enforcement stays consistent with
  the current rules without the caller having to ask twice. See `WindowsUsbProtectionHandler`
  for exactly which mutation triggers which action - they are not all the same, and getting
  this wrong was the subject of a real bug fix (git history: "correct device blocking -
  Bluetooth kinds, internal-device guard, dedup, unblock events").
- **Duplicate device entries are rejected** - `UsbDeviceList.SameDevice` compares vid/pid/
  serial/mac/kind case-insensitively (label is cosmetic and ignored); `Add`/`Set` dedupe
  against this before persisting.
- **A composite USB device is judged as ONE physical device, not per interface.** Windows
  enumerates one physical device (a keyboard, most commonly) as several separate interfaces,
  each possibly resolving to a different `DeviceKind`. `UsbMonitor.IsGroupCompliant` decides
  compliance for the whole `GroupId`: allowed if ANY sibling interface matches the whitelist,
  blocked if ANY sibling matches the blacklist. Never make a block/allow decision from a
  single interface's kind alone without checking this - a mismatched sibling interface can
  otherwise escalate to disabling the shared composite parent, taking down a device that WAS
  correctly whitelisted (this was a real shipped bug: `{kind: keyboard}` alone blocked the
  keyboard itself). See PROJECT.md section 5.2/5.4.
- **Whitelist mode fails closed on anything unclassifiable** - a device with an unrecognized
  interface GUID (`DeviceKind.Unknown`) or an unparseable path is still evaluated against
  policy (and therefore blocked under whitelist, since it won't match a specific entry)
  rather than silently skipped. `IsProtectedInternal` covers `Unknown` the same way it covers
  `StrictInputKinds`, so an internal-but-unrecognized device still can't be blocked blind.
  See PROJECT.md section 5.8.

---

## 8. Build and schema export

```bash
# Build (debug)
dotnet build DlpEndpointMonitor.slnx

# Publish the real artifact: trimmed, self-contained, single-file win-x64
dotnet publish DlpEndpointMonitor/DlpEndpointMonitor.csproj -c Release

# Dump the full command/event JSON Schema (for the sibling Node.js agent's type generation)
dotnet run --project DlpEndpointMonitor -- --schema
```

There is still no CI (no `.github/workflows`) yet - see PROJECT.md "Not implemented". A test
project now exists (`DlpEndpointMonitor.Tests/`, xUnit) covering the hardware-independent
pure-logic layer only - `ai_agent_doc/TEST-PLAN.md` section 1 has the full feasibility breakdown of
what is and is not covered and why.

### 8.1 Validation gate (run before every commit)

1. `dotnet build DlpEndpointMonitor.slnx` - must exit 0 (builds both projects). Nullable-
   reference warnings and analyzer warnings are worth reading even though nothing currently
   fails the build on them.
2. `dotnet test DlpEndpointMonitor.Tests/DlpEndpointMonitor.Tests.csproj` - must report 0
   failures. Covers `ai_agent_doc/TEST-PLAN.md` section 2 (device-kind resolution, USB/Bluetooth/
   Display path parsing, whitelist/blacklist matching+dedup, command dispatch, event
   emission, schema export) - if you touched any of those files, this is a real automated
   check, not just a manual one.
3. If you touched anything under `Commands/`, `Core/Enums.cs`, or any `IEvent`/`ICommand`
   record shape, run `dotnet run --project DlpEndpointMonitor -- --schema` and sanity-check
   the diff - that output is a public contract with a repo you cannot see from here.
4. Manually exercise the change if it touches device blocking or display topology
   (`Monitors/*`, `Handlers/Windows/*`, or the Win32-calling methods of `Actions/*`) - these
   paths still cannot be meaningfully unit-tested without real hardware/OS state (see
   `ai_agent_doc/TEST-PLAN.md` section 1's feasibility table and section 3's manual test matrix), so
   a manual pass (plug/unplug the relevant device class, or toggle whitelist/blacklist and
   confirm the event stream) is the actual test for that part today.

---

## 9. Rules for writing code here

**Scope and files**
- Do exactly what was asked - nothing extra.
- Prefer editing an existing file over creating a new one.
- Do not create documentation files unless asked.
- Keep files under 500 lines.
- Never commit secrets - there are none expected in this repo (no network calls, no
  credentials), so a diff that adds any is almost certainly wrong.

**DRY (Don't Repeat Yourself)**
- Before writing a value, constant, or piece of logic, check whether it already exists
  somewhere else in the codebase - reuse or extract a shared helper instead of retyping it.
  Concrete precedent: `Core/StorageLocation.cs` holds the one `%ProgramData%\DlpEndpointMonitor`
  computation that `UsbDeviceList` and `DisabledDevices` both need - it used to be duplicated
  identically in both files (copy-pasted when `DisabledDevices` was added), which is exactly
  the failure mode this rule exists to prevent: two copies silently drifting apart the next
  time the path changes (as it did, twice - see PROJECT.md section 6).
- This is the same instinct as the harness's "REUSE FIRST" rule
  (`ai_agent_doc/scripts/WORKFLOW-CRITERIA.md`) applied to hand-written edits, not just
  workflow-spawned agents: grep for an existing symbol before writing raw code, and if a
  genuine third use of the same value/logic shows up, that is the signal to extract a shared
  helper in the right layer (`Actions/` for Win32, `Core/` for state) rather than adding a
  third copy.

**Comments**
- Write direct, objective comments: explain the *why* (a non-obvious Windows quirk, a
  concurrency invariant, a past bug this line prevents), never restate the *what* the code
  already says. This codebase already does this well (see the comments in `UsbActions.cs`,
  `DisplayMonitor.cs`) - match that style. Keep them short; delete a comment that just
  narrates the next line.

**Types and validation**
- `Nullable` reference types are enabled (`<Nullable>enable</Nullable>`) - respect `?`
  annotations; do not silence a nullable warning with `!` unless you have actually proven
  the value cannot be null at that point.
- Every wire type (`ICommand`, `IEvent`) is a `record` with a `[JsonDiscriminant(...)]`
  attribute driving `System.Text.Json` source generation (`AppJsonContext`,
  `CommandsJsonContext`). Add a new command/event by adding a record + attribute + a
  `case` in `CommandDispatcher`/a `Handle` overload - never hand-write ad-hoc
  `JsonSerializer.Serialize(obj)` calls outside `EventEmitter.Emit`, which resolves the
  source-gen `TypeInfo` for you.
- Trimming is on (`PublishTrimmed`) - reflection-based code must be confined to
  `SchemaExporter`, which is explicitly excluded from the trimmed runtime path (only runs
  under `--schema`) and is marked `[RequiresUnreferencedCode]`.

**Structure (keep each layer to one job)**
- `Monitors/*` - Win32-message-driven watchers. They decide *whether* a device is allowed
  (by asking whitelist/blacklist) and call an `Actions/*` function to act. No P/Invoke
  directly in a monitor.
- `Actions/*` - stateless Win32 P/Invoke wrappers. No policy decisions, no `EventEmitter`
  calls except deep utility logging is not their job either - they return `(bool ok, string?
  error)` tuples and let the caller decide what to emit.
- `Handlers/Windows/*` - the command-side counterpart: translate one `ICommand` into one or
  more `Actions/*` calls and emit exactly one reply/result event.
- `Core/UsbDeviceList` and subclasses - persistence and matching only. No Win32 calls.

**Concurrency**
- The message-loop thread (STA) and the stdin-reading loop (its own `Task.Run`) both read
  `whitelist`/`blacklist`/`disabled` concurrently - `UsbDeviceList` protects itself with a
  `ReaderWriterLockSlim`; do not add a second, separate lock around it.
- Blocking/restoring a device happens on a `Task.Run` off both the message thread and the
  stdin thread - never call a slow `Actions/*` function synchronously from `OnDeviceChanged`
  or similar Win32 callback, or you stall the message pump for every other monitor.
- A Win32 message-handler delegate (`WndProc`, the keyboard hook `Callback`) must **never
  throw** - wrap the body in try/catch and emit an `ErrorEvent` instead. An unhandled
  exception on those threads kills the whole process (this is exactly what happened in the
  `DisplayMonitor.OnDisplayChanged` `ObjectDisposedException` bug - see PROJECT.md section
  10 for the fix).

**Errors**
- Every `Actions/*` failure path returns `(false, "<reason>")` rather than throwing; the
  caller emits a `*BlockFailedEvent`/`ReplyEvent(ok:false, error)`. Do not introduce
  exceptions for expected Win32 failure codes (device busy, access denied, not found) -
  that is normal control flow here, not an error to be raised.
- A silent `catch { }` is only acceptable where a Win32 read racily fails and a stale/empty
  result is an acceptable fallback (e.g. `IsUsbStorageEnabled`'s `catch { return true; }` -
  read the comment there for why `true`, not `false`, is the safe default). Everywhere else,
  catch, call `EventEmitter.EmitError(source, message)`, and keep going - a monitor thread
  must survive one bad event and keep watching.

---

## 10. Sharp edges to remember

- **Never block a built-in keyboard/touchpad/hub.** `UsbActions.IsProtectedInternal` is the
  single choke point for this; it is decided by transport bus (USB non-removable, or no
  USB/Bluetooth ancestor at all = internal), not just device kind. Camera/video are
  deliberately excluded - a built-in webcam CAN be blocked. `DeviceKind.Unknown` is ALSO
  routed through this same check (not just `StrictInputKinds`) - if you add a new
  block/evaluate path for any device kind, route it through `IsProtectedInternal` or you can
  brick input, or blindly disable an unrecognized-but-essential internal device.
- **Never decide block/allow from a single USB interface's kind in isolation.** A composite
  device (a keyboard, most commonly) enumerates as multiple interfaces that can resolve to
  different `DeviceKind`s. Always run the decision through `UsbMonitor.IsGroupCompliant`
  (checks every sibling interface sharing the same `GroupId`) before blocking, and through
  `IsRecordCompliant` when restoring - never trust one interface's kind, or one persisted
  record's stale kind, in isolation. Getting this wrong was a real shipped bug: a
  `{kind: keyboard}`-only whitelist entry ended up disabling the whole physical keyboard via
  a sibling `Hid`-kind interface's collateral block.
- **Bluetooth-backed HID devices are disabled at the Bluetooth device node, never the HID
  leaf, and never the shared radio.** `UsbActions.GetBluetoothDeviceNode` finds the right
  node; disabling the HID leaf gets silently re-enabled by vendor software (e.g. Logitech
  Options), and disabling the radio would take out every Bluetooth device at once.
  `HasBluetoothAncestor` is checked *before* any USB-group escalation so a wireless
  keyboard is never mistaken for an internal USB one (the BT radio itself is often a USB
  device sitting above it in the tree).
- **For BLE, `BTHLEDEVICE\` is NOT the Bluetooth peripheral's own node - it's a GATT-service
  child, one level below the true `BTHLE\` peripheral node.** Verified live against real
  hardware (`Get-PnpDevice`): a BLE mouse's tree is `BTH\MS_BTHLE\...` (enumerator, never
  touch) -> `BTHLE\DEV_<mac>\...` (the actual device, e.g. "MX Vertical") ->
  `BTHLEDEVICE\{service-guid}..._<mac>\...` (one GATT service, e.g. the HID service) ->
  `HID\{...}_COL0N\...` (the input leaf). Classic Bluetooth (`BTHENUM\`) has no such extra
  layer. Both `UsbActions.GetBluetoothDeviceNode` and `BluetoothActions.FindInstanceIdByMac`
  walk all the way up to `BTHLE\` for BLE devices - never stop at `BTHLEDEVICE\`, or you're
  disabling a GATT service instead of the peripheral (Windows cascades a parent's disable to
  its children, so this may still "work" by accident, but it's the wrong node).
- **Bluetooth blocking falls back to `RemovePairing` (irreversible unpair) only when the new
  MAC-to-instance-ID resolution can't find a matching PnP node** - never as the primary path
  and never when the node WAS found but disabling it failed (that's a real failure, not an
  unresolvable-device case, and must surface as such, not be masked by a silent unpair).
- **`KSCATEGORY_CAPTURE` is deliberately NOT mapped to `video`** in `UsbKind.cs` - it is an
  audio+video capture category that onboard HD-audio codecs also register under
  (`wavemicin`, `wavespeaker`, HDMI-audio topology), so mapping it to video made a "block
  all video" policy try to disable the sound card. Only `KSCATEGORY_VIDEO` is video-specific.
- **Disabled devices are tracked by exact instance ID in `DisabledDevices`, not
  re-discovered by enumeration**, because a disabled device has no active interface to
  enumerate. This is what makes `RestoreCompliant` work correctly even across a process
  restart between block and unblock - do not "simplify" this into a live re-scan.
- **The USB block path escalates**: HID/leaf disable -> composite-parent (`GroupId`)
  disable -> physical eject, and only escalates when there IS a USB group and NO Bluetooth
  ancestor. Windows rejects `CM_Disable_DevNode` on some input devices specifically to
  prevent keyboard lockout, which is why eject exists as a last resort - but eject is
  **not reversible** by this process (no `CM_Enable` undoes it; a physical replug is
  needed), unlike disable.
- **`DisplayMonitor` debounces `WM_DISPLAYCHANGE` by 800ms** because Windows fires it
  repeatedly while HDMI audio interfaces cycle during a topology switch. The debounce
  token's cancel-then-dispose must happen *before* starting the new timer, in the same
  place - a past bug (`ObjectDisposedException` on the message thread, which killed the
  whole process) came from disposing the old token in a continuation while a new
  `WM_DISPLAYCHANGE` could still call `.Cancel()` on it. If you touch this debounce, keep
  the swap atomic.
- **Instance IDs recovered by parsing a device *interface* path are unreliable for some HID
  paths** - `UsbActions.EnumerateConnected` prefers the real instance ID from
  `SetupDiGetDeviceInstanceId` over the path-derived one for exactly this reason (a
  Bluetooth-LE HID mouse's path collapsed to `"hid"`, which silently made every subsequent
  `CM_Locate_DevNodeW` fail).
- **State files** (`whitelist.json`, `blacklist.json`, `disabled-devices.json`) live under
  `%ProgramData%\DlpEndpointMonitor\` - machine-wide and user-agnostic, since this process
  runs elevated and may run under a different effective user context (e.g. a service/SYSTEM
  account) than the interactive user, where a per-user profile folder would resolve to the
  wrong place. Written via a temp-file-then-atomic-rename (`File.Move(tmp, path,
  overwrite: true)`) - never write them in place, a half-written file here would corrupt
  policy state on next load.
- **No test project, no CI yet.** Device-blocking and display-topology logic depend on
  real Win32/hardware state that is hard to fake - treat a careful reading of the code plus
  manual verification on a real machine as the actual test today (see AGENTS.md 8.1 /
  PROJECT.md "Not implemented").
- **An unclassified device (`DeviceKind.Unknown`, or an unparseable monitor path) is
  deliberately still evaluated against policy, not skipped** - `ParsePartialDevice`/
  `ParseMonitorPath` return an identity-less `ParsedDevice` (`""` sentinel for Vid/Pid)
  instead of `null` for these cases, so whitelist mode fails closed on them instead of
  letting them connect invisibly. Do not reintroduce a `return null` short-circuit for
  `Kind == Unknown` - that was the root cause of "unrecognized devices connect regardless of
  whitelist." `EnumerateConnected()` still only iterates the ~25 known interface GUIDs in
  `DeviceKindResolver` though - a device exposing ONLY an unlisted GUID is still invisible to
  the sweep entirely; this fix only helps devices that get enumerated but fail identity
  parsing (see PROJECT.md section 5.8).
- **`DeviceKind.Network` is handled entirely by its own `Monitors/NetworkMonitor.cs`, never by
  `UsbMonitor`** - a NIC is not a composite device and disabling one is catastrophic (it can be
  the machine's only path back to the sibling dlp_v2 agent), so it does not fit `UsbMonitor`'s
  per-interface / composite-group pipeline built for HID-style peripherals. `UsbMonitor`
  explicitly excludes `Kind == Network` at every entry point (`OnDeviceChanged`,
  `EnumerateExisting`, `BlockNonCompliant`, and its `byGroup` bucketing), and `NetworkMonitor`
  is the sole consumer of `Kind == Network` - both monitors watch the same
  `window.DeviceChanged` event and partition purely by resolved `Kind`. Before disabling any
  network devnode, `NetworkMonitor.BlockDevice` calls `UsbActions.IsBuiltIn` (a public helper
  factored out of `IsProtectedInternal`'s bus-ancestry walk) - internal/non-removable adapters
  are never blocked, only an external/removable USB WiFi/Ethernet dongle is. This is the fix
  for a real shipped bug: `IsProtectedInternal`'s first-line gate did not cover `Network`, so a
  "block network kind" policy disabled the machine's own built-in WiFi/Ethernet adapter with no
  protection at all (see PROJECT.md section 5.9 and bug history).
- **Clipboard content policy is intentionally different from device policy - do not "fix" it to
  match.** `ClipboardWhitelist`/`ClipboardBlacklist` can both be enabled at once; this is a valid,
  intended state, not a `conflict` - there is no `ProtectionMode`/mode concept and no startup
  conflict-guard for clipboard, unlike `DeviceWhitelistEnableCmd`/`DeviceBlacklistEnableCmd`
  (which force-disable the other list), `ClipboardWhitelistEnableCmd`/`ClipboardBlacklistEnableCmd`
  (`WindowsClipboardProtectionHandler`) deliberately do NOT touch the other list's `Enabled` flag.
  Enforcement is the same AND-formula every device monitor already uses (`whitelist.IsAllowed(...)
  && !blacklist.IsBlocked(...)`), just applied to clipboard Text/Files content instead of a device
  identity - see PROJECT.md section 5.10. Separately, and just as important: `KeyboardHook`'s
  paste-interception path (`ShouldBlockPaste`) deliberately **fails OPEN, not closed**, on any
  error - it is a global, system-wide `WH_KEYBOARD_LL` hook, so an uncaught exception there would
  silently break paste for every application on the machine until the process restarts, which is
  worse than occasionally letting through content that should have been blocked. This is the
  opposite of `IsProtectedInternal`/`IsRemovable`'s fail-**closed** defaults in device blocking
  (section 10's other entries, PROJECT.md section 5.1) - do not port that fail-closed instinct
  onto the paste path, or a bad regex from an operator can brick paste system-wide.

---

## 11. Where to look for the truth

| To answer... | Open this file |
|---|---|
| What commands exist and their arg shapes | `Commands/Commands.cs` |
| What events exist and their payload shapes | `Core/EventEmitter.cs` |
| The full enum vocabulary (events, commands, device kinds) | `Core/Enums.cs` |
| How a stdin line becomes a handler call | `Core/CommandDispatcher.cs` |
| How whitelist/blacklist enable/disable/mutate interact | `Handlers/Windows/WindowsUsbProtectionHandler.cs` |
| How a device actually gets blocked/unblocked | `Monitors/UsbMonitor.cs` (`BlockDevice`, `RestoreCompliant`), `Actions/UsbActions.cs` |
| How a composite device's siblings are judged together | `Monitors/UsbMonitor.cs` (`IsGroupCompliant`, `IsRecordCompliant`), `Actions/UsbActions.cs` (`EnumerateGroupSiblings`) |
| How Bluetooth devices are matched/blocked/restored | `Monitors/BluetoothMonitor.cs`, `Actions/BluetoothActions.cs` (`FindInstanceIdByMac`) |
| How external displays are disabled/restored | `Monitors/DisplayMonitor.cs`, `Actions/DisplayActions.cs` |
| How network adapters are matched/blocked/restored, and how the built-in NIC is protected | `Monitors/NetworkMonitor.cs`, `Actions/UsbActions.cs` (`IsBuiltIn`) |
| Windows interface GUID -> DeviceKind mapping | `Core/UsbKind.cs` |
| The Win32 message pump / STA requirement | `Core/MessageWindow.cs`, `Program.cs` |
| Every P/Invoke signature and struct | `Win32/NativeMethods.cs` |
| The `--schema` JSON-Schema export | `Core/SchemaExporter.cs` |
| Deep design, protocol tables, principles, roadmap | `ai_agent_doc/PROJECT.md` |
| The validation gate to run before a commit | AGENTS.md section 8.1 |
| What's unit-testable today vs. hardware-only, and the full test case list | `ai_agent_doc/TEST-PLAN.md` |
