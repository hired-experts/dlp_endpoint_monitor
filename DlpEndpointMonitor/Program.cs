using DlpEndpointMonitor.Actions;
using DlpEndpointMonitor.Core;
using DlpEndpointMonitor.Handlers.Windows;
using DlpEndpointMonitor.Monitors;

// Schema export mode — exits immediately; must run before any stdout writes.
if (args.Contains("--schema"))
{
#pragma warning disable IL2026 // SchemaExporter is intentionally reflection-based; never runs in trimmed builds.
    SchemaExporter.Export(Console.Out);
#pragma warning restore IL2026
    return;
}

// Clipboard/keyboard companion mode — launched BY the primary (Session-0) instance via
// SessionActions.LaunchIntoSession into the interactive user's session, where clipboard/keyboard
// hooks actually reach a real desktop. This instance has NO stdin link to the agent (the agent
// only ever talks to the original primary) and deliberately runs NONE of the device monitors or
// the CommandDispatcher — device policy stays exclusively the primary's job. It shares live
// policy with the primary through the same %ProgramData% files and relays every event it emits
// back to the primary's stdout over a named pipe. Checked before any device-list construction.
if (args.Contains("--session-companion"))
{
    try
    {
        // SAME default storage location (no override) as the primary — this is what lets the
        // companion read the identical clipboard whitelist/blacklist files the primary mutates.
        var cbWhitelist = new ClipboardWhitelist();
        var cbBlacklist = new ClipboardBlacklist();
        var cbCutHint   = new ClipboardOperationHint();
        var cbScreenshotBlockPolicy = new ScreenshotBlockPolicy();

        // Construct the two companion-HOSTED relay servers FIRST, before the blocking relayClient
        // connect below. The primary fires its own ~2s connect retries at these servers almost
        // immediately at startup; if relayClient's ~2s blocking connect to the PRIMARY's server sat
        // ahead of them, it could delay these two from listening and make the primary's connects
        // race and lose — the exact class of regression that broke display/Bluetooth relays (see the
        // DisplayChangeRelay note on the companion thread below). Servers before any blocking client.
        //
        // This companion runs in the interactive session, so it — not the headless Session-0
        // primary — is the process whose DisplayActions topology calls actually take effect. Host
        // the display relay server so the primary's DisplayMonitor can route its two
        // DisableExternalDisplays/EnableExternalDisplays calls here for real execution. Kept alive
        // (via using) for the companion's whole lifetime — disposed only when this process exits.
        (bool ok, string? error) ExecuteDisplayCommand(string command) => command switch
        {
            "disable" => DisplayActions.DisableExternalDisplays(),
            "enable"  => DisplayActions.EnableExternalDisplays(),
            _         => (false, $"unknown display command: {command}"),
        };
        using var displayRelayServer = new DisplayCompanionRelay.Server(ExecuteDisplayCommand);

        // The companion runs in the interactive user's session with a real user context, so its
        // BluetoothActions.EnumerateConnected actually sees the paired devices — the headless
        // Session-0 primary sees none. Host the Bluetooth relay server so the primary's
        // BluetoothMonitor can fetch the real device list from here. Kept alive (via using) for the
        // companion's whole lifetime — disposed only when this process exits.
        using var bluetoothRelayServer = new BluetoothCompanionRelay.Server(
            () => BluetoothActions.EnumerateConnected().ToList());

        // Only now — after the hosted servers are listening — connect back to the primary's
        // ClipboardCompanionRelay.Server (a ~2s blocking connect). Deliberately NOT deferred to a
        // Task.Run (unlike DisplayChangeRelay.Client below): RawLineSink must be set as early as
        // possible so error events from the later companion startup steps (FileSystemWatcher setup,
        // etc.) are actually relayed to the primary/agent instead of only reaching this
        // session-crossed child's own unread stdout. Forward every event this companion emits to the
        // primary's stdout too; the companion's own Console.Out output is left untouched (harmless).
        using var relayClient = new ClipboardCompanionRelay.Client();
        EventEmitter.RawLineSink = relayClient.WriteLine;

        var companionReady        = new ManualResetEventSlim(false);
        var companionStartupFailed = false;

        // Same STA / background message-pump shape as the primary thread below, but hosting ONLY
        // the clipboard listener + keyboard hook — no USB/BT/Display/Network monitors, and nothing
        // to EnumerateExisting (clipboard/keyboard have no pre-connected state to sweep).
        var companionThread = new Thread(() =>
        {
            try
            {
                using var window       = new MessageWindow();
                using var clipboardMon = new ClipboardMonitor(window, cbWhitelist, cbBlacklist, cbCutHint);
                using var keyboardHook = new KeyboardHook(cbWhitelist, cbBlacklist, cbCutHint, cbScreenshotBlockPolicy);

                companionReady.Set();
                EventEmitter.EmitInfo("clipboard companion ready");

                // Connecting to the primary's DisplayChangeRelay happens off this thread entirely,
                // not inline here - its constructor blocks for up to ~2s, and doing that here (even
                // AFTER displayRelayServer/bluetoothRelayServer below) would delay
                // MessageWindow.RunMessageLoop from ever starting, meaning WM_CLIPBOARDUPDATE/
                // keyboard-hook messages wouldn't pump during that window either. Sitting it
                // sequentially on the entry thread BEFORE displayRelayServer/bluetoothRelayServer
                // (as a first attempt at this fix did) was worse still: it delayed those two
                // companion-hosted servers from ever being constructed, which made the primary's own
                // ~2s connect retry for THEM race and lose against a companion that hadn't started
                // listening yet - a real regression: "could not connect to companion pipe" for both
                // display and bluetooth relays while clipboard (which doesn't depend on a
                // companion-hosted server) worked fine. This companion's own message window lives in
                // the interactive session, so it - not the headless Session-0 primary - is the one
                // that actually receives WM_DISPLAYCHANGE; see
                // DisplayMonitor.NotifyExternalDisplayChange for why WM_DEVICECHANGE alone isn't
                // enough. Best-effort/fire-and-forget: Notify() never throws even if this connect is
                // still in flight (or failed) when a display change happens.
                _ = Task.Run(() =>
                {
                    try
                    {
                        var client = new DisplayChangeRelay.Client();

                        // Mirrors "clipboard companion ready" - a positive confirmation that this
                        // leg of the companion came up too, not just silence-means-success. A
                        // failed (but non-throwing) connect gets an EmitError instead, matching
                        // this file's own "never silently swallow" convention - IsConnected==false
                        // is exactly the case a bare try/catch around the constructor cannot catch.
                        if (client.IsConnected)
                            EventEmitter.EmitInfo("monitor companion ready");
                        else
                            EventEmitter.EmitError("display_change_relay", $"could not connect to primary's display-change relay: {client.ConnectError}");

                        window.DisplayChanged += () => client.Notify();
                    }
                    catch (Exception ex)
                    {
                        EventEmitter.EmitError("display_change_relay", $"failed to set up display-change relay client: {ex.Message}");
                    }
                });

                MessageWindow.RunMessageLoop(); // blocks until WM_QUIT / session logoff kills us
            }
            catch (Exception ex)
            {
                companionStartupFailed = true;
                EventEmitter.EmitError("clipboard_companion_msg_thread", ex.Message);
                companionReady.Set(); // unblock the entry point even on failure
            }
        });
        companionThread.SetApartmentState(ApartmentState.STA);
        companionThread.IsBackground = true;
        companionThread.Start();

        companionReady.Wait();
        if (companionStartupFailed)
            return;

        // Pick up policy changes the primary writes to the shared files. The atomic
        // temp-file-then-rename Save() in ClipboardRuleList can raise several raw filesystem
        // events per logical save, and Regex recompilation on Reload() is not free — so debounce:
        // collapse a burst of events for one file into a single Reload() after a short quiet gap.
        const int ReloadDebounceMs = 300;
        using var whitelistReload = new Timer(_ => cbWhitelist.Reload(), null, Timeout.Infinite, Timeout.Infinite);
        using var blacklistReload = new Timer(_ => cbBlacklist.Reload(), null, Timeout.Infinite, Timeout.Infinite);
        using var screenshotBlockReload = new Timer(_ => cbScreenshotBlockPolicy.Reload(), null, Timeout.Infinite, Timeout.Infinite);

        void OnStorageChanged(object sender, FileSystemEventArgs e)
        {
            if (string.Equals(e.Name, "clipboard-whitelist.json", StringComparison.OrdinalIgnoreCase))
                whitelistReload.Change(ReloadDebounceMs, Timeout.Infinite);
            else if (string.Equals(e.Name, "clipboard-blacklist.json", StringComparison.OrdinalIgnoreCase))
                blacklistReload.Change(ReloadDebounceMs, Timeout.Infinite);
            else if (string.Equals(e.Name, "screenshot-block.json", StringComparison.OrdinalIgnoreCase))
                screenshotBlockReload.Change(ReloadDebounceMs, Timeout.Infinite);
        }

        // FileSystemWatcher throws ArgumentException if the directory doesn't exist yet - a real
        // possibility here (fresh install, nothing ever saved yet) and the exact crash observed in
        // production: it took the whole companion process down (caught by the outer try/catch
        // below, which just logs and returns), silently killing display/Bluetooth relay + clipboard
        // monitoring together, every time, with nothing to ever relaunch it afterward.
        Directory.CreateDirectory(StorageLocation.Default);
        using var watcher = new FileSystemWatcher(StorageLocation.Default)
        {
            // The rename half of the atomic save surfaces as a Renamed (new name = the json file);
            // the write half as Changed/Created — watch all three and filter by name in the handler.
            NotifyFilter        = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = true,
        };
        watcher.Changed += OnStorageChanged;
        watcher.Created += OnStorageChanged;
        watcher.Renamed += OnStorageChanged;

        // No stdin to watch and no natural exit — block on the pump thread until this process is
        // terminated externally (e.g. session logoff), so background threads keep the process alive.
        companionThread.Join();
    }
    catch (Exception ex)
    {
        EventEmitter.EmitError("clipboard_companion", ex.Message);
    }
    return;
}

