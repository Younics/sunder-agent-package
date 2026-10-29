extern alias MemoryApp;

using MemoryApp::Sunder.Package.Agent.Memory.Semantic;
using MemoryApp::Sunder.Package.Agent.Memory.Semantic.PackageViews;
using MemoryApp::Sunder.Package.Agent.Memory.Semantic.Runtime;
using MemoryApp::Sunder.Package.Agent.Memory.Semantic.Services;
using AgentSessionContextCheckpointRecord = MemoryApp::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentSessionContextCheckpointRecord;
using AgentSessionRecord = MemoryApp::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentSessionRecord;
using AgentSessionState = MemoryApp::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentSessionState;
using AgentWorkingSummaryRecord = MemoryApp::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentWorkingSummaryRecord;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class MemoryInspectorAuthorityRaceTests
{
    [Fact]
    public async Task RefreshAndTabSwitch_PreserveDraftAndSaveAcceptsOnlySubmittedValues()
    {
        var session = Session("Editing");
        var now = DateTimeOffset.UtcNow;
        var record = new StoredMemoryRecord(Guid.NewGuid(), session.SessionId, "preference", "Original", null, null,
            1, 1, false, "Active", null, now, now, null, 0);
        var gateway = new DeferredMemoryInspectorGateway([session]) { MemoryRecords = [record] };
        using var model = new MemoryInspectorViewModel(gateway);
        await model.InitializeAsync();
        Assert.Equal(0, model.SelectedTab);
        model.EditContent = "Draft";
        model.SelectedTab = 1;
        gateway.MemoryRecords = [record with { Content = "External" }];
        await model.RefreshCommand.ExecuteAsync(null);
        model.SelectedTab = 0;
        Assert.Equal("Draft", model.EditContent);
        Assert.True(model.IsMemoryDirty);
        Assert.Equal(record.MemoryId, model.SelectedGraphMemoryId);

        gateway.SaveRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var save = model.SaveMemoryCommand.ExecuteAsync(null);
        await gateway.SaveEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        model.EditContent = "Newer draft";
        gateway.SaveRelease.TrySetResult();
        await save;
        Assert.Equal("Newer draft", model.EditContent);
        Assert.True(model.IsMemoryDirty);
        Assert.Equal("Draft", Assert.Single(gateway.MemoryRecords).Content);

        model.EditContent = "Draft";
        gateway.MemoryRecords = [record with { Content = "External after save" }];
        await model.RefreshCommand.ExecuteAsync(null);
        Assert.Equal("External after save", model.EditContent);
        Assert.False(model.IsMemoryDirty);
    }

    [Fact]
    public async Task ExpandedGraphNeighbor_RemainsInDataAndKeepsItsDraftAcrossRefresh()
    {
        var session = Session("Corrections");
        var now = DateTimeOffset.UtcNow;
        var current = new StoredMemoryRecord(Guid.NewGuid(), session.SessionId, "preference", "Current", null, null,
            1, 1, false, "Active", null, now, now, null, 0);
        var old = current with { MemoryId = Guid.NewGuid(), Content = "Original", State = "Superseded", SupersededByMemoryId = current.MemoryId };
        var gateway = new DeferredMemoryInspectorGateway([session]) { MemoryRecords = [current, old] };
        using var model = new MemoryInspectorViewModel(gateway);
        await model.InitializeAsync();
        Assert.Single(model.Memories);
        Assert.Contains(model.Graph.Nodes, node => node.MemoryId == old.MemoryId);

        model.SelectedGraphMemoryId = old.MemoryId;
        Assert.Equal(old.MemoryId, model.SelectedMemory?.MemoryId);
        Assert.Equal("Original", model.EditContent);
        Assert.Contains(model.FilteredMemories, memory => memory.MemoryId == old.MemoryId);
        model.EditContent = "Correction draft";
        model.SelectedTab = 1;
        await model.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(old.MemoryId, model.SelectedMemory?.MemoryId);
        Assert.Contains(model.Graph.Nodes, node => node.MemoryId == old.MemoryId);
        Assert.Equal("Correction draft", model.EditContent);
        Assert.True(model.IsMemoryDirty);
        model.SelectedGraphMemoryId = current.MemoryId;
        Assert.Equal("Current", model.EditContent);
        model.SelectedGraphMemoryId = old.MemoryId;
        Assert.Equal("Correction draft", model.EditContent);
    }

    [Fact]
    public async Task SelectionImmediatelyUsesItsOwnDraftAndLateSaveDoesNotRestoreOldSelection()
    {
        var session = Session("Selection");
        var now = DateTimeOffset.UtcNow;
        var first = new StoredMemoryRecord(Guid.NewGuid(), session.SessionId, "preference", "First", null, null,
            1, 1, false, "Active", null, now, now, null, 0);
        var second = first with { MemoryId = Guid.NewGuid(), Content = "Second" };
        var gateway = new DeferredMemoryInspectorGateway([session]) { MemoryRecords = [first, second] };
        using var model = new MemoryInspectorViewModel(gateway);
        await model.InitializeAsync();
        model.EditContent = "First draft";
        gateway.SaveRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var save = model.SaveMemoryCommand.ExecuteAsync(null);
        await gateway.SaveEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        gateway.DetailsRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        model.SelectedMemory = model.Memories.Single(memory => memory.MemoryId == second.MemoryId);
        Assert.Equal("Second", model.EditContent);
        model.EditContent = "Second draft";
        gateway.DetailsRelease.TrySetResult();
        gateway.SaveRelease.TrySetResult();
        await save;
        Assert.Equal(second.MemoryId, model.SelectedMemory?.MemoryId);
        Assert.Equal("Second draft", model.EditContent);
        Assert.Equal("First draft", gateway.MemoryRecords.Single(memory => memory.MemoryId == first.MemoryId).Content);
    }

    [Fact]
    public async Task GraphAndData_UseBoundedPagesAndSharedFilters()
    {
        var session = Session("Graph");
        var now = DateTimeOffset.UtcNow;
        var records = Enumerable.Range(0, 220).Select(index => new StoredMemoryRecord(Guid.NewGuid(), session.SessionId,
            index % 2 == 0 ? "preference" : "project-fact", "Memory " + index, null, null, 1, 1, index == 0,
            "Active", null, now, now, null, 0)).ToArray();
        var gateway = new DeferredMemoryInspectorGateway([session]) { MemoryRecords = records };
        using var model = new MemoryInspectorViewModel(gateway);
        await model.InitializeAsync();
        Assert.Equal(200, model.Memories.Count);
        Assert.True(model.CanLoadMoreMemories);
        await model.LoadMoreMemoriesCommand.ExecuteAsync(null);
        Assert.Equal(220, model.Memories.Count);
        Assert.False(model.CanLoadMoreMemories);
        var list = model.FilteredMemories;
        var retained = list.ToArray();
        var collectionChanges = 0;
        list.CollectionChanged += (_, _) => collectionChanges++;
        await model.RefreshCommand.ExecuteAsync(null);
        Assert.Same(list, model.FilteredMemories);
        Assert.Equal(0, collectionChanges);
        Assert.Equal(retained, model.FilteredMemories.ToArray());
        model.CategoryFilter = "preference";
        model.OnlyPinned = true;
        Assert.Single(model.FilteredMemories);
        Assert.Single(model.Graph.Nodes);
    }

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

    [Fact]
    public async Task ResetDuringInitialization_ReplaysFullSessionAndWorkerRefresh()
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
        gateway.RaiseResetRequired();
        initialSessions.Complete([stale]);
        await initialization.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(2, gateway.SessionListCallCount);
        Assert.Equal(2, gateway.WorkerStatusCallCount);
        Assert.Equal(current.SessionId, Assert.Single(viewModel.Sessions).SessionId);
        Assert.Equal("current-worker", viewModel.SemanticWorkerStatusText);
    }

    [Fact]
    public async Task RefreshCommand_ExpectedGatewayFailureIsContainedAndReconciled()
    {
        var selected = Session("Selected");
        var gateway = new DeferredMemoryInspectorGateway([selected]);
        using var viewModel = new MemoryInspectorViewModel(gateway);
        await viewModel.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(2));
        var failedRefresh = gateway.DeferNextSessionList();

        var refresh = viewModel.RefreshCommand.ExecuteAsync(null);
        await failedRefresh.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        failedRefresh.Fail(new SunderRpcException(new SunderRpcError(
            SunderRpcErrorKind.Unavailable,
            "test.memory-unavailable",
            "Injected semantic memory transport failure.")));
        await refresh.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(selected.SessionId, Assert.Single(viewModel.Sessions).SessionId);
        Assert.True(gateway.SessionListCallCount >= 3);
    }

    private static AgentSessionRecord Session(string title)
    {
        var now = DateTimeOffset.UtcNow;
        return new AgentSessionRecord
        {
            SessionId = Guid.NewGuid(),
            Title = title,
            State = AgentSessionState.Active,
            CreatedAtUtc = RpcTimestamp(now),
            UpdatedAtUtc = RpcTimestamp(now),
        };
    }

    private static SunderRpcOffsetDateTime RpcTimestamp(DateTimeOffset value)
        => new(value.ToString("O", System.Globalization.CultureInfo.InvariantCulture));

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
        public IReadOnlyList<StoredMemoryRecord> MemoryRecords { get; set; } = [];
        public TaskCompletionSource? SaveRelease { get; set; }
        public TaskCompletionSource SaveEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? DetailsRelease { get; set; }

        public event Action<Guid>? SessionChanged;

        public event Action? SemanticWorkerStatusChanged;

        public event Action? ResetRequired;

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

        public void RaiseResetRequired() => ResetRequired?.Invoke();

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
            => Task.FromResult<IReadOnlyList<StoredMemoryRecord>>(MemoryRecords.Where(memory => memory.SessionId == sessionId
                && (includeInactive || memory.State == "Active")
                && (string.IsNullOrEmpty(searchText) || memory.Content.Contains(searchText, StringComparison.OrdinalIgnoreCase))).ToArray());

        public async Task<IReadOnlyList<StoredMemoryEvidenceRecord>> ListEvidenceAsync(
            Guid memoryId,
            CancellationToken cancellationToken = default)
        {
            if (DetailsRelease is not null) await DetailsRelease.Task.WaitAsync(cancellationToken);
            return [];
        }

        public Task<StoredMemoryRecord?> GetSupersedingMemoryAsync(
            Guid memoryId,
            CancellationToken cancellationToken = default)
            => Task.FromResult(MemoryRecords.FirstOrDefault(memory => memory.MemoryId ==
                MemoryRecords.FirstOrDefault(selected => selected.MemoryId == memoryId)?.SupersededByMemoryId));

        public Task<IReadOnlyList<StoredMemoryRecord>> ListSupersededMemoriesAsync(
            Guid memoryId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<StoredMemoryRecord>>(MemoryRecords.Where(memory => memory.SupersededByMemoryId == memoryId).ToArray());

        public Task<IReadOnlyList<StoredMemoryRecord>> ListCorrectionLineageAsync(
            Guid memoryId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<StoredMemoryRecord>>(MemoryRecords.Where(memory => memory.MemoryId == memoryId
                || memory.SupersededByMemoryId == memoryId
                || memory.MemoryId == MemoryRecords.FirstOrDefault(selected => selected.MemoryId == memoryId)?.SupersededByMemoryId).ToArray());

        public async Task<StoredMemoryRecord> UpdateMemoryAsync(
            Guid memoryId,
            string category,
            string content,
            string? note,
            CancellationToken cancellationToken = default)
        {
            SaveEntered.TrySetResult();
            if (SaveRelease is not null) await SaveRelease.Task.WaitAsync(cancellationToken);
            var record = MemoryRecords.Single(memory => memory.MemoryId == memoryId) with { Category = category, Content = content };
            MemoryRecords = MemoryRecords.Select(memory => memory.MemoryId == memoryId ? record : memory).ToArray();
            return record;
        }

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

        public void Fail(Exception exception) => _completion.TrySetException(exception);
    }
}
