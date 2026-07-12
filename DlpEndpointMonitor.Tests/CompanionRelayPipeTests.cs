using System.Text.Json;
using DlpEndpointMonitor.Actions;
using DlpEndpointMonitor.Core;
using Xunit;

namespace DlpEndpointMonitor.Tests;

/// <summary>
/// Regression coverage for the double-dispose bug fixed in DisplayCompanionRelay.cs and
/// BluetoothCompanionRelay.cs: a StreamReader and StreamWriter wrapping the SAME InOut named
/// pipe must both be constructed with leaveOpen:true, or whichever disposes first (they dispose
/// in reverse declaration order at the end of each request) closes the shared pipe out from
/// under the other, and the second Dispose throws ObjectDisposedException("Cannot access a
/// closed pipe."). A single request/reply exchange does not reliably reproduce this - both
/// tests below drive a real Server/Client pair over the actual named pipe transport (loopback,
/// same process) through a tight, repeated loop, since the failure surfaced intermittently
/// against real hardware, not on the very first call.
///
/// Each test binds a per-run, GUID-suffixed pipe name rather than the shared production
/// PipeName constant. This machine can have a real DlpEndpointMonitor companion instance
/// already running (its own Server bound to the production pipe names), and colliding with it
/// would be worse than a flaky test - for DisplayCompanionRelay in particular, the real
/// companion would actually execute a live disable/enable against the user's real display. The
/// two Server/Client classes accept an optional pipeName purely for this isolation; production
/// callers (Program.cs) never pass it, so their behavior is unchanged.
/// </summary>
public class CompanionRelayPipeTests
{
    const int Iterations = 50;

    static string UniquePipeName(string basePipeName) => $"{basePipeName}.Test.{Guid.NewGuid():N}";

    static IEnumerable<JsonElement> ParseEvents(string output) =>
        output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line).RootElement);

    static void AssertNoErrorEvents(string output)
    {
        foreach (var evt in ParseEvents(output))
        {
            if (evt.TryGetProperty("type", out var typeEl) && typeEl.ValueKind == JsonValueKind.String)
            {
                Assert.False(typeEl.GetString() == "error", $"unexpected error event emitted: {evt}");
            }
        }
    }

    [Fact]
    public void DisplayCompanionRelay_RoundTrip_SucceedsRepeatedlyWithNoDoubleDisposeOrErrorEvents()
    {
        string pipeName = UniquePipeName(DisplayCompanionRelay.PipeName);
        using var server = new DisplayCompanionRelay.Server(command => (true, null), pipeName);
        using var client = new DisplayCompanionRelay.Client(pipeName);

        var sw = new StringWriter();
        var originalOut = Console.Out;
        Console.SetOut(sw);
        try
        {
            for (int i = 0; i < Iterations; i++)
            {
                (bool ok, string? error) = client.SendCommand("disable");

                Assert.True(ok, $"iteration {i}: expected ok=true, got error='{error}'. stdout-so-far: {sw}");
                Assert.Null(error);
            }
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        AssertNoErrorEvents(sw.ToString());
    }

    [Fact]
    public void BluetoothCompanionRelay_RoundTrip_SucceedsRepeatedlyWithNoDoubleDisposeOrErrorEvents()
    {
        var expected = new List<BluetoothActions.BtDevice>
        {
            new("AABBCCDDEEFF", DeviceKind.Bluetooth, "Test BLE Mouse"),
        };

        string pipeName = UniquePipeName(BluetoothCompanionRelay.PipeName);
        using var server = new BluetoothCompanionRelay.Server(() => expected, pipeName);
        using var client = new BluetoothCompanionRelay.Client(pipeName);

        var sw = new StringWriter();
        var originalOut = Console.Out;
        Console.SetOut(sw);
        try
        {
            for (int i = 0; i < Iterations; i++)
            {
                IReadOnlyList<BluetoothActions.BtDevice> devices = client.Enumerate();

                Assert.True(devices.Count == 1, $"iteration {i}: expected 1 device, got {devices.Count}. stdout-so-far: {sw}");
                Assert.Equal(expected[0].Mac, devices[0].Mac);
                Assert.Equal(expected[0].Kind, devices[0].Kind);
                Assert.Equal(expected[0].Name, devices[0].Name);
            }
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        AssertNoErrorEvents(sw.ToString());
    }

    [Fact]
    public void DisplayChangeRelay_Notify_InvokesCallbackRepeatedlyWithNoErrorEvents()
    {
        string pipeName = UniquePipeName(DisplayChangeRelay.PipeName);
        int callbackCount = 0;
        var callbackSeen  = new ManualResetEventSlim(false);

        using var server = new DisplayChangeRelay.Server(() =>
        {
            Interlocked.Increment(ref callbackCount);
            callbackSeen.Set();
        }, pipeName);
        using var client = new DisplayChangeRelay.Client(pipeName);

        Assert.True(client.IsConnected);

        var sw = new StringWriter();
        var originalOut = Console.Out;
        Console.SetOut(sw);
        try
        {
            for (int i = 0; i < Iterations; i++)
            {
                callbackSeen.Reset();
                client.Notify();
                Assert.True(callbackSeen.Wait(TimeSpan.FromSeconds(2)), $"iteration {i}: callback was not invoked in time. stdout-so-far: {sw}");
            }
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        Assert.Equal(Iterations, callbackCount);
        AssertNoErrorEvents(sw.ToString());
    }
}
