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

Five projects. `DlpEndpointMonitor/DlpEndpointMonitor.csproj` is the shipped binary: target
`net10.0`, built as a trimmed, single-file, self-contained `win-x64` executable (no runtime
dependency on a shared .NET install), zero NuGet dependencies (its one `ProjectReference`,
`DlpEndpointMonitor.AlertContracts`, is itself dependency-free plain records/enums, so this
does not compromise the trim/self-contained build). `DlpEndpointMonitor.Tests/` is a dev-only
xUnit test project (a NuGet dependency exists here, and in `DlpEndpointMonitor.AlertHost.Tests/`
- see section 8.1 and `ai_agent_doc/TEST-PLAN.md`. `DlpEndpointMonitor.AlertContracts/` and
`DlpEndpointMonitor.AlertHost/` are a standalone, currently-unwired alert-delivery capability -
see section 10's "Alert delivery" entry and `ai_agent_doc/PROJECT.md` section 11 for why a
second process exists and how it talks to the first.

```
DlpEndpointMonitor/
  Program.cs                 entry point: --schema and --session-companion early branches, then
                              starts the message-loop thread, decides whether clipboard/keyboard run
                              locally or via a session-crossed companion, wires up the
                              CommandDispatcher, runs until stdin closes or WM_QUIT (see section 10)
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
    ClipboardCompanionRelay.cs newline-JSON-over-named-pipe transport carrying event lines from the
                              --session-companion instance (interactive session) back to the
                              primary (Session 0) so the agent sees them on the primary's stdout -
                              Server (primary, EmitRawLine forwarder) + Client (companion sender);
                              reconnect-after-drop is an explicit known gap, see section 10
    DisplayCompanionRelay.cs  newline-JSON request/reply named-pipe transport for display-topology
                              commands, running the OPPOSITE direction to ClipboardCompanionRelay:
                              the primary (which makes the whitelist/blacklist compliance decision
                              in DisplayMonitor) sends "disable"/"enable" and blocks for one reply
                              line; the --session-companion hosts the Server and runs the real
                              DisplayActions call (via a Program.cs-supplied executeCommand delegate,
                              so this file never references DisplayActions), since a SetDisplayConfig
                              only takes effect in the interactive session - see section 10
    BluetoothCompanionRelay.cs newline-JSON request/reply named-pipe transport for Bluetooth device
                              enumeration, same direction as DisplayCompanionRelay: the primary
                              (which makes the compliance decision in BluetoothMonitor) is the
                              Client and sends "enumerate"; the --session-companion hosts the Server
                              and runs the real BluetoothActions.EnumerateConnected (via a
                              Program.cs-supplied delegate, so this file never references
                              BluetoothActions' enumeration), since only the interactive session's
                              process sees the paired devices. Unlike DisplayCompanionRelay the reply
                              is a whole List<BtDevice> JSON array (encoded through AppJsonContext, so
                              the free-form Name is escaped and no reflection escapes trim) - see
                              section 10
    DisplayChangeRelay.cs     newline fire-and-forget named-pipe transport, same direction as
                              ClipboardCompanionRelay (companion -> primary): the --session-companion
                              hosts the Client and calls Notify() on its own window's DisplayChanged
                              event; the primary hosts the Server and calls
                              DisplayMonitor.NotifyExternalDisplayChange() on every line received - see
                              section 10 for why WM_DISPLAYCHANGE needed its own relay leg distinct
                              from DisplayCompanionRelay's action-execution one
    ClipboardOperationHint.cs correlates KeyboardHook's Ctrl+X detection with ClipboardMonitor's
                              next clipboard read, so a text cut reports "cut" instead of
                              ClipboardActions.TryReadText()'s hardcoded "copy" - see section 10
    ScreenshotBlockPolicy.cs  single-boolean persisted policy (enable/disable only, no entries)
                              for the OS-native screenshot-shortcut block; KeyboardHook reads
                              IsEnabled, wired into reset_all_policy as a hand-added fifth
                              domain (not via the UsbDeviceList/ClipboardRuleList reflection
                              rule) - see PROJECT.md section 5.11
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
    UsbActions.cs             SetupAPI enumeration, CM_Disable/Enable/Eject, USBSTOR registry toggle,
                              IsMassStorageDevice/CompatibleIdsIndicateMassStorage (Compatible-IDs-based
                              mass-storage detection feeding usb_storage_blocked, section 10)
    BluetoothActions.cs       BluetoothFindFirst/NextDevice, RemoveDevice (pairing - the primary
                              block action), path/CoD parsing
    DisplayActions.cs         QueryDisplayConfig/SetDisplayConfig(Paths) topology juggling
    ClipboardActions.cs       OpenClipboard/GetClipboardData/SetClipboardData
    AlertActions.cs           ShowAlert(AlertRequest): the one stateless entry point that shows
                              a UI alert - launches/reaches DlpEndpointMonitor.AlertHost.exe in
                              the interactive session (same-session Process.Start, or
                              WTSQueryUserToken+CreateProcessAsUser cross-session); NOT called
                              from any Monitors/* yet, see section 10
    SessionActions.cs         stateless session-crossing helpers extracted from AlertActions'
                              proven pattern: GetActiveConsoleSessionId / IsRunningInSession /
                              LaunchIntoSession (WTSQueryUserToken+CreateProcessAsUser into the
                              interactive session); used by Program.cs to launch the clipboard
                              companion, same P/Invoke as AlertActions, see section 10
  Handlers/
    IHandlers.cs              one interface per command family (IClipboardHandler, IUsbStorageHandler,
                              IUsbDeviceHandler, IUsbProtectionHandler, IClipboardProtectionHandler,
                              IControlHandler)
    Windows/                  the concrete Windows implementation of each handler interface, incl.
                              WindowsClipboardProtectionHandler.cs (clipboard whitelist/blacklist
                              mutations - deliberately does NOT force-disable the other list),
                              WindowsScreenshotProtectionHandler.cs (screenshot_block_enable/
                              disable/status - bare enable/disable, no entries, see PROJECT.md
                              section 5.11)
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
DlpEndpointMonitor.AlertContracts/   plain net10.0 class library (no -windows TFM, no WPF/Win32
                             dependency), referenced by BOTH the main binary and AlertHost so
                             their wire format cannot drift. Deliberately dependency-free -
                             AlertRequest.cs/AlertType.cs/AlertSeverity.cs (plain records/enums),
                             AlertPipe.cs (the one shared `AlertPipe.Name` pipe-name constant),
                             AlertJsonContext.cs (System.Text.Json source-gen context - zero
                             reflection, same reasoning as AppJsonContext.cs/CommandsJsonContext.cs).
DlpEndpointMonitor.AlertHost/   companion WPF app (net10.0-windows, OutputType WinExe), NOT
                             started by DlpEndpointMonitor.csproj's own build/publish - it is a
                             separate deployable exe, launched on demand by
                             Actions/AlertActions.cs. Shows a Toast/FullScreen alert window
                             in the interactive user's session (this binary itself may be running
                             headless in Session 0). App.xaml.cs (mutex-guarded singleton
                             startup, --initial-alert arg parsing), AlertQueue.cs (coalesce/cap
                             dispatch), PipeTransport.cs (newline-JSON named-pipe server/client),
                             RichTextParser.cs (the closed `<strong>/<b>/<em>/<i>/<br>` inline-tag
                             allowlist), Resources/Colors.xaml (severity/surface brushes),
                             Controls/AlertBox.xaml(.cs) (the one shared rounded-box+header-band
                             control), Windows/ToastWindow|FullScreenWindow.xaml(.cs).
                             See ai_agent_doc/PROJECT.md section 11 for the full delivery-mechanism
                             design (why a second process, mutex/pipe singleton, session-crossing
                             launch, queueing policy, visual design, color-token provenance) and
                             its explicit NOT YET WIRED note.
DlpEndpointMonitor.AlertHost.Tests/   xUnit project (net10.0-windows, so it can reference
                             AlertHost's classes), AlertQueueTests.cs + RichTextParserTests.cs -
                             the two pure-logic pieces only. Does not and cannot cover the WPF
                             windows, the named pipe, the mutex, or CreateProcessAsUser - see
                             ai_agent_doc/TEST-PLAN.md sections 2.12/2.13 and its manual-test-matrix
                             entry for those.
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
  Bluetooth kinds, internal-device guard, dedup, unblock events"). **Carve-out: Bluetooth-kind
  blocks are unpair-based (`BluetoothActions.RemovePairing`), not devnode-disable-based, and
  CANNOT be auto-restored** - an unpaired device has no record for `RestoreCompliant` to find,
  so policy loosening will not bring it back; the user must manually re-pair it.
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

**Policy list completeness**
- Every whitelist/blacklist-style policy list - every non-abstract `UsbDeviceList` or
  `ClipboardRuleList` subclass in `Core/` (today: `DeviceWhitelist`, `DeviceBlacklist`,
  `ClipboardWhitelist`, `ClipboardBlacklist`) - must expose the same symmetric command set the
  existing four already do (Enable/Disable/Get/Clear/Add/Remove/Set, or the clipboard
  equivalent), **and** must be wired into `WindowsControlHandler.Handle(ResetAllPolicyCmd)`,
  clearing it with exactly the same semantics as its own individual `*_clear` command (whitelist-
  style: disable + clear; blacklist-style: clear only - see PROJECT.md section 2's
  `reset_all_policy` entry). If you add a fifth policy list without doing both of these,
  `reset_all_policy` silently stops being a true "clear everything" - a caller relying on it
  would have no way to know one list was left untouched.
- The wiring half is mechanically enforced:
  `DlpEndpointMonitor.Tests/WindowsControlHandlerTests.cs`'s
  `ResetAllPolicyCmd_HandlerConstructorCoversEveryPolicyListType` reflects over every
  `UsbDeviceList`/`ClipboardRuleList` subclass and asserts each one appears in
  `WindowsControlHandler`'s constructor parameters - it fails the moment a new list type exists
  without also updating the handler, forcing that decision to be explicit instead of silently
  skipped. It **cannot** mechanically verify the first half (that the new list also got its own
  individual `*_clear` command in the first place) - reflection can catch a wiring gap, not a
  missing feature; that half is a code-review/read-this-file discipline.

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
- **Bluetooth-backed HID devices are unpaired at the Bluetooth device node's own MAC, never
  the HID leaf, and never the shared radio.** `UsbActions.GetBluetoothDeviceNode` finds the
  right node; `BluetoothActions.ParseMacFromPath` extracts that node's MAC, which is then
  passed to `BluetoothActions.RemovePairing` - unpairing the HID leaf or the radio is not an
  option (the leaf isn't a pairing itself, and unpairing the radio would take out every
  Bluetooth device at once). `HasBluetoothAncestor` is checked *before* any USB-group
  escalation so a wireless keyboard is never mistaken for an internal USB one (the BT radio
  itself is often a USB device sitting above it in the tree).
- **For BLE, `BTHLEDEVICE\` is NOT the Bluetooth peripheral's own node - it's a GATT-service
  child, one level below the true `BTHLE\` peripheral node.** Verified live against real
  hardware (`Get-PnpDevice`): a BLE mouse's tree is `BTH\MS_BTHLE\...` (enumerator, never
  touch) -> `BTHLE\DEV_<mac>\...` (the actual device, e.g. "MX Vertical") ->
  `BTHLEDEVICE\{service-guid}..._<mac>\...` (one GATT service, e.g. the HID service) ->
  `HID\{...}_COL0N\...` (the input leaf). Classic Bluetooth (`BTHENUM\`) has no such extra
  layer. `UsbActions.GetBluetoothDeviceNode` walks all the way up to `BTHLE\` for BLE devices -
  never stop at `BTHLEDEVICE\`, or you resolve the MAC of a GATT service instead of the
  peripheral (Windows cascades a parent's disable to its children, so a disable-based approach
  may still "work" by accident on the wrong node, but `RemovePairing` on the wrong MAC would
  simply fail to match any pairing at all).
- **[fixed - real production incident, confirmed via this machine's own event log]
  `UsbMonitor.BlockDevice`'s Bluetooth branch no longer unpairs at all - it disables the
  device's own primary node (`UsbActions.GetBluetoothDeviceNode`) instead, because the
  candidate-retry mechanism unpair depended on took down a completely unrelated device.**
  Blacklisting `kind=keyboard` with a Bluetooth keyboard AND a Bluetooth mouse both connected
  resolved the keyboard's own candidate list via `UsbActions.GetBluetoothPairingCandidates`,
  which - as designed (see the next bullet) - walked up to the shared `BTH\MS_BTHLE\` enumerator
  and collected every OTHER `BTHLE\` sibling under it as a "candidate", including the mouse's own
  node, which has nothing to do with the keyboard rotating its own address. `RemovePairingAny`
  then issued a real `BluetoothRemoveDevice` call against the mouse's live, correct pairing. Both
  the keyboard's own address and the mouse's returned `ERROR_NOT_FOUND (0x490)` on that machine
  (the legacy-API limitation the next bullet already documents), so by this code's own bookkeeping
  nothing succeeded and neither device was ever recorded in `DisabledDevices` - but the mouse's
  live BLE link still degraded over the following minutes and fully disconnected, and neither
  device could be re-paired afterward even through Windows Settings, with the policy removed. The
  doc comment's claim that "a non-matching address is a harmless no-op" is therefore false in
  practice: an unpair attempt against an actively-connected BLE peripheral is not inert on real
  hardware, even when the call itself reports failure.
  Fix: `UsbMonitor.BlockDevice`'s Bluetooth branch now calls `UsbActions.GetBluetoothDeviceNode`
  (this device's own ancestor walk only - no `CM_Get_Child`/`CM_Get_Sibling` fan-out to other
  devices, so it cannot repeat this incident by construction) and `UsbActions.DisableDevice`
  directly - `GetBluetoothPairingCandidates`/`BluetoothActions.RemovePairingAny`/
  `UsbMonitor.ResolveBluetoothBlock` are all left intact and still unit-tested
  (`UsbActionsParsingTests`, `BluetoothActionsParsingTests`, `UsbMonitorTests`) for a future fix
  that scopes candidates to the SAME physical device (e.g. matching Vid/Pid) instead of every
  sibling under the shared enumerator - they are simply no longer called from this site.
  Trade-off accepted knowingly: Windows Settings' Connected/Paired indicator reads the pairing
  store, not devnode state, so a disable-only block still shows as "Connected" there, and since
  the pairing survives, Windows may keep re-establishing the underlying link whenever the device
  is in range - each reconnect re-arrives a devnode and this method just re-disables it again,
  rather than the one-time steady state a successful unpair would give. `BluetoothMonitor.BlockDevice`
  (the OTHER, MAC-only Bluetooth block path - see below) is UNCHANGED by this fix and still
  unpairs; it was not involved in this incident (it found zero devices all session) but is now
  inconsistent with `UsbMonitor`'s branch and has no devnode-from-MAC resolution to switch to
  disable-only itself without new work - a known, not-yet-addressed gap.
  `BluetoothMonitor.BlockDevice` works directly from a MAC and calls `RemovePairing` once (no
  disable fallback exists on this path - it has no devnode to disable from, only a MAC).
- **A BLE peripheral's live PnP address is not always what fails - the legacy unpair API itself
  can be the real limitation, confirmed on real hardware.** The same physical Bluetooth LE
  mouse's PnP instance ID carried a different trailing MAC-derived hex suffix on three separate
  reconnects in one session (sequential `+1` each time), and unpairing using the newest address
  failed with `BluetoothRemoveDevice failed: 0x00000490` (`ERROR_NOT_FOUND`) - initially
  suspected as the pairing store not recognizing a rotated address.
  `UsbActions.GetBluetoothPairingCandidates(instanceId)` walks one level past the primary node
  to the shared Bluetooth-enumerator parent and collects every OTHER `BTHENUM\`/`BTHLE\` sibling
  node via `CM_Get_Child`/`CM_Get_Sibling`, and `BluetoothActions.RemovePairingAny` (thin
  wrapper over the pure, unit-tested `TryCandidatesInOrder`) tries `RemovePairing` against each
  in order - believed at the time to always be safe, since a non-matching address is a harmless
  no-op against an address that isn't live. **That assumption does not hold when the "candidate"
  is another device's own live, connected pairing** - see the fixed incident bullet above, where
  this same mechanism reached an unrelated mouse instead of a stale sibling of the same device.
  `GetBluetoothPairingCandidates`/`RemovePairingAny` are no longer called from
  `UsbMonitor.BlockDevice`'s live blocking path for this reason, though they remain intact for a
  future Vid/Pid-scoped fix. **Live testing
  disproved the rotation theory for this failure**: no older sibling was still enumerable (only
  one candidate was ever found), and directly inspecting
  `HKLM\SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters\Devices` showed the failing address
  WAS present as a remembered device - yet `BluetoothRemoveDevice` still rejected it. Removing
  the same device manually through Windows Settings succeeded against that identical address,
  confirming Settings uses a different (almost certainly WinRT-based) removal path this legacy
  API has no equivalent to. The candidate-retry logic stays (still correct/safe for a genuinely
  stale address, and kind-agnostic - mouse, keyboard, audio, or generic HID/dongle alike) but
  is not sufficient alone for hardware where the legacy API fails outright - see PROJECT.md
  section 5.5 for the full writeup and the WinRT/`CsWinRT` tradeoff this surfaces.
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
- **PrintScreen must be checked on both keydown and keyup - some keyboards/drivers only ever
  deliver one of the two edges for this specific key** (a well-known historical BIOS/SysRq-era
  quirk), so `KeyboardHook.HandleScreenshotShortcut` evaluates whichever edge actually arrives
  rather than assuming keydown. `_snapshotKeyDown` dedupes the case where both edges DO fire, so
  a physical keypress is still reported/blocked exactly once either way - do not "simplify" this
  back down to a keydown-only check, or a keyboard that only sends keyup will silently stop being
  detected at all. Alt+PrintScreen and Win+Alt+PrintScreen share this same dual-edge handling
  (same `VK_SNAPSHOT`); Win+Shift+S needs none of it (single keydown edge, like Ctrl+C/X/V/Z
  above). This is a new capability, not a bug fix - same category of honest limitation as the
  paste-blocking bullet above applies here too: it is a `WH_KEYBOARD_LL` keystroke hook, so it
  can only ever see the four OS-native keystroke shortcuts it's built for. Right-click/menu-driven
  capture, the Start-Menu-launched Snipping Tool, and any third-party screenshot or
  screen-recording tool are all completely outside what any keyboard hook can observe - see
  PROJECT.md section 5.11 for the full writeup and the explicitly-deferred broader-coverage
  options (process-launch blocking, `WDA_EXCLUDEFROMCAPTURE`).
- **Showing a UI alert from this binary always requires crossing into a different Windows
  session, and `STARTUPINFO.lpDesktop` is the easy way to get that silently wrong.** This
  binary may run as a LocalSystem service in Session 0, which has no desktop at all - a WPF
  window can only ever be shown by a separate process, `DlpEndpointMonitor.AlertHost.exe`,
  running in the interactive user's own session. `Actions/AlertActions.cs` gets there via
  `WTSQueryUserToken` (get that session's user token) -> `DuplicateTokenEx` (impersonation ->
  primary token) -> `CreateEnvironmentBlock` -> `CreateProcessAsUser`. The single most common
  real-world way to get this pattern wrong is forgetting to set
  `STARTUPINFO.lpDesktop = "winsta0\\default"` before the `CreateProcessAsUser` call - without
  it, the new process is created on a non-interactive window station and its window is simply
  never visible to anyone, with no exception and no error code to explain why. Every handle
  this path acquires (`userToken`, `primaryToken`, `environment`, `procInfo.hProcess`/
  `hThread`) is closed in a `finally` block regardless of which step failed - do not add a new
  early return that skips that cleanup. If the current process already lives in the target
  interactive session (e.g. manual/dev testing, not the real service deployment), none of this
  session-crossing machinery runs at all - `ShowAlert` checks
  `Process.GetCurrentProcess().SessionId == WTSGetActiveConsoleSessionId()` first and just
  calls `Process.Start` directly.
- **Only one AlertHost.exe may own the alert-delivery pipe per interactive session, and that
  ownership is decided by a session-scoped named `Mutex`, not by any process-tracking in this
  binary.** `AlertHost.App.OnStartup` tries to create `"DlpEndpointMonitor.AlertHost.Singleton"`
  (deliberately no `"Global\"` prefix - each session must get its own owner, since this app runs
  once per logged-in user, not once per machine); whichever instance wins hosts the named pipe
  server (`AlertPipe.Name`, defined exactly once in `AlertContracts` and never hand-typed
  elsewhere) and the in-memory `AlertQueue`, and every later invocation in that session detects
  the mutex is already held, writes its `AlertRequest` as one newline-terminated JSON line to
  the pipe, and exits immediately - it never starts a second server. The very first alert in a
  session is passed to the winning instance as a base64 `--initial-alert=` argument rather than
  relying on a connect-after-launch race, since the new process becoming the owner and starting
  its pipe server is not instantaneous. See `ai_agent_doc/PROJECT.md` section 11 for the full
  queueing/coalesce/cap policy this feeds into.
