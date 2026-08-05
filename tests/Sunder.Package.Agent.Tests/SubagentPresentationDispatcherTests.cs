extern alias SubagentsPackage;

using System.Collections.Concurrent;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Subagents.Models;
using Sunder.Package.Agent.Subagents.PackageViews;
using Sunder.Package.Agent.Subagents.Runtime;
using Xunit;
using SubagentPresentationDispatcher = SubagentsPackage::Sunder.Package.Agent.Shared.Presentation.IPresentationDispatcher;
using SubagentProviderCatalogOption = SubagentsPackage::Sunder.Package.Agent.Shared.Presentation.ProviderCatalogOption;
using SubagentProviderModelCatalogResult = SubagentsPackage::Sunder.Package.Agent.Shared.Presentation.ProviderModelCatalogResult;

namespace Sunder.Package.Agent.Tests;

public sealed class SubagentPresentationDispatcherTests
{
    [Fact]
    public Task CreateSubagentSuccess_AppliesHydrationAndCompletionOnDispatcher()
        => VerifyCreateHydrationAsync(failHydration: false);

    [Fact]
    public Task CreateSubagentHydrationFailure_AppliesErrorAndCompletionOnDispatcher()
        => VerifyCreateHydrationAsync(failHydration: true);

    [Fact]
    public async Task SaveSubagentCompletion_NotifiesSiblingCommandOnDispatcher()
    {
        using var dispatcher = new DedicatedSubagentDispatcher();
        var gateway = new DeferredMutationGateway(CreateSubagent("existing", "Existing"));
        SubagentsViewModel? viewModel = null;
        try
        {
            await dispatcher.InvokeAsync(() =>
                viewModel = new SubagentsViewModel(gateway, null, dispatcher));
            await InitializeAsync(viewModel!, dispatcher);

            var propertyEvents = new ConcurrentQueue<(string? Name, bool IsBusy, int ThreadId)>();
            var siblingCommandThreads = new ConcurrentQueue<int>();
            viewModel!.PropertyChanged += (_, args) => propertyEvents.Enqueue((
                args.PropertyName,
                viewModel.IsBusy,
                Environment.CurrentManagedThreadId));
            viewModel.DeleteSubagentCommand.CanExecuteChanged += (_, _) =>
                siblingCommandThreads.Enqueue(Environment.CurrentManagedThreadId);

            Task save = Task.CompletedTask;
            await dispatcher.InvokeAsync(() => save = viewModel.SaveSubagentCommand.ExecuteAsync(null));
            await gateway.SaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Run(gateway.CompleteSave);
            await save.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.NotEqual(dispatcher.ThreadId, gateway.SaveCompletionThreadId);
            Assert.False(viewModel.IsBusy);
            Assert.Contains(propertyEvents, item => item.Name == nameof(SubagentsViewModel.IsBusy) && !item.IsBusy);
            AssertDispatcherThreads(dispatcher, propertyEvents.Select(item => item.ThreadId));
            AssertDispatcherThreads(dispatcher, siblingCommandThreads);
        }
        finally
        {
            if (viewModel is not null)
            {
                await dispatcher.InvokeAsync(viewModel.Dispose);
            }
        }
    }

    [Fact]
    public async Task DeleteSubagentCompletion_NotifiesSiblingCommandAndCollectionOnDispatcher()
    {
        using var dispatcher = new DedicatedSubagentDispatcher();
        var gateway = new DeferredMutationGateway(CreateSubagent("existing", "Existing"));
        SubagentsViewModel? viewModel = null;
        try
        {
            await dispatcher.InvokeAsync(() =>
                viewModel = new SubagentsViewModel(gateway, null, dispatcher));
            await InitializeAsync(viewModel!, dispatcher);

            var propertyEvents = new ConcurrentQueue<(string? Name, bool IsBusy, int ThreadId)>();
            var siblingCommandThreads = new ConcurrentQueue<int>();
            var collectionThreads = new ConcurrentQueue<int>();
            viewModel!.PropertyChanged += (_, args) => propertyEvents.Enqueue((
                args.PropertyName,
                viewModel.IsBusy,
                Environment.CurrentManagedThreadId));
            viewModel.SaveSubagentCommand.CanExecuteChanged += (_, _) =>
                siblingCommandThreads.Enqueue(Environment.CurrentManagedThreadId);
            viewModel.Subagents.CollectionChanged += (_, _) =>
                collectionThreads.Enqueue(Environment.CurrentManagedThreadId);

            Task delete = Task.CompletedTask;
            await dispatcher.InvokeAsync(() => delete = viewModel.DeleteSubagentCommand.ExecuteAsync(null));
            await gateway.DeleteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Run(gateway.CompleteDelete);
            await delete.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.NotEqual(dispatcher.ThreadId, gateway.DeleteCompletionThreadId);
            Assert.False(viewModel.IsBusy);
            Assert.Empty(viewModel.Subagents);
            Assert.Contains(propertyEvents, item => item.Name == nameof(SubagentsViewModel.IsBusy) && !item.IsBusy);
            AssertDispatcherThreads(dispatcher, propertyEvents.Select(item => item.ThreadId));
            AssertDispatcherThreads(dispatcher, siblingCommandThreads);
            AssertDispatcherThreads(dispatcher, collectionThreads);
        }
        finally
        {
            if (viewModel is not null)
            {
                await dispatcher.InvokeAsync(viewModel.Dispose);
            }
        }
    }

