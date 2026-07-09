# DlpEndpointMonitor

Native Windows monitor/enforcement point for a larger Data Loss Prevention (DLP) system.
A self-contained C#/.NET console binary that talks directly to Win32/SetupAPI/CfgMgr32/
Bluetooth APIs to watch and enforce policy on USB devices, Bluetooth devices, external
monitors, network adapters, and clipboard content.

## 1. Overview

This binary is one piece of a larger product. A sibling Node.js **agent** process (separate
repository) starts this binary as a child process, writes JSON commands to its stdin, reads
JSON events from its stdout, and relays both to a central hub/dashboard. The relationship is
strictly one-directional from this repo's perspective: this binary has **no network code, no
database**, and no knowledge that a dashboard exists — it only knows stdin/stdout JSON lines.

It is also the enforcement point, not just a reporter: when a device is not allowed under the
active whitelist/blacklist, this process disables it (or ejects it, or removes its Bluetooth
pairing, or turns off the external display) without waiting to be told.

### Projects

| Project | Purpose |
|---|---|
| `DlpEndpointMonitor` | The shipped binary — `net10.0`, trimmed, single-file, self-contained `win-x64`, zero NuGet dependencies |
| `DlpEndpointMonitor.Tests` | xUnit tests for the hardware-independent pure-logic layer |
| `DlpEndpointMonitor.AlertContracts` | Dependency-free plain records/enums shared between the main binary and AlertHost (wire format for UI alerts) |
| `DlpEndpointMonitor.AlertHost` | Companion WPF app (`net10.0-windows`) that shows Modal/Toast/FullScreen alert windows in the interactive user's session |
| `DlpEndpointMonitor.AlertHost.Tests` | xUnit tests for AlertHost's pure-logic pieces (`AlertQueue`, `RichTextParser`) |

## 2. Build & run

```bash
# Build (debug), both main projects
dotnet build DlpEndpointMonitor.slnx

# Publish the real artifact: trimmed, self-contained, single-file win-x64
dotnet publish DlpEndpointMonitor/DlpEndpointMonitor.csproj -c Release

# Dump the full command/event JSON Schema (consumed by the sibling Node.js agent's type generation)
dotnet run --project DlpEndpointMonitor -- --schema

# Run the pure-logic test suites
dotnet test DlpEndpointMonitor.Tests/DlpEndpointMonitor.Tests.csproj
dotnet test DlpEndpointMonitor.AlertHost.Tests/DlpEndpointMonitor.AlertHost.Tests.csproj
```

`--schema` is checked before anything else in `Program.cs` and exits before touching stdout —
it is the only reflection-based code path in the binary (`[RequiresUnreferencedCode]`), never
reachable in the normal trimmed runtime path.

## 3. Command protocol (stdin)

Every stdin line is one JSON object: `{"id": "<opaque>", "cmd": "<command_name>", ...args}`.
`id` is optional and echoed back in the reply. A command not annotated `[EmitsEvent]` replies
with a plain `ReplyEvent{id?, ok, error?}`.

`DeviceEntryDto(Vid?, Pid?, Serial?, Mac?, Kind?, Label?)` is the shared shape inside every
device `*_set` command's `entries` array. `ClipboardRuleDto(Pattern, Kind?, Label?)` is the
equivalent shape for clipboard `*_set` commands.

