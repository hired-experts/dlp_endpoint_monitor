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
    ClipboardOperationHint.cs correlates KeyboardHook's Ctrl+X detection with ClipboardMonitor's
                              next clipboard read, so a text cut reports "cut" instead of
                              ClipboardActions.TryReadText()'s hardcoded "copy" - see section 10
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
- **Bluetooth blocking tries unpair (`BluetoothActions.RemovePairing`) first, but ALWAYS falls
  back to disabling the devnode if unpair fails, for any reason.** Unpair traded away
  reversibility (an unpaired device can no longer be auto-restored by `RestoreCompliant` - see
  section 7's carve-out) for a correctness requirement: Windows Settings' Connected/Paired
  indicator reads the Bluetooth pairing/link-state store, not PnP devnode enabled/disabled
  state, so `CM_Disable_DevNode` alone stops HID input but never changes what Settings displays.
  But confirmed live: the legacy `BluetoothRemoveDevice` API can fail outright for some
  Bluetooth LE peripherals even given a correct, registered address (see the next bullet) - so
  leaving the device fully connected in that case would be a real enforcement gap, not an
  acceptable degraded mode. `BluetoothMonitor.BlockDevice` works directly from a MAC and calls
  `RemovePairing` once (no disable fallback exists on this path - it has no devnode to disable
  from, only a MAC). `UsbMonitor.BlockDevice`'s Bluetooth branch resolves one or more MAC
  **candidates** via `UsbActions.GetBluetoothPairingCandidates`, calls
  `BluetoothActions.RemovePairingAny`, and - if that fails - falls back to `DisableDevice` on
  the primary node via `UsbMonitor.ResolveBluetoothBlock` (pure decision logic, unit tested in
  `DlpEndpointMonitor.Tests/UsbMonitorTests.cs`). A device blocked via this fallback IS tracked
  in `DisabledDevices` and CAN be auto-restored - only a successful unpair loses that.
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
  in order - always safe, since a non-matching address is a harmless no-op. **Live testing
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
| How Bluetooth devices are matched/blocked/restored | `Monitors/BluetoothMonitor.cs`, `Actions/BluetoothActions.cs` (`RemovePairing`) |
| Why a BLE unpair tries multiple candidate addresses, why it can still fail, and the disable fallback | `Actions/UsbActions.cs` (`GetBluetoothPairingCandidates`), `Actions/BluetoothActions.cs` (`RemovePairingAny`, `TryCandidatesInOrder`), `Monitors/UsbMonitor.cs` (`ResolveBluetoothBlock`), `ai_agent_doc/PROJECT.md` section 5.5 |
| The companion-relay double-dispose fix (`leaveOpen: true`) | `Core/DisplayCompanionRelay.cs`, `Core/BluetoothCompanionRelay.cs`, `DlpEndpointMonitor.Tests/CompanionRelayPipeTests.cs` |
| How external displays are disabled/restored | `Monitors/DisplayMonitor.cs`, `Actions/DisplayActions.cs` |
| How network adapters are matched/blocked/restored, and how the built-in NIC is protected | `Monitors/NetworkMonitor.cs`, `Actions/UsbActions.cs` (`IsBuiltIn`) |
| Windows interface GUID -> DeviceKind mapping | `Core/UsbKind.cs` |
| The Win32 message pump / STA requirement | `Core/MessageWindow.cs`, `Program.cs` |
| Every P/Invoke signature and struct | `Win32/NativeMethods.cs` |
| The `--schema` JSON-Schema export | `Core/SchemaExporter.cs` |
| How a UI alert reaches the interactive session, and why | `Actions/AlertActions.cs`, `DlpEndpointMonitor.AlertHost/App.xaml.cs`, `ai_agent_doc/PROJECT.md` section 11 |
| Deep design, protocol tables, principles, roadmap | `ai_agent_doc/PROJECT.md` |
| The validation gate to run before a commit | AGENTS.md section 8.1 |
| What's unit-testable today vs. hardware-only, and the full test case list | `ai_agent_doc/TEST-PLAN.md` |