var cts = new CancellationTokenSource();

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true; // don't kill immediately — let tasks clean up
    cts.Cancel();
};

// Shared lists — written by CommandDispatcher, read by UsbMonitor
// Both run on different threads; UsbDeviceList uses ReaderWriterLockSlim internally
var whitelist = new DeviceWhitelist();
var blacklist = new DeviceBlacklist();
var disabled  = new DisabledDevices();

// Clipboard whitelist/blacklist - independent of device policy, and (unlike device policy)
// independently enable-able at once: no ProtectionMode/conflict concept for clipboard.
var clipboardWhitelist = new ClipboardWhitelist();
var clipboardBlacklist = new ClipboardBlacklist();
var clipboardCutHint   = new ClipboardOperationHint();
var screenshotBlockPolicy = new ScreenshotBlockPolicy();

// Conflict guard: if both lists were enabled on disk (e.g. direct file edit), disable both.
// The client can query device_protection_status to understand the current state.
if (whitelist.IsEnabled && blacklist.IsEnabled)
{
    whitelist.SetEnabled(false);
    blacklist.SetEnabled(false);
    EventEmitter.EmitError("startup_conflict",
        "Both whitelist and blacklist were enabled — both disabled. Use device_protection_status to check.");
}

// Signal set once the message window is ready
var windowReady      = new ManualResetEventSlim(false);
MessageWindow?    window           = null;
UsbMonitor?       usbMonitor       = null;
BluetoothMonitor? bluetoothMonitor = null;
DisplayMonitor?   displayMonitor   = null;
NetworkMonitor?   networkMonitor   = null;
ClipboardMonitor? clipboardMonitor = null;

