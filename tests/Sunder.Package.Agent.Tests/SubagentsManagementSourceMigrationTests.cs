extern alias SubagentsWorker;

using System.Reflection;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Worker;
using Xunit;
using SubagentRuntimeOwnerClient = SubagentsWorker::Sunder.Package.Agent.Subagents.Runtime.SubagentRuntimeOwnerClient;

namespace Sunder.Package.Agent.Tests;

extern alias SubagentsWorker;
using SubagentsWorker::Sunder.Package.Agent.Subagents.Runtime;
using SubagentsWorker::Sunder.Package.Agent.Subagents.Services;

public sealed class SubagentsManagementSourceMigrationTests
{
    internal static readonly string[] SourcePaths =
    [
        "src/Sunder.Package.Agent.Subagents/Rpc/ManagementService.cs",
        "src/Sunder.Package.Agent.Subagents/Rpc/ManagementClient.cs",
        "src/Sunder.Package.Agent.Subagents/Runtime/SubagentRuntimeOwnerClient.cs",
    ];

    [Fact]
    public async Task WorkerComposition_DefersRuntimeSubscriptionsUntilActivation()
    {
        using var package = RegressionTestPackageScope.Create();
        using var state = new RegressionTestRpcState();
        var rpc = new RegressionTestRpcClient(state, "sunder.package.agent.subagents");
        var assembly = typeof(SubagentRuntimeOwnerClient).Assembly;
        Assert.Null(assembly.GetType("Sunder.Package.Agent.Subagents.Runtime.SubagentsWorkerComposition"));
        Assert.Null(assembly.GetType("Sunder.Package.Agent.Subagents.Runtime.SubagentsWorkerBindings"));
        var generated = AgentSourceArtifactMigrationTests.CreateGeneratedWorker(assembly, package.Context, rpc);
        await using var lifetime = generated.Lifetime;
        var options = generated.Options;
        Assert.Equal(6, options.Providers.Count);
        Assert.Equal(0, state.DiscoveryCount);
        Assert.False(Directory.Exists(Path.Combine(package.RootPath, "subagents")));
        await AgentSourceArtifactMigrationTests.ActivateGeneratedComposition(options).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(state.DiscoveryCount > 0);
        Assert.True(Directory.Exists(Path.Combine(package.RootPath, "subagents")));
        await lifetime.DisposeAsync();
    }

