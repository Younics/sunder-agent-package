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
        await Task.WhenAll(warmup, navigation);
        await viewModel.OnNavigatedToAsync(context);

        Assert.Equal(1, gateway.SessionListCount);
        Assert.Equal(1, gateway.CheckpointListCount);
    }

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

        public IReadOnlyList<SubagentProviderCatalogOption> ListChatProviders() => [];

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

        public Task<IReadOnlyList<AgentTurnRecord>> ListRecentTurnsAsync(
            Guid sessionId,
            int limit,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AgentTurnRecord>>([]);

        public Task<IReadOnlyList<AgentTurnRecord>> ListTurnsBeforeAsync(
            Guid sessionId,
            DateTimeOffset beforeCreatedAtUtc,
            Guid beforeTurnId,
            int limit,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AgentTurnRecord>>([]);

        public Task<IReadOnlyList<AgentTurnRecord>> ListTurnsAfterAsync(
            Guid sessionId,
            DateTimeOffset afterCreatedAtUtc,
            Guid afterTurnId,
            int limit,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AgentTurnRecord>>([]);
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
