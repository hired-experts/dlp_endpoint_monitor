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

        _singletonMutex = new Mutex(initiallyOwned: true, SingletonMutexName, out bool createdNew);
        if (!createdNew)
        {
            // Another instance already owns this session's AlertHost - hand off the request
            // (if any) over the pipe and exit immediately rather than starting a second
            // pipe server for the same session.
            if (initialAlert is not null)
                PipeTransport.TrySendToOwner(initialAlert);
            Shutdown(0);
            return;
        }

        _queue = new AlertQueue(ShowAlertWindow);
        if (initialAlert is not null)
            _queue.Enqueue(initialAlert);

        _pipeServer = new PipeTransport.Server(_queue.Enqueue);
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
    // Dispatcher.Invoke (not InvokeAsync/BeginInvoke) specifically so the call blocks the
    // dispatch loop's thread until ShowDialog() returns. That is what makes "never two windows
    // visible at once" hold: the loop cannot dequeue the next pending alert until this one has
    // actually been dismissed.
    static void ShowAlertWindow(AlertRequest request)
    {
        Dispatcher dispatcher = Application.Current!.Dispatcher;
        dispatcher.Invoke(() =>
        {
            Window window = request.Type switch
            {
                AlertType.Toast => new ToastWindow(request),
                // FullScreen, and any future/unmapped AlertType, fails safe to the hardest-to-miss
                // window rather than silently not showing anything.
                _ => new FullScreenWindow(request),
            };
            window.ShowDialog();
        });
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
