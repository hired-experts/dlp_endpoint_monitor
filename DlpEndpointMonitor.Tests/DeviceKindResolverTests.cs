using DlpEndpointMonitor.Core;
using Xunit;

namespace DlpEndpointMonitor.Tests;

// Section 2.1: DeviceKindResolver (Core/UsbKind.cs). GUID literals copied verbatim from
// UsbKind.cs so drift in either file is caught.
public class DeviceKindResolverTests
{
    const string PrinterGuid = "{28D78FAD-5A12-11D1-AE5B-0000F803A8C2}";  // GUID_DEVINTERFACE_PRINTER
    const string KeyboardGuid = "{884b96c3-56ef-11d1-bc8c-00a0c91405dd}"; // GUID_DEVINTERFACE_KEYBOARD (override)
    const string UnknownGuid = "{00000000-0000-0000-0000-000000000000}"; // not present in any table

    // KSCATEGORY_CAPTURE - deliberately NOT mapped (see UsbKind.cs comment above the video entry);
    // mapping it caused onboard HD-audio topology endpoints to be swept up as "video".
    const string KsCategoryCaptureGuid = "{65E8773D-8F56-11D0-A3B9-00A0C9223196}";

    [Fact] // T-KIND-01
    public void Resolve_KnownNonOverrideGuid_ReturnsClassAndKind()
    {
        DeviceKind kind = DeviceKindResolver.Resolve(PrinterGuid, out int? usbClass);

        Assert.Equal(0x07, usbClass);
        Assert.Equal(DeviceKind.Printer, kind);
    }

    [Fact] // T-KIND-02
    public void Resolve_GuidInOverrideTable_UsbClassFromTableButKindFromOverride()
    {
        DeviceKind kind = DeviceKindResolver.Resolve(KeyboardGuid, out int? usbClass);

        Assert.Equal(0x03, usbClass);
        Assert.Equal(DeviceKind.Keyboard, kind);
    }

    [Fact] // T-KIND-03
    public void Resolve_EntirelyUnknownGuid_ReturnsNullClassAndUnknownKind()
    {
        DeviceKind kind = DeviceKindResolver.Resolve(UnknownGuid, out int? usbClass);

        Assert.Null(usbClass);
        Assert.Equal(DeviceKind.Unknown, kind);
    }

    [Fact] // T-KIND-04
    public void Resolve_NullClassGuid_ReturnsNullClassAndUnknownKindWithoutThrowing()
    {
        DeviceKind kind = DeviceKindResolver.Resolve(null, out int? usbClass);

        Assert.Null(usbClass);
        Assert.Equal(DeviceKind.Unknown, kind);
    }

    [Fact] // T-KIND-05
    public void Resolve_IsCaseInsensitiveOnGuidString()
    {
        DeviceKind lower = DeviceKindResolver.Resolve(KeyboardGuid, out int? lowerClass);
        DeviceKind upper = DeviceKindResolver.Resolve(KeyboardGuid.ToUpperInvariant(), out int? upperClass);

        Assert.Equal(lower, upper);
        Assert.Equal(lowerClass, upperClass);
    }

    [Fact] // T-KIND-06
    public void Resolve_KsCategoryCapture_StaysUnmapped()
    {
        DeviceKind kind = DeviceKindResolver.Resolve(KsCategoryCaptureGuid, out int? usbClass);

        Assert.Null(usbClass);
        Assert.Equal(DeviceKind.Unknown, kind);
    }

    [Fact] // T-KIND-07
    public void KnownInterfaceGuids_HasNoDuplicatesAndAllParseAsGuids()
    {
        Guid[] guids = DeviceKindResolver.KnownInterfaceGuids;

        Assert.NotEmpty(guids);
        // Construction of each element as a Guid already happened at static init (see
        // KnownInterfaceGuids' `new Guid(k)` projection); a malformed entry would have thrown
        // before this test ever ran. The remaining regression risk is duplicate entries.
        Assert.Equal(guids.Length, guids.Distinct().Count());
    }
}
