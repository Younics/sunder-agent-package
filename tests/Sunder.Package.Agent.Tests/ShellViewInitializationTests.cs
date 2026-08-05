extern alias SubagentsPackage;

using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Subagents.Models;
using Sunder.Package.Agent.Subagents.PackageViews;
using Sunder.Package.Agent.Subagents.Runtime;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Runtime;
using Xunit;
using SubagentProviderCatalogOption = SubagentsPackage::Sunder.Package.Agent.Shared.Presentation.ProviderCatalogOption;
using SubagentProviderModelCatalogResult = SubagentsPackage::Sunder.Package.Agent.Shared.Presentation.ProviderModelCatalogResult;

namespace Sunder.Package.Agent.Tests;

public sealed class ShellViewInitializationTests
{
    [Fact]
    public async Task SubagentsViewModel_ConstructionIsColdAndInitializationIsShared()
    {
        var gateway = new BlockingSubagentGateway();
        using var viewModel = new SubagentsViewModel(gateway);

        Assert.Equal(0, gateway.ListCount);
        var warmup = viewModel.InitializeAsync();
        await gateway.ListStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using var cancellation = new CancellationTokenSource();
        var canceledWait = viewModel.InitializeAsync(cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledWait);
        Assert.Equal(1, gateway.ListCount);
        gateway.ReleaseList.TrySetResult();
        await warmup;
        await viewModel.InitializeAsync();

        Assert.Equal(1, gateway.ListCount);
    }

    [Fact]
    public async Task SubagentsViewModel_SelectionDuringSaveSuppressesLateNavigationAndStatus()
    {
        var gateway = new SaveRaceSubagentGateway();
        using var viewModel = new SubagentsViewModel(gateway);
        await viewModel.InitializeAsync();
        viewModel.IsCompactLayout = true;
        viewModel.ActivateSubagent(viewModel.Subagents.Single(item => item.SubagentId == "alpha"));
        viewModel.Description = "Saved description";

        var save = viewModel.SaveSubagentCommand.ExecuteAsync(null);
        await gateway.SaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        viewModel.ActivateSubagent(viewModel.Subagents.Single(item => item.SubagentId == "beta"));

        gateway.ReleaseSave.TrySetResult();
        await save;

        Assert.Equal("beta", viewModel.SelectedSubagent?.SubagentId);
        Assert.Equal("Beta", viewModel.DisplayName);
        Assert.Empty(viewModel.StatusText);
    }

    [Fact]
    public async Task SubagentsViewModel_ResizeDuringSaveCannotClearEditedSelection()
    {
        var gateway = new SaveRaceSubagentGateway();
        using var viewModel = new SubagentsViewModel(gateway);
        await viewModel.InitializeAsync();
        viewModel.Description = "Saved description";

        var save = viewModel.SaveSubagentCommand.ExecuteAsync(null);
        await gateway.SaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        viewModel.IsCompactLayout = true;
        gateway.ReleaseSave.TrySetResult();
        await save.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal("alpha", viewModel.SelectedSubagent?.SubagentId);
        Assert.True(viewModel.IsEditorActive);
        Assert.True(viewModel.ShowCompactEditor);
    }

