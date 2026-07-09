namespace DlpEndpointMonitor.Core;

/// <summary>
/// Generic, Win32-free retry helper for a boolean "did this succeed" operation. Not
/// clipboard-specific - reusable anywhere a transient failure is worth a small, bounded
/// number of retries (see Actions/ClipboardActions.cs for the motivating use: OpenClipboard
/// can transiently fail while another listener still holds the clipboard open).
/// </summary>
static class RetryPolicy
{
    /// <summary>
    /// Calls <paramref name="attempt"/> up to <paramref name="maxAttempts"/> times, returning
    /// true as soon as it succeeds. Sleeps <paramref name="delay"/> between attempts only -
    /// never after the last attempt, whether it succeeded or failed.
    /// </summary>
    public static bool Execute(Func<bool> attempt, int maxAttempts, TimeSpan delay)
    {
        for (int i = 0; i < maxAttempts; i++)
        {
            if (attempt())
            {
                return true;
            }

            if (i < maxAttempts - 1)
            {
                Thread.Sleep(delay);
            }
        }

        return false;
    }
}