| `cmd` | Record | Key fields | Replies with |
|---|---|---|---|
| `clipboard_read` | `ClipboardReadCmd` | — | `ClipboardReadEvent` |
| `clipboard_set` | `ClipboardSetCmd` | `content` | `ReplyEvent` |
| `clipboard_clear` | `ClipboardClearCmd` | — | `ReplyEvent` |
| `clipboard_protection_status` | `ClipboardProtectionStatusCmd` | — | `ClipboardProtectionStatusEvent` |
| `clipboard_whitelist_enable` | `ClipboardWhitelistEnableCmd` | — | `ReplyEvent` |
| `clipboard_whitelist_disable` | `ClipboardWhitelistDisableCmd` | — | `ReplyEvent` |
| `clipboard_whitelist_get` | `ClipboardWhitelistGetCmd` | — | `ClipboardWhitelistGetEvent` |
| `clipboard_whitelist_clear` | `ClipboardWhitelistClearCmd` | — | `ReplyEvent` |
| `clipboard_whitelist_add` | `ClipboardWhitelistAddCmd` | `pattern, kind?, label?` | `ReplyEvent` |
| `clipboard_whitelist_remove` | `ClipboardWhitelistRemoveCmd` | `pattern, kind?` | `ReplyEvent` |
| `clipboard_whitelist_set` | `ClipboardWhitelistSetCmd` | `entries: ClipboardRuleDto[]` | `ReplyEvent` |
| `clipboard_blacklist_enable` | `ClipboardBlacklistEnableCmd` | — | `ReplyEvent` |
| `clipboard_blacklist_disable` | `ClipboardBlacklistDisableCmd` | — | `ReplyEvent` |
| `clipboard_blacklist_get` | `ClipboardBlacklistGetCmd` | — | `ClipboardBlacklistGetEvent` |
| `clipboard_blacklist_clear` | `ClipboardBlacklistClearCmd` | — | `ReplyEvent` |
| `clipboard_blacklist_add` | `ClipboardBlacklistAddCmd` | `pattern, kind?, label?` | `ReplyEvent` |
| `clipboard_blacklist_remove` | `ClipboardBlacklistRemoveCmd` | `pattern, kind?` | `ReplyEvent` |
| `clipboard_blacklist_set` | `ClipboardBlacklistSetCmd` | `entries: ClipboardRuleDto[]` | `ReplyEvent` |
| `usb_eject` | `UsbEjectCmd` | `drive` (e.g. `"E:\\"`) | `ReplyEvent` |
| `usb_disable_storage` | `UsbDisableStorageCmd` | — | `ReplyEvent` |
| `usb_enable_storage` | `UsbEnableStorageCmd` | — | `ReplyEvent` |
| `usb_storage_status` | `UsbStorageStatusCmd` | — | `UsbStorageStatusEvent` |
| `device_disable` | `DeviceDisableCmd` | `instanceId` | `ReplyEvent` |
| `device_enable` | `DeviceEnableCmd` | `instanceId` | `ReplyEvent` |
| `device_protection_status` | `DeviceProtectionStatusCmd` | — | `DeviceProtectionStatusEvent` |
| `device_whitelist_enable` | `DeviceWhitelistEnableCmd` | — | `ReplyEvent` |
| `device_whitelist_disable` | `DeviceWhitelistDisableCmd` | — | `ReplyEvent` |
| `device_whitelist_get` | `DeviceWhitelistGetCmd` | — | `DeviceWhitelistGetEvent` |
| `device_whitelist_clear` | `DeviceWhitelistClearCmd` | — | `ReplyEvent` |
| `device_whitelist_add` | `DeviceWhitelistAddCmd` | `vid?, pid?, serial?, mac?, kind?, label?` | `ReplyEvent` |
| `device_whitelist_remove` | `DeviceWhitelistRemoveCmd` | `vid?, pid?, serial?, mac?, kind?` | `ReplyEvent` |
| `device_whitelist_set` | `DeviceWhitelistSetCmd` | `entries: DeviceEntryDto[]` | `ReplyEvent` |
| `device_blacklist_enable` | `DeviceBlacklistEnableCmd` | — | `ReplyEvent` |
| `device_blacklist_disable` | `DeviceBlacklistDisableCmd` | — | `ReplyEvent` |
| `device_blacklist_get` | `DeviceBlacklistGetCmd` | — | `DeviceBlacklistGetEvent` |
| `device_blacklist_clear` | `DeviceBlacklistClearCmd` | — | `ReplyEvent` |
| `device_blacklist_add` | `DeviceBlacklistAddCmd` | `vid?, pid?, serial?, mac?, kind?, label?` | `ReplyEvent` |
| `device_blacklist_remove` | `DeviceBlacklistRemoveCmd` | `vid?, pid?, serial?, mac?, kind?` | `ReplyEvent` |
| `device_blacklist_set` | `DeviceBlacklistSetCmd` | `entries: DeviceEntryDto[]` | `ReplyEvent` |
| `ping` | `PingCmd` | — | `ReplyEvent` |
| `shutdown` | `ShutdownCmd` | — | `ReplyEvent`, then process exit |

