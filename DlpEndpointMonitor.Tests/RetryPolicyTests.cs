using DlpEndpointMonitor.Core;
using Xunit;

namespace DlpEndpointMonitor.Tests;

/// <summary>
/// RetryPolicy is a small, pure, Win32-free helper: retry a boolean "attempt" delegate up to
/// maxAttempts times with a delay between tries, stopping as soon as it succeeds and never
/// sleeping after the last attempt. Exists so ClipboardActions can retry a transiently-failing
/// OpenClipboard (another process/listener can hold the clipboard open for a brief window right
/// after WM_CLIPBOARDUPDATE fires) without hand-rolling a loop at each of its 3 call sites.
/// </summary>
public class RetryPolicyTests
{
    // T-RETRY-01: the common case - succeeds immediately, no wasted attempts or delay.
    [Fact]
    public void Execute_SucceedsOnFirstAttempt_ReturnsTrueAndCallsAttemptOnce()
    {
        int calls = 0;
        bool result = RetryPolicy.Execute(() => { calls++; return true; }, maxAttempts: 5, delay: TimeSpan.FromMilliseconds(10));

        Assert.True(result);
        Assert.Equal(1, calls);
    }

    // T-RETRY-02: succeeds on the 3rd of up to 5 attempts - must stop retrying immediately upon
    // success, not burn through the remaining allotted attempts.
    [Fact]
    public void Execute_SucceedsOnThirdAttempt_ReturnsTrueAndStopsRetrying()
    {
        int calls = 0;
        bool result = RetryPolicy.Execute(() =>
        {
            calls++;
            return calls == 3;
        }, maxAttempts: 5, delay: TimeSpan.FromMilliseconds(1));

        Assert.True(result);
        Assert.Equal(3, calls);
    }

    // T-RETRY-03: never succeeds - tries exactly maxAttempts times, then gives up.
    [Fact]
    public void Execute_NeverSucceeds_ReturnsFalseAfterExactlyMaxAttempts()
    {
        int calls = 0;
        bool result = RetryPolicy.Execute(() => { calls++; return false; }, maxAttempts: 5, delay: TimeSpan.FromMilliseconds(1));

        Assert.False(result);
        Assert.Equal(5, calls);
    }

    // T-RETRY-04: maxAttempts of 1 - a single attempt, no retry loop at all regardless of outcome.
    [Fact]
    public void Execute_WithMaxAttemptsOne_TriesOnceRegardlessOfOutcome()
    {
        int calls = 0;
        bool result = RetryPolicy.Execute(() => { calls++; return false; }, maxAttempts: 1, delay: TimeSpan.FromMilliseconds(50));

        Assert.False(result);
        Assert.Equal(1, calls);
    }
}
