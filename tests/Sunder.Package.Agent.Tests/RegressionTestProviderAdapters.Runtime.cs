using System.Runtime.CompilerServices;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Rpc.Public;
using AgentServices = Sunder.Generated.Packages.Sunder_Package_Agent_Services;

namespace Sunder.Package.Agent.Tests;

internal sealed class RegressionTestRuntimeCatalogProvider : AgentServices.IRuntimeCatalogService
{
    private readonly AgentServices.IRuntimeCatalogService? _service;
    private readonly IAgentRuntimeCatalog? _catalog;
    private readonly Type? _transcriptContract;

    public RegressionTestRuntimeCatalogProvider(AgentServices.IRuntimeCatalogService service) => _service = service;

    public RegressionTestRuntimeCatalogProvider(IAgentRuntimeCatalog catalog)
    {
        _catalog = catalog;
        _transcriptContract = catalog.GetType().GetInterfaces().FirstOrDefault(type =>
            type.FullName == "Sunder.Package.Agent.Contracts.Contracts.IAgentTranscriptCatalog");
    }

    public ValueTask<IReadOnlyList<AgentSessionRecord>> ListSessionsAsync(CancellationToken cancellationToken = default)
        => _service?.ListSessionsAsync(cancellationToken) ?? FromCatalog(static catalog => catalog.ListSessions(), cancellationToken);
    public ValueTask<IReadOnlyList<AgentSessionRecord>> ListSessionsForProfileAsync(string profileId, CancellationToken cancellationToken = default)
        => _service?.ListSessionsForProfileAsync(profileId, cancellationToken) ?? FromCatalog(catalog => catalog.ListSessionsForProfile(profileId), cancellationToken);
    public ValueTask<IReadOnlyList<AgentSessionRecord>> ListSessionsForWorkspaceAsync(string workspaceId, CancellationToken cancellationToken = default)
        => _service?.ListSessionsForWorkspaceAsync(workspaceId, cancellationToken) ?? FromCatalog(catalog => catalog.ListSessionsForWorkspace(workspaceId), cancellationToken);
    public ValueTask<AgentSessionRecord?> GetSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
        => _service?.GetSessionAsync(sessionId, cancellationToken) ?? FromCatalog(catalog => catalog.GetSession(sessionId), cancellationToken);
    public ValueTask<IReadOnlyList<AgentWorkspaceRecord>> ListWorkspacesAsync(CancellationToken cancellationToken = default)
        => _service?.ListWorkspacesAsync(cancellationToken) ?? _catalog!.ListWorkspacesAsync(cancellationToken);
    public ValueTask<AgentWorkspaceRecord?> GetWorkspaceAsync(string workspaceId, CancellationToken cancellationToken = default)
        => _service?.GetWorkspaceAsync(workspaceId, cancellationToken) ?? _catalog!.GetWorkspaceAsync(workspaceId, cancellationToken);
    public ValueTask<AgentProfileRecord?> GetSessionProfileAsync(Guid sessionId, CancellationToken cancellationToken = default)
        => _service?.GetSessionProfileAsync(sessionId, cancellationToken) ?? FromCatalog(catalog => catalog.GetSessionProfile(sessionId), cancellationToken);
    public ValueTask<AgentWorkingSummaryRecord?> GetWorkingSummaryAsync(Guid sessionId, CancellationToken cancellationToken = default)
        => _service?.GetWorkingSummaryAsync(sessionId, cancellationToken) ?? FromCatalog(catalog => catalog.GetWorkingSummary(sessionId), cancellationToken);
    public ValueTask<AgentSessionContextCheckpointRecord?> GetSessionContextCheckpointAsync(Guid sessionId, CancellationToken cancellationToken = default)
        => _service?.GetSessionContextCheckpointAsync(sessionId, cancellationToken) ?? FromCatalog(catalog => catalog.GetLatestSessionContextCheckpoint(sessionId), cancellationToken);
    public ValueTask<AgentRunCheckpointRecord?> GetLatestCheckpointAsync(Guid sessionId, CancellationToken cancellationToken = default)
        => _service?.GetLatestCheckpointAsync(sessionId, cancellationToken) ?? FromCatalog(catalog => catalog.GetLatestCheckpoint(sessionId), cancellationToken);
    public ValueTask<IReadOnlyList<AgentTurnRecord>> ListRecentTurnsAsync(Guid sessionId, int limit, CancellationToken cancellationToken = default)
        => _service?.ListRecentTurnsAsync(sessionId, limit, cancellationToken) ?? FromCatalog(catalog => catalog.ListRecentTurns(sessionId, limit), cancellationToken);
    public ValueTask<IReadOnlyList<AgentTurnRecord>> ListTurnsBeforeAsync(Guid sessionId, DateTimeOffset createdAtUtc, Guid turnId, int limit, CancellationToken cancellationToken = default)
        => _service?.ListTurnsBeforeAsync(sessionId, createdAtUtc, turnId, limit, cancellationToken) ?? FromCatalog(catalog => catalog.ListTurnsBefore(sessionId, createdAtUtc, turnId, limit), cancellationToken);
    public ValueTask<IReadOnlyList<AgentTurnRecord>> ListTurnsAfterAsync(Guid sessionId, DateTimeOffset createdAtUtc, Guid turnId, int limit, CancellationToken cancellationToken = default)
        => _service?.ListTurnsAfterAsync(sessionId, createdAtUtc, turnId, limit, cancellationToken) ?? FromCatalog(catalog => catalog.ListTurnsAfter(sessionId, createdAtUtc, turnId, limit), cancellationToken);
    public ValueTask<IReadOnlyList<AgentTurnRecord>> ListRecentTranscriptHeadersAsync(Guid sessionId, int limit, CancellationToken cancellationToken = default)
        => _service?.ListRecentTranscriptHeadersAsync(sessionId, limit, cancellationToken) ?? FromTranscript<IReadOnlyList<AgentTurnRecord>>("ListRecentTranscriptHeaders", [sessionId, limit], [], cancellationToken);
    public ValueTask<IReadOnlyList<AgentTurnRecord>> ListTranscriptHeadersBeforeAsync(Guid sessionId, DateTimeOffset createdAtUtc, Guid turnId, int limit, CancellationToken cancellationToken = default)
        => _service?.ListTranscriptHeadersBeforeAsync(sessionId, createdAtUtc, turnId, limit, cancellationToken) ?? FromTranscript<IReadOnlyList<AgentTurnRecord>>("ListTranscriptHeadersBefore", [sessionId, createdAtUtc, turnId, limit], [], cancellationToken);
    public ValueTask<IReadOnlyList<AgentTurnRecord>> ListTranscriptHeadersAfterAsync(Guid sessionId, DateTimeOffset createdAtUtc, Guid turnId, int limit, CancellationToken cancellationToken = default)
        => _service?.ListTranscriptHeadersAfterAsync(sessionId, createdAtUtc, turnId, limit, cancellationToken) ?? FromTranscript<IReadOnlyList<AgentTurnRecord>>("ListTranscriptHeadersAfter", [sessionId, createdAtUtc, turnId, limit], [], cancellationToken);
    public ValueTask<AgentTranscriptToolDetailRecord?> GetTranscriptToolDetailAsync(AgentTranscriptToolDetailRequest request, CancellationToken cancellationToken = default)
        => _service?.GetTranscriptToolDetailAsync(request, cancellationToken) ?? FromTranscript<AgentTranscriptToolDetailRecord?>("GetTranscriptToolDetail", [request], null, cancellationToken);
    public ValueTask<IReadOnlyList<AgentProfileRecord>> ListProfilesAsync(CancellationToken cancellationToken = default)
        => _service?.ListProfilesAsync(cancellationToken) ?? FromCatalog(static catalog => catalog.ListProfiles(), cancellationToken);
    public ValueTask<AgentProfileRecord?> GetProfileAsync(string profileId, CancellationToken cancellationToken = default)
        => _service?.GetProfileAsync(profileId, cancellationToken) ?? FromCatalog(catalog => catalog.GetProfile(profileId), cancellationToken);
    public ValueTask<AgentProfileModelBindingRecord?> GetSessionModelBindingAsync(Guid sessionId, string capabilityKind, CancellationToken cancellationToken = default)
        => _service?.GetSessionModelBindingAsync(sessionId, capabilityKind, cancellationToken) ?? FromCatalog(catalog => catalog.GetSessionModelBinding(sessionId, capabilityKind), cancellationToken);
    public ValueTask<AgentProfileModelBindingRecord?> GetModelBindingAsync(string profileId, string capabilityKind, CancellationToken cancellationToken = default)
        => _service?.GetModelBindingAsync(profileId, capabilityKind, cancellationToken) ?? FromCatalog(catalog => catalog.GetModelBinding(profileId, capabilityKind), cancellationToken);

