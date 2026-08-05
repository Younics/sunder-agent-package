using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.PackageViews;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.Runtime;
using Sunder.Package.Agent.Services;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class WorkspaceExecutionTargetAuthorityRaceTests
{
    [Fact]
    public async Task TargetRefreshes_PreserveNewerSelectionAndRejectOutOfOrderCompletion()
    {
        var workspaceGateway = new RuntimeWorkspaceGateway();
        var executionGateway = new DeferredExecutionGateway(
            Target("alpha"),
            Target("beta"));
        using var catalog = new RegressionTestExtensionCatalog();
        using var viewModel = new AgentWorkspacesViewModel(
            workspaceGateway,
            executionGateway,
            catalog);
        await viewModel.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(2));
        viewModel.SelectedExecutionTarget = viewModel.ExecutionTargets.Single(
            target => target.TargetId == "alpha");

        workspaceGateway.RaiseConnected();
        await executionGateway.FirstRefresh.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var staleRefresh = viewModel.CurrentExecutionTargetRefresh;
        viewModel.SelectedExecutionTarget = viewModel.ExecutionTargets.Single(
            target => target.TargetId == "beta");

        workspaceGateway.RaiseConnected();
        await executionGateway.SecondRefresh.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var currentRefresh = viewModel.CurrentExecutionTargetRefresh;
        executionGateway.SecondRefresh.Complete(
            [Target("alpha"), Target("beta"), Target("current-only")]);
        await currentRefresh.WaitAsync(TimeSpan.FromSeconds(2));

        executionGateway.FirstRefresh.Complete(
            [Target("alpha"), Target("beta"), Target("stale-only")]);
        await staleRefresh.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal("beta", viewModel.SelectedExecutionTarget?.TargetId);
        Assert.Contains(viewModel.ExecutionTargets, target => target.TargetId == "current-only");
        Assert.DoesNotContain(viewModel.ExecutionTargets, target => target.TargetId == "stale-only");
    }

    [Fact]
    public async Task CatalogChangeDuringInitialization_ReplaysTargetRefresh()
    {
        var workspaceGateway = new BlockingInitializationWorkspaceGateway();
        var executionGateway = new CatalogReplayExecutionGateway();
        using var catalog = new RegressionTestExtensionCatalog();
        using var viewModel = new AgentWorkspacesViewModel(
            workspaceGateway,
            executionGateway,
            catalog);

        var initialization = viewModel.InitializeAsync();
        await workspaceGateway.InitializationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var catalogChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        catalog.Changed += OnCatalogChanged;
        try
        {
            catalog.AddProvider(
                AgentRpcServices.WorkspaceEditors,
                new NoOpWorkspaceEditorContributor());
            await catalogChanged.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            catalog.Changed -= OnCatalogChanged;
        }

        workspaceGateway.ReleaseInitialization.TrySetResult();
        await initialization.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(2, executionGateway.LoadCount);
        Assert.Contains(viewModel.ExecutionTargets, target => target.TargetId == "catalog-replay");

        void OnCatalogChanged(object? sender, AgentRpcCatalogChangedEventArgs change)
        {
            if (change.IncludesContract(AgentRpcContractIds.WorkspaceEditor))
            {
                catalogChanged.TrySetResult();
            }
        }
    }

    private static AgentExecutionTargetDescriptor Target(string id)
        => new("test", id, id, null, SupportsShell: false, SupportsFiles: false);

    private sealed class DeferredExecutionGateway(
        params AgentExecutionTargetDescriptor[] initialTargets) :
        IAgentExecutionGateway,
        IAgentExecutionTargetLoader
    {
        private int _loadCount;

        public DeferredResult<IReadOnlyList<AgentExecutionTargetDescriptor>> FirstRefresh { get; } = new();

        public DeferredResult<IReadOnlyList<AgentExecutionTargetDescriptor>> SecondRefresh { get; } = new();

        public IReadOnlyList<AgentExecutionTargetDescriptor> ListTargets() => initialTargets;

        public Task<AgentExecutionTargetWarmupResult> WarmWorkspaceAsync(
            AgentWorkspaceRecord workspace,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<AgentExecutionTargetDescriptor>> ListTargetsAsync(
            CancellationToken cancellationToken = default)
            => Interlocked.Increment(ref _loadCount) switch
            {
                1 => Task.FromResult<IReadOnlyList<AgentExecutionTargetDescriptor>>(initialTargets),
                2 => FirstRefresh.WaitAsync(),
                3 => SecondRefresh.WaitAsync(),
                _ => throw new InvalidOperationException("Unexpected execution-target load."),
            };
    }

    private sealed class CatalogReplayExecutionGateway :
        IAgentExecutionGateway,
        IAgentExecutionTargetLoader
    {
        private int _loadCount;

        public int LoadCount => Volatile.Read(ref _loadCount);

        public IReadOnlyList<AgentExecutionTargetDescriptor> ListTargets() => [];

        public Task<AgentExecutionTargetWarmupResult> WarmWorkspaceAsync(
            AgentWorkspaceRecord workspace,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<AgentExecutionTargetDescriptor>> ListTargetsAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AgentExecutionTargetDescriptor>>(
                Interlocked.Increment(ref _loadCount) == 1
                    ? []
                    : [Target("catalog-replay")]);
    }

    private sealed class RuntimeWorkspaceGateway : EmptyWorkspaceGateway, IAgentRuntimeAvailability
    {
        public event Action<AgentRuntimeConnectionState>? ConnectionStateChanged;

        public AgentRuntimeConnectionState ConnectionState { get; private set; }

        public bool IsRuntimeAvailable => ConnectionState == AgentRuntimeConnectionState.Connected;

        public void RaiseConnected()
        {
            ConnectionState = AgentRuntimeConnectionState.Connected;
            ConnectionStateChanged?.Invoke(ConnectionState);
        }
    }

    private sealed class BlockingInitializationWorkspaceGateway : EmptyWorkspaceGateway
    {
        public TaskCompletionSource InitializationStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseInitialization { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            InitializationStarted.TrySetResult();
            await ReleaseInitialization.Task.WaitAsync(cancellationToken);
        }
    }

    private abstract class EmptyWorkspaceGateway : IAgentWorkspaceGateway
    {
        public event Action? WorkspacesChanged
        {
            add { }
            remove { }
        }

        public IReadOnlyList<AgentWorkspaceRecord> ListWorkspaces() => [];

        public AgentWorkspaceRecord? GetWorkspace(string workspaceId) => null;

        public AgentWorkspaceRecord CreateWorkspace(string displayName) => throw new NotSupportedException();

        public void SaveWorkspace(string workspaceId, string displayName, string? description)
            => throw new NotSupportedException();

        public void SaveWorkspaceAggregate(
            string workspaceId,
            string displayName,
            string? description,
            IReadOnlyList<AgentWorkspacePathRecord> paths,
            IReadOnlyList<AgentWorkspaceDocumentRecord> documents,
            string? executionTargetId)
            => throw new NotSupportedException();

        public void DeleteWorkspace(string workspaceId) => throw new NotSupportedException();

        public IReadOnlyList<AgentWorkspaceBindingRecord> ListBindings(string workspaceId) => [];

        public AgentWorkspaceBindingRecord SavePrimaryExecutionBinding(
            string workspaceId,
            string contributionId,
            string displayRole = AgentWorkspaceBindingRoles.PrimaryExecutionTarget)
            => throw new NotSupportedException();

        public virtual Task InitializeAsync(CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class NoOpWorkspaceEditorContributor : IAgentWorkspaceEditorContributor
    {
        public string ContributorId => "catalog-replay-editor";

        public bool CanEdit(AgentWorkspaceEditorContext context) => false;

        public ValueTask<IReadOnlyList<AgentEditorSection>> GetSectionsAsync(
            AgentWorkspaceEditorContext context,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<AgentEditorSection>>([]);

        public ValueTask<AgentEditorSaveResult> SaveSectionAsync(
            AgentWorkspaceEditorContext context,
            AgentEditorSaveRequest request,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(AgentEditorSaveResult.Ok("Saved."));
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
