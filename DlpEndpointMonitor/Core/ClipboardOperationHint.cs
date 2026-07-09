namespace DlpEndpointMonitor.Core;

/// <summary>
/// Correlates KeyboardHook's Ctrl+X detection with ClipboardMonitor's next
/// WM_CLIPBOARDUPDATE-driven read, so a genuine text cut is reported as "cut" instead of
/// ClipboardActions.TryReadText()'s hardcoded "copy" - Windows gives no clipboard-format signal
/// distinguishing plain-text cut from copy (unlike Explorer's file-drag "Preferred DropEffect"
/// convention, which ClipboardActions.ReadDropEffect already reads correctly for Files). Both
/// monitors run on the same single STA message-pump thread (Program.cs), and the low-level
/// keyboard hook always observes Ctrl+X before the resulting WM_CLIPBOARDUPDATE is dispatched
/// (an input-pipeline hook fires before the target app even processes the keystroke) - so this
/// class needs no locking, just a short recency window as a safety net against a stale hint
/// lingering if a cut was detected but no clipboard change ever followed (e.g. nothing selected).
/// </summary>
sealed class ClipboardOperationHint
{
    static readonly TimeSpan RecencyWindow = TimeSpan.FromSeconds(2);

    long _cutAtTicks = -1;

    public void MarkCut() => _cutAtTicks = DateTime.UtcNow.Ticks;

    /// <summary>
    /// True if a cut was marked within the recency window. Always clears the hint regardless of
    /// the outcome, so a stale or unrelated cut can never bleed into a later, unrelated clipboard
    /// change.
    /// </summary>
    public bool ConsumeRecentCut()
    {
        long markedAt = _cutAtTicks;
        _cutAtTicks = -1;
        return markedAt >= 0 && (DateTime.UtcNow.Ticks - markedAt) <= RecencyWindow.Ticks;
    }
}
