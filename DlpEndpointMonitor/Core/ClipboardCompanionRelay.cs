using System.IO;
using System.IO.Pipes;

namespace DlpEndpointMonitor.Core;

/// <summary>
/// Newline-delimited JSON transport that carries fully-formed event lines from a COMPANION
/// process instance (running in the interactive user's session, where clipboard/keyboard
/// hooks actually reach the user) back to the PRIMARY (Session-0) instance, so the primary
/// can relay them out its own stdout to the Node agent verbatim. Mirrors the same
/// newline-JSON-over-named-pipe idiom AlertHost's <c>PipeTransport</c> uses, but for the
/// opposite direction and payload (raw event lines, not AlertRequests).
///
/// The primary hosts <see cref="Server"/>; the single companion is a <see cref="Client"/>.
/// This file is purely the pipe plumbing - who calls <see cref="EventEmitter.EmitRawLine"/>,
/// and what the companion feeds into <see cref="Client.WriteLine"/>, is wired in Program.cs.
/// </summary>
static class ClipboardCompanionRelay
{
    public const string PipeName = "DlpEndpointMonitor.ClipboardCompanionRelay";

    // A connected companion that freezes mid-line must not pin the single pipe slot forever;
    // 5s matches PipeTransport.Server's own stalled-client bound (a companion has at most one
    // primary, so a stalled reader would otherwise block every later event indefinitely).
    const int ReadInactivityTimeoutMs = 5000;

    /// <summary>
    /// Primary-side listener: accepts ONE companion connection at a time, reads its
    /// newline-delimited event lines, and forwards each verbatim to this process's stdout via
    /// <see cref="EventEmitter.EmitRawLine"/>. If the companion disconnects it loops back and
    /// accepts a new one (the companion may restart). One malformed/empty line is skipped, never
    /// fatal to the accept loop.
    /// </summary>
    public sealed class Server : IDisposable
    {
        readonly CancellationTokenSource _cts = new();
        readonly Task _acceptLoop;

        public Server()
        {
            _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
        }

        async Task AcceptLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                NamedPipeServerStream pipe;
                try
                {
                    // maxNumberOfServerInstances=1: exactly one companion reports to us at a time.
                    pipe = new NamedPipeServerStream(
                        PipeName, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                }
                catch (Exception ex)
                {
                    // Cannot create the pipe at all (e.g. a stale handle still holds the name) -
                    // no companion can ever reach us; report once and stop rather than spin.
                    EventEmitter.EmitError("clipboard_companion_relay", $"failed to create relay pipe server: {ex.Message}");
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
                    // Companion disconnected mid-read - not fatal, loop back for the next one.
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
                    // Timed out on this companion specifically (not a server shutdown) - drop it
                    // and free the single pipe slot for a fresh connection.
                    EventEmitter.EmitError("clipboard_companion_relay", "dropping stalled relay client: no line received within timeout");
                    return;
                }
                if (line is null) break;        // companion disconnected
                if (line.Length == 0) continue; // skip blank keep-alive/framing lines

                // The line is already a complete, valid event JSON produced by the companion's
                // own EventEmitter.Emit - relay it verbatim, no deserialize/reserialize round trip.
                EventEmitter.EmitRawLine(line);
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _acceptLoop.Wait(TimeSpan.FromSeconds(2)); } catch { /* best-effort on shutdown */ }
            _cts.Dispose();
        }
    }

    /// <summary>
    /// Companion-side sender: connects to the primary's pipe ONCE at construction. Event
    /// reporting to the primary is best-effort - the companion's LOCAL clipboard/keyboard
    /// enforcement must keep working even if this relay never connects, so a failed connect
    /// surfaces via <see cref="IsConnected"/> (false) instead of throwing.
    /// </summary>
    public sealed class Client : IDisposable
    {
        // Bounded, small connect retry - the primary starts its relay server just before
        // spawning the companion, so a connection normally succeeds on the first or second try;
        // this only rides out the brief window before the server's WaitForConnectionAsync is up.
        // Same small/bounded spirit as ClipboardActions' OpenClipboard retry. 10 x 200ms = ~2s.
        const int ConnectMaxAttempts = 10;
        static readonly TimeSpan ConnectRetryDelay = TimeSpan.FromMilliseconds(200);

        // Per-attempt connect timeout; short so an exhausted set of attempts stays inside the
        // ~2s bound above rather than blocking on a single slow Connect.
        const int ConnectTimeoutMs = 200;

        readonly NamedPipeClientStream? _pipe;
        readonly StreamWriter? _writer;
        readonly Lock _writeLock = new();

        // A dropped connection is worth reporting exactly once, not on every subsequent event -
        // set the first time a write fails after we were connected.
        bool _writeFailureReported;

        public Client()
        {
            var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.Asynchronous);
            bool connected = RetryPolicy.Execute(() =>
            {
                try
                {
                    pipe.Connect(ConnectTimeoutMs);
                    return true;
                }
                catch
                {
                    // No owner listening yet (or at all) - the retry loop decides whether to
                    // try again; a final failure just means IsConnected stays false.
                    return false;
                }
            }, ConnectMaxAttempts, ConnectRetryDelay);

            if (connected)
            {
                _pipe = pipe;
                _writer = new StreamWriter(pipe) { AutoFlush = true };
                IsConnected = true;
            }
            else
            {
                pipe.Dispose();
            }
        }

        /// <summary>
        /// True only if the one-shot connect at construction succeeded. Companion startup code
        /// uses this to decide messaging, never to gate local enforcement.
        /// </summary>
        public bool IsConnected { get; }

        /// <summary>
        /// Writes one already-serialized event JSON line to the primary. Safe no-op (never
        /// throws) if not connected or if the pipe write itself fails (e.g. the primary died).
        /// The first post-connection write failure is reported once via EmitError; later ones
        /// are swallowed so a lost primary is visible exactly once, not on every event.
        /// NOTE: reconnection after a drop is intentionally out of scope for this pass -
        /// re-establishing over the process's whole lifetime is deferred to a follow-up.
        /// </summary>
        public void WriteLine(string json)
        {
            if (!IsConnected || _writer is null)
            {
                return;
            }

            lock (_writeLock)
            {
                try
                {
                    _writer.WriteLine(json);
                }
                catch (Exception ex)
                {
                    if (!_writeFailureReported)
                    {
                        _writeFailureReported = true;
                        EventEmitter.EmitError("clipboard_companion_relay", $"relay write to primary failed, event reporting lost: {ex.Message}");
                    }
                }
            }
        }

        public void Dispose()
        {
            lock (_writeLock)
            {
                try { _writer?.Dispose(); } catch { /* best-effort */ }
                try { _pipe?.Dispose(); } catch { /* best-effort */ }
            }
        }
    }
}