`device_disable`/`device_enable` act on an arbitrary PnP instance ID directly — not
policy-aware, unlike everything under whitelist/blacklist.

## 4. Event protocol (stdout)

Every stdout line is one JSON object with a `type` discriminant, written exclusively through
`EventEmitter.Emit` under a lock — never a raw `Console.WriteLine` elsewhere. `info`/`error`
share the same stream as protocol events; there is no separate log channel.

| `type` | Record | Key fields | Fires when |
|---|---|---|---|
| `error` | `ErrorEvent` | `source, message, ts` | Any caught exception; unregistered-type serialization guard |
| `info` | `InfoEvent` | `message, ts` | Startup/shutdown milestones, policy-apply/restore summaries |
| `reply` | `ReplyEvent` | `id?, ok, error?` | Default ack for most commands |
| `clipboard_read` | `ClipboardReadEvent` | `id?, ok, content?` | Reply to `clipboard_read` |
| `clipboard_change` | `ClipboardTextEvent` / `ClipboardFilesEvent` / `ClipboardImageEvent` / `ClipboardUnknownEvent` (kind `text`/`files`/`image`/`unknown`) | `operation` (`copy`/`cut`/`paste`), content, `ts` | Unsolicited, every `WM_CLIPBOARDUPDATE` |
| `clipboard_protection_status` | `ClipboardProtectionStatusEvent` | `id?, ok, whitelistEnabled, blacklistEnabled` | Reply to `clipboard_protection_status` |
| `clipboard_whitelist_get` / `clipboard_blacklist_get` | `ClipboardWhitelistGetEvent` / `ClipboardBlacklistGetEvent` | `id?, ok, enabled, entries: ClipboardRuleEntryDto[]` | Reply to the `_get` commands |
| `clipboard_content_blocked` | `ClipboardContentBlockedEvent` | `operation, kind, reason (blacklist_match/whitelist_gate), matchedPattern?, ts` | Copy/cut/paste content violates policy |
| `clipboard_content_block_failed` | `ClipboardContentBlockFailedEvent` | `operation, kind, error?, ts` | The remediation action (clipboard clear) itself fails |
| `usb_drive_connected` / `usb_drive_disconnected` | `UsbDriveConnectedEvent` / `UsbDriveDisconnectedEvent` | `drives: string[], ts` | `DBT_DEVTYP_VOLUME` arrival/removal |
| `usb_device_detected` | `UsbDeviceDetectedEvent` | `vid?, pid?, serial?, devicePath, usbClass?, kind, nativeClass?, groupId?, ts` | Arrival where VID/PID could not be parsed |
| `usb_device_connected` / `usb_device_disconnected` | `UsbDeviceConnectedEvent` / `UsbDeviceDisconnectedEvent` | `vid, pid, serial?, usbClass?, kind, nativeClass?, groupId?, devicePath, allowed(connect only), ts` | USB device-interface arrival/removal |
| `usb_device_blocked` / `usb_device_block_failed` | `UsbDeviceBlockedEvent` / `UsbDeviceBlockFailedEvent` | `..., instanceId, error?(failed only), ts` | After a block attempt |
| `usb_device_unblocked` / `usb_device_unblock_failed` | `UsbDeviceUnblockedEvent` / `UsbDeviceUnblockFailedEvent` | `vid?, pid?, serial?, kind, instanceId, error?(failed only), ts` | During `RestoreCompliant` |
| `usb_storage_status` | `UsbStorageStatusEvent` | `id?, ok, enabled` | Reply to `usb_storage_status` |
| `device_protection_status` | `DeviceProtectionStatusEvent` | `id?, ok, mode, error?` | Reply to `device_protection_status`; also usable standalone |
| `device_whitelist_get` / `device_blacklist_get` | `DeviceWhitelistGetEvent` / `DeviceBlacklistGetEvent` | `id?, ok, enabled, entries: WhitelistEntryDto[]` | Reply to the `_get` commands |
| `monitor_connected` / `monitor_disconnected` | `MonitorConnectedEvent` / `MonitorDisconnectedEvent` | `vid?, pid?, devicePath, ts` | External monitor arrival/removal (`vid`/`pid` = EDID manufacturer/product code) |
| `monitor_blocked` / `monitor_block_failed` | `MonitorBlockedEvent` / `MonitorBlockFailedEvent` | `vid?, pid?, devicePath, error?(failed only), ts` | After `DisableExternalDisplays` |
| `keyboard_shortcut` | `KeyboardShortcutEvent` | `action (copy/cut/paste/undo), ts` | Ctrl+C/X/V/Z detected (reporting only) |
| `bluetooth_device_connected` / `_disconnected` | `BluetoothDeviceConnectedEvent` / `BluetoothDeviceDisconnectedEvent` | `mac, kind, name, allowed(connect only), ts` | Paired BT device arrival/removal |
| `bluetooth_device_blocked` / `_block_failed` | `BluetoothDeviceBlockedEvent` / `BluetoothDeviceBlockFailedEvent` | `mac, kind, name, error?(failed only), ts` | After block attempt |
| `bluetooth_device_unblocked` / `_unblock_failed` | `BluetoothDeviceUnblockedEvent` / `BluetoothDeviceUnblockFailedEvent` | `mac, kind, error?(failed only), ts` | During restore |
| `network_device_connected` / `_disconnected` | `NetworkDeviceConnectedEvent` / `NetworkDeviceDisconnectedEvent` | `vid?, pid?, serial?, usbClass?, nativeClass?, devicePath, allowed(connect only), ts` | NIC arrival/removal |
| `network_device_blocked` / `_block_failed` | `NetworkDeviceBlockedEvent` / `NetworkDeviceBlockFailedEvent` | `vid?, pid?, serial?, instanceId, error?(failed only), ts` | After block attempt (refused if `IsBuiltIn`) |
| `network_device_unblocked` / `_unblock_failed` | `NetworkDeviceUnblockedEvent` / `NetworkDeviceUnblockFailedEvent` | `vid?, pid?, serial?, instanceId, error?(failed only), ts` | During restore |

