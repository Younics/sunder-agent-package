using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Memory.Semantic.Services;
using Sunder.Sdk.Runtime;

namespace Sunder.Package.Agent.Memory.Semantic.Runtime;

internal interface IMemoryInspectorGateway
{
    Task InitializeAsync(CancellationToken cancellationToken = default)
        => Task.CompletedTask;
    event Action<Guid>? SessionChanged;
    event Action? SemanticWorkerStatusChanged;
    Task<IReadOnlyList<AgentSessionRecord>> ListSessionsAsync(CancellationToken cancellationToken = default);
    Task<AgentSessionContextCheckpointRecord?> GetSessionContextCheckpointAsync(Guid sessionId, CancellationToken cancellationToken = default);
    Task<AgentWorkingSummaryRecord?> GetWorkingSummaryAsync(Guid sessionId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<StoredMemoryRecord>> ListMemoriesAsync(Guid sessionId, string? searchText = null, bool includeInactive = false, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<StoredMemoryEvidenceRecord>> ListEvidenceAsync(Guid memoryId, CancellationToken cancellationToken = default);
    Task<StoredMemoryRecord?> GetSupersedingMemoryAsync(Guid memoryId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<StoredMemoryRecord>> ListSupersededMemoriesAsync(Guid memoryId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<StoredMemoryRecord>> ListCorrectionLineageAsync(Guid memoryId, CancellationToken cancellationToken = default);
    Task<StoredMemoryRecord> UpdateMemoryAsync(Guid memoryId, string category, string content, string? note, CancellationToken cancellationToken = default);
    Task<StoredMemoryRecord> SetPinnedAsync(Guid memoryId, bool isPinned, CancellationToken cancellationToken = default);
    Task<StoredMemoryRecord> ContestMemoryAsync(Guid memoryId, CancellationToken cancellationToken = default);
    Task<StoredMemoryRecord> ForgetMemoryAsync(Guid memoryId, CancellationToken cancellationToken = default);
    Task<StoredMemoryRecord> SupersedeMemoryAsync(Guid memoryId, CancellationToken cancellationToken = default);
    Task<MemoryCorrectionResult> CreateCorrectedMemoryAsync(Guid sourceMemoryId, string category, string content, CancellationToken cancellationToken = default);
    Task<MemorySemanticIndexStatusRecord> GetSemanticIndexStatusAsync(StoredMemoryRecord memory, SemanticEmbeddingContext? context, CancellationToken cancellationToken = default);
    Task<SemanticMemorySessionStateRecord> GetSemanticSessionStateAsync(Guid sessionId, string? profileId = null, CancellationToken cancellationToken = default);
    Task<SemanticMemoryReindexResult> ReindexSessionAsync(Guid sessionId, string? profileId = null, CancellationToken cancellationToken = default);
    Task<SemanticMemoryWorkerStatusRecord> GetSemanticWorkerStatusAsync(CancellationToken cancellationToken = default);
    Task<SemanticMemoryMetricsSnapshot> GetMetricsSnapshotAsync(CancellationToken cancellationToken = default);
}

internal static class MemoryRuntimeOperations
{
    internal static readonly PackageRuntimeOperation<MemoryQuery, MemoryProjection> Query =
        new("semantic-memory.query.v1");
    internal static readonly PackageRuntimeOperation<MemoryCommand, MemoryProjection> Command =
        new("semantic-memory.command.v1");
    internal static readonly PackageRuntimeStream<MemoryChangeSubscription, MemoryChanged> Changes =
        new("semantic-memory.changes.v1");
}

internal enum MemoryQueryKind { Sessions, SessionCheckpoint, WorkingSummary, Memories, Details, SemanticState, SemanticIndexStatus, WorkerStatus, Metrics }
internal sealed record MemoryQuery(
    MemoryQueryKind Kind,
    Guid? SessionId = null,
    Guid? MemoryId = null,
    string? SearchText = null,
    bool IncludeInactive = false,
    string? ProfileId = null,
    StoredMemoryRecord? Memory = null,
    SemanticEmbeddingContext? SemanticContext = null);

internal enum MemoryCommandKind { Update, Pin, Contest, Forget, Supersede, Correct, Reindex }
internal sealed record MemoryCommand(
    MemoryCommandKind Kind,
    Guid MemoryId,
    string? Category = null,
    string? Content = null,
    string? Note = null,
    bool IsPinned = false,
    string? ProfileId = null);

internal sealed record MemoryProjection(
    IReadOnlyList<AgentSessionRecord>? Sessions = null,
    AgentSessionContextCheckpointRecord? Checkpoint = null,
    AgentWorkingSummaryRecord? WorkingSummary = null,
    IReadOnlyList<StoredMemoryRecord>? Memories = null,
    IReadOnlyList<StoredMemoryEvidenceRecord>? Evidence = null,
    StoredMemoryRecord? Memory = null,
    StoredMemoryRecord? SupersedingMemory = null,
    IReadOnlyList<StoredMemoryRecord>? SupersededMemories = null,
    IReadOnlyList<StoredMemoryRecord>? CorrectionLineage = null,
    MemoryCorrectionResult? Correction = null,
    MemorySemanticIndexStatusRecord? IndexStatus = null,
    SemanticMemorySessionStateRecord? SemanticState = null,
    SemanticMemoryReindexResult? ReindexResult = null,
    SemanticMemoryWorkerStatusRecord? WorkerStatus = null,
    SemanticMemoryMetricsSnapshot? Metrics = null);

internal sealed record MemoryChangeSubscription;
internal enum MemoryChangeKind { Session, Worker }
internal sealed record MemoryChanged(MemoryChangeKind Kind, Guid? SessionId = null);

internal sealed class MemoryAppRuntimeGateway : IMemoryInspectorGateway, IDisposable
{
    private readonly IPackageRuntimeClient _client;
    private readonly CancellationTokenSource _lifetime = new();
    private int _observationStarted;
    private int _disposed;

    public MemoryAppRuntimeGateway(IPackageRuntimeClient client)
    {
        _client = client;
    }

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

    public event Action<Guid>? SessionChanged;
    public event Action? SemanticWorkerStatusChanged;

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
        => (await QueryAsync(
            new MemoryQuery(MemoryQueryKind.SemanticState, SessionId: sessionId, ProfileId: profileId), cancellationToken)
            .ConfigureAwait(false)).SemanticState!;
    public async Task<SemanticMemoryReindexResult> ReindexSessionAsync(Guid sessionId, string? profileId = null, CancellationToken cancellationToken = default)
        => (await CommandAsync(
            new MemoryCommand(MemoryCommandKind.Reindex, sessionId, ProfileId: profileId), cancellationToken)
            .ConfigureAwait(false)).ReindexResult!;
    public async Task<SemanticMemoryWorkerStatusRecord> GetSemanticWorkerStatusAsync(CancellationToken cancellationToken = default)
        => (await QueryAsync(new(MemoryQueryKind.WorkerStatus), cancellationToken).ConfigureAwait(false)).WorkerStatus!;
    public async Task<SemanticMemoryMetricsSnapshot> GetMetricsSnapshotAsync(CancellationToken cancellationToken = default)
        => (await QueryAsync(new(MemoryQueryKind.Metrics), cancellationToken).ConfigureAwait(false)).Metrics!;

    private Task<MemoryProjection> DetailsAsync(Guid memoryId, CancellationToken cancellationToken)
        => QueryAsync(new(MemoryQueryKind.Details, MemoryId: memoryId), cancellationToken);
    private async Task<MemoryProjection> QueryAsync(MemoryQuery request, CancellationToken cancellationToken)
    {
        using var invocation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetime.Token);
        return await _client.InvokeAsync(MemoryRuntimeOperations.Query, request, invocation.Token)
            .ConfigureAwait(false);
    }

    private async Task<MemoryProjection> CommandAsync(MemoryCommand request, CancellationToken cancellationToken)
    {
        using var invocation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetime.Token);
        return await _client.InvokeAsync(MemoryRuntimeOperations.Command, request, invocation.Token)
            .ConfigureAwait(false);
    }

    private async Task ObserveChangesAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await foreach (var change in _client.SubscribeAsync(MemoryRuntimeOperations.Changes, new MemoryChangeSubscription(), cancellationToken))
                {
                    if (change.Kind == MemoryChangeKind.Session && change.SessionId is { } sessionId) SessionChanged?.Invoke(sessionId);
                    if (change.Kind == MemoryChangeKind.Worker) SemanticWorkerStatusChanged?.Invoke();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch { }
            try { await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}

internal sealed class MemoryRuntimeHandler(MemoryInspectorService inspector)
    : IPackageRuntimeOperationHandler<MemoryQuery, MemoryProjection>,
      IPackageRuntimeOperationHandler<MemoryCommand, MemoryProjection>
{
    public async ValueTask<MemoryProjection> HandleAsync(MemoryQuery request, CancellationToken cancellationToken = default)
        => request.Kind switch
        {
            MemoryQueryKind.Sessions => new(Sessions: inspector.ListSessions()),
            MemoryQueryKind.SessionCheckpoint => new(Checkpoint: inspector.GetSessionContextCheckpoint(Require(request.SessionId))),
            MemoryQueryKind.WorkingSummary => new(WorkingSummary: inspector.GetWorkingSummary(Require(request.SessionId))),
            MemoryQueryKind.Memories => new(Memories: inspector.ListMemories(Require(request.SessionId), request.SearchText, request.IncludeInactive)),
            MemoryQueryKind.Details => Details(Require(request.MemoryId)),
            MemoryQueryKind.SemanticState => new(SemanticState: await inspector.GetSemanticSessionStateAsync(Require(request.SessionId), request.ProfileId, cancellationToken)),
            MemoryQueryKind.SemanticIndexStatus => new(IndexStatus: inspector.GetSemanticIndexStatus(request.Memory ?? throw new InvalidOperationException("Memory is required."), request.SemanticContext)),
            MemoryQueryKind.WorkerStatus => new(WorkerStatus: inspector.GetSemanticWorkerStatus()),
            MemoryQueryKind.Metrics => new(Metrics: inspector.GetMetricsSnapshot()),
            _ => throw new InvalidOperationException("Unknown memory query."),
        };

    public async ValueTask<MemoryProjection> HandleAsync(MemoryCommand request, CancellationToken cancellationToken = default)
        => request.Kind switch
        {
            MemoryCommandKind.Update => new(Memory: inspector.UpdateMemory(request.MemoryId, Require(request.Category), Require(request.Content), request.Note)),
            MemoryCommandKind.Pin => new(Memory: inspector.SetPinned(request.MemoryId, request.IsPinned)),
            MemoryCommandKind.Contest => new(Memory: inspector.ContestMemory(request.MemoryId)),
            MemoryCommandKind.Forget => new(Memory: inspector.ForgetMemory(request.MemoryId)),
            MemoryCommandKind.Supersede => new(Memory: inspector.SupersedeMemory(request.MemoryId)),
            MemoryCommandKind.Correct => new(Correction: inspector.CreateCorrectedMemory(request.MemoryId, Require(request.Category), Require(request.Content))),
            MemoryCommandKind.Reindex => new(ReindexResult: await inspector.ReindexSessionAsync(request.MemoryId, request.ProfileId, cancellationToken)),
            _ => throw new InvalidOperationException("Unknown memory command."),
        };

    private MemoryProjection Details(Guid memoryId) => new(
        Evidence: inspector.ListEvidence(memoryId),
        SupersedingMemory: inspector.GetSupersedingMemory(memoryId),
        SupersededMemories: inspector.ListSupersededMemories(memoryId),
        CorrectionLineage: inspector.ListCorrectionLineage(memoryId));

    private static Guid Require(Guid? value) => value ?? throw new InvalidOperationException("An identifier is required.");
    private static string Require(string? value) => string.IsNullOrWhiteSpace(value) ? throw new InvalidOperationException("A value is required.") : value;
}

internal sealed class MemoryRuntimeChangeStream(MemoryInspectorService inspector)
    : IPackageRuntimeStreamHandler<MemoryChangeSubscription, MemoryChanged>
{
    public async IAsyncEnumerable<MemoryChanged> SubscribeAsync(MemoryChangeSubscription request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var channel = Channel.CreateBounded<MemoryChanged>(new BoundedChannelOptions(16)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
        void Session(Guid id) => channel.Writer.TryWrite(new(MemoryChangeKind.Session, id));
        void Worker() => channel.Writer.TryWrite(new(MemoryChangeKind.Worker));
        inspector.SessionChanged += Session;
        inspector.SemanticWorkerStatusChanged += Worker;
        try
        {
            await foreach (var change in channel.Reader.ReadAllAsync(cancellationToken)) yield return change;
        }
        finally
        {
            inspector.SessionChanged -= Session;
            inspector.SemanticWorkerStatusChanged -= Worker;
            channel.Writer.TryComplete();
        }
    }
}
