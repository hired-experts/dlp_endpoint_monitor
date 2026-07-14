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

                // Connecting to the primary's DisplayChangeRelay happens off this thread entirely -
                // its constructor blocks for up to ~2s, and doing that inline (before or after
                // displayRelayServer/bluetoothRelayServer) would delay MessageWindow.RunMessageLoop
                // from starting, or delay those servers from listening before the primary's own
                // connect retries hit them (see AGENTS.md section 10 for the regression this caused).
                // This companion's own message window lives in the interactive session, so it - not
                // the headless Session-0 primary - is the one that actually receives WM_DISPLAYCHANGE.
                // Best-effort/fire-and-forget: Notify() never throws even if this connect is still in
                // flight (or failed) when a display change happens.
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

// Policy-only cleanup mode — launched by the Node agent's uninstall cleanup step
// (cleanup-policies.ts) as a throwaway, one-shot process distinct from the real primary. Exists
// because the real primary's normal startup unconditionally calls EnsureCompanionForActiveSession,
// which would kill and replace whatever companion a REAL, still-running primary already has live in
// the interactive session — a self-inflicted protection gap during every uninstall. This mode does
// ZERO session/companion/monitor work — no EnsureCompanionForActiveSession, no
// UsbMonitor/BluetoothMonitor/DisplayMonitor/NetworkMonitor — it just wires a CommandDispatcher
// straight to the same default %ProgramData% policy files the real primary uses, so
// reset_all_policy/usb_enable_storage/screenshot_block_disable/shutdown from the cleanup script
// mutate real state without disturbing anything live. Checked in the same early block as
// --schema/--session-companion, before any of the real primary's own state is constructed.
if (args.Contains("--policy-only"))
{
    try
    {
        var policyCts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true; // don't kill immediately — let ShutdownCmd's own path run instead
            policyCts.Cancel();
        };

        // SAME default storage locations (no override) as the real primary — this is what lets
        // this ephemeral process mutate the exact same persisted files the real primary (and any
        // --session-companion reading the same %ProgramData% directory) already uses.
        var policyDeviceWhitelist    = new DeviceWhitelist();
        var policyDeviceBlacklist    = new DeviceBlacklist();
        var policyClipboardWhitelist = new ClipboardWhitelist();
        var policyClipboardBlacklist = new ClipboardBlacklist();
        var policyScreenshotBlock    = new ScreenshotBlockPolicy();

        // No monitors exist in this mode, so there is nothing here to re-enforce live — every
        // reconcile delegate below is a no-op. reset_all_policy/usb_enable_storage still correctly
        // mutate the real persisted files and reply ok; a live re-enforcement sweep, if one is
        // needed, is the REAL primary's own job the next time it re-checks policy, not this
        // one-shot process's.
        var policyDispatcher = new CommandDispatcher(
            cancellationToken:    policyCts.Token,
            clipboard:            new WindowsClipboardHandler(),
            usbStorage:           new WindowsUsbStorageHandler(
                blockAlreadyConnectedStorage: () => { },
                restoreStorageDisabled:       () => { },
                startStoragePoll:             () => { },
                stopStoragePoll:              () => { }),
            usbDevice:            new WindowsUsbDeviceHandler(),
            usbProtection:        new WindowsUsbProtectionHandler(policyDeviceWhitelist, policyDeviceBlacklist,
                applyPolicy:    () => { },
                restoreDevices: () => { }),
            clipboardProtection:  new WindowsClipboardProtectionHandler(policyClipboardWhitelist, policyClipboardBlacklist,
                reevaluate: () => { }),
            control:              new WindowsControlHandler(stopCompanion: () => { },
                policyDeviceWhitelist, policyDeviceBlacklist, policyClipboardWhitelist, policyClipboardBlacklist,
                policyScreenshotBlock,
                restoreDevices: () => { }, clipboardReevaluate: () => { }),
            alert:                new WindowsAlertHandler(),
            screenshotProtection: new WindowsScreenshotProtectionHandler(policyScreenshotBlock));

        try
        {
            await policyDispatcher.RunAsync();
        }
        catch (OperationCanceledException) { }
    }
    catch (Exception ex)
    {
        EventEmitter.EmitError("policy_only", ex.Message);
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

// Additive detection path for a driverless mass-storage devnode that never registers a device
// interface at all (see UsbStorageDriverlessPoll, AGENTS.md section 10) - must already be running
// if the kill switch was left on across a restart, same "don't wait for a command to notice
// existing state" reasoning as the whitelist/blacklist conflict check just above.
var storagePoll = new UsbStorageDriverlessPoll();
if (!UsbActions.IsUsbStorageEnabled())
{
    storagePoll.Start();
}

// Signal set once the message window is ready
var windowReady      = new ManualResetEventSlim(false);
MessageWindow?    window           = null;
UsbMonitor?       usbMonitor       = null;
BluetoothMonitor? bluetoothMonitor = null;
DisplayMonitor?   displayMonitor   = null;
NetworkMonitor?   networkMonitor   = null;
ClipboardMonitor? clipboardMonitor = null;
// Outer-scoped (not a msgThread-local) for the same reason clipboardMonitor is outer-scoped:
// EnsureCompanionForActiveSession must be able to tear this down the moment a companion actually
// takes over, not just flip runClipboardLocally (which msgThread's construction check only ever
// reads once, at its own startup) - see the `if (ok)` branch below.
KeyboardHook? localKeyboardHook = null;

// ── Clipboard/keyboard placement decision ─────────────────────────────────────
// The clipboard listener and keyboard hook only function in an interactive session with a real
// desktop. Under the real deployment this binary runs as LocalSystem in Session 0, which has no
// desktop — its own hooks would watch an inert clipboard. Decide here whether THIS process can run
// them itself, or must launch a --session-companion copy into the logged-on user's session.
bool runClipboardLocally = true;
ClipboardCompanionRelay.Server? relayServer = null;
DisplayChangeRelay.Server? displayChangeRelayServer = null;

// `stopCompanion` is a STABLE wrapper that always reads/invokes whatever `currentStopCompanion`
// holds AT CALL TIME - the same indirection reasoning as disableExternalDisplays/
// enableExternalDisplays/enumerateBluetoothDevices below. WindowsControlHandler captures the
// delegate instance it's given in a readonly field at construction, so if a session-change
// relaunch reassigned a plain `stopCompanion` variable directly, WindowsControlHandler would keep
// calling the STALE closure (the previous session's exePath/session id) forever, and `shutdown`
// would fail to terminate the actually-running companion.
Action currentStopCompanion = () => { };
Action stopCompanion = () =>
{
    currentStopCompanion();

    // AlertHost cleanup rides the same shutdown path (ShutdownCmd and the Ctrl+Break/cancellation
    // finally block both call `stopCompanion`) but, unlike the companion, is NOT closed over a
    // session captured at launch time - AlertActions.ShowAlert re-resolves its target session
    // fresh on every call, so this re-derives the CURRENT active console session the same way
    // rather than trusting whatever session a companion (if any) last launched into. A null result
    // (no interactive session attached right now) is a legitimate no-op, not an error.
    if (SessionActions.GetActiveConsoleSessionId() is uint sessionId)
    {
        int killedAlertHost = SessionActions.TerminateStaleAlertHost(sessionId);
        if (killedAlertHost > 0)
            EventEmitter.EmitInfo($"terminated {killedAlertHost} AlertHost process(es) on shutdown");
    }
};

// How DisplayMonitor's two topology-mutating calls are invoked, and how BluetoothMonitor
// enumerates paired devices. Default: call DisplayActions/BluetoothActions directly (interactive/
// dev run, or no user session yet). When a companion IS launched, these route through its relay
// instead - the only process whose SetDisplayConfig/EnumerateConnected reaches the interactive
// desktop.
//
// enumerateBluetoothDevices/disableExternalDisplays/enableExternalDisplays are each constructed
// ONCE and passed ONCE into BluetoothMonitor/DisplayMonitor's constructors - those constructors
// store the Func<> INSTANCE in a private field, so reassigning these outer variables later would
// do nothing. Instead, each Func<> reads FROM the mutable current*RelayClient variable on EVERY
// invocation, so EnsureCompanionForActiveSession can swap THAT variable on a session change
// without ever touching the Func<> instance the monitors captured at construction.
DisplayCompanionRelay.Client? currentDisplayRelayClient = null;
Func<(bool ok, string? error)> disableExternalDisplays = () =>
    currentDisplayRelayClient is not null
        ? currentDisplayRelayClient.SendCommand("disable")
        : DisplayActions.DisableExternalDisplays();
Func<(bool ok, string? error)> enableExternalDisplays = () =>
    currentDisplayRelayClient is not null
        ? currentDisplayRelayClient.SendCommand("enable")
        : DisplayActions.EnableExternalDisplays();

BluetoothCompanionRelay.Client? currentBluetoothRelayClient = null;
Func<IReadOnlyList<BluetoothActions.BtDevice>> enumerateBluetoothDevices = () =>
    currentBluetoothRelayClient is not null
        ? currentBluetoothRelayClient.Enumerate()
        : BluetoothActions.EnumerateConnected().ToList();

// The session id a --session-companion was last (successfully) launched into, or null when no
// companion is running (no session yet, or this process itself sits in the interactive session
// and hosts hooks locally). EnsureCompanionForActiveSession below compares against this on every
// call to tell a REAL session change from a redundant re-check (WM_WTSSESSION_CHANGE can fire more
// than once for one logical transition), and — on a real change — to know which OLD session's
// companion needs to be torn down before a fresh one is launched into the new session.
uint? companionTargetSession = null;
bool companionEverResolved   = false;

// Re-derives "what session should own clipboard/keyboard/relay duties right now" and acts on any
// change since the last call - called once below and again from OnSessionChanged whenever Windows
// reports a session transition (WM_WTSSESSION_CHANGE, e.g. Fast User Switching or a logout+
// different-user-login).
//
// SCOPE: this binary's OWN session never changes in the real deployment (LocalSystem in Session 0).
// The only transition this repairs is "a companion was already launched into some session, and the
// active console session is now a DIFFERENT one" - it tears down the stale companion, launches a
// fresh one, rebuilds the relay clients, and forces a fresh compliance re-check (the actual fix for
// stale enforcement after a user switch). It does NOT rebuild this primary's own local
// clipboardMonitor/keyboardHook live - that would require reconstructing objects on the
// message-pump thread, out of scope here. A call while runClipboardLocally is still true just
// re-runs the startup decision idempotently.
void EnsureCompanionForActiveSession()
{
    var activeSession = SessionActions.GetActiveConsoleSessionId();

    if (companionEverResolved && activeSession == companionTargetSession)
        return; // nothing changed since the last resolution - cheap no-op for bursty notifications

    var previousTargetSession = companionTargetSession;
    companionEverResolved = true;
    companionTargetSession = activeSession;

    if (activeSession is null)
    {
        // No user logged on (yet, or anymore). Nothing to cross into - matches the original
        // startup message. An existing companion from a still-remembered previous session is not
        // proactively torn down here; it will be replaced the next time a real session resolves,
        // via the branch below (which always terminates whatever the PREVIOUS session was).
        EventEmitter.EmitInfo("no interactive session yet — clipboard/keyboard protection inactive until a user logs on");
        return;
    }

    if (SessionActions.IsRunningInSession(activeSession.Value))
    {
        // Already inside the interactive session (manual/dev run — same fast path
        // AlertActions.ShowAlert uses). The local hooks reach a real desktop; no companion needed.
        // Per scope above, this never tears down/rebuilds those local hooks live.
        return;
    }

    // Real Session-0-style deployment path: a companion belongs in `activeSession`. Host the relay
    // servers the FIRST time this path is ever taken - a prior successful run already has them up
    // and listening, and they are session-agnostic (any companion, in any session, dials back into
    // the SAME pipe names), so they are never recreated on a mere session change.
    relayServer ??= new ClipboardCompanionRelay.Server();
    displayChangeRelayServer ??= new DisplayChangeRelay.Server(() =>
    {
        if (!windowReady.IsSet) return; // msgThread hasn't published displayMonitor yet - benign no-op, same happens-before gate ApplyPolicy/RestoreDevices/ReevaluateClipboard rely on
        displayMonitor?.NotifyExternalDisplayChange();
    });

    string? exePath = Environment.ProcessPath; // this process's own executable path

    // A companion from an OLDER session must be stopped explicitly by ITS OWN session id - the
    // stale-leftover kill below only targets `activeSession` (the NEW one), so a companion still
    // running in the PREVIOUS session would otherwise never be found and killed. AlertHost gets the
    // same treatment (see SessionActions.TerminateStaleAlertHost), gated only on the session-change
    // condition, not on `exePath` (a companion-specific path AlertHost cleanup doesn't need).
    if (previousTargetSession is not null && previousTargetSession.Value != activeSession.Value)
    {
        if (exePath is not null)
        {
            int killedOld = SessionActions.TerminateCompanionProcesses(previousTargetSession.Value, exePath);
            if (killedOld > 0)
                EventEmitter.EmitInfo($"terminated {killedOld} companion process(es) from the previous session ({previousTargetSession.Value}) after a session change to {activeSession.Value}");
        }

        int killedOldAlertHost = SessionActions.TerminateStaleAlertHost(previousTargetSession.Value);
        if (killedOldAlertHost > 0)
            EventEmitter.EmitInfo($"terminated {killedOldAlertHost} AlertHost process(es) from the previous session ({previousTargetSession.Value}) after a session change to {activeSession.Value}");
    }

    // Kill any companion left behind by a prior primary generation BEFORE launching a fresh one -
    // otherwise it lingers indefinitely, still independently enforcing whatever policy was
    // current at its own startup (see SessionActions.TerminateCompanionProcesses for the real
    // production bug this fixes). Same reasoning extends to a stale AlertHost left in the NEW
    // session from an earlier run.
    if (exePath is not null)
    {
        int staleKilled = SessionActions.TerminateCompanionProcesses(activeSession.Value, exePath);
        if (staleKilled > 0)
            EventEmitter.EmitInfo($"terminated {staleKilled} stale companion process(es) left over from a prior run before launching a fresh one");
    }

    int staleAlertHostKilled = SessionActions.TerminateStaleAlertHost(activeSession.Value);
    if (staleAlertHostKilled > 0)
        EventEmitter.EmitInfo($"terminated {staleAlertHostKilled} stale AlertHost process(es) left over from a prior run before launching a fresh one");

    var (ok, error) = exePath is null
        ? (false, "cannot resolve own executable path (Environment.ProcessPath was null)")
        : SessionActions.LaunchIntoSession(activeSession.Value, exePath, "--session-companion");

    if (ok)
    {
        // Companion now owns clipboard/keyboard exclusively — do NOT also build inert local hooks
        // on this Session-0 desktop.
        runClipboardLocally = false;

        // The companion now owns clipboard/keyboard exclusively - tear down any local hooks this
        // primary built earlier (e.g. at startup before a session existed yet). Both Dispose() calls
        // are simple, thread-safe operations safe to call from any thread (KeyboardHook.Dispose ->
        // UnhookWindowsHookEx, ClipboardMonitor.Dispose -> a plain event unsubscribe). This is the
        // fix for "clipboard/screenshot policy never enforces until the service is restarted" -
        // without it, local hooks built before a session existed were never torn down once a
        // companion took over. Safe as a no-op when both are already null.
        if (clipboardMonitor is not null) { clipboardMonitor.Dispose(); clipboardMonitor = null; }
        if (localKeyboardHook is not null) { localKeyboardHook.Dispose(); localKeyboardHook = null; }

        // Dispose the OLD client only AFTER the new one is assigned to the variable the Func<>
        // delegates above read from, so there is never a window where it's null while a companion is
        // genuinely up. A concurrent read racing this swap is safe by construction: both
        // Client.SendCommand and Client.Enumerate wrap their entire body in a broad catch, turning a
        // disposed-pipe ObjectDisposedException into a normal (false, reason)/empty-list result.
        var oldDisplayClient = currentDisplayRelayClient;
        currentDisplayRelayClient = new DisplayCompanionRelay.Client();
        oldDisplayClient?.Dispose();

        var oldBluetoothClient = currentBluetoothRelayClient;
        currentBluetoothRelayClient = new BluetoothCompanionRelay.Client();
        oldBluetoothClient?.Dispose();

        // Closes over THIS launch's own exePath/session so shutdown can clean up the exact
        // companion just started, regardless of how this primary is later told to stop.
        // exePath! is proven non-null here: ok can only be true via the branch of the ternary
        // above that requires exePath is not null to even call LaunchIntoSession.
        string companionExePath = exePath!;
        uint   companionSession = activeSession.Value;
        currentStopCompanion = () =>
        {
            int killed = SessionActions.TerminateCompanionProcesses(companionSession, companionExePath);
            if (killed > 0)
                EventEmitter.EmitInfo($"terminated {killed} companion process(es) on shutdown");
        };

        EventEmitter.EmitInfo(previousTargetSession is null
            ? "clipboard/keyboard companion launched into interactive session"
            : $"clipboard/keyboard companion relaunched into new interactive session {companionSession} after a session change");

        // Anything that connected/reconnected during the transition (e.g. a Bluetooth mouse/
        // keyboard re-pairing with a new device-tree instance in the new session) needs a fresh
        // compliance re-check - this is the actual fix for "policy stops enforcing after a user
        // switch", not just the relay-plumbing swap above. Guarded on windowReady exactly like
        // ApplyPolicy/RestoreDevices: on the very first call (startup), the monitors haven't been
        // published yet and there's nothing to re-check - EnumerateExisting/BlockNonCompliant
        // already run once unconditionally right after windowReady is set, further down.
        if (windowReady.IsSet)
        {
            _ = Task.Run(bluetoothMonitor!.EnumerateExisting);
            _ = Task.Run(displayMonitor!.BlockNonCompliant);
        }
    }
    else
    {
        // Fail safe, not silent: keep whatever was running before this attempt (the (inert-but-
        // present) local hooks on a first-ever failure, or nothing new beyond what already existed
        // on a failed relaunch) so a failure never leaves us worse off than before, and surface the
        // exact reason. Tear down the relay infrastructure so the NEXT call (e.g. the next
        // WM_WTSSESSION_CHANGE) starts clean rather than reusing possibly-stale pipe objects.
        relayServer?.Dispose();
        relayServer = null;
        displayChangeRelayServer?.Dispose();
        displayChangeRelayServer = null;

        var staleDisplayClient = currentDisplayRelayClient;
        currentDisplayRelayClient = null;
        staleDisplayClient?.Dispose();

        var staleBluetoothClient = currentBluetoothRelayClient;
        currentBluetoothRelayClient = null;
        staleBluetoothClient?.Dispose();

        EventEmitter.EmitError("clipboard_companion_launch", error ?? "unknown failure");
    }
}

EnsureCompanionForActiveSession();

// Debounce token for WM_WTSSESSION_CHANGE - Windows can deliver several notifications for one
// logical transition (e.g. lock + disconnect + connect during a fast user switch), and each one
// re-derives the active session from scratch (EnsureCompanionForActiveSession's own comparison
// against companionTargetSession is what makes a redundant call cheap) - the debounce here just
// avoids firing several overlapping attempts in the same instant.
CancellationTokenSource? sessionChangeCts = null;

void OnSessionChanged()
{
    try
    {
        // Same cancel-then-dispose-atomically ordering as DisplayMonitor.OnDisplayChanged: cancel
        // and dispose the PREVIOUS token here, before starting a new one, never in a continuation.
        // Getting this ordering wrong is exactly what caused a real ObjectDisposedException there
        // that killed the whole process (see AGENTS.md section 10) - mirror the proven pattern
        // rather than re-deriving it.
        var previous = sessionChangeCts;
        sessionChangeCts = null;
        if (previous is not null)
        {
            try { previous.Cancel(); } catch (ObjectDisposedException) { }
            previous.Dispose();
        }

        var cts = new CancellationTokenSource();
        sessionChangeCts = cts;

        Task.Delay(800, cts.Token).ContinueWith(t =>
        {
            // Never call EnsureCompanionForActiveSession directly on the message-pump thread - it
            // does blocking Win32 calls (CreateProcessAsUser) and ~2s relay-client connects, which
            // would stall every other monitor's message handling for that whole duration.
            if (!t.IsCanceled)
                _ = Task.Run(EnsureCompanionForActiveSession);
        }, TaskScheduler.Default);
    }
    catch (Exception ex)
    {
        // WM_WTSSESSION_CHANGE runs on the message-loop thread - never let this throw, or the
        // unhandled exception tears down the whole native process (same reasoning as
        // DisplayMonitor.OnDisplayChanged).
        EventEmitter.EmitError("session_change", ex.Message);
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
        window.SessionChanged += OnSessionChanged; // WM_WTSSESSION_CHANGE -> debounced re-check of the active session (see EnsureCompanionForActiveSession above)
        usbMonitor       = new UsbMonitor(window, whitelist, blacklist, disabled, storagePoll);
        bluetoothMonitor = new BluetoothMonitor(window, whitelist, blacklist, disabled, enumerateBluetoothDevices);
        displayMonitor   = new DisplayMonitor(window, whitelist, blacklist, disableExternalDisplays, enableExternalDisplays);
        networkMonitor   = new NetworkMonitor(window, whitelist, blacklist, disabled);

        using var usbMon           = usbMonitor;
        using var btMon            = bluetoothMonitor;
        using var dispMon          = displayMonitor;
        using var netMon           = networkMonitor;

        // Clipboard listener + keyboard hook run locally ONLY when no companion was launched
        // (interactive session, or no session to cross into yet). Otherwise the companion owns
        // them and these stay null. Assigned to the OUTER clipboardMonitor/localKeyboardHook
        // variables (not disposed via `using` here) so EnsureCompanionForActiveSession can tear
        // them down later if a companion successfully takes over after this one-time check ran -
        // final disposal on process shutdown happens in the outer finally block instead.
        if (runClipboardLocally)
        {
            clipboardMonitor  = new ClipboardMonitor(window, clipboardWhitelist, clipboardBlacklist, clipboardCutHint);
            localKeyboardHook = new KeyboardHook(clipboardWhitelist, clipboardBlacklist, clipboardCutHint, screenshotBlockPolicy);
        }

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
// Null-tolerant: clipboardMonitor is legitimately null whenever a companion owns clipboard duties
// instead - in that case the companion reevaluates its own policy independently (its own
// FileSystemWatcher-driven cbWhitelist.Reload()/cbBlacklist.Reload() in the --session-companion
// branch), so a no-op here is correct, not a gap.
void ReevaluateClipboard() => clipboardMonitor?.ApplyPolicy();

var dispatcher = new CommandDispatcher(
    cancellationToken: cts.Token,
    clipboard:         new WindowsClipboardHandler(),
    usbStorage:        new WindowsUsbStorageHandler(
        blockAlreadyConnectedStorage: () => usbMonitor!.BlockAlreadyConnectedStorage(),
        restoreStorageDisabled:       () => usbMonitor!.RestoreStorageDisabled(),
        startStoragePoll:             storagePoll.Start,
        stopStoragePoll:              storagePoll.Stop),
    usbDevice:         new WindowsUsbDeviceHandler(),
    usbProtection:     new WindowsUsbProtectionHandler(whitelist, blacklist,
        applyPolicy:    ApplyPolicy,
        restoreDevices: RestoreDevices),
    clipboardProtection: new WindowsClipboardProtectionHandler(clipboardWhitelist, clipboardBlacklist,
        reevaluate: ReevaluateClipboard),
    control:           new WindowsControlHandler(stopCompanion,
        whitelist, blacklist, clipboardWhitelist, clipboardBlacklist, screenshotBlockPolicy,
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
    // Covers the case where this primary ran with local hooks for its entire lifetime (no
    // companion ever took over) - a no-op if EnsureCompanionForActiveSession already disposed
    // these earlier.
    clipboardMonitor?.Dispose();
    localKeyboardHook?.Dispose();
    relayServer?.Dispose(); // stop the companion relay listener, if one was hosted
    displayChangeRelayServer?.Dispose(); // stop the display-change-notify listener, if one was hosted
    storagePoll.Dispose(); // stop the driverless-storage poll timer, if it was running
    currentDisplayRelayClient?.Dispose(); // release the display relay client, if one was constructed
    currentBluetoothRelayClient?.Dispose(); // release the bluetooth relay client, if one was constructed
    // Bounded cancel+dispose of the session-change debounce token, same atomic ordering as
    // DisplayMonitor.Dispose's own _displayChangeCts cleanup - a pending WM_WTSSESSION_CHANGE
    // debounce must not outlive shutdown.
    var pendingSessionChangeCts = sessionChangeCts;
    sessionChangeCts = null;
    if (pendingSessionChangeCts is not null)
    {
        try { pendingSessionChangeCts.Cancel(); } catch (ObjectDisposedException) { }
        pendingSessionChangeCts.Dispose();
    }
    // Covers the Ctrl+Break/cancellation shutdown route - WindowsControlHandler's ShutdownCmd
    // handler covers the `shutdown` stdin command route. A no-op if no companion was launched.
    stopCompanion();
    EventEmitter.EmitInfo("shutdown");
}
