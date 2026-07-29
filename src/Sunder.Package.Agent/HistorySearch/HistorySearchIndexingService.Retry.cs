namespace Sunder.Package.Agent.HistorySearch;

internal sealed partial class HistorySearchIndexingService
{
    private void ScheduleTextRebuildRetry()
    {
        lock (_pendingLock)
        {
            _textRebuildFailureCount = Math.Min(_textRebuildFailureCount + 1, 31);
            _textRebuildRetryAt = UtcNow + GetTextRebuildRetryDelay(_textRebuildFailureCount);
        }
        Signal();
    }

    internal static TimeSpan GetTextRebuildRetryDelay(int failureCount)
    {
        var exponent = Math.Min(Math.Max(1, failureCount) - 1, 6);
        var delayMilliseconds = Math.Min(
            InitialTextRebuildRetryDelay.TotalMilliseconds * (1 << exponent),
            MaximumTextRebuildRetryDelay.TotalMilliseconds);
        return TimeSpan.FromMilliseconds(delayMilliseconds);
    }

    private void ResetTextRebuildRetry()
    {
        lock (_pendingLock)
        {
            _textRebuildFailureCount = 0;
            _textRebuildRetryAt = null;
        }
    }

    private void RequestRebuildUnlessCoolingDown()
    {
        var requested = false;
        lock (_pendingLock)
        {
            if (_textRebuildRetryAt is null)
            {
                _rebuildRequested = true;
                requested = true;
            }
        }
        if (requested)
        {
            Signal();
        }
    }
}
