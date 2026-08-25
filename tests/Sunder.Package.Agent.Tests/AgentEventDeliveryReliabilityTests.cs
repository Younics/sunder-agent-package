using System.Runtime.CompilerServices;
using System.Text.Json;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Mcp.Runtime;
using Sunder.Package.Agent.Memory.Semantic.Runtime;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class AgentEventDeliveryReliabilityTests
{
    [Fact]
    public async Task RuntimeCatalogStream_SaturationCoalescesDroppedRangeIntoReset()
    {
        var catalog = new TestRuntimeCatalog();
        using var cancellation = new CancellationTokenSource();
        await using var events = AgentRuntimeCatalogRpc.CreateHandler(catalog)
            .InvokeServerStreamAsync(
                null!,
                AgentRuntimeCatalogRpc.ServiceId,
                "watch",
                JsonSerializer.SerializeToElement(new AgentRpcEmpty()),
                cancellation.Token)
            .GetAsyncEnumerator(cancellation.Token);

        Assert.True(await events.MoveNextAsync());
        Assert.Equal("reset", events.Current.GetProperty("kind").GetString());
        Assert.True(AgentRpcContractDescriptors.Get(AgentRpcContractIds.RuntimeCatalog).IsValid(
            "#/$defs/AgentRuntimeCatalogEvent",
            events.Current,
            out var schemaError), schemaError);

        for (var index = 0; index < 300; index++)
        {
            catalog.RaiseSessionChanged(Guid.NewGuid());
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
        using var client = new AgentRuntimeCatalogRpcClient(
            transport,
            new SunderRpcEndpointReference("rpc1_runtime_catalog_reliability"));
        var reset = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ResetRequired += () => reset.TrySetResult();
        client.SessionChanged += id => session.TrySetResult(id);

        await reset.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(sessionId, await session.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(transport.SubscriptionAttempts >= 2);
    }

    [Theory]
    [InlineData("list-workspaces")]
    [InlineData("get-workspace")]
    public async Task RuntimeCatalogWorkspaceHandlers_PreserveRoutesAndPropagateCancellation(
        string methodId)
    {
        var catalog = new TestRuntimeCatalog();
        var handler = AgentRuntimeCatalogRpc.CreateHandler(catalog);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var request = methodId == "list-workspaces"
            ? JsonSerializer.SerializeToElement(new AgentRpcEmpty())
            : JsonSerializer.SerializeToElement(new { value = "workspace" });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => handler.InvokeUnaryAsync(
            null!,
            AgentRuntimeCatalogRpc.ServiceId,
            methodId,
            request,
            cancellation.Token).AsTask());

        Assert.Equal(
            cancellation.Token,
            methodId == "list-workspaces"
                ? catalog.ListWorkspacesCancellationToken
                : catalog.GetWorkspaceCancellationToken);
    }

    [Fact]
    public async Task RuntimeCatalogWorkspaceClient_LinksListCallToClientLifetime()
    {
        var transport = new RetryingRuntimeCatalogRpcClient(Guid.NewGuid());
        var client = new AgentRuntimeCatalogRpcClient(
            transport,
            new SunderRpcEndpointReference("rpc1_runtime_catalog_workspace_lifetime"));
        var disposed = false;
        try
        {
            var listing = client.ListWorkspacesAsync().AsTask();
            var route = await transport.UnaryStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(AgentRuntimeCatalogRpc.ServiceId, route.ServiceId);
            Assert.Equal("list-workspaces", route.MethodId);

            client.Dispose();
            disposed = true;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => listing)
                .WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            if (!disposed)
            {
                client.Dispose();
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

    private sealed class RetryingRuntimeCatalogRpcClient(Guid sessionId) : ISunderRpcClient
    {
        private int _subscriptionAttempts;

        public int SubscriptionAttempts => Volatile.Read(ref _subscriptionAttempts);

        public TaskCompletionSource<(string ServiceId, string MethodId)> UnaryStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<SunderRpcProviderSnapshot?> GetProviderAsync(
            SunderRpcEndpointReference endpoint,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<bool> TryReportInvariantViolationAsync(
            SunderRpcEndpointReference endpoint,
            Exception exception,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<SunderRpcCatalogSnapshot> DiscoverAsync(
            string contractId,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<SunderRpcCatalogEvent> WatchAsync(
            long afterRevision,
            long afterSequence,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

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

            yield return SerializeEvent(new AgentSessionCatalogEvent(2, sessionId));
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        private static JsonElement SerializeEvent(AgentRuntimeCatalogEvent value)
        {
            using var document = JsonDocument.Parse(AgentRpcServiceHandler.Serialize(value));
            return document.RootElement.Clone();
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