// ── Clipboard/keyboard placement decision ─────────────────────────────────────
// The clipboard listener and keyboard hook only function in an interactive session with a real
// desktop. Under the real deployment this binary runs as LocalSystem in Session 0, which has no
// desktop — its own hooks would watch an inert clipboard. Decide here whether THIS process can run
// them itself, or must launch a --session-companion copy into the logged-on user's session.
bool runClipboardLocally = true;
ClipboardCompanionRelay.Server? relayServer = null;
DisplayChangeRelay.Server? displayChangeRelayServer = null;

// Set below only when a companion is actually launched, closing over the exact session/exePath
// used for that launch. Called from this process's own shutdown path (both the Ctrl+Break/
// cancellation route and the `shutdown` stdin command) so an uninstall, service stop, or
// explicit shutdown command doesn't leave today's companion running as tomorrow's orphan - see
// SessionActions.TerminateCompanionProcesses's doc comment for the production bug this closes.
Action stopCompanion = () => { };

// How DisplayMonitor's two topology-mutating calls are invoked. Default: this process calls
// DisplayActions directly (interactive/dev run, or no user session to cross into yet). When a
// companion IS launched below, these are swapped for relay calls routed to that companion — the
// only process whose SetDisplayConfig actually takes effect on the interactive desktop.
DisplayCompanionRelay.Client? displayRelayClient = null;
Func<(bool ok, string? error)> disableExternalDisplays = DisplayActions.DisableExternalDisplays;
Func<(bool ok, string? error)> enableExternalDisplays  = DisplayActions.EnableExternalDisplays;

