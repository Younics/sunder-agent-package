using System.Runtime.CompilerServices;
using Sunder.Package.Agent.Memory.Semantic.Runtime;
using Sunder.Package.Agent.Memory.Semantic.Services;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;

namespace Sunder.Package.Agent.Memory.Semantic;

[SunderService(Key = "management", ContractId = "sunder.agent.memory.semantic.inspector", ServiceId = "memory-inspector", IncludeSchemaDialect = true)]
[SunderServiceProvider(ProviderId = "semantic.memory.inspector")]
[SunderServiceError("memory.semantic.operation-failed", Description = "The requested semantic memory operation could not be completed.")]
public sealed partial class MemoryInspectorService
{
    [SunderMethod(Idempotency = SunderServiceIdempotency.Idempotent, Errors = ["memory.semantic.operation-failed"])]
    public ValueTask<SunderContent> SessionsAsync(CancellationToken token = default)
        => ProjectAsync(async () => new MemoryProjection(Sessions: await _modelRuntimeResolver.InvokeRuntimeCatalogAsync(
            (catalog, cancellation) => catalog.ListSessionsAsync(cancellation), (IReadOnlyList<AgentSessionRecord>)[], token).ConfigureAwait(false)), token);

    [SunderMethod(Idempotency = SunderServiceIdempotency.Idempotent, Errors = ["memory.semantic.operation-failed"])]
    public ValueTask<SunderContent> SessionCheckpointAsync(Guid sessionId, CancellationToken token = default)
        => ProjectAsync(async () => new MemoryProjection(Checkpoint: await _modelRuntimeResolver.InvokeRuntimeCatalogAsync(
            (catalog, cancellation) => catalog.GetSessionContextCheckpointAsync(sessionId, cancellation), null as AgentSessionContextCheckpointRecord, token).ConfigureAwait(false)), token);

    [SunderMethod(Idempotency = SunderServiceIdempotency.Idempotent, Errors = ["memory.semantic.operation-failed"])]
    public ValueTask<SunderContent> WorkingSummaryAsync(Guid sessionId, CancellationToken token = default)
        => ProjectAsync(async () => new MemoryProjection(WorkingSummary: await _modelRuntimeResolver.InvokeRuntimeCatalogAsync(
            (catalog, cancellation) => catalog.GetWorkingSummaryAsync(sessionId, cancellation), null as AgentWorkingSummaryRecord, token).ConfigureAwait(false)), token);

    [SunderMethod(Idempotency = SunderServiceIdempotency.Idempotent, Errors = ["memory.semantic.operation-failed"])]
    public ValueTask<SunderContent> MemoriesAsync(Guid sessionId, string? searchText, bool includeInactive, CancellationToken token = default)
        => Project(() => new(Memories: ListMemories(sessionId, searchText, includeInactive)), token);

    [SunderMethod(Idempotency = SunderServiceIdempotency.Idempotent, Errors = ["memory.semantic.operation-failed"])]
    public ValueTask<SunderContent> DetailsAsync(Guid memoryId, CancellationToken token = default)
        => Project(() => new(Evidence: ListEvidence(memoryId), SupersedingMemory: GetSupersedingMemory(memoryId),
            SupersededMemories: ListSupersededMemories(memoryId), CorrectionLineage: ListCorrectionLineage(memoryId)), token);

    [SunderMethod(Idempotency = SunderServiceIdempotency.Idempotent, Errors = ["memory.semantic.operation-failed"])]
    public ValueTask<SunderContent> SemanticStateAsync(Guid sessionId, string? profileId, CancellationToken token = default)
        => ProjectAsync(async () => new(SemanticState: await GetSemanticSessionStateAsync(sessionId, profileId, token).ConfigureAwait(false)), token);

    [SunderMethod(Idempotency = SunderServiceIdempotency.Idempotent, Errors = ["memory.semantic.operation-failed"])]
    public ValueTask<SunderContent> IndexStatusAsync(Guid memoryId, SemanticEmbeddingContext? context, CancellationToken token = default)
        => Project(() => new(IndexStatus: GetSemanticIndexStatus(GetMemory(memoryId)
            ?? throw new MemoryInspectorOperationException("Memory was not found."), context)), token);

    [SunderMethod(Idempotency = SunderServiceIdempotency.Idempotent, Errors = ["memory.semantic.operation-failed"])]
    public ValueTask<SunderContent> WorkerStatusAsync(CancellationToken token = default)
        => Project(() => new(WorkerStatus: GetSemanticWorkerStatus()), token);