    public async IAsyncEnumerable<AgentRuntimeCatalogEvent> WatchAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (_service is { } service)
        {
            await foreach (var item in service.WatchAsync(cancellationToken).ConfigureAwait(false))
                yield return item;
            yield break;
        }

        var catalog = _catalog!;
        var channel = Channel.CreateBounded<(long Sequence, AgentRuntimeCatalogEvent Event)>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
        var publishSync = new object();
        long sequence = 0;
        void Publish(Func<long, AgentRuntimeCatalogEvent> create)
        {
            lock (publishSync)
                channel.Writer.TryWrite((++sequence, create(sequence)));
        }

        void Session(Guid id) => Publish(next => new AgentSessionCatalogEvent(next, id));
        void Turn(Guid id, AgentTurnRecord turn) => Publish(next => new AgentTurnCatalogEvent(next, id, turn));
        void Profile(string id) => Publish(next => new AgentProfileCatalogEvent(next, id));

        Publish(next => new AgentRuntimeCatalogResetEvent(next));
        catalog.SessionChanged += Session;
        catalog.TurnChanged += Turn;
        catalog.ProfileChanged += Profile;
        catalog.ResetRequired += Reset;
        try
        {
            var expectedSequence = 1L;
            await foreach (var item in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return item.Sequence == expectedSequence ? item.Event : new AgentRuntimeCatalogResetEvent(item.Sequence);
                expectedSequence = item.Sequence + 1;
            }
        }
        finally
        {
            catalog.SessionChanged -= Session;
            catalog.TurnChanged -= Turn;
            catalog.ProfileChanged -= Profile;
            catalog.ResetRequired -= Reset;
            channel.Writer.TryComplete();
        }