// How BluetoothMonitor enumerates paired devices. Default: this process enumerates directly
// (interactive/dev run, or no user session to cross into yet). When a companion IS launched below,
// this is swapped for a relay call routed to that companion — the only process whose
// BluetoothActions.EnumerateConnected sees the paired devices on the interactive desktop.
BluetoothCompanionRelay.Client? bluetoothRelayClient = null;
Func<IReadOnlyList<BluetoothActions.BtDevice>> enumerateBluetoothDevices =
    () => BluetoothActions.EnumerateConnected().ToList();

var activeSession = SessionActions.GetActiveConsoleSessionId();
if (activeSession is null)
{
    // No user logged on yet (e.g. the lock screen before first logon). Nothing to cross into —
    // run the hooks locally as before. Launching a companion once a user later logs on is a known
    // limitation (not retried/polled in this pass); say so rather than going silently blind.
    EventEmitter.EmitInfo("no interactive session yet — clipboard/keyboard protection inactive until a user logs on");
}
else if (SessionActions.IsRunningInSession(activeSession.Value))
{
    // Already inside the interactive session (manual/dev run — same fast path AlertActions.ShowAlert
    // uses). The local hooks reach a real desktop; no companion needed.
}
else
{
    // Real Session-0 service deployment. Host the relay server FIRST so it is already listening
    // when the companion connects, then launch a companion copy of this exe into the user's session.
    relayServer = new ClipboardCompanionRelay.Server();

    // displayMonitor is captured by reference and assigned later on the message-loop thread below
    // (null-conditional here since a notification could theoretically arrive before that thread
    // finishes starting up) — this is what lets the companion's real WM_DISPLAYCHANGE drive the
    // primary's own compliance re-check on a pure projection switch, see DisplayMonitor's doc.
    displayChangeRelayServer = new DisplayChangeRelay.Server(() =>
    {
        if (!windowReady.IsSet) return; // msgThread hasn't published displayMonitor yet - benign no-op, same happens-before gate ApplyPolicy/RestoreDevices/ReevaluateClipboard rely on
        displayMonitor?.NotifyExternalDisplayChange();
    });

    string? exePath = Environment.ProcessPath; // this process's own executable path

    // Kill any companion left behind by a prior primary generation BEFORE launching a fresh one -
    // otherwise it lingers indefinitely, still independently enforcing whatever policy was
    // current at its own startup (see SessionActions.TerminateCompanionProcesses for the real
    // production bug this fixes).
    if (exePath is not null)
    {
        int staleKilled = SessionActions.TerminateCompanionProcesses(activeSession.Value, exePath);
        if (staleKilled > 0)
            EventEmitter.EmitInfo($"terminated {staleKilled} stale companion process(es) left over from a prior run before launching a fresh one");
    }

    var (ok, error) = exePath is null
        ? (false, "cannot resolve own executable path (Environment.ProcessPath was null)")
        : SessionActions.LaunchIntoSession(activeSession.Value, exePath, "--session-companion");

    if (ok)
    {
        // Companion now owns clipboard/keyboard exclusively — do NOT also build inert local hooks
        // on this Session-0 desktop.
        runClipboardLocally = false;

        // The same companion hosts the display relay server; route DisplayMonitor's two topology
        // calls through it, since a SetDisplayConfig from this Session-0 process has no visible
        // desktop to affect. The static-method-group defaults above are replaced with relay calls.
        displayRelayClient = new DisplayCompanionRelay.Client();
        disableExternalDisplays = () => displayRelayClient.SendCommand("disable");
        enableExternalDisplays  = () => displayRelayClient.SendCommand("enable");

        // Same reasoning for Bluetooth enumeration: only the companion's process sees the paired
        // devices, so route BluetoothMonitor's enumeration through the relay instead of this
        // Session-0 process's own (empty) BluetoothActions.EnumerateConnected.
        bluetoothRelayClient = new BluetoothCompanionRelay.Client();
        enumerateBluetoothDevices = () => bluetoothRelayClient.Enumerate();

        // Closes over THIS launch's own exePath/session so shutdown can clean up the exact
        // companion just started, regardless of how this primary is later told to stop.
        // exePath! is proven non-null here: ok can only be true via the branch of the ternary
        // above that requires exePath is not null to even call LaunchIntoSession.
        string companionExePath = exePath!;
        stopCompanion = () =>
        {
            int killed = SessionActions.TerminateCompanionProcesses(activeSession.Value, companionExePath);
            if (killed > 0)
                EventEmitter.EmitInfo($"terminated {killed} companion process(es) on shutdown");
        };

        EventEmitter.EmitInfo("clipboard/keyboard companion launched into interactive session");
    }
    else
    {
        // Fail safe, not silent: keep the (inert-but-present) local hooks so the failure never
        // leaves us with NO clipboard/keyboard component at all, and surface the exact reason.
        relayServer.Dispose();
        relayServer = null;
        displayChangeRelayServer.Dispose();
        displayChangeRelayServer = null;
        EventEmitter.EmitError("clipboard_companion_launch", error ?? "unknown failure");
    }
}

