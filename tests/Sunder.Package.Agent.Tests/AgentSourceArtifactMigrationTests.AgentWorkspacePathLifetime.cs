using System.Reflection;
using System.Text.Json;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentSourceArtifactMigrationTests
{
    private static async Task VerifyAgentWorkspacePathLifetime(Assembly owner, Assembly local, Assembly probes)
    {
        using var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
        var peer = new WorkspacePathPeer();
        var handler = (SurfaceHandler)AddGeneratedPeer(state, "sunder.package.agent.execution.local", RegressionTestBindings.WorkspacePathMigrators,
            peer, peer.ContributorId, value => new SurfaceHandler(value));
        var rpc = new FilesRpcObserver(new RegressionTestRpcClient(state, "sunder.package.agent"), "list-legacy-paths");
        Assert.Null(owner.GetType("Sunder.Package.Agent.Runtime.Rpc.GeneratedWorkspacePathMigratorClient"));
        await using var lifetime = new GeneratedServiceTestScope(rpc);
        var selected = Assert.Single(await ProjectedProviders(lifetime, owner, "IWorkspacePathMigratorService"));
        using var client = selected.Service;
        Assert.Same(owner, client.Value.GetType().Assembly);
        var now = DateTimeOffset.UtcNow;
        var context = new AgentWorkspacePathMigrationContext(new("workspace", "Workspace", null, now, now),
            new("binding", "workspace", RegressionTestBindings.ExecutionTargets, "local", "primary-execution-target", true, 0, now, now));
        Assert.Equal(peer.ContributorId, (await client.InvokeAsync<AgentServiceIdentity>("DescribeAsync", CancellationToken.None)).Id);
        Assert.True(await client.InvokeAsync<bool>("CanMigrateAsync", context, CancellationToken.None));
        Assert.False(await client.InvokeAsync<bool>("CanMigrateAsync", context with { Binding = context.Binding with { ContributionId = "other" } }, CancellationToken.None));
        Assert.Equal(peer.Items, await Paths());
        foreach (var change in new Func<SunderRpcProviderSnapshot, SunderRpcProviderSnapshot>[]
        {
            value => value with { PackageId = "other.package" }, value => value with { PackageVersion = "2.0.1" },
            value => value with { ProviderId = "other.provider" }, value => value with { ContractId = "other.contract" },
            value => value with { ContractVersion = "1.0.1" }, value => value with { DescriptorSha256 = new string('c', 64) },
            value => value with { AbiSha256 = new string('d', 64) }, value => value with { ActivationId = Guid.NewGuid() },
            value => value with { ActivationEpoch = value.ActivationEpoch + 1 }, value => value with { SessionGeneration = value.SessionGeneration + 1 },
            value => value with { Endpoint = new("other-endpoint") }, value => value with { State = SunderRpcProviderState.Inactive },
        })
        {
            rpc.Transform = change;
            Assert.Equal(SunderRpcErrorKind.StaleEndpoint, (await Assert.ThrowsAsync<SunderRpcException>(() => client.InvokeAsync<bool>("CanMigrateAsync", context, CancellationToken.None).AsTask())).Error.Kind);
            Assert.Equal(SunderRpcErrorKind.StaleEndpoint, (await Assert.ThrowsAsync<SunderRpcException>(() => Complete())).Error.Kind);
        }
        Assert.Equal(0, peer.Completed); rpc.Transform = null;
        rpc.InvalidateAfterInvoke = true;
        Assert.Equal(SunderRpcErrorKind.StaleEndpoint, (await Assert.ThrowsAsync<SunderRpcException>(() => Paths())).Error.Kind);
        rpc.InvalidateAfterInvoke = false; rpc.Transform = null;
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel(); var calls = handler.Calls.Count;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Paths(cancelled.Token));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Complete(cancelled.Token));
            Assert.Equal(calls, handler.Calls.Count);
        }
        handler.BlockMethod = "complete"; handler.Gate = new();
        var pending = Complete();
        await handler.Gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stopped = lifetime.DisposeAsync().AsTask(); Assert.Same(stopped, lifetime.DisposeAsync().AsTask());
        var failure = await Assert.ThrowsAsync<SunderRpcException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(SunderRpcErrorKind.Unavailable, failure.Error.Kind);
        Assert.Equal("rpc.binding.retired", failure.Error.Code);
        await stopped.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(0, peer.Completed);
        var disposed = Assert.Throws<TargetInvocationException>(() => selected.Reference.GetType().GetProperty("Service")!.GetValue(selected.Reference));
        Assert.Equal(SunderRpcErrorKind.Unavailable, Assert.IsType<SunderRpcException>(disposed.InnerException).Error.Kind);
        rpc.AssertScopedOperations(); Assert.All(rpc.Scopes, scope => Assert.True(scope.Disposed));
        Task<AgentWorkspacePathMigrationItem[]> Paths(CancellationToken token = default)
            => client.InvokeAsync<AgentWorkspacePathMigrationItem[]>("ListLegacyPathsAsync", context, token).AsTask();
        Task<object?> Complete(CancellationToken token = default)
            => client.InvokeAsync<object?>("CompleteAsync", context, token).AsTask();
    }

    private sealed class WorkspacePathPeer : IAgentWorkspacePathMigrationContributor
    {
        public string ContributorId => "fixture.workspace.paths";
        internal int Completed;
        internal IReadOnlyList<AgentWorkspacePathMigrationItem> Items { get; } = [new("/workspace/漢字", true, int.MinValue), new("/workspace/two", false, int.MaxValue)];
        public bool CanMigrate(AgentWorkspacePathMigrationContext context) => context.Binding.ContributionId == "local";
        public Task<IReadOnlyList<AgentWorkspacePathMigrationItem>> GetLegacyWorkspacePathsAsync(AgentWorkspacePathMigrationContext context, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(Items); }
        public Task CompleteWorkspacePathMigrationAsync(AgentWorkspacePathMigrationContext context, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); Completed++; return Task.CompletedTask; }
    }
}
