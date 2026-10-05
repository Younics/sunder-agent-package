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
    private static async Task VerifySubagentRuntimeConsumers(Assembly worker, Assembly agentOwner)
    {
        using var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
        var rpc = new MemoryConsumerRpc(new RegressionTestRpcClient(state, "sunder.package.agent.subagents"));
        var type = worker.GetType("Sunder.Package.Agent.Subagents.Rpc.SubagentRuntimeBindings", true)!;
        var bindings = Activator.CreateInstance(type, rpc)!;
        await using var owned = (IAsyncDisposable)bindings;
        Assert.Empty(References("GetRuntimeCatalogs"));
        Assert.Empty(References("GetChildRunExecutors"));
        Assert.Equal(0, rpc.DiscoveryCount);
        var domain = new SubagentTranscriptRuntime();
        var child = new ArtifactChildExecutor();
        AddSubagentSourceProvider(state, agentOwner, "RuntimeCatalogService", domain, AgentRpcContractIds.RuntimeCatalog);
        AddSubagentSourceProvider(state, agentOwner, "ChildRunExecutorService", child, AgentRpcContractIds.ChildRunExecutor);
        await type.GetMethod("StartAsync")!.CreateDelegate<Func<CancellationToken, ValueTask>>(bindings)(default);
        var catalogReference = Assert.Single(References("GetRuntimeCatalogs"));
        Assert.Same(catalogReference, Assert.Single(References("GetRuntimeCatalogs")));
        using var catalog = AcquireSubagent(catalogReference);
        Assert.Same(worker, catalog.Service.Value.GetType().Assembly);
        Assert.Equal(domain.Session.SessionId, Assert.Single(catalog.Service.Invoke<AgentSessionRecord[]>("ListSessions")).SessionId);
        Assert.Single(catalog.Service.Invoke<AgentSessionRecord[]>("ListSessionsForProfile", domain.Profile.ProfileId));
        Assert.Empty(catalog.Service.Invoke<AgentSessionRecord[]>("ListSessionsForWorkspace", "missing"));
        Assert.Equal(domain.Session, catalog.Service.Invoke<AgentSessionRecord?>("GetSession", domain.Session.SessionId));
        Assert.Null(catalog.Service.Invoke<AgentSessionRecord?>("GetSession", Guid.NewGuid()));
        Assert.Empty(await catalog.Service.InvokeAsync<AgentWorkspaceRecord[]>("ListWorkspacesAsync", CancellationToken.None));
        Assert.Null(await catalog.Service.InvokeAsync<AgentWorkspaceRecord?>("GetWorkspaceAsync", "missing", CancellationToken.None));
        Assert.Equal(domain.Profile.ProfileId, catalog.Service.Invoke<AgentProfileRecord?>("GetSessionProfile", domain.Session.SessionId)!.ProfileId);
        Assert.Equal(domain.Profile.ProfileId, Assert.Single(catalog.Service.Invoke<AgentProfileRecord[]>("ListProfiles")).ProfileId);
        Assert.Equal(domain.Profile.ProfileId, catalog.Service.Invoke<AgentProfileRecord?>("GetProfile", domain.Profile.ProfileId)!.ProfileId);
        Assert.Null(catalog.Service.Invoke<AgentProfileRecord?>("GetProfile", "missing"));
        Assert.Null(catalog.Service.Invoke<AgentWorkingSummaryRecord?>("GetWorkingSummary", domain.Session.SessionId));
        Assert.Null(catalog.Service.Invoke<AgentSessionContextCheckpointRecord?>("GetLatestSessionContextCheckpoint", domain.Session.SessionId));
        Assert.Null(catalog.Service.Invoke<AgentRunCheckpointRecord?>("GetLatestCheckpoint", domain.Session.SessionId));
        Assert.Empty(catalog.Service.Invoke<AgentTurnRecord[]>("ListRecentTurns", domain.Session.SessionId, 3));
        Assert.Empty(catalog.Service.Invoke<AgentTurnRecord[]>("ListTurnsBefore", domain.Session.SessionId, domain.Turn.CreatedAtUtc, domain.Turn.TurnId, 3));
        Assert.Empty(catalog.Service.Invoke<AgentTurnRecord[]>("ListTurnsAfter", domain.Session.SessionId, domain.Turn.CreatedAtUtc, domain.Turn.TurnId, 3));
        Assert.NotNull(catalog.Service.Invoke<AgentProfileModelBindingRecord?>("GetModelBinding", domain.Profile.ProfileId, AgentModelCapabilityKinds.Embedding));
        Assert.NotNull(catalog.Service.Invoke<AgentProfileModelBindingRecord?>("GetSessionModelBinding", domain.Session.SessionId, AgentModelCapabilityKinds.Embedding));
        Assert.True(worker.GetType("Sunder.Package.Agent.Contracts.Contracts.IAgentTranscriptCatalog", true)!.IsInstanceOfType(catalog.Service.Value));
        var transcript = catalog.Service;
        Assert.Equal(domain.Turn.TurnId, Assert.Single(transcript.Invoke<AgentTurnRecord[]>("ListRecentTranscriptHeaders", domain.Session.SessionId, 2)).TurnId);
        Assert.Single(transcript.Invoke<AgentTurnRecord[]>("ListTranscriptHeadersBefore", domain.Session.SessionId, domain.Turn.CreatedAtUtc.AddTicks(1), Guid.NewGuid(), 2));
        Assert.Single(transcript.Invoke<AgentTurnRecord[]>("ListTranscriptHeadersAfter", domain.Session.SessionId, domain.Turn.CreatedAtUtc.AddTicks(-1), Guid.Empty, 2));
        Assert.Equal(domain.Detail, transcript.Invoke<AgentTranscriptToolDetailRecord?>("GetTranscriptToolDetail", new AgentTranscriptToolDetailRequest(domain.Session.SessionId, ItemId: domain.Detail.ResultItemId)));
        Assert.Null(transcript.Invoke<AgentTranscriptToolDetailRecord?>("GetTranscriptToolDetail", new AgentTranscriptToolDetailRequest(Guid.NewGuid())));
        var sessionChanged = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        var turnChanged = new TaskCompletionSource<AgentTurnRecord>(TaskCreationOptions.RunContinuationsAsynchronously);
        var profileChanged = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reset = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        catalog.Service.On("SessionChanged", (Action<Guid>)(id => sessionChanged.TrySetResult(id)));
        catalog.Service.OnTurn((_, turn) => turnChanged.TrySetResult(turn));
        catalog.Service.On("ProfileChanged", (Action<string>)(id => profileChanged.TrySetResult(id)));
        catalog.Service.On("ResetRequired", (Action)(() => reset.TrySetResult()));
        await domain.Subscribed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        domain.NotifySession(); domain.NotifyTurn(domain.Turn); domain.NotifyProfile(); domain.NotifyReset();
        Assert.Equal(domain.Session.SessionId, await sessionChanged.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(domain.Turn.TurnId, (await turnChanged.Task.WaitAsync(TimeSpan.FromSeconds(5))).TurnId);
        Assert.Equal(domain.Profile.ProfileId, await profileChanged.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        await reset.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var childReference = Assert.Single(References("GetChildRunExecutors"));
        using var executor = AcquireSubagent(childReference);
        Assert.Same(worker, executor.Service.Value.GetType().Assembly);
        var request = new AgentChildRunRequest(domain.Session.SessionId, Guid.NewGuid(), 7, "parent.call", "workspace", "task", domain.Profile, "A task λ", "Child");
        foreach (var status in new[] { AgentRunStatus.Completed, AgentRunStatus.WaitingForApproval, AgentRunStatus.Stopped, AgentRunStatus.Failed })
        {
            child.Status = status; child.NullFields = status == AgentRunStatus.Stopped;
            var result = await executor.Service.InvokeAsync<AgentChildRunResult>("RunChildAsync", request, CancellationToken.None);
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
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => executor.Service.InvokeAsync<AgentChildRunResult>("RunChildAsync", request, cancelled.Token).AsTask());
            Assert.Equal(calls, child.Requests.Count);
        }
        foreach (var kind in new[] { SunderRpcErrorKind.Unavailable, SunderRpcErrorKind.PermissionDenied })
        {
            rpc.LookupFailure = kind;
            if (kind == SunderRpcErrorKind.Unavailable) Assert.Null(TryAcquireSubagent(childReference));
            else Assert.Equal(kind, Assert.IsType<SunderRpcException>(Assert.Throws<TargetInvocationException>(() => TryAcquireSubagent(childReference)).InnerException).Error.Kind);
        }
        rpc.LookupFailure = null;
        child.AfterReply = () => rpc.LookupTransform = value => value.ContractId == AgentRpcContractIds.ChildRunExecutor ? value with { SessionGeneration = value.SessionGeneration + 1 } : value;
        Assert.Equal(SunderRpcErrorKind.StaleEndpoint, (await Assert.ThrowsAsync<SunderRpcException>(() => executor.Service.InvokeAsync<AgentChildRunResult>("RunChildAsync", request, CancellationToken.None).AsTask())).Error.Kind);
        child.AfterReply = null; rpc.LookupTransform = null;
        child.Block = true;
        using (var cancelled = new CancellationTokenSource())
        {
            var pending = executor.Service.InvokeAsync<AgentChildRunResult>("RunChildAsync", request, cancelled.Token).AsTask();
            await child.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        child.Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var retiring = executor.Service.InvokeAsync<AgentChildRunResult>("RunChildAsync", request, CancellationToken.None).AsTask();
        await child.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await state.RetireAsync(child).WaitAsync(TimeSpan.FromSeconds(5));
        var retiredFailure = await Record.ExceptionAsync(() => retiring.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(retiredFailure is OperationCanceledException || retiredFailure is SunderRpcException { Error.Kind: SunderRpcErrorKind.StaleEndpoint or SunderRpcErrorKind.Cancelled });
        Assert.Null(TryAcquireSubagent(childReference));
        executor.Dispose();
        var replacement = new ArtifactChildExecutor();
        AddSubagentSourceProvider(state, agentOwner, "ChildRunExecutorService", replacement, AgentRpcContractIds.ChildRunExecutor);
        Assert.Null(TryAcquireSubagent(childReference));
        Assert.Empty(replacement.Requests);
        using var current = AcquireSubagent(Assert.Single(References("GetChildRunExecutors")));
        Assert.Equal(AgentRunStatus.Completed, (await current.Service.InvokeAsync<AgentChildRunResult>("RunChildAsync", request, CancellationToken.None)).Status);
        await state.RetireAsync(domain).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(TryAcquireSubagent(catalogReference));
        catalog.Dispose();
        Assert.Equal(0, domain.SessionSubscriberCount);
        AddSubagentSourceProvider(state, agentOwner, "RuntimeCatalogService", new SubagentTranscriptRuntime(), AgentRpcContractIds.RuntimeCatalog);
        using (var nextCatalog = AcquireSubagent(Assert.Single(References("GetRuntimeCatalogs")))) Assert.Single(nextCatalog.Service.Invoke<AgentSessionRecord[]>("ListSessions"));
        var stopping = owned.DisposeAsync().AsTask();
        Assert.Same(stopping, owned.DisposeAsync().AsTask());
        Assert.False(stopping.IsCompleted); // The remaining borrowed child client must be returned.
        current.Dispose();
        await stopping.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, rpc.ActiveWatches);
        foreach (var scope in rpc.Scopes)
            await Assert.ThrowsAsync<ObjectDisposedException>(() => scope.DiscoverAsync(AgentRpcContractIds.RuntimeCatalog).AsTask());

        object[] References(string method) => ((IEnumerable)type.GetMethod(method)!.Invoke(bindings, null)!).Cast<object>().ToArray();
    }

    private static ImportedSubagentLease AcquireSubagent(object reference) => Assert.IsType<ImportedSubagentLease>(TryAcquireSubagent(reference));
    private static ImportedSubagentLease? TryAcquireSubagent(object reference)
    {
        object?[] arguments = [null];
        return (bool)reference.GetType().GetMethod("TryAcquire")!.Invoke(reference, arguments)! ? new(arguments[0]!) : null;
    }
    private sealed class ImportedSubagentLease(object lease) : IDisposable
    {
        internal RoleLocalValue Service { get; } = new(lease.GetType().GetProperty("Service")!.GetValue(lease)!);
        public void Dispose() { Service.Dispose(); ((IDisposable)lease).Dispose(); }
    }
    private static void AddSubagentSourceProvider(RegressionTestRpcState state, Assembly owner, string typeName, object implementation, string contractId)
    {
        var type = owner.GetType("Sunder.Package.Agent.Rpc.Public." + typeName, true)!;
        var provider = AgentRoleBoundary.Create(type, implementation);
        var registration = Assert.IsType<SunderWorkerProviderRegistration>(type.GetMethod("CreateSunderRpcProviderRegistration")!.Invoke(provider, null));
        var descriptor = RegressionTestRpcDescriptors.Get(contractId);
        Assert.Equal(descriptor.Sha256, registration.DescriptorSha256);
        Assert.Equal(descriptor.AbiSha256, registration.AbiSha256);
        state.Add("sunder.package.agent", descriptor, implementation, registration.Handler, Noop.Instance, registration.ProviderId, "2.0.0");
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
