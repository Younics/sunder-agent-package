extern alias McpWorker;
extern alias MemoryWorker;
extern alias SubagentsWorker;

using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Services;
using McpWorker::Sunder.Package.Agent.Mcp.Runtime;
using MemoryWorker::Sunder.Package.Agent.Memory.Semantic.Runtime;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Rpc;
using RuntimeCatalogServices = SubagentsWorker::Sunder.Generated.Packages.Sunder_Package_Agent_Services;
using RuntimeCatalogModels = SubagentsWorker::Sunder.Generated.Packages.Sunder_Package_Agent_Models;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class AgentEventDeliveryReliabilityTests
{
    [Fact]
    public async Task RuntimeCatalogStream_SaturationCoalescesDroppedRangeIntoReset()
    {
        using var scope = RegressionTestPackageScope.Create();
        await using var services = RegressionTestRuntime.Create(scope.Context);
        var sessions = services.GetRequiredService<AgentSessionService>();
        var workspaces = services.GetRequiredService<AgentWorkspaceService>();
        var catalog = new AgentRuntimeCatalog(
            sessions,
            services.GetRequiredService<AgentProfileService>(),
            workspaces);
        using var cancellation = new CancellationTokenSource();
        await using var events = catalog.CreateSunderRpcProviderRegistration().Handler
            .InvokeServerStreamAsync(
                null!,
                "runtime-catalog",
                "watch",
                JsonSerializer.SerializeToElement(new { }),
                cancellation.Token)
            .GetAsyncEnumerator(cancellation.Token);

        Assert.True(await events.MoveNextAsync());
        Assert.Equal("reset", events.Current.GetProperty("kind").GetString());
        Assert.True(CurrentPublicRpcDescriptors.Get(RegressionTestBindings.RuntimeCatalog).IsValid(
            "#/$defs/AgentRuntimeCatalogEvent",
            events.Current,
            out var schemaError), schemaError);

        for (var index = 0; index < 300; index++)
        {
            var workspace = workspaces.CreateWorkspace($"Runtime catalog {index}");
            sessions.CreateSession($"Session {index}", workspaceId: workspace.WorkspaceId);
        }

        Assert.True(await events.MoveNextAsync());
        Assert.Equal("reset", events.Current.GetProperty("kind").GetString());
        Assert.True(await events.MoveNextAsync());
        Assert.Equal("session", events.Current.GetProperty("kind").GetString());
        cancellation.Cancel();
    }

    [Fact]
    public async Task RuntimeCatalogClient_RetriesTransientFailureAndSignalsSequenceGap()
    {
        var sessionId = Guid.NewGuid();
        var transport = new RetryingRuntimeCatalogRpcClient(sessionId);
        await using var services = new GeneratedServiceTestScope(transport);
        var client = await ResolveRuntimeCatalogClientAsync(services);

        await using (var initial = client.WatchAsync().GetAsyncEnumerator())
        {
            var error = await Assert.ThrowsAsync<SunderRpcException>(() => initial.MoveNextAsync().AsTask());
            Assert.Equal(SunderRpcErrorKind.Unavailable, error.Error.Kind);
        }

        await using var events = client.WatchAsync().GetAsyncEnumerator();
        Assert.True(await events.MoveNextAsync());
        Assert.Equal(1, Assert.IsType<RuntimeCatalogModels.AgentRuntimeCatalogResetEvent>(events.Current).Sequence);
        Assert.True(await events.MoveNextAsync());
        var session = Assert.IsType<RuntimeCatalogModels.AgentSessionCatalogEvent>(events.Current);
        Assert.Equal(2, session.Sequence);
        Assert.Equal(sessionId, session.SessionId);
        Assert.True(transport.SubscriptionAttempts >= 2);
    }

    [Theory]
    [InlineData("list-workspaces")]
    [InlineData("get-workspace")]
    public async Task RuntimeCatalogWorkspaceHandlers_PreserveRoutesAndPropagateCancellation(
        string methodId)
    {
        using var scope = RegressionTestPackageScope.Create();
        await using var services = RegressionTestRuntime.Create(scope.Context);
        var catalog = new AgentRuntimeCatalog(
            services.GetRequiredService<AgentSessionService>(),
            services.GetRequiredService<AgentProfileService>(),
            services.GetRequiredService<AgentWorkspaceService>());
        var handler = catalog.CreateSunderRpcProviderRegistration().Handler;
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var request = methodId == "list-workspaces"
            ? JsonSerializer.SerializeToElement(new { })
            : JsonSerializer.SerializeToElement("workspace");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => handler.InvokeUnaryAsync(
            null!,
            "runtime-catalog",
            methodId,
            request,
            cancellation.Token).AsTask());
    }

    [Fact]
    public async Task RuntimeCatalogWorkspaceClient_LinksListCallToClientLifetime()
    {
        var transport = new RetryingRuntimeCatalogRpcClient(Guid.NewGuid());
        await using var services = new GeneratedServiceTestScope(transport);
        var client = await ResolveRuntimeCatalogClientAsync(services);
        var disposed = false;
        try
        {
            var listing = client.ListWorkspacesAsync().AsTask();
            var route = await transport.UnaryStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal("runtime-catalog", route.ServiceId);
            Assert.Equal("list-workspaces", route.MethodId);

            await services.DisposeAsync();
            disposed = true;
            var retired = await Assert.ThrowsAsync<SunderRpcException>(() => listing);
            Assert.Equal(SunderRpcErrorKind.Unavailable, retired.Error.Kind);
            Assert.Equal("rpc.binding.retired", retired.Error.Code);
            Assert.Equal(SunderRpcDispatchState.PossiblyDispatched, retired.Error.DispatchState);
            Assert.Equal(SunderRpcRetrySafety.Unsafe, retired.Error.RetrySafety);
        }
        finally
        {
            if (!disposed)
            {
                await services.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task MemoryChangeQueue_DistinctSessionOverflowCollapsesPendingChangesToReset()
    {
        var boundaryQueue = new MemoryChangeQueue();
        var sessionIds = Enumerable.Range(0, MemoryChangeQueue.PendingSessionCapacity)
            .Select(static _ => Guid.NewGuid())
            .ToArray();
        foreach (var sessionId in sessionIds)
        {
            boundaryQueue.PublishSession(sessionId);
            boundaryQueue.PublishSession(sessionId);
        }
        boundaryQueue.PublishWorker();
        boundaryQueue.Complete();

        var boundaryChanges = new List<MemoryChanged>();
        await foreach (var change in boundaryQueue.ReadAllAsync())
        {
            boundaryChanges.Add(change);
        }
        var deliveredSessions = boundaryChanges
            .Where(static change => change.Kind == MemoryChangeKind.Session)
            .Select(static change => Assert.IsType<Guid>(change.SessionId))
            .ToHashSet();
        Assert.True(sessionIds.ToHashSet().SetEquals(deliveredSessions));
        Assert.Single(boundaryChanges, static change => change.Kind == MemoryChangeKind.Worker);
        Assert.DoesNotContain(boundaryChanges, static change => change.Kind == MemoryChangeKind.ResetRequired);

        var overflowQueue = new MemoryChangeQueue();
        foreach (var sessionId in sessionIds)
        {
            overflowQueue.PublishSession(sessionId);
        }
        overflowQueue.PublishWorker();
        overflowQueue.PublishSession(Guid.NewGuid());
        for (var index = 0; index < 300; index++)
        {
            overflowQueue.PublishSession(Guid.NewGuid());
            overflowQueue.PublishWorker();
        }
        overflowQueue.Complete();

        var overflowChanges = new List<MemoryChanged>();
        await foreach (var change in overflowQueue.ReadAllAsync())
        {
            overflowChanges.Add(change);
        }

        Assert.Collection(
            overflowChanges,
            static change => Assert.Equal(MemoryChangeKind.ResetRequired, change.Kind));
    }

    [Fact]
    public async Task McpChangeQueue_SaturationCoalescesToBothProjectionInvalidations()
    {
        var queue = new McpChangeQueue();
        for (var index = 0; index < 300; index++)
        {
            queue.Publish();
        }
        queue.Complete();

        var delivered = new List<McpChanged>();
        await foreach (var change in queue.ReadAllAsync())
        {
            delivered.Add(change);
        }

        Assert.Equal(
            [McpChangeKind.Servers, McpChangeKind.Status],
            delivered.Select(static change => change.Kind));
    }

    private static async ValueTask<RuntimeCatalogServices.IRuntimeCatalogService> ResolveRuntimeCatalogClientAsync(
        GeneratedServiceTestScope services)
        => Assert.Single((await services.Providers<RuntimeCatalogServices.IRuntimeCatalogService>().ListAsync()).Providers,
            static provider => provider.ProviderId == "agent.runtime.catalog").Service;

    private sealed class RetryingRuntimeCatalogRpcClient(Guid sessionId) : ISunderRpcCallScope
    {
        private int _subscriptionAttempts;
        public SunderRpcProviderSnapshot Provider { get; } = CreateProvider();
        public DateTimeOffset DeadlineUtc { get; } = DateTimeOffset.UtcNow.AddMinutes(1);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public ValueTask<ISunderRpcCallScope> CreateCallScopeAsync(SunderRpcCallOptions? options = null, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<ISunderRpcCallScope>(this);
        public ValueTask<SunderRpcContentReference> RegisterContentAsync(SunderRpcEndpointReference endpoint, Stream source,
            SunderRpcContentRegistrationOptions options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<SunderRpcContentReference> RegisterContentFileAsync(SunderRpcEndpointReference endpoint, string filePath,
            SunderRpcContentRegistrationOptions options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<Stream> OpenContentAsync(SunderRpcContentReference reference, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        private static SunderRpcProviderSnapshot CreateProvider()
        {
            var descriptor = CurrentPublicRpcDescriptors.Get(RegressionTestBindings.RuntimeCatalog);
            return new("sunder.package.agent", "2.0.0", "agent.runtime.catalog", descriptor.ContractId,
                descriptor.Version, descriptor.Sha256, descriptor.AbiSha256!, Guid.NewGuid(), 1, 1,
                new("rpc1_runtime_catalog_reliability"), 1, SunderRpcProviderState.Active);
        }

        public int SubscriptionAttempts => Volatile.Read(ref _subscriptionAttempts);

        public TaskCompletionSource<(string ServiceId, string MethodId)> UnaryStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<SunderRpcProviderSnapshot?> GetProviderAsync(
            SunderRpcEndpointReference endpoint,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<SunderRpcProviderSnapshot?>(Provider);

        public ValueTask<bool> TryReportInvariantViolationAsync(
            SunderRpcEndpointReference endpoint,
            Exception exception,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<SunderRpcCatalogSnapshot> DiscoverAsync(
            string contractId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new SunderRpcCatalogSnapshot(
                1,
                1,
                contractId == Provider.ContractId ? [Provider] : []));
        }

        public async IAsyncEnumerable<SunderRpcCatalogEvent> WatchAsync(
            long afterRevision,
            long afterSequence,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }

        public async ValueTask<JsonElement> InvokeAsync(
            SunderRpcEndpointReference endpoint,
            string serviceId,
            string methodId,
            JsonElement request,
            SunderRpcCallOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            UnaryStarted.TrySetResult((serviceId, methodId));
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return default;
        }

        public async IAsyncEnumerable<JsonElement> SubscribeAsync(
            SunderRpcEndpointReference endpoint,
            string serviceId,
            string methodId,
            JsonElement request,
            SunderRpcCallOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var attempt = Interlocked.Increment(ref _subscriptionAttempts);
            await Task.Yield();
            if (attempt == 1)
            {
                throw new SunderRpcException(new SunderRpcError(
                    SunderRpcErrorKind.Unavailable,
                    "test.runtime-catalog-transient",
                    "Injected transient watch failure."));
            }

            yield return JsonSerializer.SerializeToElement(new { kind = "reset", sequence = 1 });
            yield return JsonSerializer.SerializeToElement(new { kind = "session", sequence = 2, sessionId });
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class TestRuntimeCatalog : IAgentRuntimeCatalog
    {
        public event Action<Guid>? SessionChanged;
        public event Action<Guid, AgentTurnRecord>? TurnChanged
        {
            add { }
            remove { }
        }
        public event Action<string>? ProfileChanged
        {
            add { }
            remove { }
        }
        public event Action? ResetRequired
        {
            add { }
            remove { }
        }

        public void RaiseSessionChanged(Guid sessionId) => SessionChanged?.Invoke(sessionId);

        public IReadOnlyList<AgentSessionRecord> ListSessions() => [];
        public IReadOnlyList<AgentSessionRecord> ListSessionsForProfile(string profileId) => [];
        public IReadOnlyList<AgentSessionRecord> ListSessionsForWorkspace(string workspaceId) => [];
        public AgentSessionRecord? GetSession(Guid sessionId) => null;
        public CancellationToken ListWorkspacesCancellationToken { get; private set; }
        public CancellationToken GetWorkspaceCancellationToken { get; private set; }

        public ValueTask<IReadOnlyList<AgentWorkspaceRecord>> ListWorkspacesAsync(
            CancellationToken cancellationToken = default)
        {
            ListWorkspacesCancellationToken = cancellationToken;
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IReadOnlyList<AgentWorkspaceRecord>>([]);
        }

        public ValueTask<AgentWorkspaceRecord?> GetWorkspaceAsync(
            string workspaceId,
            CancellationToken cancellationToken = default)
        {
            GetWorkspaceCancellationToken = cancellationToken;
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<AgentWorkspaceRecord?>(null);
        }
        public AgentProfileRecord? GetSessionProfile(Guid sessionId) => null;
        public AgentWorkingSummaryRecord? GetWorkingSummary(Guid sessionId) => null;
        public AgentSessionContextCheckpointRecord? GetLatestSessionContextCheckpoint(Guid sessionId) => null;
        public AgentRunCheckpointRecord? GetLatestCheckpoint(Guid sessionId) => null;
        public IReadOnlyList<AgentTurnRecord> ListRecentTurns(Guid sessionId, int limit) => [];
        public IReadOnlyList<AgentTurnRecord> ListTurnsBefore(Guid sessionId, DateTimeOffset beforeCreatedAtUtc, Guid beforeTurnId, int limit) => [];
        public IReadOnlyList<AgentTurnRecord> ListTurnsAfter(Guid sessionId, DateTimeOffset afterCreatedAtUtc, Guid afterTurnId, int limit) => [];
        public IReadOnlyList<AgentProfileRecord> ListProfiles() => [];
        public AgentProfileRecord? GetProfile(string profileId) => null;
        public AgentProfileModelBindingRecord? GetSessionModelBinding(Guid sessionId, string capabilityKind) => null;
        public AgentProfileModelBindingRecord? GetModelBinding(string profileId, string capabilityKind) => null;
    }
}
