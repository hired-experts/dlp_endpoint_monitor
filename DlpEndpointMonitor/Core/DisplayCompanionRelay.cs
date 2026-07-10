using System.IO;
using System.IO.Pipes;
using System.Text.Json;

namespace DlpEndpointMonitor.Core;

/// <summary>
/// Newline-delimited request/reply transport for display-topology commands. Unlike
/// <see cref="ClipboardCompanionRelay"/> (companion -> primary, one-way fire-and-forget event
/// lines), this pipe runs the OPPOSITE direction as a request/reply pair: the PRIMARY (which
/// makes the whitelist/blacklist compliance decision in DisplayMonitor) sends a one-word
/// command ("disable"/"enable") and blocks briefly for exactly one reply line before it can
/// emit its own monitor_blocked / monitor_block_failed / monitor_policy_restore event.
///
/// The COMPANION hosts <see cref="Server"/> - it runs in the interactive user's session where a
/// QueryDisplayConfig/SetDisplayConfig topology change actually takes effect, so it is the one
/// that can execute the DisplayActions call. The PRIMARY (which may be a headless Session-0
/// service with no desktop) is a <see cref="Client"/>.
///
/// This file is purely the pipe plumbing. The Server does NOT reference DisplayActions - the
/// actual DisableExternalDisplays/EnableExternalDisplays call is supplied as the
/// <c>executeCommand</c> delegate wired in Program.cs, mirroring how
/// <see cref="ClipboardCompanionRelay.Server"/> only knows <see cref="EventEmitter.EmitRawLine"/>.
/// </summary>
static class DisplayCompanionRelay
{
    // Different name from ClipboardCompanionRelay.PipeName - these are two independent pipes.
    public const string PipeName = "DlpEndpointMonitor.DisplayCompanionRelay";

    // A primary that connects then freezes mid-command must not pin the single pipe slot forever;
    // 5s matches ClipboardCompanionRelay.Server's own stalled-client bound. The command line is
    // one short word, so any wait beyond this means the client is stuck, not merely slow.
    const int ReadInactivityTimeoutMs = 5000;

    /// <summary>
    /// Companion-side listener: accepts ONE primary connection at a time, reads exactly one
    /// command line ("disable" or "enable"), runs it through the caller-supplied
    /// <paramref name="executeCommand"/> delegate, writes back exactly one reply line
    /// (<c>{"ok":true}</c> or <c>{"ok":false,"error":"..."}</c>), then loops back to accept a
    /// fresh connection for the next command. Commands are infrequent, so a connection is not held
    /// open indefinitely - one request/reply per connection is the simplest, most robust shape.
    /// </summary>
    public sealed class Server : IDisposable
    {
        readonly CancellationTokenSource _cts = new();
        readonly Task _acceptLoop;
        readonly Func<string, (bool ok, string? error)> _executeCommand;

        public Server(Func<string, (bool ok, string? error)> executeCommand)
        {
            _executeCommand = executeCommand;
            _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
        }

        async Task AcceptLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                NamedPipeServerStream pipe;
                try
                {
                    // maxNumberOfServerInstances=1: exactly one primary drives us at a time.
                    // PipeDirection.InOut because this is request (read) then reply (write).
                    pipe = new NamedPipeServerStream(
                        PipeName, PipeDirection.InOut, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                }
                catch (Exception ex)
                {
                    // Cannot create the pipe at all (e.g. a stale handle still holds the name) -
                    // no primary can ever reach us; report once and stop rather than spin.
                    EventEmitter.EmitError("display_companion_relay", $"failed to create relay pipe server: {ex.Message}");
                    return;
                }

                try
                {
                    await pipe.WaitForConnectionAsync(token).ConfigureAwait(false);
                    await HandleRequestAsync(pipe, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Shutting down - fall through and let the while-condition exit the loop.
                }
                catch (IOException)
                {
                    // Primary disconnected mid-exchange - not fatal, loop back for the next one.
                }
                finally
                {
                    pipe.Dispose();
                }
            }
        }

        async Task HandleRequestAsync(NamedPipeServerStream pipe, CancellationToken token)
        {
            using var reader = new StreamReader(pipe);
            using var writer = new StreamWriter(pipe) { AutoFlush = true };

            using var readTimeout = new CancellationTokenSource(ReadInactivityTimeoutMs);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, readTimeout.Token);

            string? line;
            try
            {
                line = await reader.ReadLineAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                // Primary connected but never sent its command within the bound - drop it and free
                // the single pipe slot for a fresh connection.
                EventEmitter.EmitError("display_companion_relay", "dropping stalled relay client: no command received within timeout");
                return;
            }

            if (line is null || line.Length == 0)
            {
                // Empty/closed connection with no command - nothing to reply to, just loop back.
                return;
            }

            // Execute on this accept-loop thread and reply synchronously. The delegate owns any
            // failure semantics; we only translate its (ok, error) tuple onto the wire.
            (bool ok, string? error) result;
            try
            {
                result = _executeCommand(line);
            }
            catch (Exception ex)
            {
                // The delegate is expected to return (false, reason) rather than throw, but a
                // surprise throw must still yield a well-formed reply, not kill the accept loop.
                result = (false, ex.Message);
            }

