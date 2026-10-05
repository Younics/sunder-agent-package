using Microsoft.Extensions.Logging;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Memory.Semantic.Runtime;
using Sunder.Package.Agent.Memory.Semantic.Services;
using Sunder.Package.Agent.Shared.Presentation;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Memory.Semantic;

internal sealed partial class MemoryAppRuntimeGateway :
    IMemoryInspectorGateway,
    IOwnerStreamAvailability,
    IDisposable,
    IAsyncDisposable
{
    private readonly ISunderRpcClient _rpc;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ILogger<MemoryAppRuntimeGateway>? _logger;
    private readonly OwnerStreamObserver _observer;
    private Task? _observationTask;
    private int _observationStarted;
    private int _disposed;
    private int _lifetimeDisposed;

    public event Action<Guid>? SessionChanged;
    public event Action? SemanticWorkerStatusChanged;
    public event Action? ResetRequired;
    public bool IsOwnerStreamAvailable => _observer.IsAvailable;
    public event Action<bool>? OwnerStreamAvailabilityChanged
    {
        add => _observer.AvailabilityChanged += value;
        remove => _observer.AvailabilityChanged -= value;
    }

    internal MemoryAppRuntimeGateway(ISunderRpcClient rpc)
        : this(rpc, loggerFactory: null)
    {
    }

    internal MemoryAppRuntimeGateway(ISunderRpcClient rpc, ILoggerFactory? loggerFactory)
    {
        _rpc = rpc;
        _logger = loggerFactory?.CreateLogger<MemoryAppRuntimeGateway>();
        _observer = new OwnerStreamObserver(LogObservationRetry);
    }

    internal Task? ObservationTask => _observationTask;

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.Exchange(ref _observationStarted, 1) == 0)
        {
            _observationTask = _observer.RunAsync(ObserveChangesOnceAsync, _lifetime.Token);
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
        StopObservation();
        DisposeLifetime();
    }

    public async ValueTask DisposeAsync()
    {
        StopObservation();
        try
        {
            if (_observationTask is { } observationTask)
            {
                await observationTask.ConfigureAwait(false);
            }
        }
        finally
        {
            DisposeLifetime();
        }
    }

    private void StopObservation()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _lifetime.Cancel();
        }
    }

    private void DisposeLifetime()
    {
        if (Interlocked.Exchange(ref _lifetimeDisposed, 1) == 0)
        {
            _lifetime.Dispose();
        }
    }

    private Task<MemoryProjection> DetailsAsync(Guid memoryId, CancellationToken cancellationToken)
        => QueryAsync(new(MemoryQueryKind.Details, MemoryId: memoryId), cancellationToken);

    private Task<MemoryProjection> QueryAsync(MemoryQuery request, CancellationToken cancellationToken)
        => InvokeAsync(token => QueryTransportAsync(request, token), cancellationToken);

    private Task<MemoryProjection> CommandAsync(MemoryCommand request, CancellationToken cancellationToken)
        => InvokeAsync(token => CommandTransportAsync(request, token), cancellationToken);

    private async Task<MemoryProjection> InvokeAsync(
        Func<CancellationToken, ValueTask<MemoryProjection>> invoke,
        CancellationToken cancellationToken)
    {
        using var invocation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        return await invoke(invocation.Token).ConfigureAwait(false);
    }

    private async Task ObserveChangesOnceAsync(
        Action markAvailable,
        CancellationToken cancellationToken)
    {
        var connected = false;
        await foreach (var change in WatchTransportAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!connected)
            {
                connected = true;
                markAvailable();
            }
            switch (change.Kind)
            {
                case MemoryChangeKind.Session when change.SessionId is { } sessionId:
                    SessionChanged?.Invoke(sessionId);
                    break;
                case MemoryChangeKind.Worker:
                    SemanticWorkerStatusChanged?.Invoke();
                    break;
                case MemoryChangeKind.ResetRequired:
                    ResetRequired?.Invoke();
                    break;
                default:
                    throw new InvalidDataException("The Semantic Memory Runtime returned a malformed change event.");
            }
        }
    }

    private void LogObservationRetry(Exception exception, TimeSpan retryDelay)
        => _logger?.LogWarning(
            "Semantic Memory owner change observation failed and will retry in {RetryDelayMilliseconds} ms ({ExceptionType}).",
            retryDelay.TotalMilliseconds,
            exception.GetType().Name);

}