    [Fact]
    public async Task SubsessionInitialization_DeferredWorkerResultsApplyOnDispatcher()
    {
        using var dispatcher = new DedicatedSubagentDispatcher();
        var gateway = new DeferredSubsessionGateway();
        SubsessionsViewModel? viewModel = null;
        try
        {
            await dispatcher.InvokeAsync(() =>
                viewModel = new SubsessionsViewModel(gateway, gateway, gateway, gateway, dispatcher));
            var propertyThreads = new ConcurrentQueue<int>();
            var collectionThreads = new ConcurrentQueue<int>();
            viewModel!.PropertyChanged += (_, _) =>
                propertyThreads.Enqueue(Environment.CurrentManagedThreadId);
            viewModel.Subsessions.CollectionChanged += (_, _) =>
                collectionThreads.Enqueue(Environment.CurrentManagedThreadId);

            var initialization = viewModel.InitializeAsync();
            await Task.WhenAll(
                gateway.SessionsStarted.Task,
                gateway.CheckpointsStarted.Task).WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Run(gateway.CompleteSnapshot);
            await initialization.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.NotEqual(dispatcher.ThreadId, gateway.SnapshotCompletionThreadId);
            Assert.Single(viewModel.Subsessions);
            Assert.False(viewModel.HasLoadError);
            AssertDispatcherThreads(dispatcher, propertyThreads);
            AssertDispatcherThreads(dispatcher, collectionThreads);
        }
        finally
        {
            if (viewModel is not null)
            {
                await dispatcher.InvokeAsync(viewModel.Dispose);
            }
        }
    }

    [Fact]
    public async Task SubsessionTurnEvent_WorkerCallbackAppliesTranscriptOnDispatcher()
    {
        using var dispatcher = new DedicatedSubagentDispatcher();
        var gateway = new DeferredSubsessionGateway();
        SubsessionsViewModel? viewModel = null;
        try
        {
            await dispatcher.InvokeAsync(() =>
                viewModel = new SubsessionsViewModel(gateway, gateway, gateway, gateway, dispatcher));
            var initialization = viewModel!.InitializeAsync();
            await Task.WhenAll(
                gateway.SessionsStarted.Task,
                gateway.CheckpointsStarted.Task).WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Run(gateway.CompleteSnapshot);
            await initialization.WaitAsync(TimeSpan.FromSeconds(5));

            var propertyThreads = new ConcurrentQueue<int>();
            var messageThreads = new ConcurrentQueue<int>();
            viewModel.PropertyChanged += (_, _) =>
                propertyThreads.Enqueue(Environment.CurrentManagedThreadId);
            viewModel.Messages.CollectionChanged += (_, _) =>
                messageThreads.Enqueue(Environment.CurrentManagedThreadId);

            await Task.Run(gateway.RaiseTurnChanged);
            await dispatcher.InvokeAsync(() => { });

            Assert.NotEqual(dispatcher.ThreadId, gateway.TurnEventThreadId);
            Assert.Single(viewModel.Messages);
            AssertDispatcherThreads(dispatcher, propertyThreads);
            AssertDispatcherThreads(dispatcher, messageThreads);
        }
        finally
        {
            if (viewModel is not null)
            {
                await dispatcher.InvokeAsync(viewModel.Dispose);
            }
        }
    }

