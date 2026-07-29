namespace Sunder.Package.Agent.Services;

internal sealed class AgentRuntimeGenerationOptions
{
    internal TimeSpan LeaseDuration { get; init; } = TimeSpan.FromSeconds(30);

    internal TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(5);

    internal TimeSpan ClaimPollInterval { get; init; } = TimeSpan.FromMilliseconds(50);

    internal TimeSpan RetryInitialDelay { get; init; } = TimeSpan.FromMilliseconds(100);

    internal TimeSpan RetryMaximumDelay { get; init; } = TimeSpan.FromSeconds(2);

    internal TimeSpan LeaseSafetyMargin { get; init; } = TimeSpan.FromSeconds(5);

    internal TimeSpan RenewalCommandTimeout { get; init; } = TimeSpan.FromSeconds(1);

    internal TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    internal Func<TimeSpan, CancellationToken, Task>? DelayAsync { get; init; }

    internal Func<TimeSpan, CancellationToken, Task>? DeadlineDelayAsync { get; init; }

    internal Action? RenewalCommandStarting { get; init; }

    internal int RenewalCommandTimeoutSeconds
        => checked((int)Math.Ceiling(RenewalCommandTimeout.TotalSeconds));

    internal Task Delay(TimeSpan delay, CancellationToken cancellationToken)
        => DelayAsync?.Invoke(delay, cancellationToken)
           ?? Task.Delay(delay, TimeProvider, cancellationToken);

    internal Task DelayUntilDeadline(TimeSpan delay, CancellationToken cancellationToken)
        => DeadlineDelayAsync?.Invoke(delay, cancellationToken)
           ?? Task.Delay(delay, TimeProvider, cancellationToken);

    internal void Validate()
    {
        if (LeaseDuration <= TimeSpan.Zero
            || HeartbeatInterval <= TimeSpan.Zero
            || ClaimPollInterval <= TimeSpan.Zero
            || RetryInitialDelay <= TimeSpan.Zero
            || RetryMaximumDelay < RetryInitialDelay
            || LeaseSafetyMargin <= TimeSpan.Zero
            || LeaseSafetyMargin >= LeaseDuration
            || RenewalCommandTimeout < TimeSpan.FromSeconds(1)
            || RenewalCommandTimeout.TotalSeconds > int.MaxValue
            || TimeSpan.FromSeconds(Math.Ceiling(RenewalCommandTimeout.TotalSeconds)) >= LeaseSafetyMargin
            || HeartbeatInterval >= LeaseDuration - LeaseSafetyMargin)
        {
            throw new InvalidOperationException("Agent Runtime generation lease timing options are invalid.");
        }
    }
}
