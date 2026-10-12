using System.Collections;
using System.Reflection;
using System.Text.Json;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Worker;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentSourceArtifactMigrationTests
{
    internal static async Task VerifySubagentRuntimeConsumers(Assembly worker, Assembly agentOwner)
    {
        using var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
        var rpc = new MemoryConsumerRpc(new RegressionTestRpcClient(state, "sunder.package.agent.subagents"));
        Assert.Null(worker.GetType("Sunder.Package.Agent.Subagents.Rpc.SubagentRuntimeBindings"));
        await using var owned = new GeneratedServiceTestScope(rpc);
        Assert.Equal(0, rpc.DiscoveryCount);
        Assert.Empty(await ProjectedProviders(owned, worker, "IRuntimeCatalogService"));
        Assert.Empty(await ProjectedProviders(owned, worker, "IChildRunExecutorService"));
        var domain = new SubagentTranscriptRuntime();
        var child = new ArtifactChildExecutor();
        AddSubagentSourceProvider(state, agentOwner, "RuntimeCatalogService", domain, RegressionTestBindings.RuntimeCatalog);
        AddSubagentSourceProvider(state, agentOwner, "ChildRunExecutorService", child, RegressionTestBindings.ChildRunExecutor);
        var catalogReference = Assert.Single(await ProjectedProviders(owned, worker, "IRuntimeCatalogService"));
        Assert.Equal(catalogReference.Snapshot, Assert.Single(await ProjectedProviders(owned, worker, "IRuntimeCatalogService")).Snapshot);
        using var catalog = catalogReference.Service;
        Assert.Same(worker, catalog.Value.GetType().Assembly);
        Assert.Equal(domain.Session.SessionId, Assert.Single(await catalog.InvokeAsync<AgentSessionRecord[]>("ListSessionsAsync", CancellationToken.None)).SessionId);
        Assert.Single(await catalog.InvokeAsync<AgentSessionRecord[]>("ListSessionsForProfileAsync", domain.Profile.ProfileId, CancellationToken.None));
        Assert.Empty(await catalog.InvokeAsync<AgentSessionRecord[]>("ListSessionsForWorkspaceAsync", "missing", CancellationToken.None));
        Assert.Equal(domain.Session, await catalog.InvokeAsync<AgentSessionRecord?>("GetSessionAsync", domain.Session.SessionId, CancellationToken.None));
        Assert.Null(await catalog.InvokeAsync<AgentSessionRecord?>("GetSessionAsync", Guid.NewGuid(), CancellationToken.None));
        Assert.Empty(await catalog.InvokeAsync<AgentWorkspaceRecord[]>("ListWorkspacesAsync", CancellationToken.None));
        Assert.Null(await catalog.InvokeAsync<AgentWorkspaceRecord?>("GetWorkspaceAsync", "missing", CancellationToken.None));
        Assert.Equal(domain.Profile.ProfileId, (await catalog.InvokeAsync<AgentProfileRecord?>("GetSessionProfileAsync", domain.Session.SessionId, CancellationToken.None))!.ProfileId);
        Assert.Equal(domain.Profile.ProfileId, Assert.Single(await catalog.InvokeAsync<AgentProfileRecord[]>("ListProfilesAsync", CancellationToken.None)).ProfileId);
        Assert.Equal(domain.Profile.ProfileId, (await catalog.InvokeAsync<AgentProfileRecord?>("GetProfileAsync", domain.Profile.ProfileId, CancellationToken.None))!.ProfileId);
        Assert.Null(await catalog.InvokeAsync<AgentProfileRecord?>("GetProfileAsync", "missing", CancellationToken.None));
        Assert.Null(await catalog.InvokeAsync<AgentWorkingSummaryRecord?>("GetWorkingSummaryAsync", domain.Session.SessionId, CancellationToken.None));
        Assert.Null(await catalog.InvokeAsync<AgentSessionContextCheckpointRecord?>("GetSessionContextCheckpointAsync", domain.Session.SessionId, CancellationToken.None));
        Assert.Null(await catalog.InvokeAsync<AgentRunCheckpointRecord?>("GetLatestCheckpointAsync", domain.Session.SessionId, CancellationToken.None));
        Assert.Empty(await catalog.InvokeAsync<AgentTurnRecord[]>("ListRecentTurnsAsync", new { domain.Session.SessionId, limit = 3 }, CancellationToken.None));
        var window = new { domain.Session.SessionId, domain.Turn.CreatedAtUtc, domain.Turn.TurnId, limit = 3 };
        Assert.Empty(await catalog.InvokeAsync<AgentTurnRecord[]>("ListTurnsBeforeAsync", window, CancellationToken.None));
        Assert.Empty(await catalog.InvokeAsync<AgentTurnRecord[]>("ListTurnsAfterAsync", window, CancellationToken.None));
        Assert.NotNull(await catalog.InvokeAsync<AgentProfileModelBindingRecord?>("GetModelBindingAsync", new { domain.Profile.ProfileId, capabilityKind = AgentModelCapabilityKinds.Embedding }, CancellationToken.None));
        Assert.NotNull(await catalog.InvokeAsync<AgentProfileModelBindingRecord?>("GetSessionModelBindingAsync", new { domain.Session.SessionId, capabilityKind = AgentModelCapabilityKinds.Embedding }, CancellationToken.None));
        Assert.Equal(domain.Turn.TurnId, Assert.Single(await catalog.InvokeAsync<AgentTurnRecord[]>("ListRecentTranscriptHeadersAsync", new { domain.Session.SessionId, limit = 2 }, CancellationToken.None)).TurnId);
        Assert.Single(await catalog.InvokeAsync<AgentTurnRecord[]>("ListTranscriptHeadersBeforeAsync", new { domain.Session.SessionId, createdAtUtc = domain.Turn.CreatedAtUtc.AddTicks(1), turnId = Guid.NewGuid(), limit = 2 }, CancellationToken.None));
        Assert.Single(await catalog.InvokeAsync<AgentTurnRecord[]>("ListTranscriptHeadersAfterAsync", new { domain.Session.SessionId, createdAtUtc = domain.Turn.CreatedAtUtc.AddTicks(-1), turnId = Guid.Empty, limit = 2 }, CancellationToken.None));
        Assert.Equal(domain.Detail, await catalog.InvokeAsync<AgentTranscriptToolDetailRecord?>("GetTranscriptToolDetailAsync", new AgentTranscriptToolDetailRequest(domain.Session.SessionId, ItemId: domain.Detail.ResultItemId), CancellationToken.None));
        Assert.Null(await catalog.InvokeAsync<AgentTranscriptToolDetailRecord?>("GetTranscriptToolDetailAsync", new AgentTranscriptToolDetailRequest(Guid.NewGuid()), CancellationToken.None));
        using var watchCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var watch = ProjectedCatalogEvents(catalog.Value, watchCancellation.Token).GetAsyncEnumerator();
        Task<bool>? pendingWatch = null;
        Task<bool> MoveNext() => pendingWatch = watch.MoveNextAsync().AsTask();
        try
        {
        var first = MoveNext();
        await domain.Subscribed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(await first.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("reset", watch.Current.GetProperty("kind").GetString());
        Assert.Equal(1, watch.Current.GetProperty("sequence").GetInt64());
        domain.NotifySession(); domain.NotifyTurn(domain.Turn); domain.NotifyProfile(); domain.NotifyReset();
        Assert.True(await MoveNext().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(domain.Session.SessionId, watch.Current.GetProperty("sessionId").GetGuid());
        Assert.True(await MoveNext().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(domain.Turn.TurnId, watch.Current.GetProperty("turn").GetProperty("turnId").GetGuid());
        Assert.True(await MoveNext().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(domain.Profile.ProfileId, watch.Current.GetProperty("profileId").GetString());
        Assert.True(await MoveNext().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("reset", watch.Current.GetProperty("kind").GetString());
        Assert.Equal(5, watch.Current.GetProperty("sequence").GetInt64());
        var nextEvent = MoveNext();

        var childReference = Assert.Single(await ProjectedProviders(owned, worker, "IChildRunExecutorService"));
        using var executor = childReference.Service;
        Assert.Same(worker, executor.Value.GetType().Assembly);
        var request = new AgentChildRunRequest(domain.Session.SessionId, Guid.NewGuid(), 7, "parent.call", "workspace", "task", domain.Profile, "A task λ", "Child");
        foreach (var status in new[] { AgentRunStatus.Completed, AgentRunStatus.WaitingForApproval, AgentRunStatus.Stopped, AgentRunStatus.Failed })
        {
            child.Status = status; child.NullFields = status == AgentRunStatus.Stopped;
            var result = await executor.InvokeAsync<AgentChildRunResult>("RunChildAsync", request, CancellationToken.None);
            Assert.Equal(status, result.Status);
            Assert.Equal(child.NullFields ? null : request.Title, result.Title);
            Assert.Equal(child.NullFields ? null : "Child artifact result", result.Content);
        }
        Assert.Equal(request.UserMessage, child.Requests[0].UserMessage);
        Assert.Equal(request.ParentRunRevision, child.Requests[0].ParentRunRevision);
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            var calls = child.Requests.Count;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => executor.InvokeAsync<AgentChildRunResult>("RunChildAsync", request, cancelled.Token).AsTask());
            Assert.Equal(calls, child.Requests.Count);
        }
        foreach (var kind in new[] { SunderRpcErrorKind.Unavailable, SunderRpcErrorKind.PermissionDenied })
        {
            await using var lookupReceiver = new GeneratedServiceTestScope(rpc);
            var lookupReference = Assert.Single(await ProjectedProviders(lookupReceiver, worker, "IChildRunExecutorService"));
            rpc.LookupFailure = kind;
            try { Assert.Equal(kind, (await Assert.ThrowsAsync<SunderRpcException>(() => lookupReference.EnsureCurrentAsync())).Error.Kind); }
            finally { rpc.LookupFailure = null; }
        }
        rpc.LookupFailure = null;
        var staleTransport = new FilesRpcObserver(new RegressionTestRpcClient(state, "sunder.package.agent.subagents"), "run-child");
        await using (var staleReceiver = new GeneratedServiceTestScope(staleTransport))
        {
            var staleReference = Assert.Single(await ProjectedProviders(staleReceiver, worker, "IChildRunExecutorService"));
            using var staleClient = staleReference.Service;
            child.AfterReply = () => staleTransport.Transform = value => value with { SessionGeneration = value.SessionGeneration + 1 };
            try
            {
                Assert.Equal(SunderRpcErrorKind.StaleEndpoint, (await Assert.ThrowsAsync<SunderRpcException>(() => staleClient.InvokeAsync<AgentChildRunResult>("RunChildAsync", request, CancellationToken.None).AsTask())).Error.Kind);
            }
            finally { child.AfterReply = null; staleTransport.Transform = null; }
            Assert.True(Assert.Single(staleTransport.Scopes).Disposed);
        }
        child.Block = true;
        using (var cancelled = new CancellationTokenSource())
        {
            var pending = executor.InvokeAsync<AgentChildRunResult>("RunChildAsync", request, cancelled.Token).AsTask();
            await child.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        child.Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var retiring = executor.InvokeAsync<AgentChildRunResult>("RunChildAsync", request, CancellationToken.None).AsTask();
        await child.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await state.RetireAsync(child).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(SunderRpcErrorKind.StaleEndpoint, (await Assert.ThrowsAsync<SunderRpcException>(() => retiring.WaitAsync(TimeSpan.FromSeconds(5)))).Error.Kind);
        Assert.Equal(SunderRpcErrorKind.StaleEndpoint, (await Assert.ThrowsAsync<SunderRpcException>(() => childReference.EnsureCurrentAsync())).Error.Kind);
        executor.Dispose();
        var replacement = new ArtifactChildExecutor();
        AddSubagentSourceProvider(state, agentOwner, "ChildRunExecutorService", replacement, RegressionTestBindings.ChildRunExecutor);
        Assert.Equal(SunderRpcErrorKind.StaleEndpoint, (await Assert.ThrowsAsync<SunderRpcException>(() => childReference.EnsureCurrentAsync())).Error.Kind);
        Assert.Empty(replacement.Requests);
        using var current = Assert.Single(await ProjectedProviders(owned, worker, "IChildRunExecutorService")).Service;
        Assert.Equal(AgentRunStatus.Completed, (await current.InvokeAsync<AgentChildRunResult>("RunChildAsync", request, CancellationToken.None)).Status);
        await state.RetireAsync(domain).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(SunderRpcErrorKind.StaleEndpoint, (await Assert.ThrowsAsync<SunderRpcException>(() => catalogReference.EnsureCurrentAsync())).Error.Kind);
        Assert.Equal(SunderRpcErrorKind.StaleEndpoint, (await Assert.ThrowsAsync<SunderRpcException>(() => nextEvent.WaitAsync(TimeSpan.FromSeconds(5)))).Error.Kind);
        catalog.Dispose();
        Assert.Equal(0, domain.SessionSubscriberCount);
        AddSubagentSourceProvider(state, agentOwner, "RuntimeCatalogService", new SubagentTranscriptRuntime(), RegressionTestBindings.RuntimeCatalog);
        using (var nextCatalog = Assert.Single(await ProjectedProviders(owned, worker, "IRuntimeCatalogService")).Service)
            Assert.Single(await nextCatalog.InvokeAsync<AgentSessionRecord[]>("ListSessionsAsync", CancellationToken.None));
        replacement.Block = true;
        var stoppingCall = current.InvokeAsync<AgentChildRunResult>("RunChildAsync", request, CancellationToken.None).AsTask();
        await replacement.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stopping = owned.DisposeAsync().AsTask();
        Assert.Same(stopping, owned.DisposeAsync().AsTask());
        var stoppedCall = await Assert.ThrowsAsync<SunderRpcException>(() => stoppingCall.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(SunderRpcErrorKind.Unavailable, stoppedCall.Error.Kind);
        Assert.Equal("rpc.binding.retired", stoppedCall.Error.Code);
        await stopping.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, rpc.ActiveWatches);
        foreach (var scope in rpc.Scopes)
            await Assert.ThrowsAsync<ObjectDisposedException>(() => scope.DiscoverAsync(RegressionTestBindings.RuntimeCatalog).AsTask());
        }
        finally
        {
            await watchCancellation.CancelAsync();
            if (pendingWatch is not null) await Record.ExceptionAsync(() => pendingWatch.WaitAsync(TimeSpan.FromSeconds(5)));
            await watch.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static void AddSubagentSourceProvider(RegressionTestRpcState state, Assembly owner, string typeName, object implementation, string contractId)
    {
        Assert.True(owner.GetType("Sunder.Generated.Packages.Sunder_Package_Agent_Services.I" + typeName, true)!.IsInterface);
        var receiver = new GeneratedServiceTestScope(new RegressionTestRpcClient(state, "sunder.package.agent"));
        using var providers = new RegressionTestSourceProviders();
        var registration = providers.Create(contractId, implementation, receiver);
        var descriptor = CurrentPublicRpcDescriptors.Get(contractId);
        Assert.Equal(descriptor.Sha256, registration.DescriptorSha256);
        Assert.Equal(descriptor.AbiSha256, registration.AbiSha256);
        state.Add("sunder.package.agent", descriptor, implementation, registration.Handler, new GeneratedPeerLifetime(receiver), registration.ProviderId, "2.0.0");
    }
    private sealed class SubagentTranscriptRuntime : MemoryConsumerRuntime, IAgentTranscriptCatalog
    {
        internal AgentTurnRecord Turn { get; }
        internal AgentTranscriptToolDetailRecord Detail { get; }
        internal SubagentTranscriptRuntime()
        {
            Turn = new AgentPublicSourceMigrationTests.FullRunControlProbe().Turn with { SessionId = Session.SessionId };
            Detail = new(Session.SessionId, Guid.NewGuid(), "call", Guid.NewGuid(), Guid.NewGuid(), "tool", "{opaque", "Output λ", "Summary", null, null, null,
                false, false, null, "backend", AgentToolExecutionStatus.Ambiguous, "package.tool", "schema", "1.0.0", 9) { RunId = Guid.NewGuid(), RunRevision = 7 };
        }
        public IReadOnlyList<AgentTurnRecord> ListRecentTranscriptHeaders(Guid sessionId, int limit) => sessionId == Session.SessionId ? [Turn] : [];
        public IReadOnlyList<AgentTurnRecord> ListTranscriptHeadersBefore(Guid sessionId, DateTimeOffset created, Guid turnId, int limit) => sessionId == Session.SessionId && created > Turn.CreatedAtUtc ? [Turn] : [];
        public IReadOnlyList<AgentTurnRecord> ListTranscriptHeadersAfter(Guid sessionId, DateTimeOffset created, Guid turnId, int limit) => sessionId == Session.SessionId && created < Turn.CreatedAtUtc ? [Turn] : [];
        public AgentTranscriptToolDetailRecord? GetTranscriptToolDetail(AgentTranscriptToolDetailRequest request) => request.SessionId == Session.SessionId ? Detail : null;
    }
}
