using Microsoft.Extensions.Logging;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Memory.Semantic.Runtime;
using Sunder.Package.Agent.Memory.Semantic.Services;
using Sunder.Package.Agent.Shared.Presentation;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;
using Sunder.Generated.Packages.Younics_Ai_Agent_Memory_Semantic_Services;

namespace Sunder.Package.Agent.Memory.Semantic;

internal sealed partial class MemoryAppRuntimeGateway :
    IMemoryInspectorGateway,
    IOwnerStreamAvailability,
    IDisposable,
    IAsyncDisposable
{
    private readonly IMemoryInspectorService _service;
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

    internal MemoryAppRuntimeGateway(IMemoryInspectorService service)
        : this(service, loggerFactory: null)
    {
    }

    internal MemoryAppRuntimeGateway(IMemoryInspectorService service, ILoggerFactory? loggerFactory)
    {
        _service = service;
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
        => (await InvokeAsync(token => _service.SessionsAsync(token), cancellationToken).ConfigureAwait(false)).Sessions ?? [];

    public async Task<AgentSessionContextCheckpointRecord?> GetSessionContextCheckpointAsync(Guid sessionId, CancellationToken cancellationToken = default)
        => (await InvokeAsync(token => _service.SessionCheckpointAsync(sessionId, token), cancellationToken).ConfigureAwait(false)).Checkpoint;

    public async Task<AgentWorkingSummaryRecord?> GetWorkingSummaryAsync(Guid sessionId, CancellationToken cancellationToken = default)
        => (await InvokeAsync(token => _service.WorkingSummaryAsync(sessionId, token), cancellationToken).ConfigureAwait(false)).WorkingSummary;

    public async Task<IReadOnlyList<StoredMemoryRecord>> ListMemoriesAsync(Guid sessionId, string? searchText = null, bool includeInactive = false, CancellationToken cancellationToken = default)
        => (await InvokeAsync(token => _service.MemoriesAsync(sessionId, searchText, includeInactive, token), cancellationToken).ConfigureAwait(false)).Memories ?? [];

    public async Task<IReadOnlyList<StoredMemoryEvidenceRecord>> ListEvidenceAsync(Guid memoryId, CancellationToken cancellationToken = default)
        => (await DetailsAsync(memoryId, cancellationToken).ConfigureAwait(false)).Evidence ?? [];

    public async Task<StoredMemoryRecord?> GetSupersedingMemoryAsync(Guid memoryId, CancellationToken cancellationToken = default)
        => (await DetailsAsync(memoryId, cancellationToken).ConfigureAwait(false)).SupersedingMemory;

    public async Task<IReadOnlyList<StoredMemoryRecord>> ListSupersededMemoriesAsync(Guid memoryId, CancellationToken cancellationToken = default)
        => (await DetailsAsync(memoryId, cancellationToken).ConfigureAwait(false)).SupersededMemories ?? [];

    public async Task<IReadOnlyList<StoredMemoryRecord>> ListCorrectionLineageAsync(Guid memoryId, CancellationToken cancellationToken = default)
        => (await DetailsAsync(memoryId, cancellationToken).ConfigureAwait(false)).CorrectionLineage ?? [];

    public async Task<StoredMemoryRecord> UpdateMemoryAsync(Guid memoryId, string category, string content, string? note, CancellationToken cancellationToken = default)
        => (await InvokeAsync(token => _service.UpdateAsync(memoryId, category, content, note, token), cancellationToken).ConfigureAwait(false)).Memory!;

    public async Task<StoredMemoryRecord> SetPinnedAsync(Guid memoryId, bool isPinned, CancellationToken cancellationToken = default)
        => (await InvokeAsync(token => _service.PinAsync(memoryId, isPinned, token), cancellationToken).ConfigureAwait(false)).Memory!;

    public async Task<StoredMemoryRecord> ContestMemoryAsync(Guid memoryId, CancellationToken cancellationToken = default)
        => (await InvokeAsync(token => _service.ContestAsync(memoryId, token), cancellationToken).ConfigureAwait(false)).Memory!;

    public async Task<StoredMemoryRecord> ForgetMemoryAsync(Guid memoryId, CancellationToken cancellationToken = default)
        => (await InvokeAsync(token => _service.ForgetAsync(memoryId, token), cancellationToken).ConfigureAwait(false)).Memory!;

    public async Task<StoredMemoryRecord> SupersedeMemoryAsync(Guid memoryId, CancellationToken cancellationToken = default)
        => (await InvokeAsync(token => _service.SupersedeAsync(memoryId, token), cancellationToken).ConfigureAwait(false)).Memory!;

    public async Task<MemoryCorrectionResult> CreateCorrectedMemoryAsync(Guid sourceMemoryId, string category, string content, CancellationToken cancellationToken = default)
        => (await InvokeAsync(token => _service.CorrectAsync(sourceMemoryId, category, content, token), cancellationToken).ConfigureAwait(false)).Correction!;

    public async Task<MemorySemanticIndexStatusRecord> GetSemanticIndexStatusAsync(StoredMemoryRecord memory, SemanticEmbeddingContext? context, CancellationToken cancellationToken = default)
        => (await InvokeAsync(token => _service.IndexStatusAsync(memory.MemoryId, context, token), cancellationToken).ConfigureAwait(false)).IndexStatus!;

    public async Task<SemanticMemorySessionStateRecord> GetSemanticSessionStateAsync(Guid sessionId, string? profileId = null, CancellationToken cancellationToken = default)
        => (await InvokeAsync(token => _service.SemanticStateAsync(sessionId, profileId, token), cancellationToken).ConfigureAwait(false)).SemanticState!;

    public async Task<SemanticMemoryReindexResult> ReindexSessionAsync(Guid sessionId, string? profileId = null, CancellationToken cancellationToken = default)
        => (await InvokeAsync(token => _service.ReindexAsync(sessionId, profileId, token), cancellationToken).ConfigureAwait(false)).ReindexResult!;

    public async Task<SemanticMemoryWorkerStatusRecord> GetSemanticWorkerStatusAsync(CancellationToken cancellationToken = default)
        => (await InvokeAsync(token => _service.WorkerStatusAsync(token), cancellationToken).ConfigureAwait(false)).WorkerStatus!;

    public async Task<SemanticMemoryMetricsSnapshot> GetMetricsSnapshotAsync(CancellationToken cancellationToken = default)
        => (await InvokeAsync(token => _service.MetricsAsync(token), cancellationToken).ConfigureAwait(false)).Metrics!;

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
        => InvokeAsync(token => _service.DetailsAsync(memoryId, token), cancellationToken);

    private async Task<MemoryProjection> InvokeAsync(
        Func<CancellationToken, ValueTask<SunderContent>> invoke,
        CancellationToken cancellationToken)
    {
        using var invocation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        try { return await ProjectionContent.Read(invoke(invocation.Token), invocation.Token).ConfigureAwait(false); }
        catch (SunderRpcException error) when (IsMissingProvider(error)) { throw Unavailable(); }
    }

    private async Task ObserveChangesOnceAsync(
        Action markAvailable,
        CancellationToken cancellationToken)
    {
        var connected = false;
        await using var changes = _service.ChangesAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);
        while (await MoveNextAsync(changes).ConfigureAwait(false))
        {
            var change = changes.Current;
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

    private static async ValueTask<bool> MoveNextAsync(IAsyncEnumerator<MemoryChanged> changes)
    {
        try { return await changes.MoveNextAsync().ConfigureAwait(false); }
        catch (SunderRpcException error) when (IsMissingProvider(error)) { throw Unavailable(); }
    }
    private static bool IsMissingProvider(SunderRpcException error)
        => error.Error.Kind == SunderRpcErrorKind.NotFound && error.Error.Code == "rpc.provider-not-found";
    private static SunderRpcException Unavailable()
        => new(new(SunderRpcErrorKind.Unavailable, "semantic-memory.inspector-unavailable", "The semantic memory inspector Runtime provider is unavailable."));

    private void LogObservationRetry(Exception exception, TimeSpan retryDelay)
        => _logger?.LogWarning(
            "Semantic Memory owner change observation failed and will retry in {RetryDelayMilliseconds} ms ({ExceptionType}).",
            retryDelay.TotalMilliseconds,
            exception.GetType().Name);

}
