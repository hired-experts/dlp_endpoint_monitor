using DlpEndpointMonitor.Core;
using Xunit;

namespace DlpEndpointMonitor.Tests;

/// <summary>
/// USB-WHITELIST-BYPASS-FIX-PLAN.md section 2.2.1/2.5: StartupConflictResolver.Resolve - the pure
/// decision factored out of Program.cs's startup whitelist/blacklist conflict check. Confirms the
/// regression the plan flags is actually prevented: a corrupted-load fail-closed fallback
/// (LoadFailed=true) must never be treated the same as a genuine, direct-file-edit conflict, or a
/// machine's real, correctly-configured blacklist protection would be force-disabled as collateral
/// damage.
/// </summary>
public class StartupConflictResolverTests
{
    [Fact]
    public void Resolve_OnlyWhitelistEnabled_ReturnsNone()
    {
        var result = StartupConflictResolver.Resolve(
            whitelistEnabled: true, whitelistLoadFailed: false,
            blacklistEnabled: false, blacklistLoadFailed: false);

        Assert.Equal(StartupConflictResolver.Action.None, result);
    }

    [Fact]
    public void Resolve_OnlyBlacklistEnabled_ReturnsNone()
    {
        var result = StartupConflictResolver.Resolve(
            whitelistEnabled: false, whitelistLoadFailed: false,
            blacklistEnabled: true, blacklistLoadFailed: false);

        Assert.Equal(StartupConflictResolver.Action.None, result);
    }

    [Fact]
    public void Resolve_NeitherEnabled_ReturnsNone()
    {
        var result = StartupConflictResolver.Resolve(
            whitelistEnabled: false, whitelistLoadFailed: false,
            blacklistEnabled: false, blacklistLoadFailed: false);

        Assert.Equal(StartupConflictResolver.Action.None, result);
    }

    // The core regression this plan's section 2.2.1 exists to prevent: a genuinely-configured,
    // correctly-running blacklist machine whose UNRELATED whitelist.json happens to corrupt must
    // not have its real blacklist protection force-disabled as collateral damage.
    [Fact]
    public void Resolve_WhitelistLoadFailedBlacklistGenuine_ReturnsDisableWhitelistOnly()
    {
        var result = StartupConflictResolver.Resolve(
            whitelistEnabled: true, whitelistLoadFailed: true,
            blacklistEnabled: true, blacklistLoadFailed: false);

        Assert.Equal(StartupConflictResolver.Action.DisableWhitelistOnly, result);
    }

    // Symmetric case, even though DeviceBlacklist has no fail-closed override today - kept correct
    // for a future blacklist-side fail-closed fallback.
    [Fact]
    public void Resolve_BlacklistLoadFailedWhitelistGenuine_ReturnsDisableBlacklistOnly()
    {
        var result = StartupConflictResolver.Resolve(
            whitelistEnabled: true, whitelistLoadFailed: false,
            blacklistEnabled: true, blacklistLoadFailed: true);

        Assert.Equal(StartupConflictResolver.Action.DisableBlacklistOnly, result);
    }

    [Fact]
    public void Resolve_BothGenuinelyEnabled_ReturnsDisableBoth()
    {
        var result = StartupConflictResolver.Resolve(
            whitelistEnabled: true, whitelistLoadFailed: false,
            blacklistEnabled: true, blacklistLoadFailed: false);

        Assert.Equal(StartupConflictResolver.Action.DisableBoth, result);
    }

    [Fact]
    public void Resolve_BothLoadFailedSimultaneously_ReturnsDisableBoth()
    {
        var result = StartupConflictResolver.Resolve(
            whitelistEnabled: true, whitelistLoadFailed: true,
            blacklistEnabled: true, blacklistLoadFailed: true);

        Assert.Equal(StartupConflictResolver.Action.DisableBoth, result);
    }
}
