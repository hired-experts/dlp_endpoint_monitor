namespace ClipboardUsbMonitor.Core;

static class DeviceKindResolver
{
    // Windows device interface class GUIDs (from DEV_BROADCAST_DEVICEINTERFACE.dbcc_classguid)
    // mapped to standard USB class codes (bDeviceClass / bInterfaceClass, USB-IF spec).
    static readonly Dictionary<string, int> _guidToClass = new(StringComparer.OrdinalIgnoreCase)
    {
        // ── HID ──────────────────────────────────────────────────────────────
        { "{4D1E55B2-F16F-11CF-88CB-001111000030}", 0x03 }, // GUID_DEVINTERFACE_HID (generic)
        { "{884b96c3-56ef-11d1-bc8c-00a0c91405dd}", 0x03 }, // GUID_DEVINTERFACE_KEYBOARD
        { "{378DE44C-56EF-11D1-BC8C-00A0C91405DD}", 0x03 }, // GUID_DEVINTERFACE_MOUSE
        { "{DEEBE6AD-9E01-47E2-A3B2-A66AA2C036C9}", 0x03 }, // Windows Biometric Framework (fingerprint)
        { "{BA1BB692-9B7A-4833-9A1E-525ED134E7E2}", 0x03 }, // GUID_DEVINTERFACE_SENSOR (HID-based sensors)
        // ── Storage ──────────────────────────────────────────────────────────
        { "{53F56307-B6BF-11D0-94F2-00A0C91EFB8B}", 0x08 }, // GUID_DEVINTERFACE_DISK
        { "{53F56308-B6BF-11D0-94F2-00A0C91EFB8B}", 0x08 }, // GUID_DEVINTERFACE_CDROM
        { "{53F5630B-B6BF-11D0-94F2-00A0C91EFB8B}", 0x08 }, // GUID_DEVINTERFACE_TAPE
        { "{53F5630D-B6BF-11D0-94F2-00A0C91EFB8B}", 0x08 }, // GUID_DEVINTERFACE_VOLUME
        // ── Hub / Host Controller ─────────────────────────────────────────────
        { "{AD498944-762F-11D0-8DCB-00C04FC3358C}", 0x09 }, // USB Hub (legacy GUID)
        { "{F18A0E88-C30C-11D0-8815-00A0C906BED8}", 0x09 }, // GUID_DEVINTERFACE_USB_HUB
        { "{3ABF6F2D-71C4-462A-8A92-1E6861E6AF27}", 0x09 }, // GUID_DEVINTERFACE_USB_HOST_CONTROLLER
        // ── Audio ─────────────────────────────────────────────────────────────
        { "{6994AD04-93EF-11D0-A3CC-00A0C9223196}", 0x01 }, // KSCATEGORY_AUDIO (capture)
        { "{65E8773E-8F56-11D0-A3B9-00A0C9223196}", 0x01 }, // KSCATEGORY_RENDER (speaker/output)
        { "{509CCB5A-45CA-400F-BB6E-DDFCD9F7BA14}", 0x01 }, // USB Audio 2.0
        // ── Camera / Imaging ──────────────────────────────────────────────────
        { "{6BDD1FC6-810F-11D0-BEC7-08002BE2092F}", 0x06 }, // GUID_DEVINTERFACE_IMAGE (cameras, scanners)
        { "{6AC27878-A6FA-4155-BA85-F98F491D4F33}", 0x06 }, // GUID_DEVINTERFACE_WPD (MTP/PTP)
        { "{BA0C718F-4DED-49B7-BDD3-FABE28661211}", 0x06 }, // GUID_DEVINTERFACE_WPD_PRIVATE
        { "{E6D0DF1C-9DEF-11D2-A16C-00C04F8EEA30}", 0x06 }, // WPD MTP cameras and phones
        { "{F33FDC04-D1AC-4E8E-9A30-19BBD4B108AE}", 0x06 }, // WPD Portable Video
        { "{2EEF81BE-33FA-4800-9670-1CD474972C3F}", 0x06 }, // WPD Generic
        // ── Smartcard / Printer ───────────────────────────────────────────────
        { "{50DD5230-BA8A-11D1-BF5D-0000F805F530}", 0x0B }, // GUID_DEVINTERFACE_SMARTCARD_READER
        { "{28D78FAD-5A12-11D1-AE5B-0000F803A8C2}", 0x07 }, // GUID_DEVINTERFACE_PRINTER
        // ── Bluetooth ─────────────────────────────────────────────────────────
        { "{E0CBF06C-CD8B-4647-BB8A-263B43F0F974}", 0xE0 }, // Bluetooth LE GATT service
        { "{781EF630-72B2-11D2-B852-00C04FAD5171}", 0xE0 }, // Bluetooth Radio
        { "{0850302A-B344-4fda-9BE9-90576B8D46F0}", 0xE0 }, // GUID_BTHPORT_DEVICE_INTERFACE
        // ── Network / Serial ──────────────────────────────────────────────────
        { "{CAC88484-7515-4C03-82E6-71A87ABAC361}", 0x02 }, // GUID_DEVINTERFACE_NET (USB Ethernet adapters)
        { "{2C7089AA-2E0E-11D1-B114-00C04FC2AAE4}", 0x02 }, // GUID_DEVINTERFACE_MODEM
        { "{86E0D1E0-8089-11D0-9CE4-08003E301F73}", 0x02 }, // GUID_DEVINTERFACE_COMPORT
        { "{4D36E978-E325-11CE-BFC1-08002BE10318}", 0x02 }, // Serial Port enumerator
        // ── Video ─────────────────────────────────────────────────────────────
        { "{65E8773D-8F56-11D0-A3B9-00A0C9223196}", 0x0E }, // KSCATEGORY_CAPTURE (video capture)
        { "{6994AD05-93EF-11D0-A3CC-00A0C9223196}", 0x0E }, // KSCATEGORY_VIDEO (video control)
        // ── Generic ───────────────────────────────────────────────────────────
        { "{A5DCBF10-6530-11D2-901F-00C04FB951ED}", 0x00 }, // GUID_DEVINTERFACE_USB_DEVICE (generic)
    };