- **Windows gives no clipboard-format signal distinguishing a plain-text cut from a copy** -
  unlike Explorer's file-drag convention (a synthetic "Preferred DropEffect" format,
  `ClipboardActions.ReadDropEffect`, correctly read for Files), so `ClipboardActions.TryReadText()`
  cannot determine cut-vs-copy from the clipboard content alone and would hardcode "copy" for
  every text change including a genuine cut. `Core/ClipboardOperationHint.cs` closes this gap by
  correlating `KeyboardHook`'s Ctrl+X detection with `ClipboardMonitor`'s next
  `WM_CLIPBOARDUPDATE`-driven read - both run on the same STA message-pump thread (`Program.cs`),
  and the low-level keyboard hook always observes Ctrl+X before the resulting clipboard change is
  dispatched (an input-pipeline hook fires before the target app even processes the keystroke), so
  no locking is needed, just the hint's own short recency window as a safety net. Only Text needs
  this - Files already gets a correct cut/copy signal from the drop-effect format.
- **OpenClipboard can transiently fail even when nothing is actually wrong.** Windows can
  broadcast `WM_CLIPBOARDUPDATE` to listeners while another process (or another listener that
  reacted first) still holds the clipboard open, so a single unretried `OpenClipboard` call can
  return `ERROR_ACCESS_DENIED` for a brief window on a perfectly normal copy/cut/paste.
  `Actions/ClipboardActions.cs` retries every `OpenClipboard` call (`Read()`, `SetText()`,
  `Clear()`) through the generic `Core/RetryPolicy.cs` helper with a small, bounded budget
  (5 attempts, 10ms apart - worst case ~40ms) rather than hand-rolling a loop at each call site;
  kept deliberately small because `Read()` is also called from `KeyboardHook.ShouldBlockPaste`
  inside a global low-level keyboard hook, which Windows can silently unhook if the callback
  takes too long. A persistent failure (all retries exhausted, `Read()` still returns `null`) is
  no longer silently swallowed - both `ClipboardMonitor.OnClipboardChanged` and
  `KeyboardHook.ShouldBlockPaste` now emit `EventEmitter.EmitError("clipboard_read_failed", ...)`
  for visibility before returning, without changing `ShouldBlockPaste`'s fail-open guarantee.
