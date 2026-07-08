namespace DlpEndpointMonitor.Core;

sealed class ClipboardBlacklist : ClipboardRuleList
{
    public ClipboardBlacklist(string? storageDir = null) : base("clipboard-blacklist.json", storageDir) { }

    /// <summary>Clipboard content: always false when disabled.</summary>
    public bool IsBlocked(ClipboardKind kind, string candidate)
    {
        if (!IsEnabled) return false;
        return MatchesAny(kind, candidate);
    }

    /// <summary>
    /// Returns the specific pattern that matched, or null if disabled or nothing matches - used
    /// to report which rule blocked a clipboard operation (ClipboardContentBlockedEvent.MatchedPattern).
    /// </summary>
    public string? FindMatchedPattern(ClipboardKind kind, string candidate)
    {
        if (!IsEnabled) return null;
        return FindMatch(kind, candidate);
    }
}