    // USB class codes → device kind.
    static readonly Dictionary<int, DeviceKind> _classToKind = new()
    {
        { 0x01, DeviceKind.Audio },
        { 0x02, DeviceKind.Network },
        { 0x03, DeviceKind.Hid },
        { 0x06, DeviceKind.Camera },
        { 0x07, DeviceKind.Printer },
        { 0x08, DeviceKind.Storage },
        { 0x09, DeviceKind.Hub },
        { 0x0B, DeviceKind.Smartcard },
        { 0x0E, DeviceKind.Video },
        { 0xE0, DeviceKind.Bluetooth },
        { 0xFF, DeviceKind.Vendor },
    };

    // GUIDs that share a usbClass with a broader category but deserve a more specific kind.
    // Checked before the usbClass→kind table so these override the generic class name.
    static readonly Dictionary<string, DeviceKind> _guidKindOverride = new(StringComparer.OrdinalIgnoreCase)
    {
        { "{884b96c3-56ef-11d1-bc8c-00a0c91405dd}", DeviceKind.Keyboard  }, // GUID_DEVINTERFACE_KEYBOARD  (0x03 HID → keyboard)
        { "{378DE44C-56EF-11D1-BC8C-00A0C91405DD}", DeviceKind.Mouse     }, // GUID_DEVINTERFACE_MOUSE     (0x03 HID → mouse)
        { "{DEEBE6AD-9E01-47E2-A3B2-A66AA2C036C9}", DeviceKind.Biometric }, // Windows Biometric Framework  (0x03 HID → biometric)
        { "{BA1BB692-9B7A-4833-9A1E-525ED134E7E2}", DeviceKind.Sensor    }, // GUID_DEVINTERFACE_SENSOR    (0x03 HID → sensor)
    };

    // Parsed once at startup; consumed by UsbActions.EnumerateConnected for SetupAPI calls.
    public static Guid[] KnownInterfaceGuids { get; } =
        [.. _guidToClass.Keys.Select(k => new Guid(k))];

    public static int? ClassGuidToUsbClass(string? classGuid) =>
        classGuid is not null && _guidToClass.TryGetValue(classGuid, out int c) ? c : null;

    public static DeviceKind UsbClassToKind(int? usbClass) =>
        usbClass.HasValue && _classToKind.TryGetValue(usbClass.Value, out DeviceKind kind) ? kind : DeviceKind.Unknown;

    /// <summary>Resolves a Windows classGuid to both usbClass and kind in one call.</summary>
    public static DeviceKind Resolve(string? classGuid, out int? usbClass)
    {
        usbClass = ClassGuidToUsbClass(classGuid);

        // Sub-class overrides: same usbClass as their parent (0x03/HID) but more specific kind.
        if (classGuid is not null && _guidKindOverride.TryGetValue(classGuid, out DeviceKind overrideKind))
            return overrideKind;

        return UsbClassToKind(usbClass);
    }
}
