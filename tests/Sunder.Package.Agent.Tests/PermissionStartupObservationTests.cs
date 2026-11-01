extern alias AgentCore;

using System.Runtime.CompilerServices;
using AgentCore::Sunder.Package.Agent.Contracts.Models;
using AgentCore::Sunder.Package.Agent.PackageViews;
using AgentCore::Sunder.Package.Agent.Runtime;
using Xunit;
using AgentPresentationDispatcher = AgentCore::Sunder.Package.Agent.Shared.Presentation.IPresentationDispatcher;
using static Sunder.Package.Agent.Tests.RuntimeSnapshotFixture;

namespace Sunder.Package.Agent.Tests;

[Collection("Runtime correctness")]
public sealed class PermissionStartupObservationTests
{
    [Fact]
    public async Task PermissionsViewModel_StartsRuntimeObservation()
    {
        var client = new PermissionRuntimeClient();
        using var dispatcher = new PermissionPresentationDispatcher();
        await using var gateway = new AgentAppRuntimeGateway(client);
        AgentPermissionsViewModel? viewModel = null;
        var populated = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            // With no Avalonia application, the default dispatcher executes inline.
            // Initialization and Connected can reload concurrently; model the actual
            // serialized UI boundary for both publication and collection assertions.
            await dispatcher.InvokeAsync(() =>
            {
                viewModel = new AgentPermissionsViewModel(gateway, dispatcher);
                viewModel.Rows.CollectionChanged += (_, _) => ObserveRows();
                ObserveRows();
            });
            await WaitUntilAsync(() => client.SubscriptionCount > 0);
            Assert.Equal(dispatcher.ThreadId, await populated.Task.WaitAsync(TimeSpan.FromSeconds(10)));
            await dispatcher.InvokeAsync(() =>
            {
                Assert.Single(viewModel!.Rows);
                Assert.Equal("test.mutate", viewModel.Rows[0].ActionId);
            });
        }
        finally
        {
            if (viewModel is not null) await dispatcher.InvokeAsync(viewModel.Dispose);
        }

        void ObserveRows()
        {
            if (viewModel!.Rows.Count > 0) populated.TrySetResult(Environment.CurrentManagedThreadId);
        }
    }

    private sealed class PermissionPresentationDispatcher : AgentPresentationDispatcher, IDisposable
    {
        private readonly DedicatedThreadDispatcher _dispatcher = new("Permission observation test dispatcher");
        public int ThreadId => _dispatcher.ThreadId;
        public bool CheckAccess() => _dispatcher.CheckAccess();
        public Task InvokeAsync(Action action) => _dispatcher.InvokeAsync(action);
        public void Dispose() => _dispatcher.Dispose();
    }

    private sealed class PermissionRuntimeClient : OwnerServiceStub
    {
        private int _subscriptionCount;

        public bool IsAvailable => true;

        public int SubscriptionCount => Volatile.Read(ref _subscriptionCount);

        public override ValueTask<TResponse> RespondAsync<TRequest, TResponse>(
            TRequest request,
            CancellationToken cancellationToken = default)
            where TRequest : class
            where TResponse : class
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request is AgentDashboardRequest)
            {
                return ValueTask.FromResult((TResponse)(object)new AgentDashboardProjection(0, [], [], []));
            }
            if (request is AgentPermissionCommand)
            {
                return ValueTask.FromResult((TResponse)(object)new AgentPermissionProjection(
                    0,
                    SessionState: null,
                    [new AgentPermissionActionDescriptor(
                        "test.mutate",
                        "Mutate test state",
                        "Mutates deterministic test state.",
                        [new AgentPermissionBoundaryDescriptor(
                            "test-boundary",
                            "Test boundary",
                            "Requires approval.",
                            AgentPermissionDecision.Ask)])],
                    Overrides: [],
                    PendingRequests: []));
            }

            return ValueTask.FromException<TResponse>(new NotSupportedException(typeof(TRequest).Name));
        }

        public override async IAsyncEnumerable<TEvent> ObserveAsync<TRequest, TEvent>(
            TRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
            where TRequest : class
            where TEvent : class
        {
            Interlocked.Increment(ref _subscriptionCount);
            yield return (TEvent)(object)new AgentRuntimeChange(0, AgentRuntimeChangeKind.Connected);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }
}
