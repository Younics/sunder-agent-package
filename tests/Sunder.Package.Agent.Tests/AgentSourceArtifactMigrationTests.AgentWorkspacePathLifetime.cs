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
        Func<string, bool> canMigrate = value => peer.CanMigrate(JsonSerializer.Deserialize<AgentWorkspacePathMigrationContext>(value, MemoryProviderJson)!);
        Func<string, CancellationToken, Task<string>> paths = async (value, token) => JsonSerializer.Serialize(
            await peer.GetLegacyWorkspacePathsAsync(JsonSerializer.Deserialize<AgentWorkspacePathMigrationContext>(value, MemoryProviderJson)!, token), MemoryProviderJson);
        Func<string, CancellationToken, Task> complete = (value, token) => peer.CompleteWorkspacePathMigrationAsync(
            JsonSerializer.Deserialize<AgentWorkspacePathMigrationContext>(value, MemoryProviderJson)!, token);
        var rolePeer = Activator.CreateInstance(probes.GetType("RoleLocalWorkspaceMigrator", true)!, peer.ContributorId, canMigrate, paths, complete)!;
        var service = Activator.CreateInstance(local.GetType("Sunder.Package.Agent.Execution.Local.Rpc.WorkspacePathMigratorProvider", true)!, rolePeer)!;
        var registration = SurfaceRegistration(service);
        var handler = AddSurfaceProvider(state, "sunder.package.agent.execution.local", registration);
        var rpc = new FilesRpcObserver(new RegressionTestRpcClient(state, "sunder.package.agent"), "list-legacy-paths");
        var snapshot = Assert.Single((await rpc.DiscoverAsync(AgentRpcContractIds.WorkspacePathMigrator)).Providers);
        var clientType = owner.GetType("Sunder.Package.Agent.Runtime.Rpc.GeneratedWorkspacePathMigratorClient", true)!;
        var client = AgentRoleBoundary.As<IAgentWorkspacePathMigrationContributor>(Activator.CreateInstance(clientType, rpc, snapshot)!);
        await using var lifetime = (IAsyncDisposable)client;
        var now = DateTimeOffset.UtcNow;
        var context = new AgentWorkspacePathMigrationContext(new("workspace", "Workspace", null, now, now),
            new("binding", "workspace", AgentRpcContractIds.ExecutionTarget, "local", "primary-execution-target", true, 0, now, now));
        Assert.Equal(peer.ContributorId, client.ContributorId); Assert.True(client.CanMigrate(context));
        Assert.False(client.CanMigrate(context with { Binding = context.Binding with { ContributionId = "other" } }));
        Assert.Equal(peer.Items, await client.GetLegacyWorkspacePathsAsync(context));
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
            Assert.Equal(SunderRpcErrorKind.StaleEndpoint, Assert.Throws<SunderRpcException>(() => client.CanMigrate(context)).Error.Kind);
            Assert.Equal(SunderRpcErrorKind.StaleEndpoint, (await Assert.ThrowsAsync<SunderRpcException>(() => client.CompleteWorkspacePathMigrationAsync(context))).Error.Kind);
        }
        Assert.Equal(0, peer.Completed); rpc.Transform = null;
        rpc.InvalidateAfterInvoke = true;
        Assert.Equal(SunderRpcErrorKind.StaleEndpoint, (await Assert.ThrowsAsync<SunderRpcException>(() => client.GetLegacyWorkspacePathsAsync(context))).Error.Kind);
        rpc.InvalidateAfterInvoke = false; rpc.Transform = null;
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel(); var calls = handler.Calls.Count;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetLegacyWorkspacePathsAsync(context, cancelled.Token));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.CompleteWorkspacePathMigrationAsync(context, cancelled.Token));
            Assert.Equal(calls, handler.Calls.Count);
        }
        handler.BlockMethod = "complete"; handler.Gate = new();
        var pending = client.CompleteWorkspacePathMigrationAsync(context);
        await handler.Gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stopped = lifetime.DisposeAsync().AsTask(); Assert.Same(stopped, lifetime.DisposeAsync().AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await stopped.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(0, peer.Completed);
        Assert.Equal(SunderRpcErrorKind.StaleEndpoint, Assert.Throws<SunderRpcException>(() => client.ContributorId).Error.Kind);
        Assert.Equal(0, rpc.UnscopedLookups); Assert.All(rpc.Scopes, scope => Assert.True(scope.Disposed));
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