            await writer.WriteLineAsync(BuildReply(result.ok, result.error)).ConfigureAwait(false);
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _acceptLoop.Wait(TimeSpan.FromSeconds(2)); } catch { /* best-effort on shutdown */ }
            _cts.Dispose();
        }
    }

    /// <summary>
    /// Primary-side sender. Unlike <see cref="ClipboardCompanionRelay.Client"/> (one lifetime-long
    /// connection), this connects FRESH for every command to match the Server's
    /// one-request-per-connection shape. <see cref="SendCommand"/> is a SYNCHRONOUS, blocking call
    /// because DisplayMonitor's BlockNonCompliant/RestoreCompliant/BlockAllExternal call sites are
    /// synchronous today and this change preserves that shape. It never throws - any connect,
    /// write, read, timeout, or parse failure comes back as <c>(false, reason)</c> so a missing or
    /// misbehaving companion can never crash the primary's DisplayMonitor call site.
    /// </summary>
    public sealed class Client : IDisposable
    {
        // Bounded, small connect retry - same small/bounded spirit as ClipboardCompanionRelay.Client's
        // connect. Commands are infrequent and the companion is normally already listening, so this
        // just rides out a brief window if the companion is momentarily between connections.
        // 10 x 200ms = ~2s worst case.
        const int ConnectMaxAttempts = 10;
        static readonly TimeSpan ConnectRetryDelay = TimeSpan.FromMilliseconds(200);

        // Per-attempt connect timeout; short so an exhausted set of attempts stays inside the ~2s
        // bound above rather than blocking on a single slow Connect.
        const int ConnectTimeoutMs = 200;

        // How long to wait for the companion's single reply line after the command was written. A
        // SetDisplayConfig topology switch on the companion side is not instantaneous, so this is
        // deliberately more generous than the connect bound; still finite so a companion that dies
        // mid-command surfaces as (false, timeout) instead of hanging the DisplayMonitor call.
        const int ReplyReadTimeoutMs = 5000;

        public (bool ok, string? error) SendCommand(string command)
        {
            NamedPipeClientStream? pipe = null;
            try
            {
                pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

                var toConnect = pipe;
                bool connected = RetryPolicy.Execute(() =>
                {
                    try
                    {
                        toConnect.Connect(ConnectTimeoutMs);
                        return true;
                    }
                    catch
                    {
                        // Companion not listening yet (or at all) - the retry loop decides whether
                        // to try again; a final failure is reported as a clean tuple below.
                        return false;
                    }
                }, ConnectMaxAttempts, ConnectRetryDelay);

                if (!connected)
                {
                    return (false, "display companion relay: could not connect to companion pipe");
                }

                using var writer = new StreamWriter(pipe) { AutoFlush = true };
                using var reader = new StreamReader(pipe);

                writer.WriteLine(command);

                // Bounded read: cancel the async read after ReplyReadTimeoutMs and surface a
                // timeout tuple rather than blocking DisplayMonitor forever on a silent companion.
                using var readTimeout = new CancellationTokenSource(ReplyReadTimeoutMs);
                string? reply;
                try
                {
                    reply = reader.ReadLineAsync(readTimeout.Token).AsTask().GetAwaiter().GetResult();
                }
                catch (OperationCanceledException)
                {
                    return (false, "display companion relay: no reply from companion within timeout");
                }

                if (string.IsNullOrEmpty(reply))
                {
                    return (false, "display companion relay: companion closed connection without replying");
                }

                return ParseReply(reply);
            }
            catch (Exception ex)
            {
                // Absolutely nothing from this relay may propagate into the DisplayMonitor call
                // site - any unexpected failure becomes a clean (false, reason).
                return (false, $"display companion relay: {ex.Message}");
            }
            finally
            {
                try { pipe?.Dispose(); } catch { /* best-effort */ }
            }
        }

        public void Dispose()
        {
            // Nothing long-lived is held: each SendCommand owns and disposes its own connection.
        }
    }

    // ---- tiny reply codec (only two possible shapes, so no source-gen context is warranted) ----

    static string BuildReply(bool ok, string? error)
    {
        if (ok)
        {
            return "{\"ok\":true}";
        }

        // Hand-escape the error so a reason containing quotes/backslashes/control characters can
        // never produce a malformed reply line. Done manually rather than via JsonSerializer to
        // stay trim-safe (reflection-based serialization is confined to SchemaExporter here).
        return $"{{\"ok\":false,\"error\":\"{EscapeJsonString(error ?? "unknown error")}\"}}";
    }

    static string EscapeJsonString(string value)
    {
        var sb = new System.Text.StringBuilder(value.Length + 8);
        foreach (char c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20)
                    {
                        sb.Append("\\u").Append(((int)c).ToString("x4"));
                    }
                    else
                    {
                        sb.Append(c);
                    }
                    break;
            }
        }
        return sb.ToString();
    }

    static (bool ok, string? error) ParseReply(string reply)
    {
        try
        {
            using var doc = JsonDocument.Parse(reply);
            var root = doc.RootElement;

            bool ok = root.TryGetProperty("ok", out var okEl) && okEl.ValueKind == JsonValueKind.True;
            if (ok)
            {
                return (true, null);
            }

            string? error = root.TryGetProperty("error", out var errEl) && errEl.ValueKind == JsonValueKind.String
                ? errEl.GetString()
                : "display companion relay: companion reported failure";
            return (false, error);
        }
        catch (JsonException)
        {
            return (false, $"display companion relay: unparseable reply from companion: {reply}");
        }
    }
}