## 5. Clipboard content policy

`ClipboardWhitelist`/`ClipboardBlacklist` (`Core/ClipboardRuleList.cs`) are structurally
separate from device whitelist/blacklist: entries are `ClipboardRuleEntry(Pattern, Kind?,
Label?)` — a regex pattern optionally scoped to a `ClipboardKind`, not a device identity.

- **Pattern syntax**: raw .NET `System.Text.RegularExpressions.Regex`, no custom DSL.
- **Case-sensitive**: matched with `RegexOptions.None` — no `IgnoreCase`.
- **Compiled once per mutation/load** (`RebuildCompiled`), not re-parsed on the hot path
  (the keyboard hook's per-keystroke Ctrl+V check), and every `Regex` carries a hard
  **250ms timeout** — a `RegexMatchTimeoutException` is caught and treated as "did not
  match" (logged via `EventEmitter.EmitError`). This exists because an operator-supplied
  catastrophic-backtracking pattern is evaluated synchronously inside a global low-level
  keyboard hook.
- **Malformed pattern handling**: a pattern that fails to construct (`ArgumentException`)
  is kept in the list (still reported by `Get`/`GetAll`) paired with a `null` compiled
  `Regex` — it never matches anything, but never crashes `Add`/`Load` or drops the rest of
  the list.
- **Two entries are the same rule** when `Pattern` (ordinal comparison) and `Kind` both
  match; `Label` is cosmetic and ignored for identity.
- **`Kind` scoping**: `null` matches any `ClipboardKind`; otherwise the rule only
  participates when the candidate's kind equals it. Only `ClipboardKind.Text` and
  `ClipboardKind.Files` content is ever evaluated — `Image`/`Unknown` always pass through.
  For `Files`, aggregation is "any single path" (any path matching blacklist blocks the
  whole operation; any path matching whitelist satisfies the whitelist gate).
- **The AND-combination formula** (`ClipboardMonitor.EvaluatePolicy`, shared with
  `KeyboardHook`): `allowed = (whitelist disabled OR any candidate matches a whitelist
  pattern) AND (blacklist disabled OR no candidate matches a blacklist pattern)` — the same
  formula every device monitor already applies.
- **Both lists enabled simultaneously is a valid, intended state** — unlike device
  whitelist/blacklist (which force-disable each other and treat simultaneous enablement as
  an unreachable `conflict`), `ClipboardWhitelistEnableCmd`/`ClipboardBlacklistEnableCmd`
  deliberately do not touch the other list's `Enabled` flag. There is no
  `ProtectionMode`/conflict concept for clipboard.
- **Enforcement is two-layer**: copy/cut is caught by `ClipboardMonitor` on
  `WM_CLIPBOARDUPDATE` (clears the clipboard on violation, emits
  `ClipboardContentBlockedEvent`/`ClipboardContentBlockFailedEvent`); paste is caught by
  `KeyboardHook` live-evaluating clipboard content on Ctrl+V keydown and swallowing the
  keystroke on violation.
- **Paste fails OPEN, not closed** — the opposite default from device blocking.
  `KeyboardHook.ShouldBlockPaste` is wrapped in try/catch; on any exception it returns
  `false` (do not block) and falls through to `CallNextHookEx`, because this is a global
  `WH_KEYBOARD_LL` hook and an uncaught exception there would silently break paste for
  every application on the machine until the process restarts.

### Examples

Block any US Social Security Number from being copied, cut, or pasted:
```json
{"id": "1", "cmd": "clipboard_blacklist_add", "pattern": "\\b\\d{3}-\\d{2}-\\d{4}\\b", "label": "SSN"}
{"id": "2", "cmd": "clipboard_blacklist_enable"}
```
Copying `"SSN: 123-45-6789"` now emits `clipboard_content_blocked` (`reason: "blacklist_match"`,
`matchedPattern: "\\b\\d{3}-\\d{2}-\\d{4}\\b"`) and the clipboard is cleared. Copying
`"call me at 555-1234"` is unaffected — no match.

Block copying/dragging a private key file, scoped to `Files` only (does not apply to `Text`):
```json
{"id": "3", "cmd": "clipboard_blacklist_add", "pattern": "\\.pem$", "kind": "files", "label": "private key"}
```
Copying `C:\keys\prod.pem` in Explorer is blocked; copying the text string `"C:\keys\prod.pem"`
from a chat window is not, since `kind: "files"` excludes `ClipboardKind.Text` candidates;
copying `C:\docs\notes.txt` is unaffected either way.

Deny-all except an internal email domain (whitelist gate, `Text` only):
```json
{"id": "4", "cmd": "clipboard_whitelist_add", "pattern": "@acme\\.com\\b", "kind": "text"}
{"id": "5", "cmd": "clipboard_whitelist_enable"}
```
Copying `"contact: alice@acme.com"` is allowed. Copying `"contact: alice@gmail.com"` is
blocked (`reason: "whitelist_gate"`, `matchedPattern: null` — whitelist misses are not
attributed to one pattern). A blacklist rule on the same content, if also enabled, would
still win over an otherwise-whitelisted match (AND-formula, blacklist has veto power).

## 6. Alert Host

`DlpEndpointMonitor.AlertHost` is a companion WPF app that shows a UI alert in the
interactive user's session — this binary may itself be running headless (e.g. LocalSystem
in Session 0, which has no desktop). **Not yet wired into any block path** — no
`Monitors/*` call site invokes `Actions/AlertActions.ShowAlert` today.

