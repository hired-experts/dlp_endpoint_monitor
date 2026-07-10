using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using DlpEndpointMonitor.Actions;

namespace DlpEndpointMonitor.Core;

/// <summary>
/// Newline-delimited request/reply transport for Bluetooth device enumeration. Same direction
/// and lifecycle as <see cref="DisplayCompanionRelay"/> - the PRIMARY (which makes the
/// whitelist/blacklist compliance decision in BluetoothMonitor) is the <see cref="Client"/>, and
/// the COMPANION hosts the <see cref="Server"/>. It is the companion that runs in the interactive
/// user's session where BluetoothFindFirstDevice/BluetoothFindNextDevice actually enumerate the
/// paired devices; a headless Session-0 primary sees none of them, so it asks the companion.
///
/// The DIFFERENCE from DisplayCompanionRelay: the reply is not a 2-shape (ok, error) - it is a
/// whole LIST of <see cref="BluetoothActions.BtDevice"/> records serialized as one JSON array
/// line. The array is (de)serialized through the source-gen <see cref="AppJsonContext"/> rather
/// than hand-built, so the free-form <c>Name</c> field (which can contain quotes, backslashes,
/// and unicode) cannot produce a malformed reply line, and no reflection escapes the trimmed
/// runtime path.
///
/// This file is purely the pipe plumbing. The Server does NOT reference BluetoothActions'
/// enumeration itself - the actual EnumerateConnected() call is supplied as the
/// <c>enumerateDevices</c> delegate wired in Program.cs, mirroring how
/// <see cref="DisplayCompanionRelay.Server"/> only knows its injected executeCommand delegate.
/// </summary>
static class BluetoothCompanionRelay
{
    // Independent pipe from the display/clipboard relays - each is its own named channel.
    public const string PipeName = "DlpEndpointMonitor.BluetoothCompanionRelay";

    // Only one command exists; keeping the wire format a single literal word avoids a parser.
    const string EnumerateCommand = "enumerate";

    // A primary that connects then freezes mid-command must not pin the single pipe slot forever;
    // 5s matches DisplayCompanionRelay.Server's own stalled-client bound. The command line is one
    // short word, so any wait beyond this means the client is stuck, not merely slow.
    const int ReadInactivityTimeoutMs = 5000;

    /// <summary>
    /// Companion-side listener: accepts ONE primary connection at a time, reads exactly one
    /// command line ("enumerate"), runs the caller-supplied <paramref name="enumerateDevices"/>
    /// delegate, writes back exactly one JSON-array reply line encoding the whole device list,
    /// then loops back to accept a fresh connection. Enumeration is infrequent, so a connection is
    /// not held open - one request/reply per connection is the simplest, most robust shape.
    /// </summary>
    public sealed class Server : IDisposable
    {
        readonly CancellationTokenSource _cts = new();
        readonly Task _acceptLoop;
        readonly Func<IReadOnlyList<BluetoothActions.BtDevice>> _enumerateDevices;

        public Server(Func<IReadOnlyList<BluetoothActions.BtDevice>> enumerateDevices)
        {
            _enumerateDevices = enumerateDevices;
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
                    EventEmitter.EmitError("bluetooth_companion_relay", $"failed to create relay pipe server: {ex.Message}");
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
                EventEmitter.EmitError("bluetooth_companion_relay", "dropping stalled relay client: no command received within timeout");
                return;
            }

            if (line is null || line.Length == 0)
            {
                // Empty/closed connection with no command - nothing to reply to, just loop back.
                return;
            }

            // Only one command exists; anything else is a protocol error but still gets a
            // well-formed (empty) reply rather than a broken connection.
            List<BluetoothActions.BtDevice> devices;
            try
            {
                devices = line == EnumerateCommand
                    ? Materialize(_enumerateDevices())
                    : new List<BluetoothActions.BtDevice>();
            }
            catch (Exception ex)
            {
                // The delegate is expected to return a list rather than throw, but a surprise throw
                // must still yield a well-formed empty reply, not kill the accept loop.
                EventEmitter.EmitError("bluetooth_companion_relay", $"enumerate delegate threw: {ex.Message}");
                devices = new List<BluetoothActions.BtDevice>();
            }

            await writer.WriteLineAsync(Serialize(devices)).ConfigureAwait(false);
        }

        static List<BluetoothActions.BtDevice> Materialize(IReadOnlyList<BluetoothActions.BtDevice> list)
            => list as List<BluetoothActions.BtDevice> ?? new List<BluetoothActions.BtDevice>(list);