    [Fact]
    public async Task SubsessionPaging_DeferredWorkerPageAppliesOnDispatcher()
    {
        using var dispatcher = new DedicatedSubagentDispatcher();
        var gateway = new DeferredSubsessionGateway(deferOlderPage: true);
        SubsessionsViewModel? viewModel = null;
        try
        {
            await dispatcher.InvokeAsync(() =>
                viewModel = new SubsessionsViewModel(gateway, gateway, gateway, gateway, dispatcher));
            var initialization = viewModel!.InitializeAsync();
            await Task.WhenAll(
                gateway.SessionsStarted.Task,
                gateway.CheckpointsStarted.Task).WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Run(gateway.CompleteSnapshot);
            await initialization.WaitAsync(TimeSpan.FromSeconds(5));

            var propertyThreads = new ConcurrentQueue<int>();
            var messageThreads = new ConcurrentQueue<int>();
            viewModel.PropertyChanged += (_, _) =>
                propertyThreads.Enqueue(Environment.CurrentManagedThreadId);
            viewModel.Messages.CollectionChanged += (_, _) =>
                messageThreads.Enqueue(Environment.CurrentManagedThreadId);

            var paging = viewModel.LoadOlderTranscriptRowsAsync();
            await gateway.OlderPageStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Run(gateway.CompleteOlderPage);
            Assert.True(await paging.WaitAsync(TimeSpan.FromSeconds(5)));

            Assert.NotEqual(dispatcher.ThreadId, gateway.OlderPageCompletionThreadId);
            Assert.Equal(2, viewModel.Messages.Count);
            AssertDispatcherThreads(dispatcher, propertyThreads);
            AssertDispatcherThreads(dispatcher, messageThreads);
        }
        finally
        {
            if (viewModel is not null)
            {
                await dispatcher.InvokeAsync(viewModel.Dispose);
            }
        }
    }

    private static async Task VerifyCreateHydrationAsync(bool failHydration)
    {
        using var dispatcher = new DedicatedSubagentDispatcher();
        var gateway = new DeferredCreateGateway(failHydration);
        SubagentsViewModel? viewModel = null;
        try
        {
            await dispatcher.InvokeAsync(() =>
                viewModel = new SubagentsViewModel(gateway, null, dispatcher));
            await InitializeAsync(viewModel!, dispatcher);

            var propertyThreads = new ConcurrentQueue<int>();
            var saveCommandThreads = new ConcurrentQueue<int>();
            var deleteCommandThreads = new ConcurrentQueue<int>();
            var collectionThreads = new ConcurrentQueue<int>();
            viewModel!.PropertyChanged += (_, _) =>
                propertyThreads.Enqueue(Environment.CurrentManagedThreadId);
            viewModel.SaveSubagentCommand.CanExecuteChanged += (_, _) =>
                saveCommandThreads.Enqueue(Environment.CurrentManagedThreadId);
            viewModel.DeleteSubagentCommand.CanExecuteChanged += (_, _) =>
                deleteCommandThreads.Enqueue(Environment.CurrentManagedThreadId);
            viewModel.Subagents.CollectionChanged += (_, _) =>
                collectionThreads.Enqueue(Environment.CurrentManagedThreadId);

            Task create = Task.CompletedTask;
            await dispatcher.InvokeAsync(() => create = viewModel.CreateSubagentCommand.ExecuteAsync(null));
            await gateway.CreateStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Run(gateway.CompleteCreate);
            await Task.WhenAll(
                gateway.LocalToolsStarted.Task,
                gateway.CapabilitiesStarted.Task).WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Run(gateway.CompleteHydration);
            await create.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.NotEqual(dispatcher.ThreadId, gateway.CreateCompletionThreadId);
            Assert.NotEqual(dispatcher.ThreadId, gateway.HydrationCompletionThreadId);
            Assert.False(viewModel.IsBusy);
            Assert.False(viewModel.IsHydrating);
            Assert.Equal("created", viewModel.SelectedSubagent?.SubagentId);
            if (failHydration)
            {
                Assert.Equal(SubagentStatusKind.Error, viewModel.StatusKind);
                Assert.Equal("Create hydration failed.", viewModel.StatusText);
            }
            else
            {
                Assert.Equal(SubagentStatusKind.None, viewModel.StatusKind);
                Assert.Empty(viewModel.StatusText);
            }

            AssertDispatcherThreads(dispatcher, propertyThreads);
            AssertDispatcherThreads(dispatcher, saveCommandThreads);
            AssertDispatcherThreads(dispatcher, deleteCommandThreads);
            AssertDispatcherThreads(dispatcher, collectionThreads);
        }
        finally
        {
            if (viewModel is not null)
            {
                await dispatcher.InvokeAsync(viewModel.Dispose);
            }
        }
    }

