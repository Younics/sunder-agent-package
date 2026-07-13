using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Callbacks;

namespace Sunder.Package.Agent.Shared.Presentation;

internal sealed class PackageCallbackFlowRunner
{
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan MaximumPollInterval = TimeSpan.FromSeconds(1);
    private readonly IPackageCallbackClient _callbacks;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _pollInterval;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public PackageCallbackFlowRunner(
        IPackageCallbackClient callbacks,
        TimeProvider? timeProvider = null,
        TimeSpan? pollInterval = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _callbacks = callbacks ?? throw new ArgumentNullException(nameof(callbacks));
        _timeProvider = timeProvider ?? TimeProvider.System;
        var requestedInterval = pollInterval ?? DefaultPollInterval;
        if (requestedInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(pollInterval), "The callback polling interval must be positive.");
        }

        _pollInterval = requestedInterval > MaximumPollInterval ? MaximumPollInterval : requestedInterval;
        _delay = delay ?? ((duration, cancellationToken) =>
            Task.Delay(duration, _timeProvider, cancellationToken));
    }

    public bool IsAvailable => _callbacks.IsAvailable;

    public async Task<PackageCallbackSessionStatus> RunAsync(
        string callbackHandlerId,
        IReadOnlyDictionary<string, string>? parameters,
        string flowName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(callbackHandlerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(flowName);
        cancellationToken.ThrowIfCancellationRequested();
        if (!_callbacks.IsAvailable)
        {
            throw new InvalidOperationException(
                $"{flowName} cannot start because the App callback capability is unavailable.");
        }

        var status = await _callbacks.StartAsync(
            callbackHandlerId,
            parameters,
            cancellationToken).ConfigureAwait(false);
        if (status.State != PackageCallbackSessionState.Pending)
        {
            return RequireCompleted(status, flowName);
        }

        if (status.LaunchUri is null || !status.LaunchUri.IsAbsoluteUri)
        {
            throw new InvalidOperationException(
                $"{flowName} did not return a valid browser launch URI.{FormatDetail(status.Message)}");
        }

        ThrowIfExpired(status, flowName);
        await _callbacks.OpenLaunchUriAsync(status.LaunchUri, cancellationToken).ConfigureAwait(false);

        while (status.State == PackageCallbackSessionState.Pending)
        {
            var remaining = status.ExpiresAtUtc - _timeProvider.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
            {
                throw Expired(flowName, status.Message);
            }

            await _delay(
                remaining < _pollInterval ? remaining : _pollInterval,
                cancellationToken).ConfigureAwait(false);
            status = await _callbacks.GetStatusAsync(
                status.CallbackSessionId,
                cancellationToken).ConfigureAwait(false);
        }

        return RequireCompleted(status, flowName);
    }

    private static PackageCallbackSessionStatus RequireCompleted(
        PackageCallbackSessionStatus status,
        string flowName)
        => status.State switch
        {
            PackageCallbackSessionState.Completed => status,
            PackageCallbackSessionState.Failed => throw new InvalidOperationException(
                $"{flowName} failed.{FormatDetail(status.Message)}"),
            PackageCallbackSessionState.Cancelled => throw new InvalidOperationException(
                $"{flowName} was cancelled.{FormatDetail(status.Message)}"),
            PackageCallbackSessionState.Expired => throw Expired(flowName, status.Message),
            _ => throw new InvalidOperationException(
                $"{flowName} ended with unsupported status '{status.State}'.{FormatDetail(status.Message)}"),
        };

    private void ThrowIfExpired(PackageCallbackSessionStatus status, string flowName)
    {
        if (status.State == PackageCallbackSessionState.Expired
            || (status.State == PackageCallbackSessionState.Pending
                && status.ExpiresAtUtc <= _timeProvider.GetUtcNow()))
        {
            throw Expired(flowName, status.Message);
        }
    }

    private static TimeoutException Expired(string flowName, string? detail)
        => new($"{flowName} expired before it completed.{FormatDetail(detail)}");

    private static string FormatDetail(string? detail)
        => string.IsNullOrWhiteSpace(detail) ? string.Empty : $" {detail.Trim()}";
}
