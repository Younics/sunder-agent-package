using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Models;
using Sunder.Sdk.Services;

namespace Sunder.Package.Agent.Services;

[SunderService(SunderServiceExposure.Public, Key = "runtime-catalog", ContractId = "younics.ai.agent.runtime.catalog", ServiceId = "runtime-catalog", IncludeSchemaDialect = true)]
[SunderServiceProvider(ProviderId = "agent.runtime.catalog")]
[SunderAppUse(Actions = ["discover", "invoke", "subscribe"])]
[SunderRuntimeUse(Required = true, Actions = ["discover", "invoke", "subscribe"])]
public sealed partial class AgentRuntimeCatalog(
    AgentSessionService sessions,
    AgentProfileService profiles,
    AgentWorkspaceService workspaces)
{
    [SunderMethod(Idempotency = SunderServiceIdempotency.Idempotent)]
    public ValueTask<IReadOnlyList<AgentSessionRecord>> ListSessionsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(sessions.ListSessions());
    }

    [SunderMethod(Idempotency = SunderServiceIdempotency.Idempotent)]
    public ValueTask<IReadOnlyList<AgentSessionRecord>> ListSessionsForProfileAsync(string profileId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IReadOnlyList<AgentSessionRecord>>(string.IsNullOrWhiteSpace(profileId)
            ? [] : sessions.ListSessions().Where(session => string.Equals(session.ProfileId, profileId, StringComparison.OrdinalIgnoreCase)).ToArray());
    }

    [SunderMethod(Idempotency = SunderServiceIdempotency.Idempotent)]
    public ValueTask<IReadOnlyList<AgentSessionRecord>> ListSessionsForWorkspaceAsync(string workspaceId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IReadOnlyList<AgentSessionRecord>>(string.IsNullOrWhiteSpace(workspaceId)
            ? [] : sessions.ListSessionsForWorkspace(workspaceId));
    }

    [SunderMethod(Idempotency = SunderServiceIdempotency.Idempotent)]
    public ValueTask<AgentSessionRecord?> GetSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(sessions.GetSession(sessionId));
    }

    [SunderMethod(Idempotency = SunderServiceIdempotency.Idempotent)]
    public async ValueTask<IReadOnlyList<AgentWorkspaceRecord>> ListWorkspacesAsync(CancellationToken cancellationToken = default)
        => await workspaces.ListWorkspacesAsync(cancellationToken).ConfigureAwait(false);

    [SunderMethod(Idempotency = SunderServiceIdempotency.Idempotent)]
    public async ValueTask<AgentWorkspaceRecord?> GetWorkspaceAsync(string workspaceId, CancellationToken cancellationToken = default)
        => await workspaces.GetWorkspaceAsync(workspaceId, cancellationToken).ConfigureAwait(false);

    [SunderMethod(Idempotency = SunderServiceIdempotency.Idempotent)]
    public ValueTask<AgentProfileRecord?> GetSessionProfileAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var profileId = sessions.GetSession(sessionId)?.ProfileId;
        return ValueTask.FromResult(string.IsNullOrWhiteSpace(profileId) ? null : profiles.GetProfile(profileId));
    }

    [SunderMethod(Idempotency = SunderServiceIdempotency.Idempotent)]
    public ValueTask<AgentWorkingSummaryRecord?> GetWorkingSummaryAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(sessions.GetWorkingSummary(sessionId));
    }

    [SunderMethod(Idempotency = SunderServiceIdempotency.Idempotent)]
    public ValueTask<AgentSessionContextCheckpointRecord?> GetSessionContextCheckpointAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(sessions.GetLatestSessionContextCheckpoint(sessionId));
    }

    [SunderMethod(Idempotency = SunderServiceIdempotency.Idempotent)]
    public ValueTask<AgentRunCheckpointRecord?> GetLatestCheckpointAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(sessions.GetLatestCheckpoint(sessionId));
    }

    [SunderMethod(Idempotency = SunderServiceIdempotency.Idempotent)]
    public ValueTask<AgentRunActivityUpdate?> GetRunActivityAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(sessions.GetRunActivity(sessionId));
    }

    [SunderMethod(Idempotency = SunderServiceIdempotency.Idempotent)]
    public ValueTask<IReadOnlyList<AgentTurnRecord>> ListRecentTurnsAsync(Guid sessionId,
        [SunderValueConstraints(Minimum = "1", Maximum = "1000")] int limit, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(sessions.ListRecentTurns(sessionId, limit));
    }

    [SunderMethod(Idempotency = SunderServiceIdempotency.Idempotent)]
    public ValueTask<IReadOnlyList<AgentTurnRecord>> ListTurnsBeforeAsync(Guid sessionId, DateTimeOffset createdAtUtc, Guid turnId,
        [SunderValueConstraints(Minimum = "1", Maximum = "1000")] int limit, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(sessions.ListTurnsBefore(sessionId, createdAtUtc, turnId, limit));
    }

    [SunderMethod(Idempotency = SunderServiceIdempotency.Idempotent)]
    public ValueTask<IReadOnlyList<AgentTurnRecord>> ListTurnsAfterAsync(Guid sessionId, DateTimeOffset createdAtUtc, Guid turnId,
        [SunderValueConstraints(Minimum = "1", Maximum = "1000")] int limit, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(sessions.ListTurnsAfter(sessionId, createdAtUtc, turnId, limit));
    }

    [SunderMethod(Idempotency = SunderServiceIdempotency.Idempotent)]
    public ValueTask<IReadOnlyList<AgentTurnRecord>> ListRecentTranscriptHeadersAsync(Guid sessionId,
        [SunderValueConstraints(Minimum = "1", Maximum = "1000")] int limit, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(sessions.ListRecentTranscriptHeaders(sessionId, limit));
    }

    [SunderMethod(Idempotency = SunderServiceIdempotency.Idempotent)]
    public ValueTask<IReadOnlyList<AgentTurnRecord>> ListTranscriptHeadersBeforeAsync(Guid sessionId, DateTimeOffset createdAtUtc, Guid turnId,
        [SunderValueConstraints(Minimum = "1", Maximum = "1000")] int limit, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(sessions.ListTranscriptHeadersBefore(sessionId, createdAtUtc, turnId, limit));
    }

    [SunderMethod(Idempotency = SunderServiceIdempotency.Idempotent)]
    public ValueTask<IReadOnlyList<AgentTurnRecord>> ListTranscriptHeadersAfterAsync(Guid sessionId, DateTimeOffset createdAtUtc, Guid turnId,
        [SunderValueConstraints(Minimum = "1", Maximum = "1000")] int limit, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(sessions.ListTranscriptHeadersAfter(sessionId, createdAtUtc, turnId, limit));
    }

    [SunderMethod(Idempotency = SunderServiceIdempotency.Idempotent)]
    public ValueTask<AgentTranscriptToolDetailRecord?> GetTranscriptToolDetailAsync(AgentTranscriptToolDetailRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(sessions.GetTranscriptToolDetail(request));
    }

    [SunderMethod(Idempotency = SunderServiceIdempotency.Idempotent)]
    public ValueTask<AgentCompactTranscriptPage> ReadCompactTranscriptAsync(
        AgentCompactTranscriptRequest request, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(Sunder.Package.Agent.Shared.PackageViews.TranscriptCompactPayload.Fit(
            sessions.Store.ReadCompactTranscript(request, cancellationToken), request.Direction));

    [SunderMethod(Idempotency = SunderServiceIdempotency.Idempotent)]
    public ValueTask<AgentTranscriptActivityMembersPage> ReadActivityMembersAsync(
        AgentTranscriptActivityMembersRequest request, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(Sunder.Package.Agent.Shared.PackageViews.TranscriptCompactPayload.Fit(
            sessions.Store.ReadActivityMembers(request, cancellationToken)));

    [SunderMethod(Idempotency = SunderServiceIdempotency.Idempotent)]
    public ValueTask<IReadOnlyList<AgentProfileRecord>> ListProfilesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(profiles.ListProfiles());
    }

    [SunderMethod(Idempotency = SunderServiceIdempotency.Idempotent)]
    public ValueTask<AgentProfileRecord?> GetProfileAsync(string profileId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(profiles.GetProfile(profileId));
    }

    [SunderMethod(Idempotency = SunderServiceIdempotency.Idempotent)]
    public ValueTask<AgentProfileModelBindingRecord?> GetSessionModelBindingAsync(Guid sessionId, string capabilityKind, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var profileId = sessions.GetSession(sessionId)?.ProfileId;
        return ValueTask.FromResult(string.IsNullOrWhiteSpace(profileId) ? null : profiles.GetModelBinding(profileId, capabilityKind));
    }

    [SunderMethod(Idempotency = SunderServiceIdempotency.Idempotent)]
    public ValueTask<AgentProfileModelBindingRecord?> GetModelBindingAsync(string profileId, string capabilityKind, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(profiles.GetModelBinding(profileId, capabilityKind));
    }

    [SunderMethod(Idempotency = SunderServiceIdempotency.Idempotent)]
    public async IAsyncEnumerable<AgentRuntimeCatalogEvent> WatchAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
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
            lock (publishSync) channel.Writer.TryWrite((++sequence, create(sequence)));
        }
        void Session(Guid id) => Publish(next => new AgentSessionCatalogEvent(next, id));
        void Turn(Guid id, AgentTurnRecord turn) => Publish(next => new AgentTurnCatalogEvent(next, id, turn)
        {
            ActivityGroup = turn.Kind is AgentTurnKind.ToolCall or AgentTurnKind.ToolResult
                ? sessions.Store.ReadActivityGroupForTurn(id, turn.TurnId) : null,
        });
        void Profile(string id) => Publish(next => new AgentProfileCatalogEvent(next, id));
        void Activity(Guid id, AgentRunActivityUpdate activity) => Publish(next => new AgentRunActivityCatalogEvent(next, id, activity));
        void Reset(Guid _) => Publish(next => new AgentRuntimeCatalogResetEvent(next));
        Publish(next => new AgentRuntimeCatalogResetEvent(next));
        sessions.SessionChanged += Session;
        sessions.TurnChanged += Turn;
        sessions.TranscriptReset += Reset;
        sessions.RunActivityChanged += Activity;
        profiles.ProfileChanged += Profile;
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
            sessions.SessionChanged -= Session;
            sessions.TurnChanged -= Turn;
            sessions.TranscriptReset -= Reset;
            sessions.RunActivityChanged -= Activity;
            profiles.ProfileChanged -= Profile;
            channel.Writer.TryComplete();
        }
    }
}
