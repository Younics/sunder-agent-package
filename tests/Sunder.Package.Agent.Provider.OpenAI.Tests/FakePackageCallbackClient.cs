using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Callbacks;

namespace Sunder.Package.Agent.Provider.OpenAI.Tests;

internal sealed class FakePackageCallbackClient : IPackageCallbackClient
{
    private readonly Queue<PackageCallbackSessionStatus> _statuses = new();

    public bool IsAvailable { get; set; } = true;
    public int StartCount { get; private set; }
    public int StatusCount { get; private set; }
    public int OpenCount { get; private set; }
    public string? StartedHandlerId { get; private set; }
    public IReadOnlyDictionary<string, string>? StartedParameters { get; private set; }
    public Uri? OpenedUri { get; private set; }
    public Func<CancellationToken, ValueTask<PackageCallbackSessionStatus>>? StartOperation { get; set; }

    public void Enqueue(params PackageCallbackSessionStatus[] statuses)
    {
        foreach (var status in statuses)
        {
            _statuses.Enqueue(status);
        }
    }

    public ValueTask<PackageCallbackSessionStatus> StartAsync(
        string callbackHandlerId,
        IReadOnlyDictionary<string, string>? parameters = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StartCount++;
        StartedHandlerId = callbackHandlerId;
        StartedParameters = parameters;
        return StartOperation?.Invoke(cancellationToken)
            ?? ValueTask.FromResult(NextStatus());
    }

    public ValueTask<PackageCallbackSessionStatus> GetStatusAsync(
        string callbackSessionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StatusCount++;
        return ValueTask.FromResult(NextStatus());
    }

    public ValueTask OpenLaunchUriAsync(Uri launchUri, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        OpenCount++;
        OpenedUri = launchUri;
        return ValueTask.CompletedTask;
    }

    private PackageCallbackSessionStatus NextStatus()
        => _statuses.Count > 0
            ? _statuses.Dequeue()
            : throw new InvalidOperationException("No fake callback status was queued.");
}
