using DlpEndpointMonitor.Actions;
using DlpEndpointMonitor.Core;
using Xunit;

namespace DlpEndpointMonitor.Tests;

// docs/TEST-PLAN.md section 2.4 - DisplayActions.ParseMonitorPath (Actions/DisplayActions.cs)
public class DisplayActionsParsingTests
{
    const string GuidSuffix = "#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";

    // T-DISP-01: well-formed DISPLAY#SAM0F91#...#{guid} path
    [Fact]
    public void ParseMonitorPath_WellFormedEdid_ExtractsVidPidAndKind()
    {
        string path = $@"\\?\DISPLAY#SAM0F91#4&1234abcd&0&UID12345{GuidSuffix}";

        var parsed = DisplayActions.ParseMonitorPath(path);

        Assert.NotNull(parsed);
        Assert.Equal("SAM", parsed!.Vid);
        Assert.Equal("0F91", parsed.Pid);
        Assert.Equal(DeviceKind.Monitor, parsed.Kind);
    }

    // T-DISP-02: regression guard for the fail-closed fix - a path that does not match the EDID
    // pattern must still return a non-null ParsedDevice with Vid=Pid="", never null.
    [Fact]
    public void ParseMonitorPath_UnmatchedEdidPattern_ReturnsNonNullWithEmptyVidPid()
    {
        string path = $@"\\?\DISPLAY#1234#4&1234abcd&0&UID12345{GuidSuffix}";

        var parsed = DisplayActions.ParseMonitorPath(path);

        Assert.NotNull(parsed);
        Assert.Equal("", parsed!.Vid);
        Assert.Equal("", parsed.Pid);
        Assert.Equal(DeviceKind.Monitor, parsed.Kind);
    }

    // T-DISP-03: the working string reduces to empty after \\?\ prefix + GUID-suffix stripping
    // (the whole remaining path IS the GUID suffix) - the one remaining legitimate null case.
    [Fact]
    public void ParseMonitorPath_WorkingStringReducesToEmpty_ReturnsNull()
    {
        string path = $@"\\?\{GuidSuffix}";

        var parsed = DisplayActions.ParseMonitorPath(path);

        Assert.Null(parsed);
    }

    // T-DISP-04: vid/pid normalization is case-insensitive -> uppercase
    [Fact]
    public void ParseMonitorPath_LowercaseEdidCode_NormalizesToUppercase()
    {
        string path = $@"\\?\DISPLAY#sam0f91#4&1234abcd&0&UID12345{GuidSuffix}";

        var parsed = DisplayActions.ParseMonitorPath(path);

        Assert.NotNull(parsed);
        Assert.Equal("SAM", parsed!.Vid);
        Assert.Equal("0F91", parsed.Pid);
    }
}
