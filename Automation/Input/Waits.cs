using System;
using System.Threading.Tasks;

namespace BeastsV3.Automation.Input;

// Poll-until-condition helpers for workflows, with timeouts scaled by AutomationInput.
public sealed class Waits
{
    private readonly AutomationInput _input;

    public Waits(AutomationInput input)
    {
        _input = input;
    }

    // Polls valueProvider until completionPredicate holds or the timeout expires, calling
    // onPending on each failed iteration.
    public async Task<T> PollAsync<T>(
        Func<T> valueProvider,
        Func<T, bool> completionPredicate,
        int timeoutMs,
        int pollDelayMs,
        int initialDelayMs = 0,
        Func<T, Task> onPendingAsync = null)
    {
        var startedAt = DateTime.UtcNow;
        var adjustedTimeoutMs = _input.ScaleTimeout(timeoutMs);
        var adjustedPollDelayMs = Math.Max(1, pollDelayMs);

        if (initialDelayMs > 0) await _input.DelayAsync(initialDelayMs);

        var lastObserved = valueProvider();
        while ((DateTime.UtcNow - startedAt).TotalMilliseconds < adjustedTimeoutMs)
        {
            _input.ThrowIfStopRequested();

            if (completionPredicate(lastObserved)) return lastObserved;

            if (onPendingAsync != null) await onPendingAsync(lastObserved);

            await _input.DelayAsync(adjustedPollDelayMs);
            lastObserved = valueProvider();
        }

        return valueProvider();
    }

    public Task<bool> WaitForAsync(
        Func<bool> condition,
        int timeoutMs,
        int pollDelayMs,
        int initialDelayMs = 0) =>
        PollAsync(condition, satisfied => satisfied, timeoutMs, pollDelayMs, initialDelayMs);
}