### Wire format (`AlertRequest`, `DlpEndpointMonitor.AlertContracts`)

```csharp
public sealed record AlertRequest(
    AlertType Type,
    string Title,
    string Message,
    string Id,               // required correlation ID, never fabricated by AlertHost
    AlertSeverity Severity = AlertSeverity.Info,
    int DurationSeconds = 5);
```

| `AlertType` | Behavior |
|---|---|
| `Modal` | Must be acknowledged; also the fail-safe fallback for any unmapped type |
| `Toast` | Auto-dismisses (timer-driven) |
| `FullScreen` | Full-screen, blocking alert |

| `AlertSeverity` | Values |
|---|---|
| — | `Info`, `Warning`, `Blocked` |

### Delivery mechanism (`Actions/AlertActions.ShowAlert`)

1. **Pipe-first**: tries `NamedPipeClientStream` against `AlertPipe.Name`
   (`"DlpEndpointMonitor.Alerts"`, defined once in `AlertContracts`) with a 300ms connect
   timeout. If an AlertHost owner is already running in that session, the request is
   written as one newline-terminated JSON line and delivery is done.
2. **Launch on no answer**: if nobody is listening, resolves the active console session
   (`WTSGetActiveConsoleSessionId`) and launches `DlpEndpointMonitor.AlertHost.exe` with the
   request embedded as a base64 `--initial-alert=` argument:
   - **Same session** (e.g. manual/dev testing): direct `Process.Start`.
   - **Different session** (the real LocalSystem/Session-0 deployment):
     `WTSQueryUserToken` → `DuplicateTokenEx` → `CreateEnvironmentBlock` →
     `CreateProcessAsUser` with `STARTUPINFO.lpDesktop = "winsta0\\default"` (without this
     the process is created on a non-interactive window station and its window is simply
     never visible). Every acquired handle is released in a `finally` block regardless of
     which step failed.

