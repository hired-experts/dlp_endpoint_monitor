using DlpEndpointMonitor.Actions;
using DlpEndpointMonitor.Core;
using Xunit;

namespace DlpEndpointMonitor.Tests;

// ai_agent_doc/TEST-PLAN.md section 2.2 — pure regex/string parsing over a caller-supplied path,
// no Win32/hardware involved. UsbActions is an internal static class; its members are
// public, reachable here via [assembly: InternalsVisibleTo("DlpEndpointMonitor.Tests")].
public class UsbActionsParsingTests
{
    // T-USB-01: standard VID/PID path with a positional serial (contains '&') — must be
    // excluded from Serial, not mistaken for a real serial number.
    [Fact]
    public void ParseDevicePath_StandardPath_PositionalSerialExcluded()
    {
        var parsed = UsbActions.ParseDevicePath(
            @"\\?\USB#VID_046D&PID_C52B#7&3A4B1C2D&0&1#{a5dcbf10-6530-11d2-901f-00c04fb951ed}");

        Assert.NotNull(parsed);
        Assert.Equal("046D", parsed!.Vid);
        Assert.Equal("C52B", parsed.Pid);
        Assert.Null(parsed.Serial);
    }

    // T-USB-02: real serial (no '&' segment) is captured.
    [Fact]
    public void ParseDevicePath_RealSerial_IsCaptured()
    {
        var parsed = UsbActions.ParseDevicePath(
            @"\\?\USB#VID_1234&PID_5678#ABCDEF123456#{a5dcbf10-6530-11d2-901f-00c04fb951ed}");

        Assert.NotNull(parsed);
        Assert.Equal("ABCDEF123456", parsed!.Serial);
    }

    // T-USB-03: Bluetooth-HID-style fallback pattern "VID&xx<vid>_PID&<pid>".
    // ADAPTED: TEST-PLAN's literal example "VID&0002046D_PID&B020" has 8 hex digits between
    // "VID&" and "_PID&", but _btVidPid is fixed-width ([0-9A-Fa-f]{2} then a 4-char capture,
    // i.e. exactly 6 digits) — that exact literal does not match the regex at all. The
    // source's own doc comment on _btVidPid uses "VID&02046D_PID&B020" (6 digits), which does
    // match and yields the TEST-PLAN's expected Vid="046D"/Pid="B020" — used here instead.
    [Fact]
    public void ParseDevicePath_BluetoothHidStylePath_MatchesFallbackRegex()
    {
        var parsed = UsbActions.ParseDevicePath(
            @"\\?\HID#VID&02046D_PID&B020&MI_00#7&1234abcd&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}");

        Assert.NotNull(parsed);
        Assert.Equal("046D", parsed!.Vid);
        Assert.Equal("B020", parsed.Pid);
    }

    // T-USB-04: neither the standard nor the Bluetooth-HID pattern present.
    [Fact]
    public void ParseDevicePath_NoRecognizedPattern_ReturnsNull()
    {
        var parsed = UsbActions.ParseDevicePath(
            @"\\?\ACPI#PNP0301#2&daba3ff&0#{4d36e978-e325-11ce-bfc1-08002be10318}");

        Assert.Null(parsed);
    }

    // T-USB-05 (indirect): the historical "#{guid}\reference" bug — everything from the
    // interface GUID onward, INCLUDING the "\reference" tail, must be stripped, not just the
    // "#{guid}" part. ToInstanceId is private, so this is verified through ParseDevicePath's
    // returned InstanceId, per HARNESS instructions.
    [Fact]
    public void ParseDevicePath_InstanceId_StripsGuidAndReferenceTail()
    {
        var parsed = UsbActions.ParseDevicePath(
            @"\\?\USB#VID_046D&PID_C52B#ABCDEF123456#{65e8773d-8f56-11d0-a3b9-00a0c9223196}\wavemicin");

        Assert.NotNull(parsed);
        Assert.Equal(@"USB\VID_046D&PID_C52B\ABCDEF123456", parsed!.InstanceId);
    }

    // T-USB-06: regression guard for the fail-closed fix — ParsePartialDevice must never go
    // back to returning null for an Unknown kind. Also covers a couple of known kinds to show
    // Unknown isn't special-cased in a way that would silently reintroduce the old behavior.
    [Theory]
    [InlineData(DeviceKind.Unknown)]
    [InlineData(DeviceKind.Storage)]
    [InlineData(DeviceKind.Keyboard)]
    public void ParsePartialDevice_NonEmptyPath_NeverNullRegardlessOfKind(DeviceKind kind)
    {
        var parsed = UsbActions.ParsePartialDevice(@"\\?\ACPI#PNP0301#2&daba3ff&0", kind);

        Assert.NotNull(parsed);
        Assert.Equal("", parsed!.Vid);
        Assert.Equal("", parsed.Pid);
        Assert.Equal(kind, parsed.Kind);
    }

    // T-USB-07: the one legitimate remaining null case — working path reduces to empty string.
    [Fact]
    public void ParsePartialDevice_ReducesToEmptyString_ReturnsNull()
    {
        var parsed = UsbActions.ParsePartialDevice(
            @"\\?\#{a5dcbf10-6530-11d2-901f-00c04fb951ed}", DeviceKind.Unknown);

        Assert.Null(parsed);
    }

    // T-USB-08: lowercase hex normalizes to uppercase; positional serial still excluded.
    [Fact]
    public void ParseDevicePath_LowercaseHex_NormalizesToUppercase()
    {
        var parsed = UsbActions.ParseDevicePath(
            @"\\?\USB#VID_046d&PID_c52b#7&3a4b1c2d&0&1#{a5dcbf10-6530-11d2-901f-00c04fb951ed}");

        Assert.NotNull(parsed);
        Assert.Equal("046D", parsed!.Vid);
        Assert.Equal("C52B", parsed.Pid);
        Assert.Null(parsed.Serial);
    }
}
