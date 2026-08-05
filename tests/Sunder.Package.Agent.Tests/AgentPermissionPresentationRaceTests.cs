extern alias AgentCore;

using System.Runtime.CompilerServices;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Models;
using Sunder.Package.Agent.PackageViews;
using Sunder.Package.Agent.Runtime;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Runtime;
using Xunit;
using AgentPresentationDispatcher = AgentCore::Sunder.Package.Agent.Shared.Presentation.IPresentationDispatcher;

namespace Sunder.Package.Agent.Tests;

public sealed class AgentPermissionPresentationRaceTests
{
    [Fact]
    public async Task SaveAndResetShareOneMutationGate()
    {
        var gateway = new BlockingMutationPermissionGateway();
        using var viewModel = new AgentPermissionsViewModel(gateway, InlineDispatcher.Instance);
        await viewModel.PrepareNavigationAsync(NavigationContext());
        viewModel.Rows[0].SelectedDecision = AgentPermissionDecision.Allow;

        var save = viewModel.SaveCommand.ExecuteAsync(null);
        await gateway.SaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(viewModel.IsBusy);
        Assert.False(viewModel.SaveCommand.CanExecute(null));
        Assert.False(viewModel.ResetToPackageDefaultsCommand.CanExecute(null));
        await viewModel.ResetToPackageDefaultsCommand.ExecuteAsync(null);
        Assert.Equal(0, gateway.DeleteCount);

        gateway.ReleaseSave.TrySetResult();
        await save.WaitAsync(TimeSpan.FromSeconds(2));
        await viewModel.ResetToPackageDefaultsCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, gateway.DeleteCount);
        Assert.Equal(1, gateway.MaximumConcurrentMutations);
        Assert.False(viewModel.IsBusy);
        Assert.Equal("Permission defaults restored.", viewModel.StatusText);
    }

    [Fact]
    public async Task OlderReloadCannotOverwriteNewerPermissionRevision()
    {
        var gateway = new RacingReloadPermissionGateway();
        using var viewModel = new AgentPermissionsViewModel(gateway, InlineDispatcher.Instance);
        await gateway.FirstLoadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var newerApplied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        viewModel.Rows.CollectionChanged += (_, _) =>
        {
            if (viewModel.Rows.SingleOrDefault()?.SelectedDecision == AgentPermissionDecision.Allow)
            {
                newerApplied.TrySetResult();
            }
        };

        gateway.RaiseConnected();
        await newerApplied.Task.WaitAsync(TimeSpan.FromSeconds(2));

        gateway.ReleaseFirstLoad.TrySetResult();
        await viewModel.PrepareNavigationAsync(NavigationContext()).AsTask().WaitAsync(TimeSpan.FromSeconds(2));

        var row = Assert.Single(viewModel.Rows);
        Assert.Equal(AgentPermissionDecision.Allow, row.SelectedDecision);
    }

    [Fact]
    public async Task DisposalSuppressesLatePermissionReloadPublication()
    {
        var gateway = new RacingReloadPermissionGateway();
        var viewModel = new AgentPermissionsViewModel(gateway, InlineDispatcher.Instance);
        await gateway.FirstLoadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        viewModel.Dispose();
        gateway.ReleaseFirstLoad.TrySetResult();
        await viewModel.PrepareNavigationAsync(NavigationContext()).AsTask().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Empty(viewModel.Rows);
        Assert.Empty(viewModel.StatusText);
        Assert.False(viewModel.SaveCommand.CanExecute(null));
        Assert.False(viewModel.ResetToPackageDefaultsCommand.CanExecute(null));
    }

    [Fact]
    public async Task GatewayRejectsStalePermissionCacheCompletionAfterMutation()
    {
        var client = new RacingPermissionRuntimeClient();
        using var gateway = new AgentAppRuntimeGateway(client);

        var staleRead = gateway.LoadGlobalPermissionsAsync();
        await client.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await gateway.SaveOverrideAsync(
            ActionId,
            BoundaryId,
            AgentPermissionDecision.Allow);

        client.ReleaseRead.TrySetResult();
        await staleRead.WaitAsync(TimeSpan.FromSeconds(2));

        var permissionOverride = Assert.Single(gateway.ListOverrides());
        Assert.Equal(AgentPermissionDecision.Allow, permissionOverride.Decision);
    }

    private const string ActionId = "test.mutate";
    private const string BoundaryId = "test-boundary";

    private static PackageViewNavigationContext NavigationContext()
        => new(
            "settings:sunder.package.agent.permissions",
            new Dictionary<string, string?>());

    private static AgentPermissionProjection Projection(
        long revision,
        AgentPermissionDecision? decision)
        => new(
            revision,
            SessionState: null,
            [new AgentPermissionActionDescriptor(
                ActionId,
                "Mutate test state",
                "Mutates deterministic test state.",
                [new AgentPermissionBoundaryDescriptor(
                    BoundaryId,
                    "Test boundary",
                    "Requires approval.",
                    AgentPermissionDecision.Ask)])],
            decision is { } configured
                ? [new AgentPermissionOverride(ActionId, BoundaryId, configured, DateTimeOffset.UtcNow)]
                : [],
            PendingRequests: []);

    private abstract class GlobalPermissionGateway :
        IAgentPermissionGateway,
        IAgentGlobalPermissionGateway
    {
        public AgentSessionPermissionState GetSessionState(Guid sessionId) => new(sessionId, false);
        public void SetSessionUnrestrictedMode(Guid sessionId, bool isEnabled) { }
        public IReadOnlyList<AgentPermissionActionDescriptor> ListActions() => [];
        public IReadOnlyList<AgentPermissionOverride> ListOverrides() => [];
        public void SaveOverride(string actionId, string boundaryId, AgentPermissionDecision decision) { }
        public void DeleteOverride(string actionId, string boundaryId) { }
        public IReadOnlyList<AgentPendingPermissionRequestRecord> ListPendingRequestsForSessionTree(Guid sessionId)
            => [];

        public abstract Task<AgentPermissionProjection> LoadGlobalPermissionsAsync(
            CancellationToken cancellationToken = default);

        public abstract Task SaveOverrideAsync(
            string actionId,
            string boundaryId,
            AgentPermissionDecision decision,
            CancellationToken cancellationToken = default);

        public abstract Task DeleteOverrideAsync(
            string actionId,
            string boundaryId,
            CancellationToken cancellationToken = default);
    }

    private sealed class BlockingMutationPermissionGateway : GlobalPermissionGateway
    {
        private readonly object _syncRoot = new();
        private AgentPermissionDecision? _decision;
        private long _revision = 1;
        private int _activeMutations;
        private int _maximumConcurrentMutations;
        private int _deleteCount;

        public TaskCompletionSource SaveStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseSave { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int DeleteCount => Volatile.Read(ref _deleteCount);
        public int MaximumConcurrentMutations => Volatile.Read(ref _maximumConcurrentMutations);

        public override Task<AgentPermissionProjection> LoadGlobalPermissionsAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_syncRoot)
            {
                return Task.FromResult(Projection(_revision, _decision));
            }
        }

        public override async Task SaveOverrideAsync(
            string actionId,
            string boundaryId,
            AgentPermissionDecision decision,
            CancellationToken cancellationToken = default)
        {
            BeginMutation();
            try
            {
                SaveStarted.TrySetResult();
                await ReleaseSave.Task.WaitAsync(cancellationToken);
                lock (_syncRoot)
                {
                    _decision = decision;
                    _revision++;
                }
            }
            finally
            {
                Interlocked.Decrement(ref _activeMutations);
            }
        }

        public override Task DeleteOverrideAsync(
            string actionId,
            string boundaryId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BeginMutation();
            try
            {
                Interlocked.Increment(ref _deleteCount);
                lock (_syncRoot)
                {
                    _decision = null;
                    _revision++;
                }
                return Task.CompletedTask;
            }
            finally
            {
                Interlocked.Decrement(ref _activeMutations);
            }
        }

        private void BeginMutation()
        {
            var active = Interlocked.Increment(ref _activeMutations);
            int maximum;
            do
            {
                maximum = Volatile.Read(ref _maximumConcurrentMutations);
                if (maximum >= active)
                {
                    return;
                }
            }
            while (Interlocked.CompareExchange(
                       ref _maximumConcurrentMutations,
                       active,
                       maximum) != maximum);
        }
    }

    private sealed class RacingReloadPermissionGateway :
        GlobalPermissionGateway,
        IAgentRuntimeAvailability
    {
        private int _loadCount;

        public TaskCompletionSource FirstLoadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseFirstLoad { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public AgentRuntimeConnectionState ConnectionState => AgentRuntimeConnectionState.Connected;
        public bool IsRuntimeAvailable => true;
        public event Action<AgentRuntimeConnectionState>? ConnectionStateChanged;

        public override async Task<AgentPermissionProjection> LoadGlobalPermissionsAsync(
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _loadCount) == 1)
            {
                FirstLoadStarted.TrySetResult();
                await ReleaseFirstLoad.Task;
                return Projection(1, AgentPermissionDecision.Deny);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return Projection(2, AgentPermissionDecision.Allow);
        }

        public override Task SaveOverrideAsync(
            string actionId,
            string boundaryId,
            AgentPermissionDecision decision,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public override Task DeleteOverrideAsync(
            string actionId,
            string boundaryId,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public void RaiseConnected()
            => ConnectionStateChanged?.Invoke(AgentRuntimeConnectionState.Connected);
    }

    private sealed class RacingPermissionRuntimeClient : IPackageRuntimeClient
    {
        public bool IsAvailable => true;

        public TaskCompletionSource ReadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseRead { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<TResponse> InvokeAsync<TRequest, TResponse>(
            PackageRuntimeOperation<TRequest, TResponse> operation,
            TRequest request,
            CancellationToken cancellationToken = default)
            where TRequest : class
            where TResponse : class
        {
            Assert.Same(AgentRuntimeOperations.Permissions, operation);
            var command = Assert.IsType<AgentPermissionCommand>(request);
            if (command.Kind == AgentPermissionCommandKind.Read)
            {
                ReadStarted.TrySetResult();
                await ReleaseRead.Task.WaitAsync(cancellationToken);
                return (TResponse)(object)Projection(1, AgentPermissionDecision.Deny);
            }

            Assert.Equal(AgentPermissionCommandKind.SaveOverride, command.Kind);
            return (TResponse)(object)Projection(2, AgentPermissionDecision.Allow);
        }

        public async IAsyncEnumerable<TEvent> SubscribeAsync<TRequest, TEvent>(
            PackageRuntimeStream<TRequest, TEvent> stream,
            TRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
            where TRequest : class
            where TEvent : class
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }
    }

    private sealed class InlineDispatcher : AgentPresentationDispatcher
    {
        public static InlineDispatcher Instance { get; } = new();

        public bool CheckAccess() => true;

        public Task InvokeAsync(Action action)
        {
            action();
            return Task.CompletedTask;
        }
    }
}
