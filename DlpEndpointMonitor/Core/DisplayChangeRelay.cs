using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace DlpEndpointMonitor.Core;

/// <summary>
/// Newline-delimited, fire-and-forget transport that tells the PRIMARY "a display topology
/// change just happened" from the COMPANION (running in the interactive user's session). Same
/// direction and shape as <see cref="ClipboardCompanionRelay"/> (companion -> primary), but a
/// different pipe/payload: WM_DISPLAYCHANGE is a session/desktop-scoped broadcast, so a primary
/// running headless in Session 0 never receives it on its own message window - unlike
/// WM_DEVICECHANGE (device arrival/removal), which is systemwide/PnP and always reaches the
/// primary regardless of session. Without this relay, a primary-in-Session-0 deployment only ever
/// re-checks display compliance on physical monitor plug/unplug, never on a pure Win+P
/// projection-mode switch of an already-connected monitor - a real reported gap.
///
/// The PRIMARY hosts <see cref="Server"/> (mirrors ClipboardCompanionRelay's direction); the
/// COMPANION is a <see cref="Client"/>. The payload carries no data - any non-empty line just
/// means "re-check now"; <see cref="DisplayMonitor.NotifyExternalDisplayChange"/> replays the
/// exact same 800ms-debounced BlockNonCompliant() a local WM_DISPLAYCHANGE would have triggered.
/// </summary>
static class DisplayChangeRelay
{
    public const string PipeName = "DlpEndpointMonitor.DisplayChangeRelay";

