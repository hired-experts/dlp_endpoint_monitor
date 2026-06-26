using ClipboardUsbMonitor.Core;
using ClipboardUsbMonitor.Handlers.Windows;
using ClipboardUsbMonitor.Monitors;

// Schema export mode — exits immediately; must run before any stdout writes.
if (args.Contains("--schema"))
{
#pragma warning disable IL2026 // SchemaExporter is intentionally reflection-based; never runs in trimmed builds.
    SchemaExporter.Export(Console.Out);
#pragma warning restore IL2026
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
var whitelist = new UsbWhitelist();
var blacklist = new UsbBlacklist();

// Conflict guard: if both lists were enabled on disk (e.g. direct file edit), disable both.
// The client can query usb_protection_status to understand the current state.
if (whitelist.IsEnabled && blacklist.IsEnabled)
{
    whitelist.SetEnabled(false);
    blacklist.SetEnabled(false);
    EventEmitter.EmitError("startup_conflict",
        "Both whitelist and blacklist were enabled — both disabled. Use usb_protection_status to check.");
}

// Signal set once the message window is ready
var windowReady = new ManualResetEventSlim(false);
MessageWindow? window     = null;
UsbMonitor?    usbMonitor = null;

// ── Message loop thread ───────────────────────────────────────────────────────
// All Win32 message-based components (clipboard listener, USB notifications,
// keyboard hook) must live on the SAME thread that runs the message pump.
// That thread must be STA.

var msgThread = new Thread(() =>
{
    try
    {
        window     = new MessageWindow();
        usbMonitor = new UsbMonitor(window, whitelist, blacklist);

        using var clipboardMonitor = new ClipboardMonitor(window);
        using var monitor          = usbMonitor;
        using var keyboardHook     = new KeyboardHook();

        windowReady.Set(); // unblock main thread — usbMonitor is set before this
        EventEmitter.EmitInfo("ready");

        // Enumerate devices already connected before we started.
        // Runs on a ThreadPool thread so the message loop is never blocked.
        _ = Task.Run(usbMonitor.EnumerateExisting);

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


var dispatcher = new CommandDispatcher(
    cancellationToken: cts.Token,
    clipboard:         new WindowsClipboardHandler(),
    usbStorage:        new WindowsUsbStorageHandler(),
    usbDevice:         new WindowsUsbDeviceHandler(),
    usbProtection:     new WindowsUsbProtectionHandler(whitelist, blacklist, () => usbMonitor!.BlockNonCompliant()),
    control:           new WindowsControlHandler());

try
{
    await dispatcher.RunAsync();
}
catch (OperationCanceledException) { }
finally
{
    window?.Stop(); // posts WM_DESTROY → message loop exits
    EventEmitter.EmitInfo("shutdown");
}
