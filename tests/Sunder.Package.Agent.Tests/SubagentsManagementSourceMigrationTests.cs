extern alias SubagentsWorker;
extern alias SubagentsApp;

using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;
using Xunit;
using AppSubagentChangeKind = SubagentsApp::Sunder.Package.Agent.Subagents.Runtime.SubagentChangeKind;
using AppSubagentChanged = SubagentsApp::Sunder.Package.Agent.Subagents.Runtime.SubagentChanged;
using AppSubagentProjection = SubagentsApp::Sunder.Package.Agent.Subagents.Runtime.SubagentProjection;
using AppSubagentProjectionContent = SubagentsApp::Sunder.Package.Agent.Subagents.Runtime.SubagentProjectionContent;
using AppSubagentSaveRequest = SubagentsApp::Sunder.Package.Agent.Subagents.Runtime.SubagentSaveRequest;
using SubagentManagementServices = SubagentsApp::Sunder.Generated.Packages.Younics_Ai_Agent_Subagents_Services;

namespace Sunder.Package.Agent.Tests;

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
        var rpc = new RegressionTestRpcClient(state, "younics.ai.agent.subagents");
        var assembly = typeof(SubagentManagementService).Assembly;
        Assert.Null(assembly.GetType("Sunder.Package.Agent.Subagents.Runtime.SubagentsWorkerComposition"));
        Assert.Null(assembly.GetType("Sunder.Package.Agent.Subagents.Runtime.SubagentsWorkerBindings"));
        var generated = AgentSourceArtifactMigrationTests.CreateGeneratedWorker(assembly, package.Context, rpc);
        await using var lifetime = generated.Lifetime;
        var options = generated.Options;
        Assert.Equal(6, options.Providers.Count);
        Assert.Equal(0, state.DiscoveryCount);
        Assert.False(Directory.Exists(Path.Combine(package.RootPath, "subagents")));
        await AgentSourceArtifactMigrationTests.ActivateGeneratedComposition(options).WaitAsync(TimeSpan.FromSeconds(5));
        // Activation establishes metadata watches; it does not require a catalog snapshot.
        Assert.Equal(0, state.DiscoveryCount);
        Assert.True(Directory.Exists(Path.Combine(package.RootPath, "subagents")));
        await lifetime.DisposeAsync();
    }

    [AgentPublicSourceMigrationFact]
    public async Task ManagementSource_PreservesContractPersistenceReplayAndContentRetirementThroughGeneratedService()
    {
        using var package = RegressionTestPackageScope.Create();
        using var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
        await using var catalog = new RegressionTestExtensionCatalog(new RegressionTestRpcClient(state, "younics.ai.agent"));
        var store = new SubagentStore(package.Context);
        store.Activate();
        var domain = new SubagentService(store);
        await using var management = await catalog.CreateSubagentRuntimeManagementAsync(domain);
        var receiver = catalog.Subagents();
        var registration = receiver.Register(management.Service, "younics.ai.agent.subagents.owner");
        Assert.Equal("subagents.management", registration.ProviderId);
        catalog.AddGeneratedProvider("younics.ai.agent.subagents", management.Service, registration);
        var selected = Assert.Single((await receiver.Providers<SubagentManagementServices.ISubagentsManagementService>().ListAsync()).Providers);
        var client = selected.Service;
        Assert.Empty((await ReadProjectionAsync(client.ManagementAsync())).Subagents!);
        var created = (await ReadProjectionAsync(client.CreateAsync("Source Subagent"))).Subagent!;
        Assert.Equal("Source Subagent", created.DisplayName);
        var request = new AppSubagentSaveRequest(created.SubagentId, " Saved λ ", "Description", "Instructions", null, null, [], null);
        var saved = (await ReadProjectionAsync(client.SaveAsync(request))).Subagent!;
        Assert.Equal("Saved λ", saved.DisplayName);
        Assert.Equal("Description", Assert.Single((await ReadProjectionAsync(client.ManagementAsync())).Subagents!).Description);
        var invalid = await Assert.ThrowsAsync<SunderRpcException>(() => client.SaveAsync(request with { Description = " " }).AsTask());
        Assert.Equal("subagents.owner.operation-failed", invalid.Error.Code);
        Assert.Empty((await ReadProjectionAsync(client.ProviderModelsAsync(" "))).Providers ?? []);
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.DeleteAsync(created.SubagentId, cancelled.Token).AsTask());
            Assert.NotNull(domain.GetSubagent(created.SubagentId));
        }

        foreach (var (kind, code) in new[]
                 {
                     (SunderRpcErrorKind.StaleEndpoint, "rpc.endpoint.stale"),
                     (SunderRpcErrorKind.Cancelled, "rpc.call.cancelled"),
                     (SunderRpcErrorKind.Unavailable, "rpc.content.authority-revoked"),
                     (SunderRpcErrorKind.DeadlineExceeded, "rpc.call.deadline-exceeded"),
                 })
        {
            await using var subscription = client.ChangesAsync(management.Changes.Revision).GetAsyncEnumerator();
            var connected = await Next(subscription, AppSubagentChangeKind.Connected);
            var authorityFailure = new SunderRpcException(
                new SunderRpcError(kind, code, "Retired test content authority."),
                hostAuthenticated: true);
            state.FailNextResponseContentRegistration(authorityFailure);
            domain.NotifySubagentsImported();
            Assert.False(await subscription.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
            await using var replay = client.ChangesAsync(connected.Revision).GetAsyncEnumerator();
            var replayed = await Next(replay, AppSubagentChangeKind.Subagents);
            Assert.Equal(connected.Revision + 1, replayed.Revision);
            Assert.Equal(connected.RuntimeInstanceId, replayed.RuntimeInstanceId);
        }

        for (var index = 0; index < 300; index++) domain.NotifySubagentsImported();
        await using (var expired = client.ChangesAsync(0).GetAsyncEnumerator())
            Assert.Equal(management.Changes.Revision, (await Next(expired, AppSubagentChangeKind.ResnapshotRequired)).Revision);
        await using (var overflow = client.ChangesAsync(management.Changes.Revision).GetAsyncEnumerator())
        {
            _ = await Next(overflow, AppSubagentChangeKind.Connected);
            for (var index = 0; index < 100; index++) domain.NotifySubagentsImported();
            var reset = await Next(overflow, AppSubagentChangeKind.ResnapshotRequired, maximum: 65);
            Assert.Equal(management.Changes.Revision, reset.Revision);
            Assert.False(await overflow.MoveNextAsync());
        }

        state.FailNextResponseContentRegistration(new IOException("private subagents content failure"));
        await using (var faulted = client.ChangesAsync(management.Changes.Revision).GetAsyncEnumerator())
        {
            var fault = await Assert.ThrowsAsync<SunderRpcException>(() => faulted.MoveNextAsync().AsTask());
            Assert.Equal(SunderRpcErrorKind.ProviderFaulted, fault.Error.Kind);
            Assert.DoesNotContain("private subagents content failure", fault.Message, StringComparison.Ordinal);
        }
        Assert.Single((await ReadProjectionAsync(client.ManagementAsync())).Subagents!);
        await using (var deleted = await client.DeleteAsync(created.SubagentId)) { }
        Assert.Empty((await ReadProjectionAsync(client.ManagementAsync())).Subagents!);
        await catalog.RetireProviderAsync(management.Service);
        var stale = await Assert.ThrowsAsync<SunderRpcException>(() => selected.EnsureCurrentAsync().AsTask());
        Assert.True(selected.RetirementToken.IsCancellationRequested);
        Assert.Equal(SunderRpcErrorKind.StaleEndpoint, stale.Error.Kind);
        var retired = await Assert.ThrowsAsync<SunderRpcException>(() => client.ManagementAsync().AsTask());
        Assert.Equal(SunderRpcErrorKind.StaleEndpoint, retired.Error.Kind);
        Assert.Equal("rpc.stale-endpoint", retired.Error.Code);
    }

    private static ValueTask<AppSubagentProjection> ReadProjectionAsync(ValueTask<SunderContent> content)
        => AppSubagentProjectionContent.ReadAsync<AppSubagentProjection>(content, default);

    private static async Task<AppSubagentChanged> Next(IAsyncEnumerator<SunderContent> subscription, AppSubagentChangeKind kind, int maximum = 8)
    {
        for (var index = 0; index < maximum; index++)
        {
            Assert.True(await subscription.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
            var change = await AppSubagentProjectionContent.ReadAsync<AppSubagentChanged>(ValueTask.FromResult(subscription.Current), default);
            if (change.Kind == kind) return change;
        }
        throw new InvalidOperationException($"Expected Subagents change '{kind}' was not observed.");
    }
}
