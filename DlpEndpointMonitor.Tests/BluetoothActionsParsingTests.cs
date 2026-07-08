using DlpEndpointMonitor.Actions;
using DlpEndpointMonitor.Core;
using Xunit;

namespace DlpEndpointMonitor.Tests;

// ai_agent_doc/TEST-PLAN.md section 2.3 - Actions/BluetoothActions.cs pure parsing/decoding logic.
public class BluetoothActionsParsingTests
{
    // T-BT-01: well-formed BTHENUM path yields canonical colon-separated uppercase MAC.
    [Fact]
    public void ParseMacFromPath_WellFormedBthenumPath_ReturnsCanonicalMac()
    {
        const string path =
            @"BTHENUM\{00001124-0000-1000-8000-00805F9B34FB}_VID&0002045E_PID&0040\7&1a2b3c4d&0&AABBCCDDEEFF_C00000000";

        string? mac = BluetoothActions.ParseMacFromPath(path);

        Assert.Equal("AA:BB:CC:DD:EE:FF", mac);
    }

    // T-BT-02: no MAC pattern present -> null.
    [Fact]
    public void ParseMacFromPath_NoMacPattern_ReturnsNull()
    {
        const string path = @"BTHENUM\Dev_no_mac_here\basic";

        string? mac = BluetoothActions.ParseMacFromPath(path);

        Assert.Null(mac);
    }

    // T-BT-11: BLE top-level peripheral's own instance ID (backslash form, as returned by
    // SetupDiGetDeviceInstanceId) - verified live against real hardware this session
    // (PROJECT.md section 5.5). Classic regex doesn't match this format at all, so this
    // exercises the BLE fallback branch added to ParseMacFromPath.
    [Fact]
    public void ParseMacFromPath_BleTopLevelInstanceId_ReturnsCanonicalMac()
    {
        const string instanceId = @"BTHLE\DEV_D15799812BE4\7&257494de&0&d15799812be4";

        string? mac = BluetoothActions.ParseMacFromPath(instanceId);

        Assert.Equal("D1:57:99:81:2B:E4", mac);
    }

    // T-BT-12: same BLE peripheral, but as a live device-interface PATH (Windows replaces the
    // top-level '\' separators with '#') - confirms the [\\#] tolerance in _bleTopLevelMac,
    // the same ambiguity _macInPath already handles for classic paths.
    [Fact]
    public void ParseMacFromPath_BleTopLevelInterfacePath_ReturnsCanonicalMac()
    {
        const string path = @"\\?\BTHLE#DEV_D15799812BE4#7&257494de&0&d15799812be4#{guid}";

        string? mac = BluetoothActions.ParseMacFromPath(path);

        Assert.Equal("D1:57:99:81:2B:E4", mac);
    }

    // T-BT-13 (regression guard): a BTHLEDEVICE child path (a GATT service, NOT the true
    // top-level peripheral - see PROJECT.md section 5.5's verified 3-level BLE hierarchy)
    // must NOT match the BLE regex - it has no "DEV_<12-hex>" substring of its own (the
    // literal "DEV_" in its instance ID is immediately followed by "VID&...", not hex digits).
    // If this ever starts matching, FindInstanceIdByMac would resolve to the wrong node.
    [Fact]
    public void ParseMacFromPath_BleGattServiceChildPath_ReturnsNull()
    {
        const string path =
            @"BTHLEDEVICE\{00001812-0000-1000-8000-00805F9B34FB}_DEV_VID&02046D_PID&B020_REV&0009_D15799812BE4\8&65B57F4&0&0021";

        string? mac = BluetoothActions.ParseMacFromPath(path);

        Assert.Null(mac);
    }

    // T-BT-03 (ADAPTED): the plan describes a FormatAddress/ParseMacToUllLong round trip, but
    // ParseMacToUllLong is a private helper only reachable through RemovePairing, which invokes
    // the real BluetoothRemoveDevice P/Invoke (a genuine hardware side effect, not unit-testable
    // per ai_agent_doc/TEST-PLAN.md section 1). Adapted to assert FormatAddress's known-input ->
    // known-output shape only, including the all-zero and all-FF boundary cases.
    [Theory]
    [InlineData(0x0000AABBCCDDEEFFUL, "AA:BB:CC:DD:EE:FF")]
    [InlineData(0x0000000000000000UL, "00:00:00:00:00:00")]
    [InlineData(0x0000FFFFFFFFFFFFUL, "FF:FF:FF:FF:FF:FF")]
    public void FormatAddress_KnownInput_ProducesKnownOutput(ulong ullLong, string expected)
    {
        Assert.Equal(expected, BluetoothActions.FormatAddress(ullLong));
    }

