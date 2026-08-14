using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Memory.Semantic.Runtime;
using Sunder.Package.Agent.Memory.Semantic.Services;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Memory.Semantic;

internal sealed class MemoryAppRuntimeGateway(ISunderRpcClient rpc) : IMemoryInspectorGateway, IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private int _observationStarted;
    private int _disposed;

    public event Action<Guid>? SessionChanged;
    public event Action? SemanticWorkerStatusChanged;

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.Exchange(ref _observationStarted, 1) == 0)
        {
            _ = ObserveChangesAsync(_lifetime.Token);
        }
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<AgentSessionRecord>> ListSessionsAsync(CancellationToken cancellationToken = default)
        => (await QueryAsync(new(MemoryQueryKind.Sessions), cancellationToken).ConfigureAwait(false)).Sessions ?? [];

    public async Task<AgentSessionContextCheckpointRecord?> GetSessionContextCheckpointAsync(Guid sessionId, CancellationToken cancellationToken = default)
        => (await QueryAsync(new(MemoryQueryKind.SessionCheckpoint, SessionId: sessionId), cancellationToken).ConfigureAwait(false)).Checkpoint;

    public async Task<AgentWorkingSummaryRecord?> GetWorkingSummaryAsync(Guid sessionId, CancellationToken cancellationToken = default)
        => (await QueryAsync(new(MemoryQueryKind.WorkingSummary, SessionId: sessionId), cancellationToken).ConfigureAwait(false)).WorkingSummary;

    public async Task<IReadOnlyList<StoredMemoryRecord>> ListMemoriesAsync(Guid sessionId, string? searchText = null, bool includeInactive = false, CancellationToken cancellationToken = default)
        => (await QueryAsync(new(MemoryQueryKind.Memories, SessionId: sessionId, SearchText: searchText, IncludeInactive: includeInactive), cancellationToken).ConfigureAwait(false)).Memories ?? [];

    public async Task<IReadOnlyList<StoredMemoryEvidenceRecord>> ListEvidenceAsync(Guid memoryId, CancellationToken cancellationToken = default)
        => (await DetailsAsync(memoryId, cancellationToken).ConfigureAwait(false)).Evidence ?? [];

    public async Task<StoredMemoryRecord?> GetSupersedingMemoryAsync(Guid memoryId, CancellationToken cancellationToken = default)
        => (await DetailsAsync(memoryId, cancellationToken).ConfigureAwait(false)).SupersedingMemory;

    public async Task<IReadOnlyList<StoredMemoryRecord>> ListSupersededMemoriesAsync(Guid memoryId, CancellationToken cancellationToken = default)
        => (await DetailsAsync(memoryId, cancellationToken).ConfigureAwait(false)).SupersededMemories ?? [];

    public async Task<IReadOnlyList<StoredMemoryRecord>> ListCorrectionLineageAsync(Guid memoryId, CancellationToken cancellationToken = default)
        => (await DetailsAsync(memoryId, cancellationToken).ConfigureAwait(false)).CorrectionLineage ?? [];

    public async Task<StoredMemoryRecord> UpdateMemoryAsync(Guid memoryId, string category, string content, string? note, CancellationToken cancellationToken = default)
        => (await CommandAsync(new(MemoryCommandKind.Update, memoryId, category, content, note), cancellationToken).ConfigureAwait(false)).Memory!;

    public async Task<StoredMemoryRecord> SetPinnedAsync(Guid memoryId, bool isPinned, CancellationToken cancellationToken = default)
        => (await CommandAsync(new(MemoryCommandKind.Pin, memoryId, IsPinned: isPinned), cancellationToken).ConfigureAwait(false)).Memory!;

    public async Task<StoredMemoryRecord> ContestMemoryAsync(Guid memoryId, CancellationToken cancellationToken = default)
        => (await CommandAsync(new(MemoryCommandKind.Contest, memoryId), cancellationToken).ConfigureAwait(false)).Memory!;

    public async Task<StoredMemoryRecord> ForgetMemoryAsync(Guid memoryId, CancellationToken cancellationToken = default)
        => (await CommandAsync(new(MemoryCommandKind.Forget, memoryId), cancellationToken).ConfigureAwait(false)).Memory!;

    public async Task<StoredMemoryRecord> SupersedeMemoryAsync(Guid memoryId, CancellationToken cancellationToken = default)
        => (await CommandAsync(new(MemoryCommandKind.Supersede, memoryId), cancellationToken).ConfigureAwait(false)).Memory!;

    public async Task<MemoryCorrectionResult> CreateCorrectedMemoryAsync(Guid sourceMemoryId, string category, string content, CancellationToken cancellationToken = default)
        => (await CommandAsync(new(MemoryCommandKind.Correct, sourceMemoryId, category, content), cancellationToken).ConfigureAwait(false)).Correction!;

    public async Task<MemorySemanticIndexStatusRecord> GetSemanticIndexStatusAsync(StoredMemoryRecord memory, SemanticEmbeddingContext? context, CancellationToken cancellationToken = default)
        => (await QueryAsync(new(MemoryQueryKind.SemanticIndexStatus, Memory: memory, SemanticContext: context), cancellationToken).ConfigureAwait(false)).IndexStatus!;

    public async Task<SemanticMemorySessionStateRecord> GetSemanticSessionStateAsync(Guid sessionId, string? profileId = null, CancellationToken cancellationToken = default)
        => (await QueryAsync(new MemoryQuery(MemoryQueryKind.SemanticState, SessionId: sessionId, ProfileId: profileId), cancellationToken).ConfigureAwait(false)).SemanticState!;

    public async Task<SemanticMemoryReindexResult> ReindexSessionAsync(Guid sessionId, string? profileId = null, CancellationToken cancellationToken = default)
        => (await CommandAsync(new MemoryCommand(MemoryCommandKind.Reindex, sessionId, ProfileId: profileId), cancellationToken).ConfigureAwait(false)).ReindexResult!;

    public async Task<SemanticMemoryWorkerStatusRecord> GetSemanticWorkerStatusAsync(CancellationToken cancellationToken = default)
        => (await QueryAsync(new(MemoryQueryKind.WorkerStatus), cancellationToken).ConfigureAwait(false)).WorkerStatus!;

    public async Task<SemanticMemoryMetricsSnapshot> GetMetricsSnapshotAsync(CancellationToken cancellationToken = default)
        => (await QueryAsync(new(MemoryQueryKind.Metrics), cancellationToken).ConfigureAwait(false)).Metrics!;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    private Task<MemoryProjection> DetailsAsync(Guid memoryId, CancellationToken cancellationToken)
        => QueryAsync(new(MemoryQueryKind.Details, MemoryId: memoryId), cancellationToken);

    private Task<MemoryProjection> QueryAsync(MemoryQuery request, CancellationToken cancellationToken)
        => InvokeAsync((client, token) => client.QueryAsync(request, token), cancellationToken);

    private Task<MemoryProjection> CommandAsync(MemoryCommand request, CancellationToken cancellationToken)
        => InvokeAsync((client, token) => client.CommandAsync(request, token), cancellationToken);

    private async Task<MemoryProjection> InvokeAsync(
        Func<MemoryInspectorRpcClient, CancellationToken, ValueTask<MemoryProjection>> invoke,
        CancellationToken cancellationToken)
    {
        using var invocation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var provider = await ResolveProviderAsync(invocation.Token).ConfigureAwait(false);
        await using var scope = await rpc.CreateCallScopeAsync(cancellationToken: invocation.Token).ConfigureAwait(false);
        return await invoke(MemoryInspectorRpc.CreateClient(scope, provider.Endpoint), invocation.Token).ConfigureAwait(false);
    }

    private async Task ObserveChangesAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var provider = await ResolveProviderAsync(cancellationToken).ConfigureAwait(false);
                await using var scope = await rpc.CreateCallScopeAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                var client = MemoryInspectorRpc.CreateClient(scope, provider.Endpoint);
                await foreach (var change in client.ChangesAsync(new MemoryChangeSubscription(), cancellationToken).ConfigureAwait(false))
                {
                    if (change.Kind == MemoryChangeKind.Session && change.SessionId is { } sessionId)
                    {
                        SessionChanged?.Invoke(sessionId);
                    }
                    if (change.Kind == MemoryChangeKind.Worker)
                    {
                        SemanticWorkerStatusChanged?.Invoke();
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                // Provider replacement is recovered by fresh owner discovery below.
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async ValueTask<SunderRpcProviderSnapshot> ResolveProviderAsync(CancellationToken cancellationToken)
    {
        var descriptor = MemorySemanticProtocol.Inspector;
        var catalog = await rpc.DiscoverAsync(descriptor.ContractId, cancellationToken).ConfigureAwait(false);
        var provider = catalog.Providers.SingleOrDefault(candidate =>
            candidate.ProviderId == MemorySemanticProtocol.ProviderId
            && candidate.State == SunderRpcProviderState.Active
            && candidate.ContractVersion == descriptor.Version
            && candidate.DescriptorSha256 == descriptor.Sha256
            && candidate.AbiSha256 == descriptor.AbiSha256);
        return provider ?? throw new SunderRpcException(new SunderRpcError(
            SunderRpcErrorKind.Unavailable,
            "semantic-memory.inspector-unavailable",
            "The semantic memory inspector Runtime provider is unavailable."));
    }
}
