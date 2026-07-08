# Test plan for the device-blocking engine

**Status: section 2 is implemented.** `DlpEndpointMonitor.Tests/` (xUnit) covers every case
in section 2 below - 78 tests, all passing (`dotnet test DlpEndpointMonitor.Tests/DlpEndpointMonitor.Tests.csproj`).
The storage-directory seam mentioned in section 2.5.1 was added (an optional constructor
parameter on `UsbDeviceList`/`DeviceWhitelist`/`DeviceBlacklist`/`DisabledDevices`, defaulting
to the exact prior `%ProgramData%\DlpEndpointMonitor\` behavior - see `ai_agent_doc/PROJECT.md`
section 6), so 2.5 is
fully covered too, not skipped. Sections 3 and 4 remain exactly as originally written - still
manual/hardware-only, still not attempted. This file is kept as the living reference for what
each layer covers and why; section 2's case tables now double as a map from test file to case
id (each test method/case in `DlpEndpointMonitor.Tests/` traces back to a `T-*` id here).

## 1. Is it possible? - feasibility by layer

The honest answer was **partially, without any production code changes; fully, only with a
small seam** - and that is what happened: section 2 below is now fully automated (including
2.5, via the seam), section 3 (hardware-only) and section 4 (the bigger `Monitors/*` seam)
remain manual/not implemented. Splitting by how much the Win32 layer gets in the way:

| Layer | Unit-testable **today**, zero production changes? | Why |
|---|---|---|
| `Core/UsbKind.cs` (`DeviceKindResolver`) | **Yes** | Pure dictionary lookups (GUID -> class -> kind). No Win32, no I/O. |
| `Actions/UsbActions.cs` parsing (`ParseDevicePath`, `ParsePartialDevice`, `ToInstanceId`) | **Yes** | Pure regex/string logic over a path you can hand it directly. |
| `Actions/BluetoothActions.cs` parsing (`ParseMacFromPath`, `ParseKindFromPath`, `FormatAddress`, `GetKindFromCoD`, `FormatHexMac`/`ParseMacToUllLong`) | **Yes** | Pure string/bit manipulation, no live radio needed. |
| `Actions/DisplayActions.cs` (`ParseMonitorPath`) | **Yes** | Pure regex over a path string. |
| `Core/UsbDeviceList.cs` / `UsbWhitelist.cs` / `UsbBlacklist.cs` matching + dedup (`MatchesAnyUsb`, `MatchesAnyBt`, `SameDevice`, `Add`/`Remove`/`Set`) | **Yes** (seam added - see 2.5.1) | Pure in-memory logic; the storage directory is now an optional constructor parameter (defaults to `%ProgramData%\DlpEndpointMonitor\`), so a test can point at a throwaway temp directory instead of the real files. |
| `Core/CommandDispatcher.cs` dispatch/error handling | **Yes** | Already takes `IClipboardHandler`/`IUsbStorageHandler`/`IUsbDeviceHandler`/`IUsbProtectionHandler`/`IControlHandler` as constructor parameters - a test can hand it fakes with zero production changes. |
| `Core/EventEmitter.cs` (`Emit`, `EmitError`, `EmitInfo`) | **Yes** | Writes to `Console.Out`, which `Console.SetOut(...)` can redirect in a test - standard .NET technique, no production change. |
| `Core/SchemaExporter.cs` (`--schema` output) | **Yes** | Pure reflection + JSON generation over a `TextWriter` you supply. |
| `Actions/UsbActions.cs` Win32-calling methods (`DisableDevice`, `EnableDevice`, `GetGroupId`, `IsRemovable`, `HasBluetoothAncestor`, `GetBluetoothDeviceNode`, `RequestEject`, `EjectDrive`, `IsProtectedInternal`, `EnumerateConnected`, `EnumerateGroupSiblings`, `SetUsbStorageEnabled`/`IsUsbStorageEnabled`) | **No** | Real `CM_*`/SetupAPI/registry calls against the live device tree. No interface, no fake-able seam. |
| `Actions/BluetoothActions.cs` Win32-calling methods (`RemovePairing`, `EnumerateConnected`) | **No** | Real Bluetooth API, needs a live radio + paired devices. |
| `Actions/DisplayActions.cs` Win32-calling methods (`DisableExternalDisplays`, `EnableExternalDisplays`, `EnumerateConnected`) | **No** | Real CCD API, needs a live display topology to observe/change. |
| `Actions/ClipboardActions.cs` | **Partially** | Real Win32 clipboard API, but the clipboard is software-only - no special hardware, works in any interactive Windows session (not typically in a headless CI agent without a desktop session, though). |
| **`Monitors/UsbMonitor.cs`, `BluetoothMonitor.cs`, `DisplayMonitor.cs`** - `BlockDevice`, `IsGroupCompliant`, `IsRecordCompliant`, `BlockNonCompliant`, `RestoreCompliant`, `HandleArrival` | **No, as currently structured** | These call `UsbActions`/`BluetoothActions`/`DisplayActions` as `static` methods directly - there is no interface/injection point to substitute a fake. **This is exactly the logic the recent whitelist/blacklist fix changed** - the group-compliance decision, the escalation ladder, the restore-recovery logic. It cannot be unit-tested without either real hardware or a source change (see section 4). |
| `Handlers/Windows/*` | **No, same reason** | Thin wrappers calling straight into `Actions/*` statically. |

**Bottom line:** every *pure decision/parsing* rule is testable right now with an ordinary
xUnit/NUnit project and zero production edits. The actual *enforcement* logic - the part
that decides whether to call `CM_Disable_DevNode`, and the part we just changed - is not,
because `Monitors/*` reaches directly into static `Actions/*` calls. Testing that part today
means running it on a real Windows machine with real devices (the manual verification list
from the implementation plan). Section 4 sketches what a minimal seam would look like if you
want that automated later - not proposed for now, since you asked for no code.

---

## 2. Test cases - things testable today (pure logic, no hardware, no seam needed)

### 2.1 `DeviceKindResolver` (`Core/UsbKind.cs`)

| # | Case | Expected |
|---|---|---|
| T-KIND-01 | `Resolve` with a known non-override GUID (e.g. printer `{28D78FAD-...}`) | Returns `usbClass=0x07`, `kind=Printer` |
| T-KIND-02 | `Resolve` with a GUID in `_guidKindOverride` (e.g. keyboard `{884b96c3-...}`) | Returns `usbClass=0x03` (from the class table) but `kind=Keyboard` (override wins) |
| T-KIND-03 | `Resolve` with an entirely unknown GUID | `usbClass=null`, `kind=Unknown` |
| T-KIND-04 | `Resolve` with `classGuid=null` | `usbClass=null`, `kind=Unknown` (no exception) |
| T-KIND-05 | `Resolve` is case-insensitive on the GUID string | Same result for `{884B96C3-...}` and `{884b96c3-...}` |
| T-KIND-06 | `KSCATEGORY_CAPTURE` GUID is absent from every table | `Resolve` on it returns `Unknown`, confirming the deliberate non-mapping from the bug history (section 3 of PROJECT.md) never regresses |
| T-KIND-07 | `KnownInterfaceGuids` contains no duplicates and every entry parses as a valid `Guid` | Regression guard against a malformed table entry silently breaking `SetupDiGetClassDevs` |

### 2.2 USB path parsing (`Actions/UsbActions.cs`)

| # | Case | Expected |
|---|---|---|
| T-USB-01 | `ParseDevicePath` on a standard `VID_046D&PID_C52B` path with a positional serial (`7&3A4B1C2D&0&1`) | `Vid="046D"`, `Pid="C52B"`, `Serial=null` (positional IDs containing `&` are excluded) |
| T-USB-02 | `ParseDevicePath` on a path with a real serial (no `&`), e.g. `...\ABCDEF123456` | `Serial="ABCDEF123456"` |
| T-USB-03 | `ParseDevicePath` on a Bluetooth-HID-style path (`VID&0002046D_PID&B020`) | Matches the `_btVidPid` fallback regex, `Vid="046D"`, `Pid="B020"` |
| T-USB-04 | `ParseDevicePath` on a path with neither pattern | Returns `null` |
| T-USB-05 | `ToInstanceId` on a path with a `#{guid}\reference` tail (the historical bug) | Strips everything from the GUID onward, including the `\reference` tail - never leaves a dangling segment |
| T-USB-06 | `ParsePartialDevice` on a non-empty working path, any `kind` including `Unknown` | Returns a non-null `ParsedDevice` with `Vid=Pid=""` - **regression guard for the fail-closed fix**: must never go back to returning `null` for `Unknown` |
| T-USB-07 | `ParsePartialDevice` on a working path that reduces to empty string | Returns `null` (the one remaining legitimate null case) |
| T-USB-08 | Case sensitivity: `VID_046d&PID_c52b` (lowercase hex) | Parses identically to uppercase, values normalized to uppercase |

### 2.3 Bluetooth parsing/decoding (`Actions/BluetoothActions.cs`)

| # | Case | Expected |
|---|---|---|
| T-BT-01 | `ParseMacFromPath` on a well-formed BTHENUM path | Returns `"AA:BB:CC:DD:EE:FF"` in canonical colon-separated uppercase form |
| T-BT-02 | `ParseMacFromPath` on a path with no MAC pattern | Returns `null` |
| T-BT-03 | `FormatAddress`/`ParseMacToUllLong` round-trip for several MACs (including all-zero and all-`FF`) | `ParseMacToUllLong(FormatAddress(x)) == x` for every case |
| T-BT-04 | `GetKindFromCoD` with major class `0x05` (peripheral), minor `01` (keyboard) | Returns `Keyboard` |
| T-BT-05 | `GetKindFromCoD` with major `0x05`, minor `02` (pointing device) | Returns `Mouse` |
| T-BT-06 | `GetKindFromCoD` with major `0x05`, minor `03` (combo keyboard+pointing) | Returns `Mouse` (documented deliberate choice - combo devices treat as mouse for blocking) |
| T-BT-07 | `GetKindFromCoD` with major `0x05`, an unspecified minor | Returns `Hid` |
| T-BT-08 | `GetKindFromCoD` with major `0x04` (audio/video), `0x06` (imaging), `0x03` (network) | `Audio`, `Camera`, `Network` respectively |
| T-BT-09 | `GetKindFromCoD` with an unrecognized major class | Returns `Unknown` |
| T-BT-10 | `ParseKindFromPath` on a path whose interface GUID suffix is a known GUID | Resolves through `DeviceKindResolver.Resolve` correctly |
| T-BT-11 | `ParseMacFromPath` on a BLE top-level peripheral's raw instance ID (`BTHLE\DEV_<mac>\...`) | Returns the canonical MAC - verified live against real hardware (PROJECT.md section 5.5) |
| T-BT-12 | `ParseMacFromPath` on the same BLE peripheral as a live device-interface PATH (`#`-separated, not `\`) | Returns the same canonical MAC - confirms tolerance for both separator forms |
| T-BT-13 (regression guard) | `ParseMacFromPath` on a `BTHLEDEVICE\` GATT-service-child path (NOT the true top-level peripheral) | Returns `null` - must never be mistaken for the peripheral's own node (see PROJECT.md section 5.5's verified 3-level BLE hierarchy) |

**Added, not automatable** (Win32-calling, per section 1's feasibility table):
`BluetoothActions.FindInstanceIdByMac` (walks the BTHENUM/BTHLE PnP tree via SetupAPI) and
`UsbActions.GetBluetoothDeviceNode`'s BLE-depth fix - both need real paired hardware. See the
manual matrix (section 3) for the corresponding cases.

### 2.4 Display path parsing (`Actions/DisplayActions.cs`)

| # | Case | Expected |
|---|---|---|
| T-DISP-01 | `ParseMonitorPath` on a well-formed `DISPLAY#SAM0F91#...#{guid}` path | `Vid="SAM"`, `Pid="0F91"`, `Kind=Monitor` |
| T-DISP-02 | `ParseMonitorPath` on a path that does **not** match the EDID pattern | **Regression guard for the fail-closed fix**: returns a non-null `ParsedDevice` with `Vid=Pid=""`, not `null` |
| T-DISP-03 | `ParseMonitorPath` on a path that reduces to an empty working string after GUID-suffix stripping | Returns `null` (the one remaining legitimate null case) |
| T-DISP-04 | `ParseMonitorPath` vid/pid normalization is case-insensitive -> uppercase | Lowercase EDID codes in the path still produce uppercase `Vid`/`Pid` |

### 2.5 Whitelist/blacklist matching and dedup (`Core/UsbDeviceList.cs`) - *caveat: see 2.5.1*

| # | Case | Expected |
|---|---|---|
| T-LIST-01 | Whitelist disabled, any device | `IsAllowed` returns `true` unconditionally (fail-open when off) |
| T-LIST-02 | Blacklist disabled, any device | `IsBlocked` returns `false` unconditionally |
| T-LIST-03 | Whitelist enabled, empty entry list | `IsAllowed` returns `false` for everything (deny-all, empty enabled list) |
| T-LIST-04 | Whitelist enabled, one kind-only entry (`{kind: keyboard}`), USB device of that kind | `IsAllowed` -> `true` |
| T-LIST-05 | Same whitelist, USB device of a different kind | `IsAllowed` -> `false` |
| T-LIST-06 | Whitelist entry with only `Vid`/`Pid` set (no kind) | Matches any device with that vid/pid regardless of kind |
| T-LIST-07 | Whitelist entry with `Mac` set, evaluated via the USB overload (`MatchesAnyUsb`) | Never matches - an entry with a `Mac` is BT-only |
| T-LIST-08 | Whitelist entry with `Vid`/`Pid`/`Serial` set, evaluated via the BT overload (`MatchesAnyBt`) | Never matches - an entry with vid/pid/serial is USB-only |
| T-LIST-09 | Case-insensitivity of every field in both match predicates | `"046d"` matches an entry stored as `"046D"` |
| T-LIST-10 | `Serial=null` on the entry (wildcard) vs. a specific serial on the connected device | Matches (serial wildcard) |
| T-LIST-11 | `SameDevice` dedup: `Add` the same vid/pid/serial/mac/kind twice, different `Label` | Second `Add` is a no-op - only one entry persists (label is cosmetic, ignored for identity) |
| T-LIST-12 | `SameDevice` dedup: `Add` two entries differing only by `Kind` | Both persist - `Kind` is part of identity |
| T-LIST-13 | `Set` with a duplicate-containing input array | Result list has duplicates collapsed to one, per the same `SameDevice` rule |
| T-LIST-14 | `Remove` with a partial filter (only `Kind` specified) | Removes every entry of that kind, regardless of other fields |
| T-LIST-15 | `Clear` then `GetAll` | Empty list |

#### 2.5.1 Resolved: the storage-path seam

`UsbDeviceList`'s constructor now accepts an optional `storageDir` parameter (defaulting to
`Environment.GetFolderPath(CommonApplicationData) + "\DlpEndpointMonitor"`, identical to the
prior hardcoded behavior when omitted - see `ai_agent_doc/PROJECT.md` section 6), threaded
through `DeviceWhitelist`/`DeviceBlacklist`, plus the equivalent parameter on
`DisabledDevices`. Every test in `DlpEndpointMonitor.Tests/UsbDeviceListTests.cs` constructs
its own throwaway `Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))` directory,
deleted in a `finally` block, and never uses the default (no-argument) constructor - so these
tests never touch the real storage location. Every production call site (`Program.cs`) still
uses the parameterless form and is unaffected.

T-LIST-01..15 below are now implemented as automated tests, with no risk of production-file
corruption on whatever machine runs them.

### 2.6 Command dispatch (`Core/CommandDispatcher.cs`) - testable via fake handlers

| # | Case | Expected |
|---|---|---|
| T-CMD-01 | Well-formed line for every `CommandType` | Dispatches to the correct fake handler's `Handle` overload, exactly once |
| T-CMD-02 | Malformed JSON (not valid JSON at all) | Emits `reply {ok:false}` with the exception message, does not throw out of `Dispatch` |
| T-CMD-03 | Valid JSON, missing `cmd` field | Throws inside `Dispatch`'s own try/catch (`GetProperty` throws) -> caught -> `reply {ok:false}` |
| T-CMD-04 | Valid JSON, unrecognized `cmd` string | `commandType` deserializes to `null` -> `reply {ok:false, error:"unknown command: <cmd>"}`, echoing the unrecognized string and the request `id` |
| T-CMD-05 | Valid `cmd`, but the command's own required field is missing (e.g. `usb_eject` without `drive`) | Deserialization throws -> caught -> `reply {ok:false}` with `id` still echoed (parsed before the throw) |
| T-CMD-06 | `id` omitted entirely (it's optional on most commands) | Handler receives `Id=null`; downstream replies carry `Id=null`, no crash |
| T-CMD-07 | Two lines with the same `id` in a row | Each dispatches independently - `CommandDispatcher` does not deduplicate by id (confirm this is actually the intended behavior, not an oversight) |
| T-CMD-08 | `RunAsync` when `Console.ReadLine` returns `null` (stdin closed) | Emits `info "stdin closed - monitoring continues"`, then blocks on `Task.Delay(Infinite)` rather than exiting the loop with an error |
| T-CMD-09 | `RunAsync` when the `CancellationToken` is already cancelled before the first read | Loop exits immediately, no dispatch attempted |

### 2.7 Event emission (`Core/EventEmitter.cs`) - testable via `Console.SetOut`

| # | Case | Expected |
|---|---|---|
| T-EVT-01 | `Emit` on a registered event type | Exactly one line written to stdout, valid JSON, ends with the process's line terminator, buffer flushed |
| T-EVT-02 | `Emit` on a type NOT registered in `AppJsonContext` (simulate by defining a throwaway `IEvent` record in the test and NOT adding it to the context) | Falls back to an `ErrorEvent("event_emit", "Type <X> is not registered...")` - never throws, never silently drops the line |
| T-EVT-03 | Concurrent `Emit` calls from multiple threads (simulate with `Task.WhenAll` of many `EmitInfo` calls) | No interleaved/malformed JSON lines - every line is independently parseable (the `lock` around `Console.WriteLine`+`Flush` holds) |
| T-EVT-04 | `EmitError`/`EmitInfo` wire shape | `type="error"`/`type="info"`, `ts` is a plausible Unix-seconds value close to "now" |

### 2.8 Schema export (`Core/SchemaExporter.cs`)

| # | Case | Expected |
|---|---|---|
| T-SCHEMA-01 | `Export` output is valid JSON | Parses without error |
| T-SCHEMA-02 | Every `CommandType` member has a corresponding `$defs` entry reachable from some `ICommand` type | No command discriminant is undocumented |
| T-SCHEMA-03 | Every `EventType` member has a corresponding `$defs` entry reachable from some `IEvent` type | No event discriminant is undocumented |
| T-SCHEMA-04 | `cmdReply` contains an entry for every command carrying `[EmitsEvent]`, and none for commands without it | Matches `Commands/Commands.cs` attribute usage exactly - a regression guard if someone adds `[EmitsEvent]` without updating callers, or vice versa |
| T-SCHEMA-05 | Every discriminated record's schema has its discriminant field marked `required` with the right `const` value | Confirms `TransformSchemaNode` logic (this is what a consumer's type generator would rely on) |

---

## 3. Test cases - the enforcement/decision logic (needs real hardware, or a seam - section 4)

This is the bulk of "all cases and edge cases" for the recent whitelist/blacklist fix. Every
row here is currently only verifiable by hand on a real Windows machine with real devices
(no unit test can exercise it without a seam per section 1's table). Organized as a manual
test script - what to set up, what to trigger, what to check in the JSON event stream.

### 3.1 Composite-device group compliance (the keyboard bug)

| # | Setup | Action | Expected |
|---|---|---|---|
| M-GRP-01 | Whitelist enabled, one entry `{kind: keyboard}` only | Plug in a composite USB keyboard (one that exposes both a `Keyboard`-kind and a `Hid`-kind interface, confirmable via Device Manager showing 2+ entries) | Every interface of the physical keyboard stays enabled; an `info` `usb_group_allowed` line appears for the non-`Keyboard`-kind sibling; **no** `usb_device_blocked` for any interface of this device |
| M-GRP-02 | Same whitelist as above, plus a plain single-interface USB mouse | Plug in the mouse | Mouse gets blocked (does not match `{kind: keyboard}`) - confirms the fix didn't over-correct into "never block anything" |
| M-GRP-03 | Blacklist enabled, entry is the composite keyboard's exact `Vid`/`Pid` (matches only the `Hid` sibling's identity, or any single interface) | Plug in the keyboard | The whole physical keyboard is blocked (group-blocked overrides group-allowed - blacklist match on ANY sibling blocks the WHOLE group) |
| M-GRP-04 | Whitelist enabled `{kind: keyboard}` AND blacklist enabled with the same keyboard's specific `Vid`/`Pid` | Plug in the keyboard | Blocked - `groupBlocked` wins over `groupAllowed` in `IsGroupCompliant`'s combination (`allowed && !blocked`) |
| M-GRP-05 | Whitelist enabled with an entry that matches NEITHER sibling kind | Plug in the composite keyboard | Both interfaces attempt to block; whichever interface's own `CM_Disable_DevNode` fails escalates to the composite parent, taking down the whole device (this is now *intended* behavior when genuinely nothing allows it - confirm it still happens, unlike M-GRP-01) |
| M-GRP-06 (recovery) | Force the pre-fix collateral-block state (or use a build predating the fix), then disable whitelist entirely | Observe `RestoreCompliant` | Composite parent gets `CM_Enable_DevNode`'d unconditionally; keyboard's interfaces re-arrive; ends up fully functional (no stuck-forever state) |
| M-GRP-07 (leaf-only restore) | A device where only ONE leaf interface got disabled (siblings still live/enumerable) | Loosen policy so that sibling now matches | `IsRecordCompliant` derives compliance from the LIVE siblings (not the stale record), re-enables correctly |
| M-GRP-08 | Bluetooth-backed mouse/keyboard, any whitelist/blacklist state | Plug/pair it | Confirm it is **not** accidentally pulled into USB group-compliance bucketing (the `!HasBluetoothAncestor` exclusion in `BlockNonCompliant`'s grouping and in `BlockDevice`'s own check) - i.e. no cross-contamination between an unrelated USB device sharing the same radio's USB instance and the BT peripheral |

### 3.2 Fail-closed unclassifiable devices

| # | Setup | Action | Expected |
|---|---|---|---|
| M-UNK-01 | Whitelist enabled, only a keyboard entry | Plug a device whose interface exposes an unrecognized class GUID (or force `Kind.Unknown` via a debug build), with a path that DOES still contain a parseable `VID_xxxx&PID_xxxx` | Device is blocked (fail-closed, not silently connected) |
| M-UNK-02 | Same whitelist | Plug a device whose path does NOT contain a parseable VID/PID at all | Still reaches `HandleArrival` via `ParsePartialDevice`'s new non-null return, gets blocked |
| M-UNK-03 | Blacklist enabled (no whitelist), same unrecognized device | Plug it in | **Not** blocked, unless an explicit wildcard/`{kind: unknown}` blacklist entry exists - confirms fail-closed is whitelist-only, blacklist's default-allow is unaffected |
| M-UNK-04 | Whitelist enabled, only a keyboard entry | Plug in an HDMI monitor whose EDID path fails the `DISPLAY#XXX0000` regex (or one that legitimately doesn't - some adapters/dongles are known to expose unusual paths) | External display gets blocked (via `BlockNonCompliant`'s sweep now seeing it, since `ParseMonitorPath`'s `""`-sentinel fallback keeps it in `EnumerateConnected()`) |
| M-UNK-05 (safety net) | An internal, essential device that resolves to `Kind.Unknown` (whatever hardware on the test machine happens to expose only an unrecognized interface GUID and has no removable USB/BT ancestor) | Whitelist enabled, this device not listed | **Not** blocked - `UsbDeviceBlockFailedEvent` "protected internal" reason, confirming the `IsProtectedInternal` extension covers `Unknown` the same as `StrictInputKinds` |
| M-UNK-06 (residual gap, expected to still fail) | A device whose interface exposes ONLY a class GUID entirely outside `DeviceKindResolver.KnownInterfaceGuids` | Whitelist enabled | Still connects unblocked - this is the **documented, accepted residual gap** (PROJECT.md 5.8) - confirm current behavior matches the doc, not a false expectation of full coverage |

### 3.3 Regression checks - existing behavior that must not have changed

| # | Setup | Action | Expected |
|---|---|---|---|
| M-REG-01 | Built-in laptop keyboard/touchpad, whitelist enabled with unrelated entries | (No physical action possible - policy alone) | Never blocked, regardless of policy - `IsProtectedInternal`'s `StrictInputKinds` path |
| M-REG-02 | Built-in webcam, whitelist enabled without a camera entry | N/A | Blocked as normal - camera/video are NOT protected kinds |
| M-REG-03 | Bluetooth-backed HID device that needs blocking | Blacklist it | Disabled at the `BTHENUM\` (classic) or `BTHLE\` (BLE - NOT `BTHLEDEVICE\`, one level too shallow) peripheral node, not the HID leaf, not the shared radio - other BT devices on the same radio stay unaffected |
| M-REG-04 | A USB HID device that Windows vetoes at both the leaf and group-disable level | Blacklist it | Falls through to `CM_Request_Device_EjectW`; confirm the eject actually disconnects it and that no `CM_Enable` is attempted afterward (irreversible by design) |
| M-REG-05 | External monitor connected, whitelist blocks it, unplug and replug rapidly (regression for the `WM_DISPLAYCHANGE` debounce fix) | Rapid connect/disconnect cycling | No crash (`ObjectDisposedException`), the debounce settles and correctly reflects final state |
| M-REG-06 | Whitelist AND blacklist both somehow enabled on disk (direct file edit) | Start the process | Both force-disabled at startup, `startup_conflict` error event emitted |
| M-REG-07 | Whitelist enabled with entries, then `device_whitelist_clear` | Observe | List disabled (not just emptied) - "factory reset" semantics - and all previously-blocked devices get restored |
| M-REG-08 | `usb_disable_storage`/`usb_enable_storage`/`usb_storage_status` | Toggle each | `HKLM\...\USBSTOR!Start` flips between `4`/`3` correctly - **this one IS runnable in CI on any Windows agent**, no special hardware needed (plain registry read/write) |

### 3.4 Bluetooth reversible blocking + Display restore (this session's fix)

| # | Setup | Action | Expected |
|---|---|---|---|
| M-BT-01 (BLE, verified live this session) | A BLE-paired mouse (e.g. an MX-series Logitech mouse) | Blacklist its MAC or kind | Windows still shows it as **paired** (Settings > Bluetooth), just non-functional - confirms disable, not unpair. `bluetooth_device_blocked` event, no `bluetooth_device_disconnected` unpair side effect |
| M-BT-02 (BLE restore) | Continuing from M-BT-01 | Clear the blacklist entry | Device reconnects and works again **without re-pairing** - `bluetooth_device_unblocked` event fires |
| M-BT-03 (classic BT - not yet live-verified, see section 1) | A classic-Bluetooth-paired device (not BLE) | Blacklist it, then clear | Same disable/restore behavior as M-BT-01/02 - **this is the one path in this fix that hasn't been confirmed against real hardware yet** |
| M-BT-04 (fallback) | Force `FindInstanceIdByMac` to fail (e.g. temporarily point its enumerator search at a wrong branch name) for a device that needs blocking | Blacklist it | Falls back to the old unpair - device still gets blocked (via `BluetoothDeviceBlockedEvent`), just irreversibly for that one device; confirms blocking never silently no-ops |
| M-BT-05 (live BLE reconnect) | A BLE device already blacklisted, currently out of range/powered off | Bring it back into range/power it on | `BluetoothMonitor.OnDeviceChanged` recognizes the `BTHLE` path immediately (not just on the next periodic sweep) and blocks it right away - regression guard for the live-arrival filter fix |
| M-DISP-01 | External monitor connected, whitelist blocks it | Disable whitelist | Event log shows either a genuine `monitor_policy_restore: external displays re-enabled` (and the monitor actually comes back) or a real error from `SetDisplayConfig` - never a false "re-enabled" claim when it silently failed (the confirmed, now-fixed bug) |
| M-DISP-02 (only if M-DISP-01's `SetDisplayConfig` genuinely fails) | Same as M-DISP-01 | Same | Confirms the deferred symmetric-reactivation redesign (section 5.6/PROJECT.md) is actually needed - not expected to pass yet, this case exists to decide if that follow-up work is warranted |

### 3.5 Network adapter blocking (`NetworkMonitor` / `UsbActions.IsBuiltIn` fix)

| # | Setup | Action | Expected |
|---|---|---|---|
| M-NET-01 (external USB dongle block) | A USB WiFi or USB Ethernet dongle plugged in | Blacklist it by kind=network (or its VID/PID) | `network_device_blocked` event fires, the dongle's devnode is disabled (Device Manager shows it disabled), and the machine's other network path (if any) stays up |
| M-NET-02 (external USB dongle restore) | Continuing from M-NET-01 | Clear the blacklist entry (or remove the rule) | `network_device_unblocked` event fires and the dongle re-enables and reconnects, same restore semantics as M-BT-02 |
| M-NET-03 (built-in adapter never blocked - the regression this fix exists for) | Blacklist `kind: network` broadly (no VID/PID scoping), on a machine whose only adapter is a built-in WiFi/Ethernet (PCIe or soldered, not USB) | Observe | Built-in adapter stays enabled and connected throughout - `network_device_block_failed` event fires with `error: "protected internal network adapter - refused to block"` instead of a disable ever being attempted; this is the confirmed, now-fixed collateral-WiFi-disable bug ("previously it disable the wifi driver, the network wifi adapter") |
| M-NET-04 (Bluetooth-tethered/PAN network interface, if available) | A Bluetooth PAN (Bluetooth-tethering) network interface is present | Blacklist `kind: network` | `NetworkMonitor` still applies (interface resolves to `DeviceKind.Network`), but `UsbActions.IsBuiltIn` treats it as external via the Bluetooth-ancestor check same as M-NET-01, not confused with the internal-adapter path |
| M-NET-05 (no duplicate Bluetooth restore events) | A Bluetooth device blacklisted and then unblocked (same setup as M-BT-01/M-BT-02) | Clear the blacklist entry | Exactly **one** `bluetooth_device_unblocked` event fires for the device - not also a second `usb_device_unblocked` for the same devnode; confirms `UsbMonitor.RestoreCompliant`'s new `d.Mac is null && d.Kind != DeviceKind.Network` filter stops it from racing `BluetoothMonitor.RestoreCompliant` on the same shared `DisabledDevices` record |

---

## 4. If you later want automated coverage of section 3 - what a seam would look like

Not proposed for implementation now (no code this round) - just naming the shape of the
decision for later, since it came up directly from the feasibility analysis above.

The blocker is that `Monitors/UsbMonitor.cs` (and `BluetoothMonitor`/`DisplayMonitor`) call
`UsbActions`/`BluetoothActions`/`DisplayActions` as `static` methods - there's no substitution
point. Closing that gap would mean introducing an interface (e.g. `IUsbActions`) that the
static class implements or is wrapped by, injected into each `Monitor` constructor the same
way `CommandDispatcher` already takes `IClipboardHandler` etc. - then section 3's cases
(M-GRP-*, M-UNK-*, M-REG-*) become expressible as ordinary unit tests against a fake that
returns canned `ParsedDevice` lists and records which `(bool ok, string? error)` calls were
made, with no real Windows/hardware dependency. This is a real architectural change (new
interfaces, constructor signature changes throughout `Program.cs`'s wiring) - worth its own
discussion, not something to fold into "just add tests."

---

## 5. Suggested priority if you want to act on this incrementally

1. ~~Section 2's pure-logic cases (2.1-2.4, 2.6-2.8)~~ - **done**: `DlpEndpointMonitor.Tests/`,
   78 passing tests.
2. ~~Section 2.5's list-matching cases~~ - **done**: the storage-path seam (2.5.1) was added,
   so 2.5 is fully automated too, not skipped.
3. Section 3's manual matrix - still to run by hand, as the actual acceptance test for the
   whitelist/blacklist fix (this is not something the automated suite covers - see section 1's
   feasibility table).
4. Section 4's seam - still not attempted; only worth it if ongoing regression risk in the
   enforcement core justifies the refactor cost.