- **Paste blocking only sees paste triggered by a keystroke - Ctrl+V and Shift+Insert, nothing
  else.** `KeyboardHook.Callback` detects both (Shift+Insert is the classic alternate paste
  shortcut, just as visible to a low-level keyboard hook as Ctrl+V), but right-click Paste, an
  app's own Paste button/menu item, or an app calling `GetClipboardData` internally are not
  keystrokes at all - there is no OS-level "a paste just happened" notification independent of
  the input action that triggers it (unlike copy/cut, which `WM_CLIPBOARDUPDATE` reports
  regardless of how the clipboard was written). Closing that remaining gap would require API
  hooking/code injection into every target process - a fundamentally different, far more
  invasive architecture (fragile across app updates, and exactly the technique antivirus/EDR
  flags as rootkit-like) - not a small addition like this one.
- **Clipboard/keyboard monitoring is structurally blind in the real LocalSystem-service
  deployment without the companion launch - Session 0 has no desktop.** When this binary runs as
  a service it lives in Session 0, whose clipboard and low-level keyboard hooks reach an inert,
  never-user-touched desktop - so a `ClipboardMonitor`/`KeyboardHook` created there watches
  nothing. `Program.cs` fixes this by reusing `AlertActions`' proven session-crossing pattern
  (now extracted into `Actions/SessionActions.cs`): if the active console session is NOT this
  process's own session, it launches a second copy of this SAME exe with `--session-companion`
  into the interactive user's session (`WTSQueryUserToken` + `CreateProcessAsUser`), and skips
  building its own local (inert) clipboard/keyboard hooks. The companion runs ONLY
  `MessageWindow` + `ClipboardMonitor` + `KeyboardHook` - never device monitors or the stdin
  `CommandDispatcher` (device policy stays exclusively the primary's) - shares live policy with
  the primary through the SAME `%ProgramData%` files (a `FileSystemWatcher`, debounced because
  the atomic temp-file-then-rename save fires several events per logical write, calls
  `ClipboardRuleList.Reload()` when the primary mutates a list), and relays every event it emits
  back to the primary's stdout over `Core/ClipboardCompanionRelay`'s named pipe so the agent sees
  those events exactly as if the primary emitted them locally (`EventEmitter.RawLineSink` forwards
  the companion's own emits into the relay Client). Launch failure fails SAFE, not silent - the
  primary keeps its (inert-but-present) local hooks and emits a `clipboard_companion_launch`
  error - and a session with no user logged on yet runs locally while emitting an info note that
  protection is inactive until logon (no retry/poll to launch a companion on a later logon in
  this pass). Reconnecting the relay after a dropped pipe connection is likewise an explicit known
  limitation, not yet implemented. See `ai_agent_doc/PROJECT.md` section 11.
- **A companion must be killed both before launching a fresh one AND when the primary that owns
  it shuts down, or it lingers indefinitely - confirmed real production bug, twice.** Nothing
  previously stopped this at either end: every service restart just launched ANOTHER
  `--session-companion` process without touching whatever was already running (observed live:
  three simultaneous companions, two of them orphaned - their own primary long since exited), and
  separately, NOTHING - not the MSI uninstall's `ServiceControl`, not the agent service host's
  stop/process-tree-kill handling, not this binary's own two shutdown paths - ever proactively
  killed the companion when the primary stopped, since it lives in a different Windows session
  reached via `CreateProcessAsUser`, not a normal process-tree child the OS or a tree-kill would
  find. Confirmed live: after an MSI uninstall, the companion was left running with no primary at
  all. Either way, a lingering companion still independently owns a real
  `ClipboardMonitor`/`KeyboardHook` (Windows lets multiple processes each register
  `WM_CLIPBOARDUPDATE`/`WH_KEYBOARD_LL` - it is not exclusive), enforcing whatever policy was
  loaded into ITS OWN memory at ITS OWN startup - so a clipboard rule disabled minutes ago can
  keep silently triggering via an orphan that never got the message, alongside (or after) the
  primary that owned it is gone. `SessionActions.TerminateCompanionProcesses(sessionId, exePath)`
  finds any other process at the same exe path already running in the target session (excluding
  this process itself) and kills it - best-effort by design (a failure to enumerate or kill one
  leftover process must never block the caller's next step). `Program.cs` calls it at TWO points:
  immediately before `LaunchIntoSession` (so a restart replaces rather than accumulates - anything
  already running from this exact binary in that session at that point can only be a prior
  generation's leftover, since the current primary has not launched its own companion yet), and
  from BOTH of this process's own shutdown routes - the `stopCompanion` delegate set when the
  companion is launched, called from the Ctrl+Break/cancellation `finally` block AND from
  `WindowsControlHandler.Handle(ShutdownCmd)` before `Environment.Exit(0)` - closing over the
  exact session/exePath used for that launch, so shutdown targets the specific companion just
  started regardless of how this primary is later told to stop. This does NOT cover every stop
  path in the outer service/MSI layer (a hard `taskkill`/crash of the primary bypasses both
  routes) - it covers the two paths this binary itself controls.
- **Display-topology enforcement crosses the same Session-0 gap as clipboard, but in the OPPOSITE
  direction - the decision stays in the primary, only the Win32 call is relayed to the companion.**
  A `SetDisplayConfig` topology switch only takes effect in the session with the real desktop, so
  when the primary runs headless in Session 0 it cannot disable/enable external displays itself.
  The fix is deliberately NOT to move `DisplayMonitor` into the companion: all its whitelist/
  blacklist compliance logic, the checked/blocked counting, and every `monitor_*` event stay in
  the primary, bit-for-bit unchanged. The ONLY thing that moves is HOW its two `DisplayActions`
  calls are invoked - `Program.cs` hands `DisplayMonitor` two `Func<(bool ok, string? error)>`
  delegates at construction: direct `DisplayActions.DisableExternalDisplays`/`EnableExternalDisplays`
  method groups when no companion is active (interactive/dev run, or no session yet), or
  `displayRelayClient.SendCommand("disable"/"enable")` calls routed over `Core/DisplayCompanionRelay`
  when a companion was launched. The companion hosts the relay Server and runs the real
  `DisplayActions` call (through a Program.cs-supplied `executeCommand` switch delegate, so the
  relay file itself never references `DisplayActions` - same seam as `ClipboardCompanionRelay.Server`
  only knowing `EmitRawLine`). Unlike the clipboard relay (companion -> primary, fire-and-forget
  event lines), this one is request/reply: the primary's synchronous `SendCommand` blocks briefly
  for exactly one `{"ok":...}` reply before it emits its own `monitor_blocked`/`monitor_block_failed`/
  `monitor_policy_restore` event. `SendCommand` NEVER throws - any connect/write/read/timeout/parse
  failure comes back as `(false, reason)`, so a missing or wedged companion can degrade the topology
  call to a clean `monitor_block_failed`, never crash the primary's `DisplayMonitor`. When you touch
  the session-decision block in `Program.cs`, build BOTH delegate pairs (relay vs. direct) from the
  SAME session decision that already picks `runClipboardLocally`/`relayServer` - do not re-detect the
  session a second time. See `ai_agent_doc/PROJECT.md` section 11.
- **The action-execution relay above does NOT fix a headless primary missing WM_DISPLAYCHANGE
  itself - that gap needed its own, separate relay leg, confirmed by a real production report
  ("plugging a monitor blocks it; switching projection mode on an already-connected monitor does
  not").** `DisplayCompanionRelay` only carries the "disable"/"enable" ACTION from primary to
  companion; the trigger for BOTH `BlockNonCompliant()` re-checks (`HandleArrival` from
  `WM_DEVICECHANGE` and the 800ms-debounced `OnDisplayChanged` from `WM_DISPLAYCHANGE`) was still
  read entirely from the PRIMARY's own `MessageWindow`. `WM_DEVICECHANGE` is systemwide/PnP, so
  physical monitor plug/unplug still reached a Session-0 primary fine and blocked correctly. But
  `WM_DISPLAYCHANGE` is a session/desktop-scoped broadcast - Session 0 has no desktop, so a
  headless primary's window NEVER receives it, and a pure Win+P projection-mode switch on a monitor
  that was already connected (no new PnP arrival) went completely unnoticed. `Core/DisplayChangeRelay.cs`
  closes this the same direction as `ClipboardCompanionRelay` (companion -> primary, fire-and-forget):
  the companion's own `MessageWindow` DOES live in the interactive session and genuinely receives
  `WM_DISPLAYCHANGE`, so `Program.cs`'s companion branch wires `window.DisplayChanged += () =>
  displayChangeRelayClient.Notify();`, and the primary's `DisplayChangeRelay.Server` calls
  `DisplayMonitor.NotifyExternalDisplayChange()` - a thin public wrapper around the SAME private
  `OnDisplayChanged()` debounce path a local WM_DISPLAYCHANGE would have used, so there is no second
  copy of the debounce/BlockNonCompliant logic. The primary hosts the Server (constructed alongside
  `relayServer` before `LaunchIntoSession`, same lifecycle/disposal points as the other two relays);
  the companion is the Client. Only relevant when the primary actually runs headless with a companion
  launched - an interactive/dev run needs no relay, since its own window already sits in the real
  session and receives WM_DISPLAYCHANGE directly.
- **[fixed - real production regression] Constructing `DisplayChangeRelay.Client` on the companion's
  entry thread, sequentially BEFORE `displayRelayServer`/`bluetoothRelayServer`, starved those two
  companion-hosted servers and broke both of them - confirmed live: "could not connect to companion
  pipe" from both `bluetooth_companion_relay` and `monitor_policy_apply` (`display companion relay`),
  while `clipboard companion ready` still logged fine.** The first version of the
  `DisplayChangeRelay.Client` wiring put `using var displayChangeRelayClient =
  new DisplayChangeRelay.Client();` in `Program.cs`'s companion branch BEFORE
  `displayRelayServer`/`bluetoothRelayServer` were constructed and before `companionThread.Start()` -
  its constructor blocks for up to ~2s (the same bounded connect-retry shape as
  `ClipboardCompanionRelay.Client`). That delay pushed back the moment the companion's OWN
  `DisplayCompanionRelay.Server`/`BluetoothCompanionRelay.Server` started listening, and the
  PRIMARY's very first connection attempts to THEM - fired almost immediately via
  `Task.Run(displayMonitor.BlockNonCompliant)`/`Task.Run(bluetoothMonitor.EnumerateExisting)` right
  after `windowReady.Set()` - carry their OWN ~2s connect-retry budget, so the two races could (and
  on a loaded/cold-starting machine, did) lose. Clipboard was unaffected because it doesn't depend on
  any companion-hosted server. The fix: `DisplayChangeRelay.Client` construction (and the
  `window.DisplayChanged` subscription) now happens inside a `Task.Run` INSIDE the companion thread's
  message-pump body, AFTER `companionReady.Set()`/`EmitInfo("clipboard companion ready")` and BEFORE
  `MessageWindow.RunMessageLoop()` - fully decoupled from both the entry thread's sequential
  companion-hosted-server setup and the message-pump startup itself, so its own (still up to ~2s,
  still one-shot-only, same accepted limitation as `ClipboardCompanionRelay.Client`) connect can never
  again sit on any other component's critical path. **Lesson for any future companion-side relay
  client**: never construct an eagerly-connecting relay `Client` sequentially before a companion-hosted
  relay `Server` (or before `MessageWindow.RunMessageLoop()`) on the shared entry/message-pump thread -
  give it its own `Task.Run`.
- **A follow-up review of `Program.cs` for the same class of bug found 4 more, all fixed together
  (none independently confirmed in production - defensive hardening, not observed regressions):**
  (1) `ClipboardCompanionRelay.Client` - the ORIGINAL, pre-existing relay client - had the exact same
  ordering risk the lesson above warns about: it sat before `displayRelayServer`/`bluetoothRelayServer`
  in the companion branch. Fixed by reordering (servers constructed first); left as a still-blocking
  construction rather than a `Task.Run`, since `EventEmitter.RawLineSink` needs to be set early enough
  that later companion-startup errors (FileSystemWatcher setup, etc.) actually reach the primary/agent
  instead of only the session-crossed child's own unread stdout. (2) The `Task.Run` wrapping
  `DisplayChangeRelay.Client` (previous bullet's fix) had no try/catch - any future throw inside it
  would have silently become an unobserved task exception with zero diagnostic trace, breaking this
  file's own "catch, `EmitError`, keep going" convention; now wrapped. (3) The primary's
  `DisplayChangeRelay.Server` callback (`() => displayMonitor?.NotifyExternalDisplayChange()`) read
  `displayMonitor` - written once on `msgThread` - with no happens-before relationship to the relay's
  own accept-loop thread, unlike `ApplyPolicy`/`RestoreDevices`/`ReevaluateClipboard` later in the same
  file, which correctly gate on `windowReady.Wait()`. Fixed with the same gate: `if (!windowReady.IsSet)
  return;` before touching `displayMonitor` - reuses the existing `ManualResetEventSlim` rather than
  introducing `Volatile`/a new primitive. Worst case before the fix: a projection-change notification
  arriving in the first instants of companion startup could be silently dropped. (4) The primary's
  final shutdown `finally` block called `window?.Stop()` (posts `WM_DESTROY`, does not wait) then
  immediately proceeded to dispose relay servers/clients and emit `"shutdown"`, with nothing waiting
  for `msgThread` to actually finish unregistering its Win32 state. Fixed with a bounded
  `msgThread.Join(TimeSpan.FromSeconds(2))` (bounded, not unbounded - a wedged monitor must never hang
  the `shutdown` command's own reply) plus an `EmitError` if it times out.
- **[fixed - confirmed via real MSI-installed `DlpAgent` service] `DisplayChangeRelay.Client`'s
  connect to the primary failed with `UnauthorizedAccessException`, not a timeout - a genuine
  cross-session pipe ACL denial, not a race.** Confirmed by reading the agent's own SQLite event
  store (`agent.db`'s `events` table - `agent.log` only carries the agent's OWN lifecycle messages,
  not every native-monitor event) after a real MSI install: `"could not connect to primary's
  display-change relay: UnauthorizedAccessException: Access to the path is denied."`, while
  `ClipboardCompanionRelay`'s identically-shaped pipe connected fine moments earlier
  (`"clipboard companion ready"`). Both pipes are constructed with .NET's default (unspecified)
  `PipeSecurity`, hosted by the same primary process - the default ACL that results does not
  reliably grant the interactive-session companion's identity access when the primary runs under
  the service account a real Windows Service install uses, unlike an ad-hoc interactive/dev run.
  Fixed by explicitly granting `PipeAccessRights.ReadWrite` to `WellKnownSidType.AuthenticatedUserSid`
  on `DisplayChangeRelay.Server`'s pipe, via `NamedPipeServerStreamAcl.Create(...)` - the
  `PipeSecurity`-accepting `NamedPipeServerStream` constructor is Windows-TFM-only and unavailable
  from this project's plain `net10.0` target, so the ACL-accepting static factory is the correct
  cross-platform-safe way to set it. `ClipboardCompanionRelay`/`DisplayCompanionRelay`/
  `BluetoothCompanionRelay` are NOT changed - they have not exhibited this failure - but if any of
  them ever does, this is the same fix. This access-control widening (Authenticated Users, not a
  narrower specific-SID grant) was confirmed with the user before applying, since it's a genuine
  security tradeoff - mitigated by the pipe carrying only a bare fire-and-forget `"changed"` trigger
  string, no data, no command/control surface.
- **Bluetooth ENUMERATION crosses the same Session-0 gap, but relayed the other way from display -
  the companion produces the DATA, the primary keeps the DECISION.** `BluetoothActions.EnumerateConnected`
  (`BluetoothFindFirst`/`BluetoothFindNextDevice`) only sees paired devices from a process running
  in the interactive user's session; a headless Session-0 primary enumerates an empty set and would
  wrongly conclude there are no Bluetooth devices to police. As with display, the fix is NOT to move
  `BluetoothMonitor` into the companion - all its whitelist/blacklist compliance logic, composite/
  group handling, and every `bt_*` event stay in the primary, bit-for-bit unchanged. The ONLY thing
  that moves is HOW it obtains the device list: `Program.cs` hands `BluetoothMonitor` a
  `Func<IReadOnlyList<BluetoothActions.BtDevice>>` at construction - a direct
  `() => BluetoothActions.EnumerateConnected().ToList()` passthrough when no companion is active
  (interactive/dev run, or no session yet), or `() => bluetoothRelayClient.Enumerate()` routed over
  `Core/BluetoothCompanionRelay` when a companion was launched. The companion hosts the relay Server
  and runs the real enumeration (through a Program.cs-supplied delegate, so the relay file never
  references `BluetoothActions`' enumeration - same seam as the display/clipboard relays). Unlike
  the display relay's two-shape `{"ok":...}` reply, this reply is a whole `List<BtDevice>` JSON
  array serialized through `AppJsonContext` (so the free-form `Name` is escaped and no reflection
  escapes the trimmed path). `Client.Enumerate` NEVER throws - any connect/write/read/timeout/parse
  failure comes back as an EMPTY list, indistinguishable from "genuinely zero paired devices" to
  `BluetoothMonitor`'s callers, so a missing or wedged companion degrades to "no BT devices seen",
  never crashes the primary. Build this delegate from the SAME session decision that already picks
  `runClipboardLocally`/`relayServer`/`displayRelayClient` - do not re-detect the session a third
  time. See `ai_agent_doc/PROJECT.md` section 11.
- **A `StreamReader` and `StreamWriter` wrapping the SAME `PipeDirection.InOut` pipe must both be
  constructed with `leaveOpen: true`, or whichever disposes first silently closes the pipe out
  from under the other.** `DisplayCompanionRelay.cs` and `BluetoothCompanionRelay.cs` both build
  a reader AND a writer over one pipe per request/reply exchange; with the default
  `leaveOpen: false`, the second one to dispose (`StreamWriter.Dispose` always calls the
  underlying stream's `Flush`, even with nothing buffered) threw
  `ObjectDisposedException("Cannot access a closed pipe.")` - a real bug observed repeatedly in
  production under both the `bluetooth_companion_relay` and `display_companion_relay`/
  `monitor_policy_restore` event sources. Fixed at all four call sites (`Server.HandleRequestAsync`'s
  reader+writer, `Client`'s writer+reader, in both files) via explicit `leaveOpen: true` plus a
  shared `NoBomUtf8` encoding field (preserves the previous default `StreamWriter(Stream)`/
  `StreamReader(Stream)` no-BOM behavior) - the existing explicit `pipe.Dispose()` at each call
  site remains the single owner that actually closes the pipe. `ClipboardCompanionRelay.cs` does
  NOT have this shape (`PipeDirection.In`/`Out`, one reader OR one writer per pipe, never both)
  and needed no change. Regression-covered by
  `DlpEndpointMonitor.Tests/CompanionRelayPipeTests.cs`, which drives a real Server/Client pair
  over the actual named-pipe transport through a 50-iteration loop against an isolated,
  test-only pipe name (a single request/reply did not reliably reproduce the bug, and reusing
  the production pipe name risks colliding with - or, for Display, actually commanding - a real
  companion instance already running on the machine).
- **[fixed - confirmed via real production logs, after the ACL fix above] `DisplayChangeRelay.Server`
  copied `ClipboardCompanionRelay`'s rolling read-inactivity timeout, and it killed every connection
  ~5s after connecting - long before a real, rare `WM_DISPLAYCHANGE` could plausibly occur.** Once the
  ACL fix above let the companion actually connect, production logs then showed
  `"dropping stalled relay client: no line received within timeout"` followed by the companion's next
  `Notify()` failing with `"Pipe is broken"` - the server had already dropped a perfectly healthy,
  silently-idle connection. Unlike `ClipboardCompanionRelay` (a steady stream of clipboard/keyboard
  events, where prolonged silence really does mean a stalled client), this relay's entire job is to
  sit connected and silent until a rare, possibly far-future display change happens - silence is the
  normal, healthy state here, not a symptom. Fixed by removing the timeout entirely from
  `ReadLinesAsync`: it now awaits `reader.ReadLineAsync(token)` with no inactivity bound, relying only
  on the overall shutdown `token` to end the wait. Confirmed safe across companion restart/crash,
  primary shutdown, and MSI update/uninstall: a pipe is a kernel object, so either end terminating
  (clean or hard-killed) closes the handle and unblocks the peer's pending read via `IOException`/EOF
  regardless of any application-level timeout - no watchdog was ever doing real work here.
- **`DisplayMonitor.BlockNonCompliant()` - the re-check both a policy-list mutation AND
  `DisplayChangeRelay`'s projection-mode notification run through - only ever emitted the aggregate
  `monitor_policy_apply: checked N, blocking N` info line, never a real `monitor_blocked`/
  `monitor_block_failed` event per non-compliant monitor.** Confirmed against real logs: many
  `monitor_policy_apply` lines, zero `monitor_blocked` events, for an entire session where projection
  blocking was demonstrably working. Unlike `HandleArrival`'s `BlockAllExternal` (a fresh monitor
  arrival, which already emitted a real per-device event) and unlike `UsbMonitor`/`BluetoothMonitor`'s
  own `BlockNonCompliant()` (which call `BlockDevice` per non-compliant device, each emitting its own
  event), this method had no per-device audit trail at all. Fixed by collecting the non-compliant
  monitors found during the sweep and, after the single `_disableExternalDisplays()` call, emitting
  one `MonitorBlockedEvent`/`MonitorBlockFailedEvent` per non-compliant monitor - same shape the
  arrival path already used. The aggregate info line is unchanged.
- **`SourceEventId` - a new, generalized correlation field, added first to clipboard and then to USB
  device events, with two DIFFERENT lifetime semantics under the same field name.** For clipboard
  (`ClipboardContentBlockedEvent`/`ClipboardContentBlockFailedEvent`), the verdict event carries no
  content of its own - unlike device events, which already self-identify via Vid/Pid/Serial/
  InstanceId, clipboard's "content" (arbitrary text/files) has no natural key - so `SourceEventId` is
  set to the `EventId` of the `ClipboardTextEvent`/`ClipboardFilesEvent` reported one call earlier, in
  the SAME method (`ClipboardMonitor.EvaluateAndEnforce` for copy/cut, `KeyboardHook.ShouldBlockPaste`
  for paste - both independently construct a change event then a verdict event; both updated). This
  required promoting `EventId` from a convention (a `{ get; } = EventEmitter.NewEventId()` property
  every record happened to declare) to a REAL member of the `IEvent` interface, so a polymorphic
  `IEvent`-typed local's own `EventId` is readable without a type-switch over every concrete record -
  source-compatible with every existing event since they all already declared a matching property.
  For USB (`UsbDeviceConnectedEvent`/`Detected`/`Disconnected`/`Blocked`/`BlockFailed`/`Unblocked`/
  `UnblockFailedEvent`), the semantics are broader: `SourceEventId` is the EventId of the FIRST event
  seen for a composite device's `GroupId` in the current connect episode - every sibling interface's
  own connect, every block, every disconnect, every unblock for that same physical device carries
  that SAME anchor id, not a fresh one each time. `UsbMonitor` tracks this via an in-memory
  `Dictionary<string,string>` (GroupId -> anchor EventId, under a `Lock`), populated on first sight and
  released via `ReleaseGroupAnchorIfLastSibling` the moment no sibling interface of that group remains
  connected (checked via a live `UsbActions.EnumerateGroupSiblings` re-enumeration, NOT a disconnect
  counter, so it survives out-of-order/partial disconnects and ejects) - this cleanup is MANDATORY, not
  optional, since an unbounded dictionary is exactly the kind of slow leak a multi-week/month-uptime
  service must not accumulate. Devices with `GroupId == null` (non-composite, or Bluetooth-backed -
  Bluetooth skips USB grouping entirely) never get an anchor; their `SourceEventId` is always null.
  `GroupId`/`InstanceId` themselves are unchanged and still required for actual device control
  (`DeviceDisableCmd`/`DeviceEnableCmd` still address by raw `InstanceId`) - `SourceEventId` is a
  short, filter-friendly correlation handle layered on top, not a replacement identity.
- **`AlertActions.LaunchDirect` leaked the `Process` object `Process.Start` returns - a native
  process-handle leak, one per direct (same-session) alert launch.** Found via a self-directed
  memory-flow audit of the whole codebase (everything else checked out clean - monitors, relay accept
  loops, persisted lists, static lookup tables all correctly bounded/disposed). Low severity today
  since `AlertActions.ShowAlert` is still not called from any `Monitors/*` (see the "Not implemented"
  note elsewhere in this file) - but would become a real, slowly-accumulating handle leak the moment
  alerting is wired in and fires repeatedly over weeks of uptime. Fixed with `using var process =
  Process.Start(...)` - disposes the .NET wrapper/handle deterministically; does not kill or wait on
  the launched child, which keeps running independently exactly as before.
- **Every `*_blocked`/`*_block_failed` event pair, across all five policy domains, now carries a
  `Reason` field - exactly `"blacklist_match"` or `"whitelist_gate"`, never a third value.**
  Prompted by a user audit asking whether a block/block-failed event says WHICH policy caused it -
  previously only `ClipboardContentBlockedEvent` did (via `Reason`/`MatchedPattern`, and even that
  was missing from `ClipboardContentBlockFailedEvent` despite both values already being computed in
  scope at that call site - a one-line fix). `Reason` answers "which list caused this"; the
  pre-existing `Error` field is unchanged and continues to answer "why did the enforcement action
  itself fail" - the two are orthogonal and must not be conflated (a `Reason` of `"blacklist_match"`
  with a populated `Error` means: this device WAS correctly targeted for blocking, but the block
  action itself failed for the reason in `Error`). No third `"protected_internal"` reason value was
  added for the built-in-keyboard/adapter safety refusals in `UsbMonitor`/`NetworkMonitor` - those
  still report whichever of the two policy reasons triggered the block ATTEMPT; `Error` already
  explains the refusal itself (e.g. `"protected internal input device..."`). For USB specifically,
  `Reason` is RE-DERIVED inside `UsbMonitor.BlockDevice` from the live group-compliance re-check
  (`IsGroupCompliant`'s `groupBlocked` out-param) when a composite group applies, rather than trusting
  the caller's earlier single-interface guess - same never-trust-a-single-interface-in-isolation
  principle this file already documents elsewhere. `DisplayMonitor.BlockNonCompliant`'s per-monitor
  loop carries each monitor's own `Reason` alongside it (a list of `(ParsedDevice, string reason)`
  tuples), not one shared value applied to the whole non-compliant batch. No new NuGet dependency, no
  wire-shape change beyond the one new field per record - `Vid`/`Pid`/`Mac`/`InstanceId`/etc. and
  `Error` are all unchanged.
- **[fixed - confirmed via real machine logs and a live reflection test against the actual
  connected monitor] `DisplayActions.DisableExternalDisplays()` could report success
  (`MonitorBlockedEvent`) for a monitor that was never actually turned off - a real incident, not a
  theoretical one.** A live event log on a real deployment (`C:\ProgramData\DLP\agent.db`) showed a
  `monitor_blocked` event fire for an external HDMI monitor (vid `MSI`, pid `3CB6`) that stayed
  visibly on, while the identical `SetDisplayConfig` call used by `EnableExternalDisplays` (the
  restore direction) failed with Win32 error `0x1F` (`ERROR_GEN_FAILURE`) on every single attempt in
  that same session - proof this machine's CCD/driver stack does not always make a `SetDisplayConfig`
  return code trustworthy on its own. Both of `DisableExternalDisplays`' success-reporting branches
  (the `SDC_TOPOLOGY_INTERNAL` "second-screen-only" fallback and the main
  `SetDisplayConfigPaths(..., SDC_APPLY | SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_SAVE_TO_DATABASE |
  SDC_ALLOW_CHANGES)` call) trusted `result == 0` alone as proof the external display actually went
  dark. Fixed by adding a private `VerifyExternalDisplaysOff()` helper - a FRESH
  `QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS)` re-query (not reusing the stale pre-mutation
  paths/modes arrays) run after either success branch, which only lets the function report
  `(true, null)` if no non-internal path is still active; otherwise it returns
  `(false, "SetDisplayConfig reported success but an external display is still active")`. This is
  READ-ONLY (no new `SetDisplayConfig` call) and deliberately does not touch the
  `if (!anyExternal) return (true, null)` early-return (a query that finds nothing external to
  block in the first place is still an honest, true success) or `EnableExternalDisplays()` itself
  (its return-code-based failure reporting was already correct - it consistently and correctly
  failed in the incident logs; only the DISABLE/block direction had a false-positive-success gap).
  `DisplayMonitor.cs`/`DisplayCompanionRelay.cs`/`Program.cs` needed ZERO changes - they already
  branch correctly on `DisableExternalDisplays`' own `(ok, error)` tuple, and this function already
  runs in the correct session either way (called directly, or via the companion's `executeCommand`
  delegate), so the verification query is automatically session-correct too. Confirmed live via a
  standalone reflection harness (bypassing `Program.cs`/stdin/companion entirely, to avoid
  disturbing the two real production instances already running on the test machine) that called the
  actual fixed `DisableExternalDisplays()` against the real connected monitor: it correctly returned
  `ok=false` with the new verification message on the first attempt - the exact false-positive this
  fix targets, caught live. Separately, repeated `EnableExternalDisplays()` calls on that same
  machine still failed with the identical `0x1F` - a pre-existing, reproducible limitation of that
  machine's CCD/driver stack this fix does not (and cannot) resolve; it only ensures the event stream
  never lies about whether the block actually took effect.
- **[fixed] `AlertHost`'s alert-delivery named pipe was one fixed string shared across every
  session - since named pipes are NOT session-isolated the way `Mutex` names are, a stale
  `AlertHost.exe` left running in a disconnected session could silently swallow alerts meant for
  the currently-active session.** `AlertHost`'s singleton `Mutex`
  (`"DlpEndpointMonitor.AlertHost.Singleton"`, section 11.2) is correctly scoped one-owner-per-session
  by deliberately omitting the `"Global\"` prefix - but that prefix convention is a `Mutex`-specific
  kernel-object rule, and it has no equivalent for named pipes: a pipe (`\\.\pipe\<name>`) lives in
  one machine-wide kernel namespace regardless of which session created it, so there was never an
  "unprefixed = per-session" option to get right for `AlertPipe.Name` the way there was for the
  mutex - the original fixed-string pipe name was *always* reachable from every session. Fast User
  Switching does not tear down the previous session's processes, so its `AlertHost.exe` (and the
  `PipeTransport.Server` it owns) kept running and kept listening on that one shared pipe name.
  `AlertActions.ShowAlert` in a LATER, currently-active session would connect to that name, write its
  `AlertRequest`, get a successful write back, and report `(true, null)` - with the alert actually
  enqueued into the disconnected session's dead window queue, never shown to anyone, and no error
  surfaced anywhere. Fixed by making the pipe name a function of session id -
  `AlertPipe.NameFor(uint sessionId)` (`AlertContracts/AlertPipe.cs`) - and threading a session id
  through every caller that used to reference the bare constant: `PipeTransport.Server`/
  `TrySendToOwner` (`AlertHost/PipeTransport.cs`) derive their own via
  `Process.GetCurrentProcess().SessionId` (`AlertHost/App.xaml.cs`), and
  `Actions/AlertActions.cs`'s `ShowAlert` now resolves the TARGET session
  (`SessionActions.GetActiveConsoleSessionId()`) *before* attempting the pipe at all, rather than
  after, since there is no longer a session-agnostic name to try first. Covered by
  `DlpEndpointMonitor.AlertHost.Tests/AlertPipeTests.cs` (pure string-formatting logic only -
  `NameFor`'s distinctness/determinism - the actual cross-session pipe behavior this fixes still
  needs real multi-session Windows state and remains manual-only, same as every other Win32-adjacent
  piece of this capability).
- **The active console session was resolved exactly once at `Program.cs` startup and never
  revisited - a live user switch (Fast User Switching) or a logout+different-user-login left the
  companion and every relay client bound to the session that was active at process start, forever,
  until a full service restart.** Concrete consequence: a Bluetooth mouse/keyboard reconnecting
  with a new device-tree instance during the switch was never re-evaluated against policy, so
  something that should have been blocked kept working right through the transition. Fixed by
  registering `MessageWindow` for `WM_WTSSESSION_CHANGE`
  (`NativeMethods.WTSRegisterSessionNotification`/`WTSUnRegisterSessionNotification`,
  `NOTIFY_FOR_ALL_SESSIONS` - this process's own session never changes in the real deployment, but
  some OTHER session's logon/logoff/connect/disconnect does) and a new `MessageWindow.SessionChanged`
  event (wParam/lParam deliberately ignored - any notification just means "go re-derive the active
  console session"). `Program.cs`'s former one-shot startup companion-decision block is now
  `EnsureCompanionForActiveSession()`, a re-runnable function dispatched off the message-pump thread
  via `Task.Run` from an 800ms-debounced `OnSessionChanged` (same cancel-then-dispose-before-
  installing-the-new-token discipline as `DisplayMonitor.OnDisplayChanged`, since Windows can fire
  several `WM_WTSSESSION_CHANGE` notifications for one logical transition) - never called directly
  from `WndProc`, since it makes blocking Win32 calls and ~2s relay-client connects. On a genuine
  session change (tracked via `companionTargetSession`/`companionEverResolved`, distinguishing a
  real transition from a redundant re-notification) it terminates the OLD session's companion
  (`SessionActions.TerminateCompanionProcesses`) before launching a fresh one into the new session,
  same `LaunchIntoSession` path as startup. **The sharp edge**: `BluetoothMonitor`/`DisplayMonitor`
  capture their enumerate/display-control `Func<>` delegates BY VALUE at construction time, so
  reassigning those delegate variables later would silently never take effect (the monitor already
  holds the old closure) - the fix instead routes through mutable
  `currentDisplayRelayClient`/`currentBluetoothRelayClient` variables that the already-constructed
  delegates read FRESH on every call, so swapping the variable changes behavior without the
  monitors ever needing a new delegate instance (same reasoning behind `stopCompanion` being a
  stable wrapper around a mutable `currentStopCompanion` field, since `WindowsControlHandler` holds
  its delegate in a `readonly` field). Any future companion-relevant value a monitor needs live
  must go through this same "mutable variable the delegate re-reads" indirection, not a reassigned
  delegate. After a successful relaunch, `EnsureCompanionForActiveSession` also forces a fresh
  `bluetoothMonitor.EnumerateExisting`/`displayMonitor.BlockNonCompliant` sweep - this, not just the
  relay-plumbing swap, is what actually fixes stale enforcement after the switch. **Deliberately
  scoped to only the already-companioned session A -> session B transition** - rebuilding the
  primary's own local `clipboardMonitor`/`keyboardHook` live (a same-session startup that later
  needs to become companioned, or vice versa) is explicitly out of scope, since that would require
  reconstructing objects live on the message-pump thread.
- **[fixed - confirmed reproducible on a fresh MSI-installed deployment, both via a plain machine
  reboot and via the sibling dlp_v2 Node.js agent respawning this binary after its own self-update]
  `msgThread` read `bool runClipboardLocally` exactly once, at its own construction - a primary that
  started before any interactive session yet existed permanently committed to local, Session-0-bound
  `ClipboardMonitor`/`KeyboardHook` hooks, even after a companion later launched successfully into a
  real session.** dlp_v2 has no session-awareness of its own and no coordination with this process's
  own session detection - it just spawns a fresh copy of this binary after its own self-update and
  hopes the timing works out, so a cold-started primary racing ahead of session detection was a real,
  recurring path into this bug, not just a boot-time curiosity. This was exactly the gap the
  `WM_WTSSESSION_CHANGE`/`EnsureCompanionForActiveSession` fix above knowingly left open at the time
  ("does not attempt to un-do local clipboardMonitor/keyboardHook hooks that are already running") -
  accepted then as a theoretical edge case, confirmed here as a reproducible one. Once a companion
  took over, the stale local hooks were never torn down (Windows lets multiple processes each
  register `WM_CLIPBOARDUPDATE`/`WH_KEYBOARD_LL` - registration isn't exclusive, so nothing ever
  errored), silently breaking clipboard AND screenshot-shortcut policy enforcement until a full
  service restart forced the startup check to run again from scratch. Fixed by promoting the local
  `KeyboardHook` (`localKeyboardHook`) to the same outer scope `clipboardMonitor` already had, and
  disposing both the instant `EnsureCompanionForActiveSession`'s `if (ok)` branch confirms a
  companion took over - `KeyboardHook.Dispose`/`ClipboardMonitor.Dispose` are both simple,
  thread-safe, safe-from-any-thread operations, so no message-pump-thread reconstruction (the reason
  the prior fix gave for leaving this out of scope) was actually needed. `ReevaluateClipboard` was
  made null-tolerant (`clipboardMonitor?.ApplyPolicy()`) since `clipboardMonitor` can now
  legitimately go from non-null back to null mid-run, and final shutdown disposal of both variables
  now covers all three possible histories: never-local, local-then-torn-down, and local-for-the-
  whole-run.
- **[known limitation, not fixed] Only ONE session is ever protected at a time - the one attached
  to the physical console.** `SessionActions.GetActiveConsoleSessionId` (`WTSGetActiveConsoleSessionId`)
  by definition returns at most one session id, and `EnsureCompanionForActiveSession` companions
  exactly that one. On a machine where a second user is connected concurrently via Remote Desktop (or
  a third-party remote-access tool like AnyDesk that creates its own Windows session rather than
  taking over the console session), that second user's clipboard/keyboard/screenshot/AlertHost
  protection is simply absent - not degraded, not delayed, entirely unenforced - for as long as they
  are not the active console session. This is an accepted, deliberate scope limitation for the
  product's current target deployment (one physical user per machine, console session only), not a
  bug to be fixed under the current scope - documented here so it is not mistaken for an oversight if
  raised again. Extending protection to every concurrently active session (not just the console one)
  would require enumerating all active sessions (`WTSEnumerateSessions`) and running the equivalent
  of `EnsureCompanionForActiveSession`'s companion-launch logic per session instead of once for a
  single "the" active session - a materially larger design than anything in this document, deferred
  until the product actually needs to support shared/terminal-server-style machines.
- **`--policy-only` is a third, throwaway startup mode alongside `--schema`/`--session-companion`,
  launched by the Node agent's uninstall cleanup step (`cleanup-policies.ts`) instead of the real
  primary.** The real primary's normal startup unconditionally calls
  `EnsureCompanionForActiveSession`, which kills and replaces whatever companion the REAL,
  still-running primary already has live in the interactive session - running that path from an
  uninstall cleanup process would open a self-inflicted protection gap during every uninstall (see
  `UNINSTALL-POLICY-CLEANUP-FIX-PLAN.md`). `--policy-only` does ZERO session/companion/monitor
  work - no `EnsureCompanionForActiveSession`, no `UsbMonitor`/`BluetoothMonitor`/`DisplayMonitor`/
  `NetworkMonitor` - it just wires a `CommandDispatcher` straight to the same default
  `%ProgramData%` policy files the real primary uses (every monitor-reconciliation delegate passed
  to its handlers is a no-op), so `reset_all_policy`/`usb_enable_storage`/`screenshot_block_disable`/
  `shutdown` sent by the cleanup script mutate the real persisted state without disturbing anything
  live. Checked in the same early argument block as `--schema`/`--session-companion`, before any of
  the real primary's own state is constructed.
- **`SessionActions.TerminateStaleAlertHost` closes the same class of gap
  `TerminateCompanionProcesses` closes for the clipboard/keyboard companion, but for
  `DlpEndpointMonitor.AlertHost.exe` - confirmed live to survive a primary restart, a session
  change, and even two agent self-updates untouched, quietly wedging its dispatch loop for the rest
  of that AlertHost's process lifetime (see `ALERTHOST-STALE-PROCESS-FIX-PLAN.md`).** It is a thin
  wrapper, not a near-duplicate implementation: AlertHost's exe path is a fixed constant (it always
  lives alongside the primary), unlike the companion's caller-supplied exe path, so it just resolves
  that one constant path and delegates the actual kill loop to `TerminateCompanionProcesses`. Called
  from three places in `Program.cs`: from `stopCompanion` on this primary's own shutdown (re-deriving
  the CURRENT active console session fresh, since AlertHost - unlike the companion - is never closed
  over a session captured at launch time), and from `EnsureCompanionForActiveSession` both for the
  OLD session (on a genuine session change) and the NEW session (any stale leftover from a prior run,
  before launching a fresh companion) - gated only on the session-change/stale-run condition itself,
  not on the companion's own exe-path check, since AlertHost cleanup doesn't need it. Alongside this,
  `AlertQueue`'s dispatch loop (`DlpEndpointMonitor.AlertHost/AlertQueue.cs`) now has an
  `OnlyOnFaulted` continuation (`LogDispatchLoopFault`) that appends a timestamped exception dump to
  `%ProgramData%\DlpEndpointMonitor\alerthost-fault.log` if the loop ever dies - a
  `CreateProcessAsUser`-launched GUI process has no console for `Console.Error` to reach, and .NET's
  default unobserved-task-exception behavior would otherwise discard the fault silently, leaving a
  wedged-forever AlertHost with zero diagnostic trail. Deliberately logging-only, not a self-restart
  - the actual root cause of the dispatch loop dying is not yet proven (most likely candidate is
  `_signal.WaitAsync` throwing something other than `OperationCanceledException`, outside the loop's
  own inner try/catch which only wraps the per-alert `_show` call), so self-healing is deferred as a
  separate, later decision once the real trigger is known; this fix's job is only to make a dead
  dispatch loop observable instead of silently vanishing.
- **USB mass-storage safe-enforcement: three coupled fixes, all triggered by the same
  `usb_disable_storage` kill switch, none of which touch each other's code path directly.** The
  design doc that originally covered this (`USB-STORAGE-SAFE-ENFORCEMENT-FIX-DESIGN.md`) has since
  been deleted now that the fix shipped - this entry is its only remaining write-up.
  1. *Volume-interface block-failed noise.* `Actions/UsbActions.cs`'s `IsVolumeInterface` (checked
     against the literal `GUID_DEVINTERFACE_VOLUME` string, `Actions/UsbActions.cs:291`) recognizes
     a `STORAGE\Volume\...` interface path as a volume-namespace identity, not an independently
     disableable devnode - `CM_Locate_DevNodeW` can never resolve it, so any `DisableDevice` attempt
     against it was a guaranteed, deterministic failure. `UsbMonitor.BlockDevice`
     (`Monitors/UsbMonitor.cs:445`) now checks this first and skips with an `EmitInfo` instead of
     attempting (and failing) the disable - the real block still happens via the sibling
     Disk/CDROM/TAPE-function interface's own independent `BlockDevice` call for the same physical
     device. Confirmed live via this machine's own event log: every mass-storage arrival used to
     produce a guaranteed `usb_device_block_failed` for its Volume sibling alongside the real block
     of its Disk sibling.
  2. *Internal boot disk exposed to `kind: storage` blacklist entries.* `IsProtectedInternal`
     (`Actions/UsbActions.cs:551`) only ever routed `StrictInputKinds` (keyboard/mouse/hid/hub) and
     `DeviceKind.Unknown` through the `IsBuiltIn` bus-ancestry check - `DeviceKind.Storage` was not
     gated at all, so a `{kind: storage}` blacklist entry could reach a real `DisableDevice` call
     against the machine's own internal boot disk, with nothing but Windows' own
     `CR_NOT_DISABLEABLE` refusal standing between that and an actual bricked machine. Fixed by
     adding `kind != DeviceKind.Storage` to the same gate check - mirrors the identical fix
     `NetworkMonitor`/`IsBuiltIn` already got for built-in NICs (section 10, `DeviceKind.Network`
     entry above).
  3. *Already-connected storage untouched by the kill switch.* `usb_disable_storage` only ever wrote
     the `USBSTOR` registry `Start` value - correct for preventing a FUTURE driver load, but a
     no-op against a driver instance already bound and mounted at the moment the switch flips, so an
     already-inserted USB drive stayed fully accessible until physically replugged.
     `UsbMonitor.BlockAlreadyConnectedStorage()`/`RestoreStorageDisabled()`
     (`Monitors/UsbMonitor.cs:230`/`:754`) retroactively disable/restore exactly those devices,
     reusing the same `DisableDeviceWithEscalation` mechanics `BlockDevice` uses. This needed two
     additive, deliberately-separate pieces rather than reusing existing ones: new events
     `UsbStorageDeviceBlockedEvent`/`UsbStorageDeviceBlockFailedEvent`
     (`Core/EventEmitter.cs:285`/`:292`) - NOT a new `Reason` value on `UsbDeviceBlockedEvent` (`Reason`
     is contractually only `blacklist_match`/`whitelist_gate`) and NOT a reuse of the pre-existing,
     purely-observational-with-no-failure-case `UsbStorageBlockedEvent`, since this action genuinely
     can fail; and a new optional `BlockedBy` tag on `DisabledDeviceRecord`
     (`Core/DisabledDevices.cs:24`, `internal const string StorageKillSwitchBlockedBy =
     "usb_storage_disabled"` in `Monitors/UsbMonitor.cs:208`) so `RestoreCompliant`'s own
     whitelist/blacklist reconciliation sweep (`Monitors/UsbMonitor.cs:685`) explicitly excludes
     kill-switch-disabled records - without that exclusion, an unrelated policy mutation (e.g.
     `DeviceWhitelistDisableCmd`) would silently re-enable a device the kill switch disabled while
     `usb_disable_storage` was still in effect, since compliance checks never consult
     `IsUsbStorageEnabled` on their own.
- **`usb_storage_blocked` never fired for a driverless single-function mass-storage stick, because
  interface-arrival notification cannot see a devnode with no driver bound at all.** Full design in
  `ai_agent_doc/USB-STORAGE-BLOCKED-POLL-DESIGN.md`; implementation in
  `Monitors/UsbStorageDriverlessPoll.cs`. Root cause: `UsbMonitor.OnDeviceChanged` - and therefore
  every USB event this process emits - is built entirely on `DBT_DEVICEARRIVAL`, which Windows only
  sends when a driver actually registers a device interface. A composite mass-storage device still
  gets one (`usbccgp.sys` binds the composite parent regardless of what happens to the storage
  child), but a plain, single-function USB flash drive has USBSTOR.sys as the *only* candidate
  driver for its entire devnode - with the kill switch on, no driver ever binds, so no interface is
  ever registered and the device is completely invisible to this process, not just missing one
  event. The fix is a separate, additive poll (`System.Threading.Timer`, never the message-pump
  thread) that enumerates by USB BUS ENUMERATOR -
  `SetupDiGetClassDevsByEnumerator(Enumerator: "USB", DIGCF_PRESENT | DIGCF_ALLCLASSES)` - rather
  than by Setup Class. The original draft proposed enumerating `GUID_DEVCLASS_USB` membership, but
  whether a truly driverless single-function devnode keeps ANY Setup Class assignment at all (versus
  falling into an unclassified "Other devices" bucket a class-based enumeration would never see) was
  unverifiable with the hardware available during design - enumerating by bus enumerator instead
  sidesteps the question entirely, since it only requires the USB bus driver to have enumerated the
  devnode at all, true the instant a device is physically present, independent of Setup Class,
  interface registration, or any bound function driver. Each candidate devnode's Compatible IDs are
  then run through the existing, already-unit-tested `UsbActions.IsMassStorageDevice`. The poll's
  lifecycle is tied directly to the kill switch (`Start()`/`Stop()`, called from the
  `usb_disable_storage`/`usb_enable_storage` command handlers and from boot if the switch is already
  on), not free-running. **The very first cycle after every `Start()` silently baselines the seen
  set instead of emitting** - every instance ID present on that first cycle is recorded as
  already-seen with nothing reported as new; only the second cycle onward diffs against the previous
  snapshot and emits `usb_storage_blocked` for genuinely new appearances. Without this, every timer
  (re)start - a service restart with the switch already on, or a live `usb_disable_storage` call -
  would treat everything already connected as "new" and fire a misleading "several new blocks just
  happened" burst for devices that had not actually changed state at all; an earlier draft of this
  design accepted that burst as harmless (like `EnumerateExisting`'s own startup re-announcement),
  but revisited it as actively misleading for what `usb_storage_blocked` is supposed to mean (a
  genuine transition, not "here's what's connected right now"). The poll shares its dedup seen-set
  with `UsbMonitor.HandleArrival`'s own inline `usb_storage_blocked` check via
  `TryClaimNewArrival(instanceId)` - both paths write into the same lock-guarded set before
  emitting, so a composite device's storage child interface that both paths could independently
  notice is only ever reported once, whichever path claims the instance ID first.

---

## 11. Where to look for the truth

| To answer... | Open this file |
|---|---|
| What commands exist and their arg shapes | `Commands/Commands.cs` |
| What events exist and their payload shapes | `Core/EventEmitter.cs` |
| What `Reason` means on a `*_blocked`/`*_block_failed` event, and how it differs from `Error` | `Core/EventEmitter.cs` (the 5 domains' Blocked/BlockFailed records), `Monitors/UsbMonitor.cs` (`BlockDevice`'s group-reason re-derivation), `Monitors/DisplayMonitor.cs` (`BlockNonCompliant`'s per-monitor reason), section 10 |
| The full enum vocabulary (events, commands, device kinds) | `Core/Enums.cs` |
| How a stdin line becomes a handler call | `Core/CommandDispatcher.cs` |
| How whitelist/blacklist enable/disable/mutate interact | `Handlers/Windows/WindowsUsbProtectionHandler.cs` |
| How a device actually gets blocked/unblocked | `Monitors/UsbMonitor.cs` (`BlockDevice`, `RestoreCompliant`), `Actions/UsbActions.cs` |
| How a composite device's siblings are judged together | `Monitors/UsbMonitor.cs` (`IsGroupCompliant`, `IsRecordCompliant`), `Actions/UsbActions.cs` (`EnumerateGroupSiblings`) |
| How Bluetooth devices are matched/blocked/restored | `Monitors/BluetoothMonitor.cs`, `Actions/BluetoothActions.cs` (`RemovePairing`) |
| Why a BLE unpair tries multiple candidate addresses, why it can still fail, and the disable fallback | `Actions/UsbActions.cs` (`GetBluetoothPairingCandidates`), `Actions/BluetoothActions.cs` (`RemovePairingAny`, `TryCandidatesInOrder`), `Monitors/UsbMonitor.cs` (`ResolveBluetoothBlock`), `ai_agent_doc/PROJECT.md` section 5.5 |
| What `SourceEventId` means for clipboard vs. USB, and why they're different lifetimes under one field name | `Core/EventEmitter.cs` (`ClipboardContentBlockedEvent`, `UsbDeviceConnectedEvent` etc.), `Monitors/UsbMonitor.cs` (`ResolveGroupAnchor`, `ResolveGroupAnchorCore`, `ReleaseGroupAnchorIfLastSibling`), section 10 |
| The companion-relay double-dispose fix (`leaveOpen: true`) | `Core/DisplayCompanionRelay.cs`, `Core/BluetoothCompanionRelay.cs`, `DlpEndpointMonitor.Tests/CompanionRelayPipeTests.cs` |
| How external displays are disabled/restored | `Monitors/DisplayMonitor.cs`, `Actions/DisplayActions.cs` |
| Why a headless primary missed pure projection-mode switches, and the relay that fixes it | `Core/DisplayChangeRelay.cs`, `Monitors/DisplayMonitor.cs` (`NotifyExternalDisplayChange`), AGENTS.md section 10 |
| What Win+P mode is currently active, and the event reporting it | `Actions/DisplayActions.cs` (`GetCurrentTopology`, `MapTopologyId`), `Monitors/DisplayMonitor.cs` (`EmitProjectionChanged`), `monitor_projection_changed` in PROJECT.md section 2.2 |
| How network adapters are matched/blocked/restored, and how the built-in NIC is protected | `Monitors/NetworkMonitor.cs`, `Actions/UsbActions.cs` (`IsBuiltIn`) |
| How the USB storage kill switch's new visibility event works | `Actions/UsbActions.cs` (`IsMassStorageDevice`), `ai_agent_doc/PROJECT.md` section 5.7 |
| Windows interface GUID -> DeviceKind mapping | `Core/UsbKind.cs` |
| The Win32 message pump / STA requirement | `Core/MessageWindow.cs`, `Program.cs` |
| Every P/Invoke signature and struct | `Win32/NativeMethods.cs` |
| The `--schema` JSON-Schema export | `Core/SchemaExporter.cs` |
| How a UI alert reaches the interactive session, and why | `Actions/AlertActions.cs`, `DlpEndpointMonitor.AlertHost/App.xaml.cs`, `ai_agent_doc/PROJECT.md` section 11 |
| How the primary reacts to a live Windows session change (Fast User Switching, logout+different-user-login) | `Core/MessageWindow.cs` (`SessionChanged`, `WM_WTSSESSION_CHANGE`), `Program.cs` (`EnsureCompanionForActiveSession`, `OnSessionChanged`), section 10 |
| How screenshot-shortcut blocking works and its scope limits | `Monitors/KeyboardHook.cs`, `ai_agent_doc/PROJECT.md` section 5.11 |
| Deep design, protocol tables, principles, roadmap | `ai_agent_doc/PROJECT.md` |
| The validation gate to run before a commit | AGENTS.md section 8.1 |
| What's unit-testable today vs. hardware-only, and the full test case list | `ai_agent_doc/TEST-PLAN.md` |
