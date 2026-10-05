using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Rpc.Public.Catalog;
using Sunder.Sdk.Rpc;
using Domain = Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Rpc.Public;

[SunderRpcService(SunderRpcExposure.Public, ContractId = "sunder.agent.runtime.catalog",
    ServiceId = "runtime-catalog", IncludeSchemaDialect = true)]
[SunderRpcProvider(ProviderId = "agent.runtime.catalog")]
[SunderRpcRuntimeUse(Required = true, Actions = ["discover", "invoke", "subscribe"])]
public sealed partial class RuntimeCatalogService(IAgentRuntimeCatalog catalog)
{
    [SunderRpcMethod(Idempotency = SunderRpcMethodIdempotency.Idempotent)]
    public ValueTask<AgentSessionRecordCollection> ListSessionsAsync(Unit request, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(Sessions(catalog.ListSessions()));

    [SunderRpcMethod(Idempotency = SunderRpcMethodIdempotency.Idempotent)]
    public ValueTask<AgentSessionRecordCollection> ListSessionsForProfileAsync(StringRequest request, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(Sessions(catalog.ListSessionsForProfile(request.Value.Value)));

    [SunderRpcMethod(Idempotency = SunderRpcMethodIdempotency.Idempotent)]
    public ValueTask<AgentSessionRecordCollection> ListSessionsForWorkspaceAsync(StringRequest request, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(Sessions(catalog.ListSessionsForWorkspace(request.Value.Value)));

    [SunderRpcMethod(Idempotency = SunderRpcMethodIdempotency.Idempotent)]
    public ValueTask<SessionLookupResult> GetSessionAsync(SessionIdRequest request, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(new SessionLookupResult { Value = RuntimeCatalogMapping.Session(catalog.GetSession(request.SessionId.Value)) });

    [SunderRpcMethod(Idempotency = SunderRpcMethodIdempotency.Idempotent)]
    public async ValueTask<AgentWorkspaceRecordCollection> ListWorkspacesAsync(Unit request, CancellationToken cancellationToken = default)
        => new() { Value = (await catalog.ListWorkspacesAsync(cancellationToken).ConfigureAwait(false)).Select(AgentWorkspaceRecord.FromDomain).ToArray() };

    [SunderRpcMethod(Idempotency = SunderRpcMethodIdempotency.Idempotent)]
    public async ValueTask<WorkspaceLookupResult> GetWorkspaceAsync(StringRequest request, CancellationToken cancellationToken = default)
    {
        var value = await catalog.GetWorkspaceAsync(request.Value.Value, cancellationToken).ConfigureAwait(false);
        return new() { Value = value is null ? null : AgentWorkspaceRecord.FromDomain(value) };
    }

    [SunderRpcMethod(Idempotency = SunderRpcMethodIdempotency.Idempotent)]
    public ValueTask<ProfileLookupResult> GetSessionProfileAsync(SessionIdRequest request, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(new ProfileLookupResult { Value = RuntimeCatalogMapping.Profile(catalog.GetSessionProfile(request.SessionId.Value)) });

    [SunderRpcMethod(Idempotency = SunderRpcMethodIdempotency.Idempotent)]
    public ValueTask<WorkingSummaryLookupResult> GetWorkingSummaryAsync(SessionIdRequest request, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(new WorkingSummaryLookupResult { Value = RuntimeCatalogMapping.Summary(catalog.GetWorkingSummary(request.SessionId.Value)) });

    [SunderRpcMethod(Idempotency = SunderRpcMethodIdempotency.Idempotent)]
    public ValueTask<SessionContextCheckpointLookupResult> GetSessionContextCheckpointAsync(SessionIdRequest request, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(new SessionContextCheckpointLookupResult { Value = RuntimeCatalogMapping.ContextCheckpoint(catalog.GetLatestSessionContextCheckpoint(request.SessionId.Value)) });

    [SunderRpcMethod(Idempotency = SunderRpcMethodIdempotency.Idempotent)]
    public ValueTask<RunCheckpointLookupResult> GetLatestCheckpointAsync(SessionIdRequest request, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(new RunCheckpointLookupResult { Value = RuntimeCatalogMapping.RunCheckpoint(catalog.GetLatestCheckpoint(request.SessionId.Value)) });

    [SunderRpcMethod(Idempotency = SunderRpcMethodIdempotency.Idempotent)]
    public ValueTask<AgentTurnRecordCollection> ListRecentTurnsAsync(RecentTurnsRequest request, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(Turns(catalog.ListRecentTurns(request.SessionId.Value, checked((int)request.Limit.Value))));

    [SunderRpcMethod(Idempotency = SunderRpcMethodIdempotency.Idempotent)]
    public ValueTask<AgentTurnRecordCollection> ListTurnsBeforeAsync(TurnBoundaryRequest request, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(Turns(catalog.ListTurnsBefore(request.SessionId.Value, request.CreatedAtUtc.ToDomain(), request.TurnId.Value, checked((int)request.Limit.Value))));

    [SunderRpcMethod(Idempotency = SunderRpcMethodIdempotency.Idempotent)]
    public ValueTask<AgentTurnRecordCollection> ListTurnsAfterAsync(TurnBoundaryRequest request, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(Turns(catalog.ListTurnsAfter(request.SessionId.Value, request.CreatedAtUtc.ToDomain(), request.TurnId.Value, checked((int)request.Limit.Value))));

    [SunderRpcMethod(Idempotency = SunderRpcMethodIdempotency.Idempotent)]
    public ValueTask<AgentTurnRecordCollection> ListRecentTranscriptHeadersAsync(RecentTurnsRequest request, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(Turns(catalog is IAgentTranscriptCatalog transcript
            ? transcript.ListRecentTranscriptHeaders(request.SessionId.Value, checked((int)request.Limit.Value))
            : catalog.ListRecentTurns(request.SessionId.Value, checked((int)request.Limit.Value))));

    [SunderRpcMethod(Idempotency = SunderRpcMethodIdempotency.Idempotent)]
    public ValueTask<AgentTurnRecordCollection> ListTranscriptHeadersBeforeAsync(TurnBoundaryRequest request, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(Turns(catalog is IAgentTranscriptCatalog transcript
            ? transcript.ListTranscriptHeadersBefore(request.SessionId.Value, request.CreatedAtUtc.ToDomain(), request.TurnId.Value, checked((int)request.Limit.Value))
            : catalog.ListTurnsBefore(request.SessionId.Value, request.CreatedAtUtc.ToDomain(), request.TurnId.Value, checked((int)request.Limit.Value))));

    [SunderRpcMethod(Idempotency = SunderRpcMethodIdempotency.Idempotent)]
    public ValueTask<AgentTurnRecordCollection> ListTranscriptHeadersAfterAsync(TurnBoundaryRequest request, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(Turns(catalog is IAgentTranscriptCatalog transcript
            ? transcript.ListTranscriptHeadersAfter(request.SessionId.Value, request.CreatedAtUtc.ToDomain(), request.TurnId.Value, checked((int)request.Limit.Value))
            : catalog.ListTurnsAfter(request.SessionId.Value, request.CreatedAtUtc.ToDomain(), request.TurnId.Value, checked((int)request.Limit.Value))));

    [SunderRpcMethod(Idempotency = SunderRpcMethodIdempotency.Idempotent)]
    public ValueTask<TranscriptToolDetailLookupResult> GetTranscriptToolDetailAsync(AgentTranscriptToolDetailRequest request, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(new TranscriptToolDetailLookupResult
        {
            Value = RuntimeCatalogMapping.ToolDetail((catalog as IAgentTranscriptCatalog)?.GetTranscriptToolDetail(request.ToDomain())),
        });

    [SunderRpcMethod(Idempotency = SunderRpcMethodIdempotency.Idempotent)]
    public ValueTask<AgentProfileRecordCollection> ListProfilesAsync(Unit request, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(new AgentProfileRecordCollection { Value = catalog.ListProfiles().Select(value => RuntimeCatalogMapping.Profile(value)!).ToArray() });

    [SunderRpcMethod(Idempotency = SunderRpcMethodIdempotency.Idempotent)]
    public ValueTask<ProfileLookupResult> GetProfileAsync(StringRequest request, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(new ProfileLookupResult { Value = RuntimeCatalogMapping.Profile(catalog.GetProfile(request.Value.Value)) });

    [SunderRpcMethod(Idempotency = SunderRpcMethodIdempotency.Idempotent)]
    public ValueTask<ModelBindingLookupResult> GetSessionModelBindingAsync(SessionCapabilityRequest request, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(new ModelBindingLookupResult { Value = RuntimeCatalogMapping.ModelBinding(catalog.GetSessionModelBinding(request.SessionId.Value, request.CapabilityKind.Value)) });

    [SunderRpcMethod(Idempotency = SunderRpcMethodIdempotency.Idempotent)]
    public ValueTask<ModelBindingLookupResult> GetModelBindingAsync(ProfileCapabilityRequest request, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(new ModelBindingLookupResult { Value = RuntimeCatalogMapping.ModelBinding(catalog.GetModelBinding(request.ProfileId.Value, request.CapabilityKind.Value)) });

    [SunderRpcMethod(Idempotency = SunderRpcMethodIdempotency.Idempotent)]
    public async IAsyncEnumerable<AgentRuntimeCatalogEvent> WatchAsync(Unit request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var channel = Channel.CreateBounded<(long Sequence, Func<long, AgentRuntimeCatalogEvent> Create)>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = false,
        });
        var publishSync = new object();
        long sequence = 0;
        void Publish(Func<long, AgentRuntimeCatalogEvent> create)
        {
            lock (publishSync) channel.Writer.TryWrite((++sequence, create));
        }
        void Session(Guid id) => Publish(next => new AgentSessionCatalogEvent { Sequence = next, SessionId = id });
        void Turn(Guid id, Domain.AgentTurnRecord turn) => Publish(next => new AgentTurnCatalogEvent { Sequence = next, SessionId = id, Turn = RuntimeCatalogMapping.Turn(turn) });
        void Profile(string id) => Publish(next => new AgentProfileCatalogEvent { Sequence = next, ProfileId = id });
        void Reset() => Publish(next => new AgentRuntimeCatalogResetEvent { Sequence = next });
        Reset();
        catalog.SessionChanged += Session;
        catalog.TurnChanged += Turn;
        catalog.ProfileChanged += Profile;
        catalog.ResetRequired += Reset;
        try
        {
            var expectedSequence = 1L;
            await foreach (var item in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return item.Sequence == expectedSequence ? item.Create(item.Sequence) : new AgentRuntimeCatalogResetEvent { Sequence = item.Sequence };
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
    }

    private static AgentSessionRecordCollection Sessions(IReadOnlyList<Domain.AgentSessionRecord> values)
        => new() { Value = values.Select(value => RuntimeCatalogMapping.Session(value)!).ToArray() };
    private static AgentTurnRecordCollection Turns(IReadOnlyList<Domain.AgentTurnRecord> values)
        => new() { Value = values.Select(RuntimeCatalogMapping.Turn).ToArray() };
}