// ── Message loop thread ───────────────────────────────────────────────────────
// All Win32 message-based components (clipboard listener, USB notifications,
// keyboard hook) must live on the SAME thread that runs the message pump.
// That thread must be STA.

var msgThread = new Thread(() =>
{
    try
    {
        window           = new MessageWindow();
        usbMonitor       = new UsbMonitor(window, whitelist, blacklist, disabled);
        bluetoothMonitor = new BluetoothMonitor(window, whitelist, blacklist, disabled, enumerateBluetoothDevices);
        displayMonitor   = new DisplayMonitor(window, whitelist, blacklist, disableExternalDisplays, enableExternalDisplays);
        networkMonitor   = new NetworkMonitor(window, whitelist, blacklist, disabled);

        using var usbMon           = usbMonitor;
        using var btMon            = bluetoothMonitor;
        using var dispMon          = displayMonitor;
        using var netMon           = networkMonitor;

        // Clipboard listener + keyboard hook run locally ONLY when no companion was launched
        // (interactive session, or no session to cross into yet). Otherwise the companion owns
        // them and these stay null. using(null) disposes to a harmless no-op.
        KeyboardHook? keyboardHook = null;
        if (runClipboardLocally)
        {
            clipboardMonitor = new ClipboardMonitor(window, clipboardWhitelist, clipboardBlacklist, clipboardCutHint);
            keyboardHook     = new KeyboardHook(clipboardWhitelist, clipboardBlacklist, clipboardCutHint, screenshotBlockPolicy);
        }
        using var cbMon            = clipboardMonitor;
        using var kbHook           = keyboardHook;

        windowReady.Set(); // unblock main thread — monitors are set before this
        EventEmitter.EmitInfo("ready");

        // Enumerate devices already connected before we started.
        // Runs on a ThreadPool thread so the message loop is never blocked.
        _ = Task.Run(usbMonitor.EnumerateExisting);
        _ = Task.Run(bluetoothMonitor.EnumerateExisting);
        _ = Task.Run(displayMonitor.BlockNonCompliant);
        _ = Task.Run(networkMonitor.EnumerateExisting);

        MessageWindow.RunMessageLoop(); // blocks until WM_QUIT
    }
    catch (Exception ex)
    {
        EventEmitter.EmitError("msg_thread", ex.Message);
        cts.Cancel();
        windowReady.Set(); // unblock main thread even on failure
    }
    finally
    {
        window?.Dispose();
    }
});