    [SunderMethod(Idempotency = SunderServiceIdempotency.Idempotent, Errors = ["memory.semantic.operation-failed"])]
    public ValueTask<SunderContent> MetricsAsync(CancellationToken token = default)
        => Project(() => new(Metrics: GetMetricsSnapshot()), token);

    [SunderMethod(Errors = ["memory.semantic.operation-failed"])]
    public ValueTask<SunderContent> UpdateAsync(Guid memoryId, [SunderValueConstraints(MinLength = 1)] string category,
        [SunderValueConstraints(MinLength = 1)] string content, string? note, CancellationToken token = default)
        => Project(() => new(Memory: UpdateMemory(memoryId, RequireValue(category), RequireValue(content), note)), token);

    [SunderMethod(Errors = ["memory.semantic.operation-failed"])]
    public ValueTask<SunderContent> PinAsync(Guid memoryId, bool isPinned, CancellationToken token = default)
        => Project(() => new(Memory: SetPinned(memoryId, isPinned)), token);

    [SunderMethod(Errors = ["memory.semantic.operation-failed"])]
    public ValueTask<SunderContent> ContestAsync(Guid memoryId, CancellationToken token = default)
        => Project(() => new(Memory: ContestMemory(memoryId)), token);

    [SunderMethod(Errors = ["memory.semantic.operation-failed"])]
    public ValueTask<SunderContent> ForgetAsync(Guid memoryId, CancellationToken token = default)
        => Project(() => new(Memory: ForgetMemory(memoryId)), token);

    [SunderMethod(Errors = ["memory.semantic.operation-failed"])]
    public ValueTask<SunderContent> SupersedeAsync(Guid memoryId, CancellationToken token = default)
        => Project(() => new(Memory: SupersedeMemory(memoryId)), token);

    [SunderMethod(Errors = ["memory.semantic.operation-failed"])]
    public ValueTask<SunderContent> CorrectAsync(Guid memoryId, [SunderValueConstraints(MinLength = 1)] string category,
        [SunderValueConstraints(MinLength = 1)] string content, CancellationToken token = default)
        => Project(() => new(Correction: CreateCorrectedMemory(memoryId, RequireValue(category), RequireValue(content))), token);

    [SunderMethod(Errors = ["memory.semantic.operation-failed"])]
    public ValueTask<SunderContent> ReindexAsync(Guid sessionId, string? profileId, CancellationToken token = default)
        => ProjectAsync(async () => new(ReindexResult: await ReindexSessionAsync(sessionId, profileId, token).ConfigureAwait(false)), token);

    [SunderMethod(Idempotency = SunderServiceIdempotency.Idempotent, Errors = ["memory.semantic.operation-failed"])]
    public async IAsyncEnumerable<MemoryChanged> ChangesAsync([EnumeratorCancellation] CancellationToken token = default)
    {
        var changes = new MemoryChangeQueue();
        void Session(Guid id) => changes.PublishSession(id);
        void Worker() => changes.PublishWorker();
        void Reset() => changes.PublishReset();
        changes.PublishReset();
        SessionChanged += Session; SemanticWorkerStatusChanged += Worker; ResetRequired += Reset;
        try
        {
            await foreach (var change in changes.ReadAllAsync(token).ConfigureAwait(false)) yield return change;
        }
        finally { SessionChanged -= Session; SemanticWorkerStatusChanged -= Worker; ResetRequired -= Reset; changes.Complete(); }
    }

    private static ValueTask<SunderContent> Project(Func<MemoryProjection> operation, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try { return ValueTask.FromResult(ProjectionContent.Create(operation())); }
        catch (MemoryInspectorOperationException error) { throw Failure(error); }
    }
    private static async ValueTask<SunderContent> ProjectAsync(Func<ValueTask<MemoryProjection>> operation, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try { return ProjectionContent.Create(await operation().ConfigureAwait(false)); }
        catch (MemoryInspectorOperationException error) { throw Failure(error); }
    }
    private static string RequireValue(string value)
        => !string.IsNullOrWhiteSpace(value) ? value : throw new MemoryInspectorOperationException("A value is required.");
    private static SunderRpcException Failure(MemoryInspectorOperationException error)
    {
        var message = string.IsNullOrWhiteSpace(error.Message) ? "The requested semantic memory operation failed." : error.Message;
        return new(new(SunderRpcErrorKind.Domain, "memory.semantic.operation-failed", message.Length > 2048 ? message[..2048] : message));
    }
}
