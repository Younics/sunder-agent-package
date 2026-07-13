using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Memory.Semantic.Services;
using Sunder.Sdk.Runtime;

namespace Sunder.Package.Agent.Memory.Semantic.Runtime;

internal interface IMemoryInspectorGateway
{
    event Action<Guid>? SessionChanged;
    event Action? SemanticWorkerStatusChanged;
    IReadOnlyList<AgentSessionRecord> ListSessions();
    AgentSessionContextCheckpointRecord? GetSessionContextCheckpoint(Guid sessionId);
    AgentWorkingSummaryRecord? GetWorkingSummary(Guid sessionId);
    IReadOnlyList<StoredMemoryRecord> ListMemories(Guid sessionId, string? searchText = null, bool includeInactive = false);
    IReadOnlyList<StoredMemoryEvidenceRecord> ListEvidence(Guid memoryId);
    StoredMemoryRecord? GetSupersedingMemory(Guid memoryId);
    IReadOnlyList<StoredMemoryRecord> ListSupersededMemories(Guid memoryId);
    IReadOnlyList<StoredMemoryRecord> ListCorrectionLineage(Guid memoryId);
    StoredMemoryRecord UpdateMemory(Guid memoryId, string category, string content, string? note);
    StoredMemoryRecord SetPinned(Guid memoryId, bool isPinned);
    StoredMemoryRecord ContestMemory(Guid memoryId);
    StoredMemoryRecord ForgetMemory(Guid memoryId);
    StoredMemoryRecord SupersedeMemory(Guid memoryId);
    MemoryCorrectionResult CreateCorrectedMemory(Guid sourceMemoryId, string category, string content);
    MemorySemanticIndexStatusRecord GetSemanticIndexStatus(StoredMemoryRecord memory, SemanticEmbeddingContext? context);
    Task<SemanticMemorySessionStateRecord> GetSemanticSessionStateAsync(Guid sessionId, string? profileId = null, CancellationToken cancellationToken = default);
    Task<SemanticMemoryReindexResult> ReindexSessionAsync(Guid sessionId, string? profileId = null, CancellationToken cancellationToken = default);
    SemanticMemoryWorkerStatusRecord GetSemanticWorkerStatus();
    SemanticMemoryMetricsSnapshot GetMetricsSnapshot();
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

    public MemoryAppRuntimeGateway(IPackageRuntimeClient client)
    {
        _client = client;
        _ = ObserveChangesAsync(_lifetime.Token);
    }

    public event Action<Guid>? SessionChanged;
    public event Action? SemanticWorkerStatusChanged;

    public IReadOnlyList<AgentSessionRecord> ListSessions() => Query(new(MemoryQueryKind.Sessions)).Sessions ?? [];
    public AgentSessionContextCheckpointRecord? GetSessionContextCheckpoint(Guid sessionId) => Query(new(MemoryQueryKind.SessionCheckpoint, SessionId: sessionId)).Checkpoint;
    public AgentWorkingSummaryRecord? GetWorkingSummary(Guid sessionId) => Query(new(MemoryQueryKind.WorkingSummary, SessionId: sessionId)).WorkingSummary;
    public IReadOnlyList<StoredMemoryRecord> ListMemories(Guid sessionId, string? searchText = null, bool includeInactive = false)
        => Query(new(MemoryQueryKind.Memories, SessionId: sessionId, SearchText: searchText, IncludeInactive: includeInactive)).Memories ?? [];
    public IReadOnlyList<StoredMemoryEvidenceRecord> ListEvidence(Guid memoryId) => Details(memoryId).Evidence ?? [];
    public StoredMemoryRecord? GetSupersedingMemory(Guid memoryId) => Details(memoryId).SupersedingMemory;
    public IReadOnlyList<StoredMemoryRecord> ListSupersededMemories(Guid memoryId) => Details(memoryId).SupersededMemories ?? [];
    public IReadOnlyList<StoredMemoryRecord> ListCorrectionLineage(Guid memoryId) => Details(memoryId).CorrectionLineage ?? [];
    public StoredMemoryRecord UpdateMemory(Guid memoryId, string category, string content, string? note)
        => Command(new(MemoryCommandKind.Update, memoryId, category, content, note)).Memory!;
    public StoredMemoryRecord SetPinned(Guid memoryId, bool isPinned)
        => Command(new(MemoryCommandKind.Pin, memoryId, IsPinned: isPinned)).Memory!;
    public StoredMemoryRecord ContestMemory(Guid memoryId) => Command(new(MemoryCommandKind.Contest, memoryId)).Memory!;
    public StoredMemoryRecord ForgetMemory(Guid memoryId) => Command(new(MemoryCommandKind.Forget, memoryId)).Memory!;
    public StoredMemoryRecord SupersedeMemory(Guid memoryId) => Command(new(MemoryCommandKind.Supersede, memoryId)).Memory!;
    public MemoryCorrectionResult CreateCorrectedMemory(Guid sourceMemoryId, string category, string content)
        => Command(new(MemoryCommandKind.Correct, sourceMemoryId, category, content)).Correction!;
    public MemorySemanticIndexStatusRecord GetSemanticIndexStatus(StoredMemoryRecord memory, SemanticEmbeddingContext? context)
        => Query(new(MemoryQueryKind.SemanticIndexStatus, Memory: memory, SemanticContext: context)).IndexStatus!;
    public async Task<SemanticMemorySessionStateRecord> GetSemanticSessionStateAsync(Guid sessionId, string? profileId = null, CancellationToken cancellationToken = default)
        => (await _client.InvokeAsync(MemoryRuntimeOperations.Query,
            new MemoryQuery(MemoryQueryKind.SemanticState, SessionId: sessionId, ProfileId: profileId), cancellationToken)
            .ConfigureAwait(false)).SemanticState!;
    public async Task<SemanticMemoryReindexResult> ReindexSessionAsync(Guid sessionId, string? profileId = null, CancellationToken cancellationToken = default)
        => (await _client.InvokeAsync(MemoryRuntimeOperations.Command,
            new MemoryCommand(MemoryCommandKind.Reindex, sessionId, ProfileId: profileId), cancellationToken)
            .ConfigureAwait(false)).ReindexResult!;
    public SemanticMemoryWorkerStatusRecord GetSemanticWorkerStatus() => Query(new(MemoryQueryKind.WorkerStatus)).WorkerStatus!;
    public SemanticMemoryMetricsSnapshot GetMetricsSnapshot() => Query(new(MemoryQueryKind.Metrics)).Metrics!;

    private MemoryProjection Details(Guid memoryId) => Query(new(MemoryQueryKind.Details, MemoryId: memoryId));
    private MemoryProjection Query(MemoryQuery request) => _client.InvokeAsync(MemoryRuntimeOperations.Query, request, _lifetime.Token).AsTask().GetAwaiter().GetResult();
    private MemoryProjection Command(MemoryCommand request) => _client.InvokeAsync(MemoryRuntimeOperations.Command, request, _lifetime.Token).AsTask().GetAwaiter().GetResult();

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