msgThread.SetApartmentState(ApartmentState.STA);
msgThread.IsBackground = true;
msgThread.Start();

// Wait until the window is created before accepting commands
windowReady.Wait(cts.Token);

if (cts.IsCancellationRequested)
    return; // fatal error during startup


// Named so ResetAllPolicyCmd (WindowsControlHandler) can reuse the exact same delegates as the
// per-list protection handlers below, rather than owning a second, separately-wired copy.
void ApplyPolicy() { usbMonitor!.BlockNonCompliant(); bluetoothMonitor!.BlockNonCompliant(); displayMonitor!.BlockNonCompliant(); networkMonitor!.BlockNonCompliant(); }
void RestoreDevices() { usbMonitor!.RestoreCompliant(); bluetoothMonitor!.RestoreCompliant(); displayMonitor!.RestoreCompliant(); networkMonitor!.RestoreCompliant(); }
void ReevaluateClipboard() => clipboardMonitor!.ApplyPolicy();

var dispatcher = new CommandDispatcher(
    cancellationToken: cts.Token,
    clipboard:         new WindowsClipboardHandler(),
    usbStorage:        new WindowsUsbStorageHandler(),
    usbDevice:         new WindowsUsbDeviceHandler(),
    usbProtection:     new WindowsUsbProtectionHandler(whitelist, blacklist,
        applyPolicy:    ApplyPolicy,
        restoreDevices: RestoreDevices),
    clipboardProtection: new WindowsClipboardProtectionHandler(clipboardWhitelist, clipboardBlacklist,
        reevaluate: ReevaluateClipboard),
    control:           new WindowsControlHandler(stopCompanion,
        whitelist, blacklist, clipboardWhitelist, clipboardBlacklist,
        restoreDevices: RestoreDevices, clipboardReevaluate: ReevaluateClipboard),
    alert:             new WindowsAlertHandler(),
    screenshotProtection: new WindowsScreenshotProtectionHandler(screenshotBlockPolicy));

try
{
    await dispatcher.RunAsync();
}
catch (OperationCanceledException) { }
finally
{
    window?.Stop(); // posts WM_DESTROY → message loop exits
    if (!msgThread.Join(TimeSpan.FromSeconds(2)))
        EventEmitter.EmitError("shutdown", "message-loop thread did not exit within 2s of WM_DESTROY");
    relayServer?.Dispose(); // stop the companion relay listener, if one was hosted
    displayChangeRelayServer?.Dispose(); // stop the display-change-notify listener, if one was hosted
    displayRelayClient?.Dispose(); // release the display relay client, if one was constructed
    bluetoothRelayClient?.Dispose(); // release the bluetooth relay client, if one was constructed
    // Covers the Ctrl+Break/cancellation shutdown route - WindowsControlHandler's ShutdownCmd
    // handler covers the `shutdown` stdin command route. A no-op if no companion was launched.
    stopCompanion();
    EventEmitter.EmitInfo("shutdown");
}