    // Confirmed in production (real MSI install, DlpAgent service): the companion's connect
    // attempt failed on EVERY retry with UnauthorizedAccessException, not the TimeoutException a
    // not-yet-listening server would produce - a genuine cross-session pipe ACL denial, not a
    // race. The primary's own service account does not always yield a default pipe DACL the
    // interactive session's companion can open a handle against. Explicitly granting Authenticated
    // Users read/write closes this regardless of which specific account hosts the primary.
    static PipeSecurity BuildPipeSecurity()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite,
            AccessControlType.Allow));
        return security;
    }

    /// <summary>
    /// Primary-side listener: accepts ONE companion connection at a time, and invokes
    /// <paramref name="onDisplayChanged"/> once per non-empty line received. If the companion
    /// disconnects it loops back and accepts a new one (the companion may restart).
    /// </summary>
    public sealed class Server : IDisposable
    {
        readonly Action _onDisplayChanged;
        readonly CancellationTokenSource _cts = new();
        readonly Task _acceptLoop;
        readonly string _pipeName;

        // pipeName defaults to the shared production PipeName - Program.cs's construction is
        // unaffected. The override exists solely so a test can bind an isolated, uniquely-named
        // pipe instead of colliding with an already-running companion/primary on the same machine.
        public Server(Action onDisplayChanged, string? pipeName = null)
        {
            _onDisplayChanged = onDisplayChanged;
            _pipeName = pipeName ?? PipeName;
            _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
        }

        async Task AcceptLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                NamedPipeServerStream pipe;
                try
                {
                    pipe = NamedPipeServerStreamAcl.Create(
                        _pipeName, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
                        0, 0, BuildPipeSecurity());
                }
                catch (Exception ex)
                {
                    // Cannot create the pipe at all (e.g. a stale handle still holds the name) -
                    // no companion can ever reach us; report once and stop rather than spin.
                    EventEmitter.EmitError("display_change_relay", $"failed to create relay pipe server: {ex.Message}");
                    return;
                }

                try
                {
                    await pipe.WaitForConnectionAsync(token).ConfigureAwait(false);

                    // Diagnostic only, and deliberately asymmetric with other relays: this pipe's
                    // client-side connect has failed in the field with no clear root cause yet
                    // (server-not-ready-in-time vs. a genuine cross-session/ACL denial look
                    // identical from the client alone). Seeing (or never seeing) this line is what
                    // tells them apart - if the client reports "could not connect" and this line
                    // NEVER appears, the client's connect attempt never reached the server at all.
                    EventEmitter.EmitInfo("display change relay: companion connected");

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
                // Deliberately NO read-inactivity timeout here, unlike ClipboardCompanionRelay
                // (which correctly treats prolonged silence in its steady clipboard/keyboard event
                // stream as a stalled client). This relay's entire job is to sit connected and
                // silent until a rare, possibly far-future display change actually happens -
                // silence is the normal, healthy state, not a symptom of a stalled companion. A
                // rolling timeout here (copied from ClipboardCompanionRelay in an earlier pass)
                // killed every connection ~5s after it connected, long before any real
                // WM_DISPLAYCHANGE could plausibly occur - confirmed in production: the client's
                // eventual Notify() call then failed with "Pipe is broken" because the server had
                // already dropped it. Only the overall shutdown token (`token`) can end this wait.
                string? line;
                try
                {
                    line = await reader.ReadLineAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return; // server shutting down
                }
                if (line is null) break;        // companion disconnected
                if (line.Length == 0) continue; // skip blank keep-alive/framing lines

                try
                {
                    _onDisplayChanged();
                }
                catch (Exception ex)
                {
                    // The callback (DisplayMonitor.NotifyExternalDisplayChange) is not expected to
                    // throw, but a surprise failure here must not kill the accept loop - one missed
                    // re-check is far better than losing this relay for the rest of the process.
                    EventEmitter.EmitError("display_change_relay", $"onDisplayChanged callback failed: {ex.Message}");
                }
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
    /// Companion-side sender: connects to the primary's pipe ONCE at construction. Notifying the
    /// primary is best-effort - the companion has no local display-blocking role of its own (the
    /// compliance decision stays in DisplayMonitor on the primary), so a failed connect just means
    /// this deployment falls back to arrival-only blocking, surfaced via <see cref="IsConnected"/>
    /// rather than throwing.
    /// </summary>
    public sealed class Client : IDisposable
    {
        // Same small/bounded connect retry as ClipboardCompanionRelay.Client - the primary starts
        // its relay server just before spawning the companion, so a connection normally succeeds
        // on the first or second try. 10 x 200ms = ~2s.
        const int ConnectMaxAttempts = 10;
        static readonly TimeSpan ConnectRetryDelay = TimeSpan.FromMilliseconds(200);
        const int ConnectTimeoutMs = 200;

        readonly NamedPipeClientStream? _pipe;
        readonly StreamWriter? _writer;
        readonly Lock _writeLock = new();

        // A dropped connection is worth reporting exactly once, not on every subsequent notify.
        bool _writeFailureReported;

        // pipeName defaults to the shared production PipeName - see Server's matching parameter
        // for why a test needs to override this.
        public Client(string? pipeName = null)
        {
            var pipe = new NamedPipeClientStream(".", pipeName ?? PipeName, PipeDirection.Out, PipeOptions.Asynchronous);

            // Captures the LAST attempt's exception - a bare `catch { return false; }` here would
            // discard it entirely, leaving no way to tell "server just wasn't listening yet"
            // (TimeoutException) apart from a genuine cross-session/ACL denial
            // (UnauthorizedAccessException) or anything else. RetryPolicy.Execute's Func<bool>
            // signature is shared with other working relay clients, so this is captured locally
            // rather than widening that shared utility for one caller.
            Exception? lastError = null;
            bool connected = RetryPolicy.Execute(() =>
            {
                try
                {
                    pipe.Connect(ConnectTimeoutMs);
                    return true;
                }
                catch (Exception ex)
                {
                    lastError = ex;
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
                ConnectError = lastError is not null
                    ? $"{lastError.GetType().Name}: {lastError.Message}"
                    : "unknown failure (no exception captured)";
            }
        }

        /// <summary>
        /// True only if the one-shot connect at construction succeeded.
        /// </summary>
        public bool IsConnected { get; }

        /// <summary>
        /// The underlying exception from the LAST connect retry attempt, when <see cref="IsConnected"/>
        /// is false - diagnostic only, e.g. "TimeoutException: ..." (server wasn't listening in time)
        /// vs "UnauthorizedAccessException: ..." (a genuine cross-session/ACL denial). Null when
        /// IsConnected is true.
        /// </summary>
        public string? ConnectError { get; }

        /// <summary>
        /// Tells the primary "a display change just happened here". Safe no-op (never throws) if
        /// not connected or if the pipe write itself fails (e.g. the primary died). The first
        /// post-connection write failure is reported once via EmitError; later ones are swallowed.
        /// </summary>
        public void Notify()
        {
            if (!IsConnected || _writer is null)
            {
                return;
            }

            lock (_writeLock)
            {
                try
                {
                    _writer.WriteLine("changed");
                }
                catch (Exception ex)
                {
                    if (!_writeFailureReported)
                    {
                        _writeFailureReported = true;
                        EventEmitter.EmitError("display_change_relay", $"relay write to primary failed, display-change reporting lost: {ex.Message}");
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
