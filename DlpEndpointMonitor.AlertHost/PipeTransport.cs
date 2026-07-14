using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using DlpEndpointMonitor.AlertContracts;

namespace DlpEndpointMonitor.AlertHost;

/// <summary>
/// Newline-delimited JSON transport over the shared <see cref="AlertPipe"/> name - mirrors the
/// stdin/stdout newline-JSON convention already used between DlpEndpointMonitor and its Node
/// agent. The session's singleton owner runs <see cref="Server"/>; every later AlertHost
/// invocation in that session is a one-shot <see cref="TrySendToOwner"/> client that writes
/// exactly one line and exits.
/// </summary>
static class PipeTransport
{
    const int ConnectTimeoutMs = 2000;

    // A connected client that never finishes sending its one line (frozen, killed mid-write)
    // must not block this pipe forever - only one instance is accepted at a time, so a stalled
    // client would otherwise starve every later alert in the session. 5s gives a slow/loaded
    // machine slack while still bounding the wait.
    const int ReadInactivityTimeoutMs = 5000;

    /// <summary>
    /// Sends one AlertRequest to whichever AlertHost instance currently owns
    /// <paramref name="sessionId"/>'s pipe. Returns false (never throws) if no owner is
    /// listening - the caller decides what that means, it is not necessarily an error (e.g. no
    /// owner has started yet).
    /// </summary>
    public static bool TrySendToOwner(AlertRequest request, uint sessionId)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", AlertPipe.NameFor(sessionId), PipeDirection.Out);
            client.Connect(ConnectTimeoutMs);
            using var writer = new StreamWriter(client) { AutoFlush = true };
            writer.WriteLine(JsonSerializer.Serialize(request, AlertJsonContext.Default.AlertRequest));
            return true;
        }
        catch
        {
            // Not fatal - just means no owner is currently listening on this session's pipe.
            return false;
        }
    }

    /// <summary>
    /// Hosts the session-owner side of the pipe: accepts one client connection at a time,
    /// reads newline-delimited JSON AlertRequest lines, and forwards each parsed request to
    /// the callback given at construction, until <see cref="Dispose"/> is called.
    /// </summary>
    public sealed class Server : IDisposable
    {
        readonly Action<AlertRequest> _onReceived;
        readonly string _pipeName;
        readonly CancellationTokenSource _cts = new();
        readonly Task _acceptLoop;

        public Server(Action<AlertRequest> onReceived, uint sessionId)
        {
            _onReceived = onReceived;
            _pipeName = AlertPipe.NameFor(sessionId);
            _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
        }

        async Task AcceptLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                NamedPipeServerStream pipe;
                try
                {
                    pipe = new NamedPipeServerStream(
                        _pipeName, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                }
                catch (Exception ex)
                {
                    // Cannot create the pipe at all (e.g. name collision from a stale handle) -
                    // no client can ever reach us; log and stop trying rather than spin forever.
                    Console.Error.WriteLine($"[AlertHost] failed to create pipe server: {ex.Message}");
                    return;
                }

                try
                {
                    await pipe.WaitForConnectionAsync(token).ConfigureAwait(false);
                    await ReadLinesAsync(pipe, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Shutting down - fall through and let the while-condition exit the loop.
                }
                catch (IOException)
                {
                    // Client disconnected mid-read - not fatal, loop back for the next client.
                }
                finally
                {
                    pipe.Dispose();
                }
            }
        }

        async Task ReadLinesAsync(NamedPipeServerStream pipe, CancellationToken token)
        {
            using var reader = new StreamReader(pipe);
            while (!token.IsCancellationRequested)
            {
                using var readTimeout = new CancellationTokenSource(ReadInactivityTimeoutMs);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, readTimeout.Token);

                string? line;
                try
                {
                    line = await reader.ReadLineAsync(linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    // Timed out waiting on this client specifically (not a server shutdown) -
                    // drop the connection and free the pipe slot for the next caller.
                    Console.Error.WriteLine("[AlertHost] dropping stalled pipe client: no line received within timeout");
                    return;
                }
                if (line is null) break; // client disconnected
                if (line.Length == 0) continue;

                AlertRequest? request;
                try
                {
                    request = JsonSerializer.Deserialize(line, AlertJsonContext.Default.AlertRequest);
                }
                catch (JsonException ex)
                {
                    // One malformed line must never take down the server's accept loop.
                    Console.Error.WriteLine($"[AlertHost] discarding malformed alert line: {ex.Message}");
                    continue;
                }
                if (request is not null)
                    _onReceived(request);
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _acceptLoop.Wait(TimeSpan.FromSeconds(2)); } catch { /* best-effort on shutdown */ }
            _cts.Dispose();
        }
    }
}
