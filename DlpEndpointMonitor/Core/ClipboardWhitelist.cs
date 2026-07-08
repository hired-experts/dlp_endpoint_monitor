namespace DlpEndpointMonitor.Core;

sealed class ClipboardWhitelist : ClipboardRuleList
{
    public ClipboardWhitelist(string? storageDir = null) : base("clipboard-whitelist.json", storageDir) { }

    /// <summary>Clipboard content: always true when disabled.</summary>
    public bool IsAllowed(ClipboardKind kind, string candidate)
    {
        if (!IsEnabled) return true;
        return MatchesAny(kind, candidate);
    }
}