        void Reset() => Publish(next => new AgentRuntimeCatalogResetEvent(next));
    }

    private ValueTask<T> FromCatalog<T>(Func<IAgentRuntimeCatalog, T> select, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(select(_catalog!));
    }

    private ValueTask<T> FromTranscript<T>(string method, object?[] arguments, T unavailable, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_transcriptContract is null) return ValueTask.FromResult(unavailable);
        try { return ValueTask.FromResult((T)_transcriptContract.GetMethod(method)!.Invoke(_catalog, arguments)!); }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }
}

internal sealed class RegressionTestSessionCleanerProvider(IAgentSessionDataCleaner cleaner) : AgentServices.ISessionCleanerService
{
    public ValueTask<AgentServiceIdentity> DescribeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new AgentServiceIdentity(cleaner.CleanerId, cleaner.CleanerId));
    }

    public ValueTask DeleteSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        cleaner.DeleteSessionData(sessionId);
        return ValueTask.CompletedTask;
    }
}

internal sealed class RegressionTestDurableLifecycleProvider(IAgentDurableLifecycleObserver observer) : AgentServices.IDurableLifecycleObserverService
{
    public ValueTask<AgentServiceIdentity> DescribeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new AgentServiceIdentity(observer.ObserverId, observer.DisplayName));
    }

    public ValueTask HandleAsync(AgentDurableLifecycleEventEnvelope request, CancellationToken cancellationToken = default)
        => observer.HandleDurableLifecycleEventAsync(request, cancellationToken);
}
