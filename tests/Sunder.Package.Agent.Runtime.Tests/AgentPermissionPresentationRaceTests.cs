extern alias AgentCore;

using System.Runtime.CompilerServices;
using AgentCore::Sunder.Package.Agent.Contracts.Models;
using AgentCore::Sunder.Package.Agent.Models;
using AgentCore::Sunder.Package.Agent.PackageViews;
using AgentCore::Sunder.Package.Agent.Runtime;
using Sunder.Sdk.Abstractions;
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
    public async Task RuntimeInvalidationAllowsLowerPermissionRevision()
    {
        var gateway = new ReplacingRuntimePermissionGateway();
        using var viewModel = new AgentPermissionsViewModel(gateway, InlineDispatcher.Instance);
        await viewModel.PrepareNavigationAsync(NavigationContext());
        Assert.Equal(AgentPermissionDecision.Deny, Assert.Single(viewModel.Rows).SelectedDecision);
        var replacementApplied = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        viewModel.Rows.CollectionChanged += (_, _) =>
        {
            if (viewModel.Rows.SingleOrDefault()?.SelectedDecision == AgentPermissionDecision.Allow)
            {
                replacementApplied.TrySetResult();
            }
        };

        gateway.ReplaceRuntime();
        await replacementApplied.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(AgentPermissionDecision.Allow, Assert.Single(viewModel.Rows).SelectedDecision);
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
        var client = new RacingPermissionOwnerClient();
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

    [Fact]
    public async Task PartialSaveFailureReloadsAuthoritativePermissionState()
    {
        var gateway = new PartialMutationPermissionGateway();
        using var viewModel = new AgentPermissionsViewModel(gateway, InlineDispatcher.Instance);
        await viewModel.PrepareNavigationAsync(NavigationContext());
        Assert.Equal(2, viewModel.Rows.Count);
        foreach (var row in viewModel.Rows)
        {
            row.SelectedDecision = AgentPermissionDecision.Allow;
        }

        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.False(viewModel.IsBusy);
        Assert.Equal(AgentPermissionDecision.Allow, viewModel.Rows[0].SelectedDecision);
        Assert.Equal(AgentPermissionDecision.Ask, viewModel.Rows[1].SelectedDecision);
        Assert.True(gateway.LoadCount >= 2);
    }

    private const string ActionId = "test.mutate";
    private const string BoundaryId = "test-boundary";

    private static PackageViewNavigationContext NavigationContext()
        => new(
            "settings:younics.ai.agent.permissions",
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
        public event Action? GlobalPermissionsInvalidated;

        protected void RaiseGlobalPermissionsInvalidated()
            => GlobalPermissionsInvalidated?.Invoke();

        public AgentSessionPermissionState GetSessionState(Guid sessionId) => new(sessionId, false);
        public void SetSessionUnrestrictedMode(Guid sessionId, bool isEnabled) { }
        public Task<IReadOnlyList<AgentPermissionActionDescriptor>> ListActionsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AgentPermissionActionDescriptor>>([]);
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

    private sealed class ReplacingRuntimePermissionGateway : GlobalPermissionGateway
    {
        private AgentPermissionProjection _projection = Projection(
            100,
            AgentPermissionDecision.Deny);

        public void ReplaceRuntime()
        {
            _projection = Projection(1, AgentPermissionDecision.Allow);
            RaiseGlobalPermissionsInvalidated();
        }

        public override Task<AgentPermissionProjection> LoadGlobalPermissionsAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_projection);
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

    private sealed class PartialMutationPermissionGateway : GlobalPermissionGateway
    {
        private readonly Dictionary<string, AgentPermissionDecision> _decisions =
            new(StringComparer.Ordinal);
        private int _saveCount;
        private long _revision = 1;

        public int LoadCount { get; private set; }

        public override Task<AgentPermissionProjection> LoadGlobalPermissionsAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LoadCount++;
            var boundaries = new[] { "first", "second" };
            return Task.FromResult(new AgentPermissionProjection(
                _revision,
                SessionState: null,
                [new AgentPermissionActionDescriptor(
                    ActionId,
                    "Mutate test state",
                    "Mutates deterministic test state.",
                    boundaries.Select(boundary => new AgentPermissionBoundaryDescriptor(
                        boundary,
                        boundary,
                        "Requires approval.",
                        AgentPermissionDecision.Ask)).ToArray())],
                boundaries
                    .Where(_decisions.ContainsKey)
                    .Select(boundary => new AgentPermissionOverride(
                        ActionId,
                        boundary,
                        _decisions[boundary],
                        DateTimeOffset.UtcNow))
                    .ToArray(),
                PendingRequests: []));
        }

        public override Task SaveOverrideAsync(
            string actionId,
            string boundaryId,
            AgentPermissionDecision decision,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Increment(ref _saveCount) == 2)
            {
                throw new IOException("Injected partial permission save failure.");
            }

            _decisions[boundaryId] = decision;
            _revision++;
            return Task.CompletedTask;
        }

        public override Task DeleteOverrideAsync(
            string actionId,
            string boundaryId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _decisions.Remove(boundaryId);
            _revision++;
            return Task.CompletedTask;
        }
    }

    private sealed class RacingPermissionOwnerClient : OwnerServiceStub
    {
        public TaskCompletionSource ReadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseRead { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<TResponse> RespondAsync<TRequest, TResponse>(
            TRequest request,
            CancellationToken cancellationToken = default)
            where TRequest : class
            where TResponse : class
        {
            Assert.IsType<AgentPermissionCommand>(request);
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

        public override async IAsyncEnumerable<TEvent> ObserveAsync<TRequest, TEvent>(
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
