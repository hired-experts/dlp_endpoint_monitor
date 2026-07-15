namespace DlpEndpointMonitor.Core;

/// <summary>
/// Pure decision for Program.cs's startup whitelist/blacklist conflict check, factored out of
/// the top-level-statement Program.cs so it is unit-testable (same precedent as
/// UsbMonitor.DecideGroupBlock/ResolveBluetoothBlock) - see
/// ai_agent_doc/USB-WHITELIST-BYPASS-FIX-PLAN.md section 2.2.1 for the regression this exists to
/// prevent: a corrupted whitelist.json's fail-closed fallback (Enabled=true, LoadFailed=true) must
/// not be treated as a real, direct-file-edit conflict on a machine genuinely running in
/// blacklist mode, or the machine's real, correctly-configured blacklist protection would be
/// force-disabled as collateral damage.
/// </summary>
static class StartupConflictResolver
{
    internal enum Action
    {
        /// <summary>Not both enabled - no conflict, nothing to do.</summary>
        None,

        /// <summary>Whitelist's Enabled=true came from its fail-closed corrupted-load fallback,
        /// not genuine configuration - only whitelist is disabled; blacklist's genuinely-loaded
        /// state is left untouched.</summary>
        DisableWhitelistOnly,

        /// <summary>Symmetric case for a future blacklist-side fail-closed fallback.</summary>
        DisableBlacklistOnly,

        /// <summary>Both genuinely enabled (a real ambiguous conflict from direct file edits), or
        /// both failed to load simultaneously - the pre-existing "disable both" behavior.</summary>
        DisableBoth,
    }

    internal static Action Resolve(bool whitelistEnabled, bool whitelistLoadFailed, bool blacklistEnabled, bool blacklistLoadFailed)
    {
        if (!(whitelistEnabled && blacklistEnabled)) return Action.None;

        if (whitelistLoadFailed && !blacklistLoadFailed) return Action.DisableWhitelistOnly;
        if (blacklistLoadFailed && !whitelistLoadFailed) return Action.DisableBlacklistOnly;
        return Action.DisableBoth;
    }
}