    private static async Task InitializeAsync(
        SubagentsViewModel viewModel,
        DedicatedSubagentDispatcher dispatcher)
    {
        Task initialization = Task.CompletedTask;
        await dispatcher.InvokeAsync(() => initialization = viewModel.InitializeAsync());
        await initialization.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static void AssertDispatcherThreads(
        DedicatedSubagentDispatcher dispatcher,
        IEnumerable<int> threads)
    {
        var observed = threads.ToArray();
        Assert.NotEmpty(observed);
        Assert.All(observed, threadId => Assert.Equal(dispatcher.ThreadId, threadId));
    }

    private static SubagentRecord CreateSubagent(string id, string displayName)
    {
        var now = DateTimeOffset.UtcNow;
        return new SubagentRecord(
            id,
            displayName,
            "Description",
            null,
            null,
            null,
            [],
            now,
            now);
    }

    private sealed class DeferredCreateGateway(bool failHydration) : ISubagentManagementGateway
    {
        private readonly TaskCompletionSource<SubagentRecord> _create =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<IReadOnlyList<AgentToolDescriptor>> _localTools =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<IReadOnlyList<AgentProfileSelectableCapabilityDescriptor>> _capabilities =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _deferHydration;

        public TaskCompletionSource CreateStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource LocalToolsStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CapabilitiesStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int CreateCompletionThreadId { get; private set; }
        public int HydrationCompletionThreadId { get; private set; }

        public event Action? SubagentsChanged
        {
            add { }
            remove { }
        }

        public event Action? CatalogChanged
        {
            add { }
            remove { }
        }

        public Task<IReadOnlyList<SubagentRecord>> ListSubagentsAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<SubagentRecord>>([]);

        public Task<SubagentRecord> CreateSubagentAsync(
            string displayName,
            CancellationToken cancellationToken = default)
        {
            CreateStarted.TrySetResult();
            return _create.Task.WaitAsync(cancellationToken);
        }

        public Task<SubagentRecord> SaveSubagentAsync(
            SubagentSaveRequest request,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task DeleteSubagentAsync(
            string subagentId,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<SubagentProviderCatalogOption>> ListChatProvidersAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<SubagentProviderCatalogOption>>([]);

        public Task<SubagentProviderModelCatalogResult> LoadChatModelsAsync(
            string providerId,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new SubagentProviderModelCatalogResult([], string.Empty));

        public Task<IReadOnlyList<AgentToolDescriptor>> ListLocalToolsAsync(
            CancellationToken cancellationToken = default)
        {
            if (Volatile.Read(ref _deferHydration) == 0)
            {
                return Task.FromResult<IReadOnlyList<AgentToolDescriptor>>([]);
            }

            LocalToolsStarted.TrySetResult();
            return _localTools.Task.WaitAsync(cancellationToken);
        }

        public Task<IReadOnlyList<AgentProfileSelectableCapabilityDescriptor>> ListPackageCapabilitiesAsync(
            CancellationToken cancellationToken = default)
        {
            if (Volatile.Read(ref _deferHydration) == 0)
            {
                return Task.FromResult<IReadOnlyList<AgentProfileSelectableCapabilityDescriptor>>([]);
            }

            CapabilitiesStarted.TrySetResult();
            return _capabilities.Task.WaitAsync(cancellationToken);
        }

        public void CompleteCreate()
        {
            CreateCompletionThreadId = Environment.CurrentManagedThreadId;
            Volatile.Write(ref _deferHydration, 1);
            _create.SetResult(CreateSubagent("created", "Created"));
        }

        public void CompleteHydration()
        {
            HydrationCompletionThreadId = Environment.CurrentManagedThreadId;
            _capabilities.SetResult([]);
            if (failHydration)
            {
                _localTools.SetException(new InvalidOperationException("Create hydration failed."));
            }
            else
            {
                _localTools.SetResult([]);
            }
        }
    }

    private sealed class DeferredMutationGateway(SubagentRecord subagent) : ISubagentManagementGateway
    {
        private readonly TaskCompletionSource<SubagentRecord> _save =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _delete =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private SubagentSaveRequest? _saveRequest;

        public TaskCompletionSource SaveStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DeleteStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int SaveCompletionThreadId { get; private set; }
        public int DeleteCompletionThreadId { get; private set; }

        public event Action? SubagentsChanged
        {
            add { }
            remove { }
        }

        public event Action? CatalogChanged
        {
            add { }
            remove { }
        }

        public Task<IReadOnlyList<SubagentRecord>> ListSubagentsAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<SubagentRecord>>([subagent]);

        public Task<SubagentRecord> CreateSubagentAsync(
            string displayName,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<SubagentRecord> SaveSubagentAsync(
            SubagentSaveRequest request,
            CancellationToken cancellationToken = default)
        {
            _saveRequest = request;
            SaveStarted.TrySetResult();
            return _save.Task.WaitAsync(cancellationToken);
        }

        public Task DeleteSubagentAsync(
            string subagentId,
            CancellationToken cancellationToken = default)
        {
            DeleteStarted.TrySetResult();
            return _delete.Task.WaitAsync(cancellationToken);
        }

        public Task<IReadOnlyList<SubagentProviderCatalogOption>> ListChatProvidersAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<SubagentProviderCatalogOption>>([]);

        public Task<SubagentProviderModelCatalogResult> LoadChatModelsAsync(
            string providerId,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new SubagentProviderModelCatalogResult([], string.Empty));

        public Task<IReadOnlyList<AgentToolDescriptor>> ListLocalToolsAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AgentToolDescriptor>>([]);

        public Task<IReadOnlyList<AgentProfileSelectableCapabilityDescriptor>> ListPackageCapabilitiesAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AgentProfileSelectableCapabilityDescriptor>>([]);

        public void CompleteSave()
        {
            SaveCompletionThreadId = Environment.CurrentManagedThreadId;
            var request = _saveRequest ?? throw new InvalidOperationException("Save did not start.");
            _save.SetResult(subagent with
            {
                DisplayName = request.DisplayName,
                Description = request.Description,
                Instructions = request.Instructions,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            });
        }

        public void CompleteDelete()
        {
            DeleteCompletionThreadId = Environment.CurrentManagedThreadId;
            _delete.SetResult();
        }
    }

    private sealed class DeferredSubsessionGateway(bool deferOlderPage = false) :
        ISubsessionSessionReader,
        ISubsessionCheckpointReader,
        ISubsessionTranscriptPageReader,
        ISubsessionChangeNotifications
    {
        private readonly TaskCompletionSource<SubsessionSessionCatalog> _sessions =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<IReadOnlyList<AgentRunCheckpointRecord>> _checkpoints =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<SubsessionTranscriptPage> _olderPage =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Guid _rootSessionId = Guid.NewGuid();
        private readonly Guid _childSessionId = Guid.NewGuid();
        private readonly Guid _recentTurnId = Guid.NewGuid();
        private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;

        public TaskCompletionSource SessionsStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CheckpointsStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource OlderPageStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int SnapshotCompletionThreadId { get; private set; }
        public int TurnEventThreadId { get; private set; }
        public int OlderPageCompletionThreadId { get; private set; }

        public event Action<Guid>? SessionChanged
        {
            add { }
            remove { }
        }

        public event Action<Guid, AgentTurnRecord>? TurnChanged;

        public event Action? ResnapshotRequired
        {
            add { }
            remove { }
        }

        public Task<SubsessionSessionCatalog> ListSessionsAsync(
            CancellationToken cancellationToken = default)
        {
            SessionsStarted.TrySetResult();
            return _sessions.Task.WaitAsync(cancellationToken);
        }

        public Task<IReadOnlyList<AgentRunCheckpointRecord>> ListLatestCheckpointsAsync(
            CancellationToken cancellationToken = default)
        {
            CheckpointsStarted.TrySetResult();
            return _checkpoints.Task.WaitAsync(cancellationToken);
        }

        public Task<SubsessionTranscriptPage> ListRecentTurnsAsync(
            Guid sessionId,
            int limit,
            CancellationToken cancellationToken = default)
            => Task.FromResult(deferOlderPage
                ? new SubsessionTranscriptPage(
                    [CreateTextTurn(_recentTurnId, _childSessionId, _now, "Recent")],
                    true)
                : new SubsessionTranscriptPage([], false));

        public Task<SubsessionTranscriptPage> ListTurnsBeforeAsync(
            Guid sessionId,
            DateTimeOffset beforeCreatedAtUtc,
            Guid beforeTurnId,
            int limit,
            CancellationToken cancellationToken = default)
        {
            if (!deferOlderPage)
            {
                return Task.FromResult(new SubsessionTranscriptPage([], false));
            }

            OlderPageStarted.TrySetResult();
            return _olderPage.Task.WaitAsync(cancellationToken);
        }

        public Task<SubsessionTranscriptPage> ListTurnsAfterAsync(
            Guid sessionId,
            DateTimeOffset afterCreatedAtUtc,
            Guid afterTurnId,
            int limit,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new SubsessionTranscriptPage([], false));

        public Task<SubsessionAroundTurnPage> LoadAroundTurnAsync(
            Guid sessionId,
            Guid turnId,
            DateTimeOffset turnCreatedAtUtc,
            Guid itemId,
            int beforeLimit,
            int afterLimit,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new SubsessionAroundTurnPage([], false, false, turnId));

        public void CompleteSnapshot()
        {
            SnapshotCompletionThreadId = Environment.CurrentManagedThreadId;
            var now = DateTimeOffset.UtcNow;
            _sessions.SetResult(new SubsessionSessionCatalog(
            [
                new AgentSessionRecord(
                    _rootSessionId,
                    "Root",
                    AgentSessionState.Active,
                    now,
                    now,
                    RootSessionId: _rootSessionId),
                new AgentSessionRecord(
                    _childSessionId,
                    "Child",
                    AgentSessionState.Active,
                    now,
                    now,
                    ParentSessionId: _rootSessionId,
                    RootSessionId: _rootSessionId,
                    AgentKind: "subagent"),
            ],
            []));
            _checkpoints.SetResult([]);
        }

        public void RaiseTurnChanged()
        {
            TurnEventThreadId = Environment.CurrentManagedThreadId;
            var now = DateTimeOffset.UtcNow;
            var turnId = Guid.NewGuid();
            var turn = new AgentTurnRecord(
                turnId,
                _childSessionId,
                AgentMessageRole.Assistant,
                AgentTurnKind.Message,
                [new AgentTurnItemRecord(
                    Guid.NewGuid(),
                    turnId,
                    0,
                    AgentTurnItemKind.Text,
                    "Deferred transcript update",
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    false,
                    false,
                    null,
                    null)],
                now,
                now);
            TurnChanged?.Invoke(_childSessionId, turn);
        }

        public void CompleteOlderPage()
        {
            OlderPageCompletionThreadId = Environment.CurrentManagedThreadId;
            _olderPage.SetResult(new SubsessionTranscriptPage(
                [CreateTextTurn(Guid.NewGuid(), _childSessionId, _now.AddMinutes(-1), "Older")],
                false));
        }

        private static AgentTurnRecord CreateTextTurn(
            Guid turnId,
            Guid sessionId,
            DateTimeOffset timestamp,
            string text)
            => new(
                turnId,
                sessionId,
                AgentMessageRole.Assistant,
                AgentTurnKind.Message,
                [new AgentTurnItemRecord(
                    Guid.NewGuid(),
                    turnId,
                    0,
                    AgentTurnItemKind.Text,
                    text,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    false,
                    false,
                    null,
                    null)],
                timestamp,
                timestamp);
    }

    private sealed class DedicatedSubagentDispatcher : SubagentPresentationDispatcher, IDisposable
    {
        private readonly BlockingCollection<WorkItem> _queue = new();
        private readonly Thread _thread;
        private readonly TaskCompletionSource<int> _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public DedicatedSubagentDispatcher()
        {
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "Subagent presentation test dispatcher",
            };
            _thread.Start();
            ThreadId = _started.Task.GetAwaiter().GetResult();
        }

        public int ThreadId { get; }

        public bool CheckAccess() => Environment.CurrentManagedThreadId == ThreadId;

        public Task InvokeAsync(Action action)
        {
            if (CheckAccess())
            {
                action();
                return Task.CompletedTask;
            }

            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _queue.Add(new WorkItem(action, completion));
            return completion.Task;
        }

        private void Run()
        {
            _started.SetResult(Environment.CurrentManagedThreadId);
            foreach (var item in _queue.GetConsumingEnumerable())
            {
                try
                {
                    item.Action();
                    item.Completion.SetResult();
                }
                catch (Exception ex)
                {
                    item.Completion.SetException(ex);
                }
            }
        }

        public void Dispose()
        {
            _queue.CompleteAdding();
            _thread.Join();
            _queue.Dispose();
        }

        private sealed record WorkItem(Action Action, TaskCompletionSource Completion);
    }
}