    [Fact]
    public async Task SubsessionsViewModel_WarmupAndNavigationShareOneSnapshotLoad()
    {
        var gateway = new BlockingSubsessionGateway();
        using var viewModel = new SubsessionsViewModel(gateway, gateway, gateway, gateway);
        var context = new PackageViewNavigationContext("subsessions", new Dictionary<string, string?>());

        Assert.Equal(0, gateway.SessionListCount);
        Assert.Equal(0, gateway.CheckpointListCount);
        var warmup = viewModel.InitializeAsync();
        await Task.WhenAll(
            gateway.SessionListStarted.Task,
            gateway.CheckpointListStarted.Task).WaitAsync(TimeSpan.FromSeconds(2));
        var navigation = viewModel.OnNavigatedToAsync(context).AsTask();
        using var cancellation = new CancellationTokenSource();
        var canceledNavigation = viewModel.OnNavigatedToAsync(context, cancellation.Token).AsTask();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledNavigation);
        Assert.Equal(1, gateway.SessionListCount);
        Assert.Equal(1, gateway.CheckpointListCount);
        gateway.ReleaseLists.TrySetResult();
        await warmup;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => navigation);
        await viewModel.OnNavigatedToAsync(context);

        Assert.Equal(1, gateway.SessionListCount);
        Assert.Equal(1, gateway.CheckpointListCount);
    }

    [Fact]
    public async Task SubsessionsViewModel_LatestNavigationWinsWhenOlderAnchorLoadFinishesLate()
    {
        var gateway = new LatestNavigationSubsessionGateway();
        using var viewModel = new SubsessionsViewModel(gateway, gateway, gateway, gateway);
        await viewModel.InitializeAsync();

        var firstNavigation = viewModel.OnNavigatedToAsync(
            CreateAnchorNavigation(
                gateway.FirstSessionId,
                gateway.FirstTurnId,
                gateway.FirstItemId)).AsTask();
        await gateway.FirstAnchorLoadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await viewModel.OnNavigatedToAsync(
            CreateAnchorNavigation(
                gateway.SecondSessionId,
                gateway.SecondTurnId,
                gateway.SecondItemId));
        gateway.ReleaseFirstAnchorLoad.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => firstNavigation);

        Assert.Equal(gateway.SecondSessionId, viewModel.SelectedSubsession?.SessionId);
        Assert.Equal($"text:{gateway.SecondTurnId:N}", viewModel.NavigationAnchorKey?.ToString());
    }

    [Fact]
    public async Task SubsessionsViewModel_UserSelectionSupersedesPendingAnchorLoad()
    {
        var gateway = new LatestNavigationSubsessionGateway();
        using var viewModel = new SubsessionsViewModel(gateway, gateway, gateway, gateway);
        await viewModel.InitializeAsync();

        var firstNavigation = viewModel.OnNavigatedToAsync(
            CreateAnchorNavigation(
                gateway.FirstSessionId,
                gateway.FirstTurnId,
                gateway.FirstItemId)).AsTask();
        await gateway.FirstAnchorLoadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        viewModel.ActivateSubsession(Assert.Single(
            viewModel.Subsessions,
            session => session.SessionId == gateway.SecondSessionId));
        gateway.ReleaseFirstAnchorLoad.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => firstNavigation);

        Assert.Equal(gateway.SecondSessionId, viewModel.SelectedSubsession?.SessionId);
        Assert.Null(viewModel.NavigationAnchorKey);
        var row = Assert.Single(viewModel.Messages.OfType<SubsessionTextTranscriptRowViewModel>());
        Assert.Equal(gateway.SecondTurnId, row.RowId);
    }

    [Fact]
    public async Task SubsessionsViewModel_UserSelectionSupersedesPendingTranscriptLoad()
    {
        var gateway = new LatestNavigationSubsessionGateway { BlockSecondRecentLoad = true };
        using var viewModel = new SubsessionsViewModel(gateway, gateway, gateway, gateway);
        await viewModel.InitializeAsync();

        var navigation = viewModel.OnNavigatedToAsync(new PackageViewNavigationContext(
            "subsessions",
            new Dictionary<string, string?>
            {
                ["sessionId"] = gateway.SecondSessionId.ToString("D"),
            })).AsTask();
        await gateway.SecondRecentLoadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        viewModel.ActivateSubsession(Assert.Single(
            viewModel.Subsessions,
            session => session.SessionId == gateway.FirstSessionId));
        gateway.ReleaseSecondRecentLoad.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => navigation);

        Assert.Equal(gateway.FirstSessionId, viewModel.SelectedSubsession?.SessionId);
        Assert.Null(viewModel.NavigationAnchorKey);
        var row = Assert.Single(viewModel.Messages.OfType<SubsessionTextTranscriptRowViewModel>());
        Assert.Equal(gateway.FirstTurnId, row.RowId);
    }

    [Fact]
    public async Task SubsessionsViewModel_BackSupersedesPendingNavigationWithoutReopeningDetail()
    {
        var gateway = new LatestNavigationSubsessionGateway { BlockSecondRecentLoad = true };
        using var viewModel = new SubsessionsViewModel(gateway, gateway, gateway, gateway);
        await viewModel.InitializeAsync();
        viewModel.IsCompactLayout = true;
        var navigation = viewModel.OnNavigatedToAsync(new PackageViewNavigationContext(
            "subsessions",
            new Dictionary<string, string?>
            {
                ["sessionId"] = gateway.SecondSessionId.ToString("D"),
            })).AsTask();
        await gateway.SecondRecentLoadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        viewModel.BackToSubsessionsListCommand.Execute(null);
        gateway.ReleaseSecondRecentLoad.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => navigation);

        Assert.Null(viewModel.SelectedSubsession);
        Assert.False(viewModel.IsDetailActive);
        Assert.True(viewModel.ShowCompactList);
        Assert.Empty(viewModel.Messages);
        Assert.Null(viewModel.NavigationAnchorKey);
    }

    [Fact]
    public async Task SubsessionsViewModel_RefreshPreservesSelectionChangedDuringRead()
    {
        var gateway = new RefreshRaceSubsessionGateway();
        using var viewModel = new SubsessionsViewModel(gateway, gateway, gateway, gateway);
        await viewModel.InitializeAsync();

        var refresh = viewModel.ReloadSubsessionsAsync(null);
        await Task.WhenAll(
            gateway.StaleSessionReadStarted.Task,
            gateway.StaleCheckpointReadStarted.Task).WaitAsync(TimeSpan.FromSeconds(2));
        viewModel.ActivateSubsession(Assert.Single(
            viewModel.Subsessions,
            session => session.SessionId == gateway.SecondSessionId));

        gateway.ReleaseStaleRefresh.TrySetResult();
        await refresh;

        Assert.Equal(gateway.SecondSessionId, viewModel.SelectedSubsession?.SessionId);
        var row = Assert.Single(viewModel.Messages.OfType<SubsessionTextTranscriptRowViewModel>());
        Assert.Equal(gateway.SecondTurnId, row.RowId);
    }

    [Fact]
    public async Task SubsessionsViewModel_StaleRefreshCannotOverwriteNewerSnapshot()
    {
        var gateway = new RefreshRaceSubsessionGateway();
        using var viewModel = new SubsessionsViewModel(gateway, gateway, gateway, gateway);
        await viewModel.InitializeAsync();

        var staleRefresh = viewModel.ReloadSubsessionsAsync(null);
        await Task.WhenAll(
            gateway.StaleSessionReadStarted.Task,
            gateway.StaleCheckpointReadStarted.Task).WaitAsync(TimeSpan.FromSeconds(2));
        viewModel.ActivateSubsession(Assert.Single(
            viewModel.Subsessions,
            session => session.SessionId == gateway.SecondSessionId));
        await viewModel.ReloadSubsessionsAsync(null);

        var selected = Assert.Single(
            viewModel.Subsessions,
            session => session.SessionId == gateway.SecondSessionId);
        Assert.Equal("Second fresh", selected.Title);
        gateway.ReleaseStaleRefresh.TrySetResult();
        await staleRefresh;

        Assert.Equal(gateway.SecondSessionId, viewModel.SelectedSubsession?.SessionId);
        Assert.Equal("Second fresh", selected.Title);
    }

    [Fact]
    public async Task SubsessionsViewModel_FailedNavigationClearsPreviousAnchorKey()
    {
        var gateway = new LatestNavigationSubsessionGateway();
        using var viewModel = new SubsessionsViewModel(gateway, gateway, gateway, gateway);
        await viewModel.InitializeAsync();
        await viewModel.OnNavigatedToAsync(
            CreateAnchorNavigation(
                gateway.SecondSessionId,
                gateway.SecondTurnId,
                gateway.SecondItemId));
        Assert.NotNull(viewModel.NavigationAnchorKey);

        await Assert.ThrowsAsync<InvalidOperationException>(() => viewModel.OnNavigatedToAsync(
            CreateAnchorNavigation(Guid.NewGuid(), Guid.NewGuid())).AsTask());

        Assert.Null(viewModel.NavigationAnchorKey);
    }

    private static PackageViewNavigationContext CreateAnchorNavigation(
        Guid sessionId,
        Guid turnId,
        Guid? itemId = null)
        => new(
            "subsessions",
            new Dictionary<string, string?>
            {
                ["sessionId"] = sessionId.ToString("D"),
                ["turnId"] = turnId.ToString("D"),
                ["itemId"] = (itemId ?? Guid.NewGuid()).ToString("D"),
                ["anchorKind"] = "Text",
                ["createdAtUtc"] = DateTimeOffset.UtcNow.ToString("O"),
            });

    [Fact]
    public async Task RuntimeBackedSubsessions_RetriesFaultedSharedSnapshot()
    {
        var runtime = new FailOnceSubsessionRuntimeClient();
        using var gateway = new SubagentAppRuntimeGateway(runtime);
        using var viewModel = new SubsessionsViewModel(gateway, gateway, gateway, gateway);

        await viewModel.InitializeAsync();
        await viewModel.InitializeAsync();

        Assert.Equal(2, runtime.RuntimeCatalogQueryCount);
        Assert.Equal("No sub-sessions have been created yet.", viewModel.StatusText);
    }

    [Fact]
    public async Task SubsessionsViewModel_OrdinaryNavigationPresentsRetryableRuntimeHydrationFailure()
    {
        var runtime = new FailOnceSubsessionRuntimeClient();
        using var gateway = new SubagentAppRuntimeGateway(runtime);
        using var viewModel = new SubsessionsViewModel(gateway, gateway, gateway, gateway);
        var context = new PackageViewNavigationContext(
            "subsessions",
            new Dictionary<string, string?>());

        Assert.True(await viewModel.PrepareNavigationAsync(context));
        Assert.True(viewModel.HasLoadError);
        Assert.Contains("Injected Runtime catalog failure", viewModel.StatusText, StringComparison.Ordinal);

        await viewModel.RetryLoadCommand.ExecuteAsync(null);

        Assert.False(viewModel.HasLoadError);
        Assert.Equal("No sub-sessions have been created yet.", viewModel.StatusText);
        Assert.Equal(2, runtime.RuntimeCatalogQueryCount);
    }

    [Fact]
    public async Task RuntimeBackedSubsessions_CallerCancellationKeepsSharedSnapshot()
    {
        var runtime = new BlockingSubsessionRuntimeClient();
        using var gateway = new SubagentAppRuntimeGateway(runtime);
        using var cancellation = new CancellationTokenSource();
        var first = gateway.ListSessionsAsync(cancellation.Token);
        await runtime.RuntimeCatalogStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);

        var second = gateway.ListLatestCheckpointsAsync();
        runtime.ReleaseRuntimeCatalog.TrySetResult();

        Assert.Empty(await second.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(1, runtime.RuntimeCatalogQueryCount);
    }

    [Fact]
    public async Task RuntimeBackedSubsessions_RetriesSharedSnapshotFaultedAfterCallerCancellation()
    {
        var runtime = new BlockingSubsessionRuntimeClient { FailRuntimeCatalog = true };
        using var gateway = new SubagentAppRuntimeGateway(runtime);
        using var cancellation = new CancellationTokenSource();
        var canceledWaiter = gateway.ListSessionsAsync(cancellation.Token);
        await runtime.RuntimeCatalogStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledWaiter);
        runtime.ReleaseRuntimeCatalog.TrySetResult();
        await runtime.InvocationCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        runtime.FailRuntimeCatalog = false;
        Assert.Empty((await gateway.ListSessionsAsync().WaitAsync(TimeSpan.FromSeconds(2))).Sessions);
        Assert.Equal(2, runtime.RuntimeCatalogQueryCount);
    }

    private sealed class BlockingSubagentGateway : ISubagentManagementGateway
    {
        private int _listCount;

        public int ListCount => Volatile.Read(ref _listCount);
        public TaskCompletionSource ListStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseList { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

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

        public async Task<IReadOnlyList<SubagentRecord>> ListSubagentsAsync(
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _listCount);
            ListStarted.TrySetResult();
            await ReleaseList.Task.WaitAsync(cancellationToken);
            return [];
        }

        public Task<SubagentRecord> CreateSubagentAsync(
            string displayName,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

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
            => Task.FromResult<IReadOnlyList<AgentToolDescriptor>>([]);

        public Task<IReadOnlyList<AgentProfileSelectableCapabilityDescriptor>> ListPackageCapabilitiesAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AgentProfileSelectableCapabilityDescriptor>>([]);
    }

    private sealed class SaveRaceSubagentGateway : ISubagentManagementGateway
    {
        private readonly SubagentRecord[] _subagents =
        [
            CreateSubagent("alpha", "Alpha"),
            CreateSubagent("beta", "Beta"),
        ];

        public TaskCompletionSource SaveStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseSave { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

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
            => Task.FromResult<IReadOnlyList<SubagentRecord>>(_subagents);

        public Task<SubagentRecord> CreateSubagentAsync(
            string displayName,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public async Task<SubagentRecord> SaveSubagentAsync(
            SubagentSaveRequest request,
            CancellationToken cancellationToken = default)
        {
            SaveStarted.TrySetResult();
            await ReleaseSave.Task;
            return _subagents.Single(item => item.SubagentId == request.SubagentId) with
            {
                DisplayName = request.DisplayName,
                Description = request.Description,
                Instructions = request.Instructions,
                ChatProviderId = request.ChatProviderId,
                ChatModelId = request.ChatModelId,
                SelectableCapabilityAssignments = request.Assignments,
                ChatModelSettingsJson = request.ChatModelSettingsJson,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            };
        }

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
            => Task.FromResult<IReadOnlyList<AgentToolDescriptor>>([]);

        public Task<IReadOnlyList<AgentProfileSelectableCapabilityDescriptor>> ListPackageCapabilitiesAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AgentProfileSelectableCapabilityDescriptor>>([]);

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
    }

    private sealed class BlockingSubsessionGateway :
        ISubsessionSessionReader,
        ISubsessionCheckpointReader,
        ISubsessionTranscriptPageReader,
        ISubsessionChangeNotifications
    {
        private int _sessionListCount;
        private int _checkpointListCount;

        public int SessionListCount => Volatile.Read(ref _sessionListCount);
        public int CheckpointListCount => Volatile.Read(ref _checkpointListCount);
        public TaskCompletionSource SessionListStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CheckpointListStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseLists { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public event Action<Guid>? SessionChanged
        {
            add { }
            remove { }
        }

        public event Action<Guid, AgentTurnRecord>? TurnChanged
        {
            add { }
            remove { }
        }

        public event Action? ResnapshotRequired
        {
            add { }
            remove { }
        }

        public async Task<SubsessionSessionCatalog> ListSessionsAsync(
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _sessionListCount);
            SessionListStarted.TrySetResult();
            await ReleaseLists.Task.WaitAsync(cancellationToken);
            return new SubsessionSessionCatalog([], []);
        }

        public async Task<IReadOnlyList<AgentRunCheckpointRecord>> ListLatestCheckpointsAsync(
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _checkpointListCount);
            CheckpointListStarted.TrySetResult();
            await ReleaseLists.Task.WaitAsync(cancellationToken);
            return [];
        }

        public Task<SubsessionTranscriptPage> ListRecentTurnsAsync(
            Guid sessionId,
            int limit,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new SubsessionTranscriptPage([], false));

        public Task<SubsessionTranscriptPage> ListTurnsBeforeAsync(
            Guid sessionId,
            DateTimeOffset beforeCreatedAtUtc,
            Guid beforeTurnId,
            int limit,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new SubsessionTranscriptPage([], false));

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
    }

    private sealed class LatestNavigationSubsessionGateway :
        ISubsessionSessionReader,
        ISubsessionCheckpointReader,
        ISubsessionTranscriptPageReader,
        ISubsessionChangeNotifications
    {
        private readonly Guid _rootSessionId = Guid.NewGuid();

        internal Guid FirstSessionId { get; } = Guid.NewGuid();
        internal Guid SecondSessionId { get; } = Guid.NewGuid();
        internal Guid FirstTurnId { get; } = Guid.NewGuid();
        internal Guid SecondTurnId { get; } = Guid.NewGuid();
        internal Guid FirstItemId { get; } = Guid.NewGuid();
        internal Guid SecondItemId { get; } = Guid.NewGuid();
        internal TaskCompletionSource FirstAnchorLoadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseFirstAnchorLoad { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource SecondRecentLoadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseSecondRecentLoad { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool BlockSecondRecentLoad { get; init; }

        public event Action<Guid>? SessionChanged
        {
            add { }
            remove { }
        }

        public event Action<Guid, AgentTurnRecord>? TurnChanged
        {
            add { }
            remove { }
        }

        public event Action? ResnapshotRequired
        {
            add { }
            remove { }
        }

        public Task<SubsessionSessionCatalog> ListSessionsAsync(
            CancellationToken cancellationToken = default)
        {
            var now = DateTimeOffset.UtcNow;
            return Task.FromResult(new SubsessionSessionCatalog(
                [
                    new AgentSessionRecord(
                        _rootSessionId,
                        "Root",
                        AgentSessionState.Active,
                        now,
                        now,
                        RootSessionId: _rootSessionId),
                    new AgentSessionRecord(
                        FirstSessionId,
                        "First",
                        AgentSessionState.Active,
                        now,
                        now,
                        ParentSessionId: _rootSessionId,
                        RootSessionId: _rootSessionId),
                    new AgentSessionRecord(
                        SecondSessionId,
                        "Second",
                        AgentSessionState.Active,
                        now.AddSeconds(-1),
                        now.AddSeconds(-1),
                        ParentSessionId: _rootSessionId,
                        RootSessionId: _rootSessionId),
                ],
                []));
        }

        public Task<IReadOnlyList<AgentRunCheckpointRecord>> ListLatestCheckpointsAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AgentRunCheckpointRecord>>([]);

        public async Task<SubsessionTranscriptPage> ListRecentTurnsAsync(
            Guid sessionId,
            int limit,
            CancellationToken cancellationToken = default)
        {
            if (BlockSecondRecentLoad && sessionId == SecondSessionId)
            {
                SecondRecentLoadStarted.TrySetResult();
                await ReleaseSecondRecentLoad.Task.WaitAsync(cancellationToken);
            }
            return new SubsessionTranscriptPage(sessionId switch
            {
                var id when id == FirstSessionId =>
                    [CreateTextTurn(FirstSessionId, FirstTurnId, FirstItemId, "First")],
                var id when id == SecondSessionId =>
                    [CreateTextTurn(SecondSessionId, SecondTurnId, SecondItemId, "Second")],
                _ => [],
            }, false);
        }

        public Task<SubsessionTranscriptPage> ListTurnsBeforeAsync(
            Guid sessionId,
            DateTimeOffset beforeCreatedAtUtc,
            Guid beforeTurnId,
            int limit,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new SubsessionTranscriptPage([], false));

        public Task<SubsessionTranscriptPage> ListTurnsAfterAsync(
            Guid sessionId,
            DateTimeOffset afterCreatedAtUtc,
            Guid afterTurnId,
            int limit,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new SubsessionTranscriptPage([], false));

        public async Task<SubsessionAroundTurnPage> LoadAroundTurnAsync(
            Guid sessionId,
            Guid turnId,
            DateTimeOffset turnCreatedAtUtc,
            Guid itemId,
            int beforeLimit,
            int afterLimit,
            CancellationToken cancellationToken = default)
        {
            if (sessionId == FirstSessionId)
            {
                FirstAnchorLoadStarted.TrySetResult();
                await ReleaseFirstAnchorLoad.Task;
            }
            var expectedItemId = sessionId == FirstSessionId ? FirstItemId : SecondItemId;
            return new SubsessionAroundTurnPage(
                [CreateTextTurn(
                    sessionId,
                    turnId,
                    expectedItemId,
                    sessionId == FirstSessionId ? "First" : "Second")],
                false,
                false,
                turnId);
        }

        private static AgentTurnRecord CreateTextTurn(
            Guid sessionId,
            Guid turnId,
            Guid itemId,
            string content)
            => new(
                turnId,
                sessionId,
                AgentMessageRole.Assistant,
                AgentTurnKind.Message,
                [new AgentTurnItemRecord(
                    itemId,
                    turnId,
                    0,
                    AgentTurnItemKind.Text,
                    content,
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
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch);
    }

    private sealed class RefreshRaceSubsessionGateway :
        ISubsessionSessionReader,
        ISubsessionCheckpointReader,
        ISubsessionTranscriptPageReader,
        ISubsessionChangeNotifications
    {
        private readonly Guid _rootSessionId = Guid.NewGuid();
        private int _sessionReadCount;
        private int _checkpointReadCount;

        internal Guid FirstSessionId { get; } = Guid.NewGuid();
        internal Guid SecondSessionId { get; } = Guid.NewGuid();
        internal Guid FirstTurnId { get; } = Guid.NewGuid();
        internal Guid SecondTurnId { get; } = Guid.NewGuid();
        internal TaskCompletionSource StaleSessionReadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource StaleCheckpointReadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseStaleRefresh { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public event Action<Guid>? SessionChanged
        {
            add { }
            remove { }
        }

        public event Action<Guid, AgentTurnRecord>? TurnChanged
        {
            add { }
            remove { }
        }

        public event Action? ResnapshotRequired
        {
            add { }
            remove { }
        }

        public async Task<SubsessionSessionCatalog> ListSessionsAsync(
            CancellationToken cancellationToken = default)
        {
            var read = Interlocked.Increment(ref _sessionReadCount);
            if (read == 2)
            {
                StaleSessionReadStarted.TrySetResult();
                await ReleaseStaleRefresh.Task.WaitAsync(cancellationToken);
            }

            var suffix = read switch
            {
                1 => "initial",
                2 => "stale",
                _ => "fresh",
            };
            var now = DateTimeOffset.UnixEpoch.AddMinutes(10);
            return new SubsessionSessionCatalog(
                [
                    new AgentSessionRecord(
                        _rootSessionId,
                        "Root",
                        AgentSessionState.Active,
                        now,
                        now,
                        RootSessionId: _rootSessionId),
                    new AgentSessionRecord(
                        FirstSessionId,
                        $"First {suffix}",
                        AgentSessionState.Active,
                        now,
                        now,
                        ParentSessionId: _rootSessionId,
                        RootSessionId: _rootSessionId),
                    new AgentSessionRecord(
                        SecondSessionId,
                        $"Second {suffix}",
                        AgentSessionState.Active,
                        now.AddMinutes(-1),
                        now.AddMinutes(-1),
                        ParentSessionId: _rootSessionId,
                        RootSessionId: _rootSessionId),
                ],
                []);
        }

        public async Task<IReadOnlyList<AgentRunCheckpointRecord>> ListLatestCheckpointsAsync(
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _checkpointReadCount) == 2)
            {
                StaleCheckpointReadStarted.TrySetResult();
                await ReleaseStaleRefresh.Task.WaitAsync(cancellationToken);
            }
            return [];
        }

        public Task<SubsessionTranscriptPage> ListRecentTurnsAsync(
            Guid sessionId,
            int limit,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new SubsessionTranscriptPage(sessionId switch
            {
                var id when id == FirstSessionId => [CreateTextTurn(FirstSessionId, FirstTurnId)],
                var id when id == SecondSessionId => [CreateTextTurn(SecondSessionId, SecondTurnId)],
                _ => [],
            }, false));

        public Task<SubsessionTranscriptPage> ListTurnsBeforeAsync(
            Guid sessionId,
            DateTimeOffset beforeCreatedAtUtc,
            Guid beforeTurnId,
            int limit,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new SubsessionTranscriptPage([], false));

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
            => Task.FromResult(new SubsessionAroundTurnPage(
                [CreateTextTurn(sessionId, turnId)],
                false,
                false,
                turnId));

        private static AgentTurnRecord CreateTextTurn(Guid sessionId, Guid turnId)
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
                    sessionId.ToString("D"),
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
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch);
    }

    private sealed class FailOnceSubsessionRuntimeClient : IPackageRuntimeClient
    {
        private int _runtimeCatalogQueryCount;

        public bool IsAvailable => true;

        public int RuntimeCatalogQueryCount => Volatile.Read(ref _runtimeCatalogQueryCount);

        public ValueTask<TResponse> InvokeAsync<TRequest, TResponse>(
            PackageRuntimeOperation<TRequest, TResponse> operation,
            TRequest request,
            CancellationToken cancellationToken = default)
            where TRequest : class
            where TResponse : class
        {
            cancellationToken.ThrowIfCancellationRequested();
            var query = Assert.IsType<SubagentQuery>(request);
            Assert.Equal(SubagentQueryKind.RuntimeCatalog, query.Kind);
            if (Interlocked.Increment(ref _runtimeCatalogQueryCount) == 1)
            {
                return ValueTask.FromException<TResponse>(
                    new InvalidOperationException("Injected Runtime catalog failure."));
            }

            return ValueTask.FromResult((TResponse)(object)new SubagentProjection(
                Sessions: [],
                Profiles: [],
                Checkpoints: []));
        }

        public async IAsyncEnumerable<TEvent> SubscribeAsync<TRequest, TEvent>(
            PackageRuntimeStream<TRequest, TEvent> stream,
            TRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken = default)
            where TRequest : class
            where TEvent : class
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }
    }

    private sealed class BlockingSubsessionRuntimeClient : IPackageRuntimeClient
    {
        private int _runtimeCatalogQueryCount;

        public bool IsAvailable => true;

        public int RuntimeCatalogQueryCount => Volatile.Read(ref _runtimeCatalogQueryCount);
        public TaskCompletionSource RuntimeCatalogStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseRuntimeCatalog { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource InvocationCompleted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool FailRuntimeCatalog { get; set; }

        public async ValueTask<TResponse> InvokeAsync<TRequest, TResponse>(
            PackageRuntimeOperation<TRequest, TResponse> operation,
            TRequest request,
            CancellationToken cancellationToken = default)
            where TRequest : class
            where TResponse : class
        {
            Interlocked.Increment(ref _runtimeCatalogQueryCount);
            RuntimeCatalogStarted.TrySetResult();
            try
            {
                await ReleaseRuntimeCatalog.Task.WaitAsync(cancellationToken);
                if (FailRuntimeCatalog)
                {
                    throw new InvalidOperationException("Injected Runtime catalog failure.");
                }
                return (TResponse)(object)new SubagentProjection(
                    Sessions: [],
                    Profiles: [],
                    Checkpoints: []);
            }
            finally
            {
                InvocationCompleted.TrySetResult();
            }
        }

        public async IAsyncEnumerable<TEvent> SubscribeAsync<TRequest, TEvent>(
            PackageRuntimeStream<TRequest, TEvent> stream,
            TRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken = default)
            where TRequest : class
            where TEvent : class
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }
    }
}
