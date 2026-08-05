using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Memory.Semantic;
using Sunder.Package.Agent.Memory.Semantic.PackageViews;
using Sunder.Package.Agent.Memory.Semantic.Runtime;
using Sunder.Package.Agent.Memory.Semantic.Services;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class MemoryInspectorAuthorityRaceTests
{
    [Fact]
    public async Task SessionRefresh_PreservesSelectionChangedAfterCapture()
    {
        var first = Session("First");
        var second = Session("Second");
        var gateway = new DeferredMemoryInspectorGateway([first, second]);
        using var viewModel = new MemoryInspectorViewModel(gateway);
        await viewModel.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(2));
        var refreshResponse = gateway.DeferNextSessionList();

        var refresh = viewModel.RefreshCommand.ExecuteAsync(null);
        await refreshResponse.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        viewModel.SelectedSession = viewModel.Sessions.Single(
            session => session.SessionId == second.SessionId);
        refreshResponse.Complete([first, second]);
        await refresh.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(second.SessionId, viewModel.SelectedSession?.SessionId);
    }

    [Fact]
    public async Task SessionRefresh_RejectsOutOfOrderCompletion()
    {
        var selected = Session("Selected");
        var staleOnly = Session("Stale only");
        var currentOnly = Session("Current only");
        var gateway = new DeferredMemoryInspectorGateway([selected]);
        using var viewModel = new MemoryInspectorViewModel(gateway);
        await viewModel.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(2));
        var staleResponse = gateway.DeferNextSessionList();
        var currentResponse = gateway.DeferNextSessionList();

        gateway.RaiseSessionChanged(selected.SessionId);
        await staleResponse.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var staleRefresh = viewModel.CurrentSessionRefresh;
        gateway.RaiseSessionChanged(selected.SessionId);
        await currentResponse.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var currentRefresh = viewModel.CurrentSessionRefresh;

        currentResponse.Complete([selected, currentOnly]);
        await currentRefresh.WaitAsync(TimeSpan.FromSeconds(2));
        staleResponse.Complete([selected, staleOnly]);
        await staleRefresh.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Contains(viewModel.Sessions, session => session.SessionId == currentOnly.SessionId);
        Assert.DoesNotContain(viewModel.Sessions, session => session.SessionId == staleOnly.SessionId);
        Assert.Equal(selected.SessionId, viewModel.SelectedSession?.SessionId);
    }

    [Fact]
    public async Task EventsDuringInitialization_ReplaySessionAndWorkerRefreshes()
    {
        var stale = Session("Stale");
        var current = Session("Current");
        var gateway = new DeferredMemoryInspectorGateway([stale]);
        var initialSessions = gateway.DeferNextSessionList();
        gateway.SetWorkerStatus("stale-worker");
        using var viewModel = new MemoryInspectorViewModel(gateway);

        var initialization = viewModel.InitializeAsync();
        await initialSessions.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        gateway.SetSessions([current]);
        gateway.SetWorkerStatus("current-worker");
        gateway.RaiseSessionChanged(current.SessionId);
        gateway.RaiseWorkerStatusChanged();
        initialSessions.Complete([stale]);
        await initialization.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(2, gateway.SessionListCallCount);
        Assert.Equal(2, gateway.WorkerStatusCallCount);
        Assert.Equal(current.SessionId, Assert.Single(viewModel.Sessions).SessionId);
        Assert.Equal("current-worker", viewModel.SemanticWorkerStatusText);
    }

    private static AgentSessionRecord Session(string title)
    {
        var now = DateTimeOffset.UtcNow;
        return new AgentSessionRecord(
            Guid.NewGuid(),
            title,
            AgentSessionState.Active,
            now,
            now);
    }

    private sealed class DeferredMemoryInspectorGateway(
        IReadOnlyList<AgentSessionRecord> sessions) : IMemoryInspectorGateway
    {
        private readonly object _syncRoot = new();
        private readonly Queue<DeferredResult<IReadOnlyList<AgentSessionRecord>>> _sessionLists = new();
        private IReadOnlyList<AgentSessionRecord> _sessions = sessions;
        private SemanticMemoryWorkerStatusRecord _workerStatus = WorkerStatus("worker");
        private int _sessionListCallCount;
        private int _workerStatusCallCount;

        public int SessionListCallCount => Volatile.Read(ref _sessionListCallCount);

        public int WorkerStatusCallCount => Volatile.Read(ref _workerStatusCallCount);

        public event Action<Guid>? SessionChanged;

        public event Action? SemanticWorkerStatusChanged;

        public Task InitializeAsync(CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public DeferredResult<IReadOnlyList<AgentSessionRecord>> DeferNextSessionList()
        {
            var result = new DeferredResult<IReadOnlyList<AgentSessionRecord>>();
            lock (_syncRoot)
            {
                _sessionLists.Enqueue(result);
            }
            return result;
        }

        public void SetSessions(IReadOnlyList<AgentSessionRecord> value)
        {
            lock (_syncRoot)
            {
                _sessions = value;
            }
        }

        public void SetWorkerStatus(string statusText)
        {
            lock (_syncRoot)
            {
                _workerStatus = WorkerStatus(statusText);
            }
        }

        public void RaiseSessionChanged(Guid sessionId) => SessionChanged?.Invoke(sessionId);

        public void RaiseWorkerStatusChanged() => SemanticWorkerStatusChanged?.Invoke();

        public async Task<IReadOnlyList<AgentSessionRecord>> ListSessionsAsync(
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _sessionListCallCount);
            DeferredResult<IReadOnlyList<AgentSessionRecord>>? deferred = null;
            IReadOnlyList<AgentSessionRecord> current;
            lock (_syncRoot)
            {
                if (_sessionLists.Count > 0)
                {
                    deferred = _sessionLists.Dequeue();
                }
                current = _sessions;
            }

            return deferred is null
                ? current
                : await deferred.WaitAsync().ConfigureAwait(false);
        }

        public Task<AgentSessionContextCheckpointRecord?> GetSessionContextCheckpointAsync(
            Guid sessionId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<AgentSessionContextCheckpointRecord?>(null);

        public Task<AgentWorkingSummaryRecord?> GetWorkingSummaryAsync(
            Guid sessionId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<AgentWorkingSummaryRecord?>(null);

        public Task<IReadOnlyList<StoredMemoryRecord>> ListMemoriesAsync(
            Guid sessionId,
            string? searchText = null,
            bool includeInactive = false,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<StoredMemoryRecord>>([]);

        public Task<IReadOnlyList<StoredMemoryEvidenceRecord>> ListEvidenceAsync(
            Guid memoryId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<StoredMemoryEvidenceRecord>>([]);

        public Task<StoredMemoryRecord?> GetSupersedingMemoryAsync(
            Guid memoryId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<StoredMemoryRecord?>(null);

        public Task<IReadOnlyList<StoredMemoryRecord>> ListSupersededMemoriesAsync(
            Guid memoryId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<StoredMemoryRecord>>([]);

        public Task<IReadOnlyList<StoredMemoryRecord>> ListCorrectionLineageAsync(
            Guid memoryId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<StoredMemoryRecord>>([]);

        public Task<StoredMemoryRecord> UpdateMemoryAsync(
            Guid memoryId,
            string category,
            string content,
            string? note,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<StoredMemoryRecord> SetPinnedAsync(
            Guid memoryId,
            bool isPinned,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<StoredMemoryRecord> ContestMemoryAsync(
            Guid memoryId,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<StoredMemoryRecord> ForgetMemoryAsync(
            Guid memoryId,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<StoredMemoryRecord> SupersedeMemoryAsync(
            Guid memoryId,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<MemoryCorrectionResult> CreateCorrectedMemoryAsync(
            Guid sourceMemoryId,
            string category,
            string content,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<MemorySemanticIndexStatusRecord> GetSemanticIndexStatusAsync(
            StoredMemoryRecord memory,
            SemanticEmbeddingContext? context,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new MemorySemanticIndexStatusRecord(string.Empty, string.Empty));

        public Task<SemanticMemorySessionStateRecord> GetSemanticSessionStateAsync(
            Guid sessionId,
            string? profileId = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new SemanticMemorySessionStateRecord(
                SemanticEmbeddingContext.Disabled("Disabled for test."),
                new SemanticMemoryStatusRecord("Disabled for test.", CanReindex: false)));

        public Task<SemanticMemoryReindexResult> ReindexSessionAsync(
            Guid sessionId,
            string? profileId = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<SemanticMemoryWorkerStatusRecord> GetSemanticWorkerStatusAsync(
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _workerStatusCallCount);
            lock (_syncRoot)
            {
                return Task.FromResult(_workerStatus);
            }
        }

        public Task<SemanticMemoryMetricsSnapshot> GetMetricsSnapshotAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult(new SemanticMemoryMetricsSnapshot(0, 0, 0, 0, 0, 0));

        private static SemanticMemoryWorkerStatusRecord WorkerStatus(string statusText)
            => new(
                statusText,
                IsRunning: false,
                PendingItemCount: 0,
                ProcessedItemCount: 0,
                LastSuccessfulRunAtUtc: null,
                LastFailureAtUtc: null,
                LastFailureMessage: null,
                HasFailure: false);
    }

    private sealed class DeferredResult<T>
    {
        private readonly TaskCompletionSource<T> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<T> WaitAsync()
        {
            Started.TrySetResult();
            return await _completion.Task.ConfigureAwait(false);
        }

        public void Complete(T value) => _completion.TrySetResult(value);
    }
}
