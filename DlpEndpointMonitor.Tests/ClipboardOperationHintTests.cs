using DlpEndpointMonitor.Core;
using Xunit;

namespace DlpEndpointMonitor.Tests;

/// <summary>
/// ClipboardOperationHint correlates KeyboardHook's Ctrl+X detection with ClipboardMonitor's
/// next WM_CLIPBOARDUPDATE-driven read, so a genuine text cut is reported as "cut" instead of
/// ClipboardActions.TryReadText()'s hardcoded "copy" - Windows gives no clipboard-format signal
/// distinguishing plain-text cut from copy (unlike Explorer's file-drag "Preferred DropEffect"
/// convention, which ClipboardActions.ReadDropEffect already reads correctly). Both monitors run
/// on the same single STA message-pump thread (Program.cs), so this class needs no locking - just
/// a short recency window as a safety net against a stale hint lingering if a cut was detected
/// but no clipboard change ever followed.
/// </summary>
public class ClipboardOperationHintTests
{
    // T-CLIPHINT-01: the base case this class exists for - a cut marked, then consumed shortly
    // after, reports true.
    [Fact]
    public void ConsumeRecentCut_AfterMarkCut_ReturnsTrue()
    {
        var hint = new ClipboardOperationHint();
        hint.MarkCut();
        Assert.True(hint.ConsumeRecentCut());
    }

    // T-CLIPHINT-02: no cut was ever marked - nothing to correlate, must not fabricate one.
    [Fact]
    public void ConsumeRecentCut_WithNoPriorMarkCut_ReturnsFalse()
    {
        var hint = new ClipboardOperationHint();
        Assert.False(hint.ConsumeRecentCut());
    }

    // T-CLIPHINT-03: consuming is destructive - a single Ctrl+X must correlate with only the ONE
    // clipboard change that follows it, not silently relabel every later copy as a cut too.
    [Fact]
    public void ConsumeRecentCut_CalledTwiceAfterOneMarkCut_SecondCallReturnsFalse()
    {
        var hint = new ClipboardOperationHint();
        hint.MarkCut();

        Assert.True(hint.ConsumeRecentCut());
        Assert.False(hint.ConsumeRecentCut());
    }

    // T-CLIPHINT-04: even when the hint predates the read and is stale/irrelevant (Ctrl+X was
    // pressed but no clipboard change ever resulted from it, e.g. nothing was selected), the
    // read still consumes/clears it so it cannot bleed into a later, unrelated clipboard change.
    [Fact]
    public void ConsumeRecentCut_Always_ClearsHintRegardlessOfOutcome()
    {
        var hint = new ClipboardOperationHint();
        hint.MarkCut();
        hint.ConsumeRecentCut(); // first read consumes it

        hint.MarkCut();
        Assert.True(hint.ConsumeRecentCut()); // a fresh MarkCut still correlates correctly
    }

    // T-CLIPHINT-05: MarkCut with no consumption in between just moves the mark forward in time -
    // does not accumulate/queue multiple pending cuts.
    [Fact]
    public void MarkCut_CalledTwiceBeforeConsume_ConsumeRecentCutOnlyReturnsTrueOnce()
    {
        var hint = new ClipboardOperationHint();
        hint.MarkCut();
        hint.MarkCut();

        Assert.True(hint.ConsumeRecentCut());
        Assert.False(hint.ConsumeRecentCut());
    }
}