    // T-BT-04: major 0x05 (peripheral), minor peripheral subclass 01 -> Keyboard.
    [Fact]
    public void GetKindFromCoD_PeripheralKeyboardMinor_ReturnsKeyboard()
    {
        // major=0x05 in bits 8-12, minor 6-bit field's top 2 bits = 01 (keyboard) in bits 2-7.
        const uint cod = 0x0540;

        Assert.Equal(DeviceKind.Keyboard, BluetoothActions.GetKindFromCoD(cod));
    }

    // T-BT-05: major 0x05, minor peripheral subclass 10 (pointing device) -> Mouse.
    [Fact]
    public void GetKindFromCoD_PeripheralPointingMinor_ReturnsMouse()
    {
        const uint cod = 0x0580;

        Assert.Equal(DeviceKind.Mouse, BluetoothActions.GetKindFromCoD(cod));
    }

    // T-BT-06: major 0x05, minor peripheral subclass 11 (combo keyboard+pointing) -> Mouse
    // (documented deliberate choice: combo devices treat as mouse for blocking).
    [Fact]
    public void GetKindFromCoD_PeripheralComboMinor_ReturnsMouse()
    {
        const uint cod = 0x05C0;

        Assert.Equal(DeviceKind.Mouse, BluetoothActions.GetKindFromCoD(cod));
    }

    // T-BT-07: major 0x05, unspecified peripheral minor -> generic Hid.
    [Fact]
    public void GetKindFromCoD_PeripheralUnspecifiedMinor_ReturnsHid()
    {
        const uint cod = 0x0500;

        Assert.Equal(DeviceKind.Hid, BluetoothActions.GetKindFromCoD(cod));
    }

    // T-BT-08: other recognized major classes -> Audio, Camera, Network respectively.
    [Theory]
    [InlineData(0x0400u, DeviceKind.Audio)]   // Audio/Video
    [InlineData(0x0600u, DeviceKind.Camera)]  // Imaging
    [InlineData(0x0300u, DeviceKind.Network)] // LAN/Network Access Point
    public void GetKindFromCoD_RecognizedMajorClasses_ReturnsExpectedKind(uint cod, DeviceKind expected)
    {
        Assert.Equal(expected, BluetoothActions.GetKindFromCoD(cod));
    }

    // T-BT-09: unrecognized major class -> Unknown.
    [Fact]
    public void GetKindFromCoD_UnrecognizedMajorClass_ReturnsUnknown()
    {
        const uint cod = 0x0100; // major 0x01 (computer) - not handled by the switch

        Assert.Equal(DeviceKind.Unknown, BluetoothActions.GetKindFromCoD(cod));
    }

    // T-BT-10: interface GUID suffix resolves through DeviceKindResolver.Resolve.
    [Fact]
    public void ParseKindFromPath_KnownInterfaceGuidSuffix_ResolvesThroughDeviceKindResolver()
    {
        // GUID_DEVINTERFACE_KEYBOARD - a _guidKindOverride entry (0x03 HID -> Keyboard).
        const string path = @"BTHENUM\Dev_AABBCCDDEEFF#{884b96c3-56ef-11d1-bc8c-00a0c91405dd}";

        Assert.Equal(DeviceKind.Keyboard, BluetoothActions.ParseKindFromPath(path));
    }

    // Regression guard: a path with no interface GUID suffix falls back to Unknown rather
    // than throwing.
    [Fact]
    public void ParseKindFromPath_NoGuidSuffix_ReturnsUnknown()
    {
        const string path = @"BTHENUM\Dev_AABBCCDDEEFF";

        Assert.Equal(DeviceKind.Unknown, BluetoothActions.ParseKindFromPath(path));
    }
}