### Singleton ownership and bootstrap (`App.xaml.cs`)

A session-scoped named `Mutex` (`"DlpEndpointMonitor.AlertHost.Singleton"`, deliberately no
`"Global\"` prefix — one owner per interactive session) decides who hosts the pipe server
and in-memory `AlertQueue`. The instance that wins creates `AlertQueue` and
`PipeTransport.Server`; every later invocation in that session detects the mutex is already
held, forwards its `AlertRequest` over the pipe (`PipeTransport.TrySendToOwner`), and exits
immediately without starting a second server. The very first alert is passed to the winning
instance via the base64 `--initial-alert=` argument rather than relying on a
connect-after-launch race, since becoming the mutex owner and starting the pipe server is
not instantaneous.

### Coalesce/cap queueing (`AlertQueue`)

- **Coalesce**: while an alert of the same `(Type, Severity)` pair is already queued (not yet
  dequeued for display), a new request of that same pair does not open a second window — it
  increments a running count folded into the eventual title as `"{Title} (+{N} more)"`. The
  entry is removed from the coalesce map at dequeue time, before `_show` is invoked, so an
  alert of the same pair arriving while one is actively on screen starts a fresh pending
  entry rather than folding into it.
- **Cap**: at most 5 (`MaxPending`) distinct pending entries are held; anything beyond that
  is dropped and logged to stderr, never silently.
- The dispatch loop shows one alert at a time via `Dispatcher.Invoke` (blocking, not
  `InvokeAsync`), so the loop cannot dequeue the next alert until the current window's
  `ShowDialog()` returns — this is what guarantees at most one alert window is ever visible.

## 7. Persistence

Three JSON files under `%ProgramData%\DlpEndpointMonitor\`
(`Environment.SpecialFolder.CommonApplicationData`): `whitelist.json`, `blacklist.json`,
`disabled-devices.json`, plus `clipboard-whitelist.json`/`clipboard-blacklist.json` for
clipboard rule lists. Machine-wide and user-agnostic — this process runs elevated and may
run under a different effective user context (e.g. a service/SYSTEM account) than the
interactive user, so a per-user profile folder would resolve to the wrong place.

Every write is atomic: serialize to `<path>.tmp`, then `File.Move(tmp, path,
overwrite: true)` — never written in place, so a crash mid-write cannot corrupt policy
state the next startup depends on. A `ReaderWriterLockSlim` guards in-memory state on every
list, since it is read from the STA message-loop thread (device/clipboard arrival events)
and written from the async command-dispatch thread (whitelist/blacklist mutations)
concurrently. Serialization goes through the source-generated `AppJsonContext` — no
reflection-based `JsonSerializer.Serialize<T>()` outside `SchemaExporter`.

The storage directory is an optional constructor parameter (defaulting to the
`%ProgramData%\DlpEndpointMonitor\` computation above) on every list type, threaded through
so tests can point at a throwaway temp directory instead of the real storage location — not
a hard-coded path, and no second competing override mechanism (e.g. an environment
variable) exists or should be added.
