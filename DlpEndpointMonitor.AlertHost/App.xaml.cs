using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using DlpEndpointMonitor.AlertContracts;
using DlpEndpointMonitor.AlertHost.Windows;

namespace DlpEndpointMonitor.AlertHost;

public partial class App : Application
{
    // Session-scoped (no "Global\" prefix) - each interactive session must get its own owner,
    // not one singleton for the whole machine, since AlertHost runs once per logged-in user.
    const string SingletonMutexName = "DlpEndpointMonitor.AlertHost.Singleton";
    const string InitialAlertArgPrefix = "--initial-alert=";

    Mutex? _singletonMutex;
    AlertQueue? _queue;
    PipeTransport.Server? _pipeServer;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Must run before the mutex/pipe/queue are touched - mirrors Program.cs's own
        // --schema-first discipline so a schema dump never races a real singleton instance.
        if (Array.IndexOf(e.Args, "--schema") >= 0)
        {
#pragma warning disable IL2026 // SchemaExporter is intentionally reflection-based; never runs in trimmed builds.
            SchemaExporter.Export(Console.Out);
#pragma warning restore IL2026
            Shutdown(0);
            return;
        }

        AlertRequest? initialAlert = ParseInitialAlertArg(e.Args);

        // This process's own SessionId always matches whatever session the launcher
        // (AlertActions.ShowAlert) targeted - either launched directly by a process already in
        // that session, or launched into it via CreateProcessAsUser - so it doubles as the pipe
        // scope with no extra Win32 call needed (see AlertPipe's doc comment for why the pipe
        // must be session-scoped at all).
        uint sessionId = (uint)Process.GetCurrentProcess().SessionId;

        _singletonMutex = new Mutex(initiallyOwned: true, SingletonMutexName, out bool createdNew);
        if (!createdNew)
        {
            // Another instance already owns this session's AlertHost - hand off the request
            // (if any) over the pipe and exit immediately rather than starting a second
            // pipe server for the same session.
            if (initialAlert is not null)
                PipeTransport.TrySendToOwner(initialAlert, sessionId);
            Shutdown(0);
            return;
        }

        _queue = new AlertQueue(ShowAlertWindow);
        if (initialAlert is not null)
            _queue.Enqueue(initialAlert);

        _pipeServer = new PipeTransport.Server(_queue.Enqueue, sessionId);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _pipeServer?.Dispose();
        _queue?.Dispose();
        // Only release a mutex this instance actually owns - the one-shot client path above
        // never enters this branch of OnStartup, so _singletonMutex there is still the mutex
        // object itself (ownership was never acquired) and ReleaseMutex would throw; guard
        // with the same createdNew condition by simply checking whether we ever built a queue.
        if (_queue is not null)
            _singletonMutex?.ReleaseMutex();
        _singletonMutex?.Dispose();
        base.OnExit(e);
    }

    // AlertQueue's dispatch loop runs on its own background Task, not the WPF dispatcher
    // thread - every window must be built and shown from the UI thread, so this hops over via
    // Dispatcher.Invoke, blocking the dispatch loop's thread until every per-monitor window for
    // this alert has been dismissed. That is what makes "only one alert shown at a time
    // system-wide" hold - one alert can now occupy several windows at once (one per connected
    // monitor), but the loop still cannot dequeue the next pending alert until all of them close.
    static void ShowAlertWindow(AlertRequest request)
    {
        Dispatcher dispatcher = Application.Current!.Dispatcher;
        dispatcher.Invoke(() =>
        {
            IReadOnlyList<MonitorHelper.MonitorInfo> monitors = MonitorHelper.GetAll();
            var windows = new List<Window>(monitors.Count);
            foreach (MonitorHelper.MonitorInfo monitor in monitors)
            {
                try
                {
                    Window window = request.Type switch
                    {
                        AlertType.Toast => new ToastWindow(request, monitor.WorkArea),
                        // FullScreen, and any future/unmapped AlertType, fails safe to the
                        // hardest-to-miss window rather than silently not showing anything.
                        _ => new FullScreenWindow(request, monitor.Bounds),
                    };
                    windows.Add(window);
                }
                catch (Exception ex)
                {
                    // A single monitor's window failing to construct (e.g. it was unplugged
                    // between MonitorHelper.GetAll() and here) must not prevent the alert from
                    // showing on every OTHER still-good monitor.
                    Console.Error.WriteLine($"[AlertHost] failed to construct alert window for a monitor: {ex.Message}");
                }
            }

            if (windows.Count == 0)
                return;

            ShowAllAndWaitForDismiss(windows);
        });
    }

    // Shows every per-monitor window for one alert at once (Show(), not ShowDialog() - there is
    // no single window left to be modal against) and blocks via a manually-pumped DispatcherFrame,
    // the same mechanism ShowDialog uses internally, until every window has closed. Dismissing ANY
    // one instance (click, close button, or its own timer) immediately closes every other instance
    // too - they all represent one single alert, not independent ones, so acknowledging it on
    // whichever monitor the user is looking at should acknowledge it everywhere.
    static void ShowAllAndWaitForDismiss(List<Window> windows)
    {
        var frame = new DispatcherFrame();
        var closed = new HashSet<Window>();
        // Only windows that actually reached a successful Show() go in here - the dismiss-
        // together cascade below must never reference (and never try to Close()) a sibling
        // that failed to show at all, or the "everyone dismisses together" invariant can never
        // be satisfied and a later Close() call could hit an unshown/half-constructed window.
        var shown = new List<Window>(windows.Count);

        try
        {
            foreach (Window window in windows)
            {
                window.Closed += (_, _) =>
                {
                    closed.Add(window);
                    if (closed.Count == shown.Count)
                    {
                        frame.Continue = false;
                        return;
                    }

                    foreach (Window other in shown)
                    {
                        if (!closed.Contains(other))
                            other.Close(); // re-enters this same handler for `other`, synchronously
                    }
                };
                window.Show();
                shown.Add(window);
            }
        }
        catch (Exception ex)
        {
            // Show() failing partway through (e.g. a monitor dropped out from under us between
            // construction and Show()) must not orphan whatever windows already made it on
            // screen for this alert - close them now rather than leaving them live with no
            // remaining path to the dismiss-together cascade, and never let the exception
            // reach the message pump.
            Console.Error.WriteLine($"[AlertHost] failed to show alert window on a monitor: {ex.Message}");
            foreach (Window w in shown)
            {
                if (closed.Contains(w)) continue;
                try { w.Close(); }
                catch (Exception closeEx)
                {
                    Console.Error.WriteLine($"[AlertHost] failed to close orphaned alert window: {closeEx.Message}");
                }
            }
            return;
        }

        if (shown.Count == 0)
            return;

        Dispatcher.PushFrame(frame);
    }

    static AlertRequest? ParseInitialAlertArg(string[] args)
    {
        string? encoded = null;
        foreach (string arg in args)
        {
            if (arg.StartsWith(InitialAlertArgPrefix, StringComparison.OrdinalIgnoreCase))
            {
                encoded = arg[InitialAlertArgPrefix.Length..];
                break;
            }
        }
        if (encoded is null) return null;

        try
        {
            byte[] bytes = Convert.FromBase64String(encoded);
            string json = Encoding.UTF8.GetString(bytes);
            return JsonSerializer.Deserialize(json, AlertJsonContext.Default.AlertRequest);
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            // A malformed --initial-alert argument must never crash startup - proceed without
            // an initial alert instead of taking down the whole host process.
            Console.Error.WriteLine($"[AlertHost] ignoring malformed --initial-alert argument: {ex.Message}");
            return null;
        }
    }
}
