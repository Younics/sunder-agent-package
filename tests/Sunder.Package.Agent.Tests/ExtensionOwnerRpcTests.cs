using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.Skills.Protocol;
using Sunder.Package.Agent.Skills.Runtime;
using Sunder.Package.Agent.Skills.Services;
using Sunder.Package.Agent.Subagents.Protocol;
using Sunder.Package.Agent.Subagents.Runtime;
using Sunder.Package.Agent.Subagents.Services;
using Sunder.Sdk.Rpc;
using Xunit;
using SkillsGenerated = Sunder.Package.Agent.Skills.Protocol.Generated;
using SubagentsGenerated = Sunder.Package.Agent.Subagents.Protocol.Generated;

namespace Sunder.Package.Agent.Tests;

public sealed class ExtensionOwnerRpcTests
{
    [Fact]
    public async Task SkillsOwnerRpc_UsesExactProviderForContentBackedUnaryAndStreamingCalls()
    {
        using var scope = RegressionTestPackageScope.Create();
        var store = new SkillStore(scope.Context);
        store.Activate();
        var now = DateTimeOffset.UtcNow;
        store.SaveSkill(new InstalledSkillRecord(
            "rpc-skill",
            $"{SkillConstants.SkillsRelativeRoot}/rpc-skill",
            "RPC Skill",
            null,
            null,
            null,
            "test",
            null,
            null,
            null,
            "hash",
            now,
            now,
            new Dictionary<string, string>(),
            []));
        var importer = new SkillImportService(store, new UnusedGitHubSkillClient(), scope.Context);
        var provider = new SkillOwnerProvider(
            new SkillRuntimeHandler(store, importer, scope.Context),
            new SkillRuntimeChangeStream(store));
        using var state = new RegressionTestRpcState();
        AddSkillsProvider(state, provider, "skills.unrelated");
        AddSkillsProvider(state, provider, SkillsOwnerProtocol.ProviderId);
        var client = new SkillOwnerClient(new RegressionTestRpcClient(state, SkillConstants.PackageId));

        Assert.Equal("rpc-skill", Assert.Single((await client.QueryAsync()).Skills).SkillId);
        await using var changes = client.SubscribeChangesAsync().GetAsyncEnumerator();
        var nextChange = changes.MoveNextAsync().AsTask();

        var projection = await client.CommandAsync(new SkillCommand(SkillCommandKind.Delete, "rpc-skill"));

        Assert.Empty(projection.Skills);
        Assert.True(await nextChange.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Empty((await client.QueryAsync()).Skills);
    }

    [Fact]
    public async Task SubagentsOwnerRpc_UsesExactProviderForContentBackedUnaryAndStreamingCalls()
    {
        using var scope = RegressionTestPackageScope.Create();
        using var catalog = new RegressionTestExtensionCatalog();
        var store = new SubagentStore(scope.Context);
        store.Activate();
        var service = new SubagentService(store);
        using var handler = new SubagentRuntimeHandler(service, catalog);
        using var changes = new SubagentRuntimeChangeStream(service, catalog);
        var provider = new SubagentOwnerProvider(handler, changes);
        using var state = new RegressionTestRpcState();
        AddSubagentsProvider(state, provider, "subagents.unrelated");
        AddSubagentsProvider(state, provider, SubagentsOwnerProtocol.ProviderId);
        var client = new SubagentOwnerClient(new RegressionTestRpcClient(state, "sunder.package.agent.subagents"));

        Assert.Empty((await client.QueryAsync(new(SubagentQueryKind.Management))).Subagents ?? []);
        await using var subscription = client.SubscribeChangesAsync(new()).GetAsyncEnumerator();
        await MoveToSubagentChangeAsync(subscription, SubagentChangeKind.Connected);

        var projection = await client.CommandAsync(new(SubagentCommandKind.Create, "RPC Subagent"));

        Assert.Equal("RPC Subagent", projection.Subagent?.DisplayName);
        await MoveToSubagentChangeAsync(subscription, SubagentChangeKind.Subagents);
        Assert.Equal(
            "RPC Subagent",
            Assert.Single((await client.QueryAsync(new(SubagentQueryKind.Management))).Subagents ?? []).DisplayName);
    }

    [Theory]
    [InlineData(SunderRpcErrorKind.StaleEndpoint, "rpc.endpoint.stale")]
    [InlineData(SunderRpcErrorKind.Cancelled, "rpc.call.cancelled")]
    [InlineData(SunderRpcErrorKind.Unavailable, "rpc.content.authority-revoked")]
    [InlineData(SunderRpcErrorKind.DeadlineExceeded, "rpc.call.deadline-exceeded")]
    public async Task SubagentsOwnerRpc_ContentAuthorityRetirementCompletesAndReplaysFromAfterRevision(
        SunderRpcErrorKind errorKind,
        string errorCode)
    {
        using var boundary = new SubagentOwnerBoundary();
        await using var subscription = boundary.Client.SubscribeChangesAsync(new())
            .GetAsyncEnumerator();
        var connected = await MoveToSubagentChangeAsync(subscription, SubagentChangeKind.Connected);
        boundary.State.FailNextResponseContentRegistration(new SunderRpcException(
            new SunderRpcError(errorKind, errorCode, "Authenticated Host retirement."),
            hostAuthenticated: true));

        boundary.Service.NotifySubagentsImported();

        Assert.False(await subscription.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        await using var replay = boundary.Client.SubscribeChangesAsync(
                new SubagentChangeSubscription(connected.Revision))
            .GetAsyncEnumerator();
        var replayed = await MoveToSubagentChangeAsync(replay, SubagentChangeKind.Subagents);
        Assert.Equal(connected.Revision + 1, replayed.Revision);
        Assert.Empty((await boundary.Client.QueryAsync(new(SubagentQueryKind.Management))).Subagents ?? []);
    }

    [Fact]
    public async Task SubagentsOwnerRpc_StaleContentAuthorityCompletesAndResnapshotsWhenReplayExpired()
    {
        using var boundary = new SubagentOwnerBoundary();
        for (var index = 0; index < 300; index++)
        {
            boundary.Service.NotifySubagentsImported();
        }
        boundary.State.FailNextResponseContentRegistration(new SunderRpcException(
            new SunderRpcError(
                SunderRpcErrorKind.Unavailable,
                "rpc.content.authority-revoked",
                "The invocation content authority retired."),
            hostAuthenticated: true));

        await using (var stale = boundary.Client.SubscribeChangesAsync(new()).GetAsyncEnumerator())
        {
            Assert.False(await stale.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        }

        await using var reconnect = boundary.Client.SubscribeChangesAsync(new()).GetAsyncEnumerator();
        var resnapshot = await MoveToSubagentChangeAsync(
            reconnect,
            SubagentChangeKind.ResnapshotRequired);
        Assert.Equal(boundary.Changes.Revision, resnapshot.Revision);
    }

    [Fact]
    public async Task SubagentsOwnerRpc_OtherContentRegistrationFailuresStillProviderFault()
    {
        const string canary = "subagents-content-registration-secret-canary";
        using var boundary = new SubagentOwnerBoundary();
        boundary.State.FailNextResponseContentRegistration(new InvalidOperationException(canary));
        await using var subscription = boundary.Client.SubscribeChangesAsync(new())
            .GetAsyncEnumerator();

        var exception = await Assert.ThrowsAsync<SunderRpcException>(async () =>
            _ = await subscription.MoveNextAsync());

        Assert.Equal(SunderRpcErrorKind.ProviderFaulted, exception.Error.Kind);
        Assert.Equal("rpc.provider.handler-fault", exception.Error.Code);
        Assert.DoesNotContain(canary, exception.Error.Message, StringComparison.Ordinal);
        Assert.Empty((await boundary.Client.QueryAsync(new(SubagentQueryKind.Management))).Subagents ?? []);
    }

    [Fact]
    public async Task SubagentsChangeStream_IgnoresUnrelatedCatalogTransitions()
    {
        using var boundary = new SubagentOwnerBoundary();
        await using var subscription = boundary.Client.SubscribeChangesAsync(new())
            .GetAsyncEnumerator();
        _ = await MoveToSubagentChangeAsync(subscription, SubagentChangeKind.Connected);
        var revision = boundary.Changes.Revision;
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void CatalogChanged(object? sender, AgentRpcCatalogChangedEventArgs args)
        {
            if (args.IncludesContract(AgentRpcContractIds.SessionCleaner))
            {
                observed.TrySetResult();
            }
        }
        boundary.Catalog.Changed += CatalogChanged;
        try
        {
            boundary.Catalog.AddProvider(
                AgentRpcServices.SessionCleaners,
                new NoOpSessionDataCleaner());
            await observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            boundary.Catalog.Changed -= CatalogChanged;
        }

        Assert.Equal(revision, boundary.Changes.Revision);
    }

    private static async Task<SubagentChanged> MoveToSubagentChangeAsync(
        IAsyncEnumerator<SubagentChanged> subscription,
        SubagentChangeKind expected)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            Assert.True(await subscription.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
            if (subscription.Current.Kind == expected)
            {
                return subscription.Current;
            }
        }

        Assert.Fail($"Subagent change '{expected}' was not observed.");
        throw new InvalidOperationException("Unreachable.");
    }

    private static void AddSkillsProvider(
        RegressionTestRpcState state,
        SkillOwnerProvider provider,
        string providerId)
        => state.Add(
            SkillConstants.PackageId,
            SkillsOwnerProtocol.Descriptor,
            provider,
            new SkillsGenerated.SunderPackageAgentSkillsOwnerRpcHandler(provider),
            new NoOpDisposable(),
            providerId);

    private static void AddSubagentsProvider(
        RegressionTestRpcState state,
        SubagentOwnerProvider provider,
        string providerId)
        => state.Add(
            "sunder.package.agent.subagents",
            SubagentsOwnerProtocol.Descriptor,
            provider,
            new SubagentsGenerated.SunderPackageAgentSubagentsOwnerRpcHandler(provider),
            new NoOpDisposable(),
            providerId);

    private sealed class UnusedGitHubSkillClient : IGitHubSkillClient
    {
        public Task<string?> TryGetDefaultBranchAsync(string owner, string repo, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<GitHubSkillFolder?> TryGetFolderAsync(
            GitHubSkillFolderRequest request,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<GitHubSkillFolder?> TryGetSkillFolderAsync(
            GitHubSkillFolderRequest request,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<GitHubSkillFile>> ListFilesAsync(
            GitHubSkillFolder folder,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<byte[]> ReadFileAsync(
            GitHubSkillFolder folder,
            GitHubSkillFile file,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class NoOpDisposable : IDisposable
    {
        public void Dispose()
        {
        }
    }

    private sealed class SubagentOwnerBoundary : IDisposable
    {
        private readonly RegressionTestPackageScope _scope = RegressionTestPackageScope.Create();
        private readonly RegressionTestExtensionCatalog _catalog = new();
        private readonly SubagentRuntimeHandler _handler;

        public SubagentOwnerBoundary()
        {
            var store = new SubagentStore(_scope.Context);
            store.Activate();
            Service = new SubagentService(store);
            _handler = new SubagentRuntimeHandler(Service, _catalog);
            Changes = new SubagentRuntimeChangeStream(Service, _catalog);
            var provider = new SubagentOwnerProvider(_handler, Changes);
            State = new RegressionTestRpcState(workerStyleProviderBoundary: true);
            AddSubagentsProvider(State, provider, SubagentsOwnerProtocol.ProviderId);
            Client = new SubagentOwnerClient(new RegressionTestRpcClient(
                State,
                "sunder.package.agent.subagents"));
        }

        public SubagentService Service { get; }

        public SubagentRuntimeChangeStream Changes { get; }

        public RegressionTestExtensionCatalog Catalog => _catalog;

        public RegressionTestRpcState State { get; }

        public SubagentOwnerClient Client { get; }

        public void Dispose()
        {
            State.Dispose();
            Changes.Dispose();
            _handler.Dispose();
            _catalog.Dispose();
            _scope.Dispose();
        }
    }

    private sealed class NoOpSessionDataCleaner : IAgentSessionDataCleaner
    {
        public string CleanerId => "test.unrelated.cleaner";

        public void DeleteSessionData(Guid sessionId)
        {
        }
    }
}