        public void Dispose()
        {
            _cts.Cancel();
            try { _acceptLoop.Wait(TimeSpan.FromSeconds(2)); } catch { /* best-effort on shutdown */ }
            _cts.Dispose();
        }
    }

    /// <summary>
    /// Primary-side sender. Like <see cref="DisplayCompanionRelay.Client"/> (and unlike
    /// ClipboardCompanionRelay's held-open connection), this connects FRESH for every request to
    /// match the Server's one-request-per-connection shape - Bluetooth enumeration, like display
    /// commands, is infrequent. <see cref="Enumerate"/> never throws: any connect, write, read,
    /// timeout, or parse failure comes back as an EMPTY list so a missing or misbehaving companion
    /// is indistinguishable from "genuinely zero paired devices" to BluetoothMonitor's callers.
    /// </summary>
    public sealed class Client : IDisposable
    {
        // Bounded, small connect retry - same small/bounded spirit and numbers as
        // DisplayCompanionRelay.Client. The companion is normally already listening, so this just
        // rides out a brief window if it is momentarily between connections. 10 x 200ms = ~2s.
        const int ConnectMaxAttempts = 10;
        static readonly TimeSpan ConnectRetryDelay = TimeSpan.FromMilliseconds(200);

        // Per-attempt connect timeout; short so an exhausted set of attempts stays inside the ~2s
        // bound above rather than blocking on a single slow Connect.
        const int ConnectTimeoutMs = 200;

        // How long to wait for the companion's single reply line after the command was written. A
        // BluetoothFindFirst/Next enumeration on the companion side is not instantaneous, so this
        // is deliberately more generous than the connect bound; still finite so a companion that
        // dies mid-request surfaces as an empty list instead of hanging the BluetoothMonitor call.
        const int ReplyReadTimeoutMs = 5000;

        public IReadOnlyList<BluetoothActions.BtDevice> Enumerate()
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
                        // to try again; a final failure is reported as an empty list below.
                        return false;
                    }
                }, ConnectMaxAttempts, ConnectRetryDelay);

                if (!connected)
                {
                    EventEmitter.EmitError("bluetooth_companion_relay", "could not connect to companion pipe");
                    return Array.Empty<BluetoothActions.BtDevice>();
                }

                using var writer = new StreamWriter(pipe) { AutoFlush = true };
                using var reader = new StreamReader(pipe);

                writer.WriteLine(EnumerateCommand);

                // Bounded read: cancel the async read after ReplyReadTimeoutMs and surface an empty
                // list rather than blocking BluetoothMonitor forever on a silent companion.
                using var readTimeout = new CancellationTokenSource(ReplyReadTimeoutMs);
                string? reply;
                try
                {
                    reply = reader.ReadLineAsync(readTimeout.Token).AsTask().GetAwaiter().GetResult();
                }
                catch (OperationCanceledException)
                {
                    EventEmitter.EmitError("bluetooth_companion_relay", "no reply from companion within timeout");
                    return Array.Empty<BluetoothActions.BtDevice>();
                }

                if (string.IsNullOrEmpty(reply))
                {
                    EventEmitter.EmitError("bluetooth_companion_relay", "companion closed connection without replying");
                    return Array.Empty<BluetoothActions.BtDevice>();
                }

                var devices = Deserialize(reply);
                if (devices is null)
                {
                    EventEmitter.EmitError("bluetooth_companion_relay", $"unparseable reply from companion: {reply}");
                    return Array.Empty<BluetoothActions.BtDevice>();
                }

                return devices;
            }
            catch (Exception ex)
            {
                // Absolutely nothing from this relay may propagate into the BluetoothMonitor call
                // site - any unexpected failure becomes a clean empty list.
                EventEmitter.EmitError("bluetooth_companion_relay", ex.Message);
                return Array.Empty<BluetoothActions.BtDevice>();
            }
            finally
            {
                try { pipe?.Dispose(); } catch { /* best-effort */ }
            }
        }

        public void Dispose()
        {
            // Nothing long-lived is held: each Enumerate owns and disposes its own connection.
        }
    }

    // ---- list codec, through the source-gen context (trim-safe, escapes the free-form Name) ----

    static string Serialize(List<BluetoothActions.BtDevice> devices)
        => JsonSerializer.Serialize(devices, AppJsonContext.Default.ListBtDevice);

    static List<BluetoothActions.BtDevice>? Deserialize(string reply)
    {
        try
        {
            return JsonSerializer.Deserialize(reply, AppJsonContext.Default.ListBtDevice);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