    [AgentPublicSourceMigrationFact]
    public async Task ManagementSource_PreservesContractPersistenceReplayAndContentRetirementAcrossIsolatedApp()
    {
        using var source = PrivateManagementSourceFixture.Load("Subagents", RegressionTestRpcDescriptors.Get("sunder.package.agent.subagents.owner"));
        using var package = RegressionTestPackageScope.Create();
        using var catalog = new RegressionTestExtensionCatalog();
        var store = new SubagentStore(package.Context);
        store.Activate();
        var domain = new SubagentService(store);
        using var handler = new SubagentRuntimeHandler(domain, catalog.Subagents());
        using var changes = new SubagentRuntimeChangeStream(domain, catalog.Subagents());
        var operations = new SubagentRuntimeOwnerClient(handler, changes);
        var type = source.Worker.GetType("Sunder.Package.Agent.Subagents.Rpc.ManagementService", true)!;
        var service = AgentRoleBoundary.Create(type, operations);
        var registration = Assert.IsType<SunderWorkerProviderRegistration>(type.GetMethod("CreateSunderRpcProviderRegistration")!.Invoke(service, null));
        Assert.Equal("subagents.management", registration.ProviderId);
        using var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
        state.Add("sunder.package.agent.subagents", source.Descriptor, service, registration.Handler, Noop.Instance, registration.ProviderId, "2.0.0");
        var client = AgentRoleBoundary.As<ISubagentOwnerClient>(AgentRoleBoundary.Create(source.App.GetType("Sunder.Package.Agent.Subagents.Runtime.SubagentOwnerClient", true)!,
            new RegressionTestRpcClient(state, "sunder.package.agent.subagents")));
        Assert.Empty((await client.QueryAsync(new(SubagentQueryKind.Management))).Subagents!);
        var created = (await client.CommandAsync(new(SubagentCommandKind.Create, "Source Subagent"))).Subagent!;
        Assert.Equal("Source Subagent", created.DisplayName);
        var request = new SubagentSaveRequest(created.SubagentId, " Saved λ ", "Description", "Instructions", null, null, [], null);
        var saved = (await client.CommandAsync(new(SubagentCommandKind.Save, Save: request))).Subagent!;
        Assert.Equal("Saved λ", saved.DisplayName);
        Assert.Equal("Description", Assert.Single((await client.QueryAsync(new(SubagentQueryKind.Management))).Subagents!).Description);
        var invalid = await Assert.ThrowsAsync<SunderRpcException>(() => client.CommandAsync(new(SubagentCommandKind.Save, Save: request with { Description = " " })).AsTask());
        Assert.Equal("subagents.owner.operation-failed", invalid.Error.Code);
        var unknown = await Assert.ThrowsAsync<SunderRpcException>(() => client.QueryAsync(new((SubagentQueryKind)999)).AsTask());
        Assert.Equal("subagents.owner.operation-failed", unknown.Error.Code);
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.CommandAsync(new(SubagentCommandKind.Delete, created.SubagentId), cancelled.Token).AsTask());
            Assert.NotNull(domain.GetSubagent(created.SubagentId));
        }

        foreach (var kind in new[] { SunderRpcErrorKind.StaleEndpoint, SunderRpcErrorKind.Cancelled, SunderRpcErrorKind.Unavailable, SunderRpcErrorKind.DeadlineExceeded })
        {
            await using var subscription = client.SubscribeChangesAsync(new(changes.Revision)).GetAsyncEnumerator();
            var connected = await Next(subscription, SubagentChangeKind.Connected);
            state.FailNextResponseContentRegistration(new SunderRpcException(new(kind, "rpc.content.authority-retired", "Retired test content authority."), hostAuthenticated: true));
            domain.NotifySubagentsImported();
            Assert.False(await subscription.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
            await using var replay = client.SubscribeChangesAsync(new(connected.Revision)).GetAsyncEnumerator();
            var replayed = await Next(replay, SubagentChangeKind.Subagents);
            Assert.Equal(connected.Revision + 1, replayed.Revision);
            Assert.Equal(connected.RuntimeInstanceId, replayed.RuntimeInstanceId);
        }

        for (var index = 0; index < 300; index++) domain.NotifySubagentsImported();
        await using (var expired = client.SubscribeChangesAsync(new()).GetAsyncEnumerator())
            Assert.Equal(changes.Revision, (await Next(expired, SubagentChangeKind.ResnapshotRequired)).Revision);
        await using (var overflow = client.SubscribeChangesAsync(new(changes.Revision)).GetAsyncEnumerator())
        {
            _ = await Next(overflow, SubagentChangeKind.Connected);
            for (var index = 0; index < 100; index++) domain.NotifySubagentsImported();
            var reset = await Next(overflow, SubagentChangeKind.ResnapshotRequired, maximum: 65);
            Assert.Equal(changes.Revision, reset.Revision);
            Assert.False(await overflow.MoveNextAsync());
        }

        state.FailNextResponseContentRegistration(new IOException("private subagents content failure"));
        await using (var faulted = client.SubscribeChangesAsync(new(changes.Revision)).GetAsyncEnumerator())
        {
            var fault = await Assert.ThrowsAsync<SunderRpcException>(() => faulted.MoveNextAsync().AsTask());
            Assert.Equal(SunderRpcErrorKind.ProviderFaulted, fault.Error.Kind);
            Assert.DoesNotContain("private subagents content failure", fault.Message, StringComparison.Ordinal);
        }
        Assert.Single((await client.QueryAsync(new(SubagentQueryKind.Management))).Subagents!);
        await client.CommandAsync(new(SubagentCommandKind.Delete, created.SubagentId));
        Assert.Empty((await client.QueryAsync(new(SubagentQueryKind.Management))).Subagents!);
        await state.RetireAsync(service);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.QueryAsync(new(SubagentQueryKind.Management)).AsTask());
    }

    private static async Task<SubagentChanged> Next(IAsyncEnumerator<SubagentChanged> subscription, SubagentChangeKind kind, int maximum = 8)
    {
        for (var index = 0; index < maximum; index++)
        {
            Assert.True(await subscription.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
            if (subscription.Current.Kind == kind) return subscription.Current;
        }
        throw new InvalidOperationException($"Expected Subagents change '{kind}' was not observed.");
    }

    private sealed class Noop : IDisposable { internal static Noop Instance { get; } = new(); public void Dispose() { } }
}
