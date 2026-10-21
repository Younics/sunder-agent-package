extern alias SkillsWorker;
extern alias SkillsApp;
extern alias SubagentsWorker;
extern alias SubagentsApp;

using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Protocol;
using SkillsWorker::Sunder.Package.Agent.Skills.Runtime;
using SkillsWorker::Sunder.Package.Agent.Skills.Services;
using SubagentsWorker::Sunder.Package.Agent.Subagents.Runtime;
using SubagentsWorker::Sunder.Package.Agent.Subagents.Services;
using SubagentsManagementService = SubagentsWorker::Sunder.Package.Agent.Subagents.Runtime.SubagentManagementService;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;
using AppSubagentChangeKind = SubagentsApp::Sunder.Package.Agent.Subagents.Runtime.SubagentChangeKind;
using AppSubagentChanged = SubagentsApp::Sunder.Package.Agent.Subagents.Runtime.SubagentChanged;
using AppSubagentProjection = SubagentsApp::Sunder.Package.Agent.Subagents.Runtime.SubagentProjection;
using AppSubagentProjectionContent = SubagentsApp::Sunder.Package.Agent.Subagents.Runtime.SubagentProjectionContent;
using SkillProjection = SkillsApp::Sunder.Package.Agent.Skills.Runtime.SkillProjection;
using SkillProjectionContent = SkillsApp::Sunder.Package.Agent.Skills.Runtime.SkillProjectionContent;
using SkillsManagementServices = SkillsApp::Sunder.Generated.Packages.Younics_Ai_Agent_Skills_Services;
using SubagentManagementServices = SubagentsApp::Sunder.Generated.Packages.Younics_Ai_Agent_Subagents_Services;
using Xunit;

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
        var provider = new SkillRuntimeHandler(store, importer, scope.Context);
        using var state = new RegressionTestRpcState();
        await using var receiver = new GeneratedServiceTestScope(
            new RegressionTestRpcClient(state, SkillConstants.PackageId));
        AddSkillsProvider(state, provider, "skills.unrelated");
        AddSkillsProvider(state, provider, "skills.management");
        var client = Assert.Single((await receiver.Providers<SkillsManagementServices.ISkillsManagementService>()
            .ListAsync()).Providers, static provider => provider.ProviderId == "skills.management").Service;

        Assert.Equal("rpc-skill", Assert.Single((await ReadSkillProjectionAsync(client.QueryAsync())).Skills).SkillId);
        await using var changes = client.ChangesAsync().GetAsyncEnumerator();
        Assert.True(await changes.MoveNextAsync());
        Assert.Equal(0, changes.Current);
        var nextChange = changes.MoveNextAsync().AsTask();

        await client.DeleteAsync("rpc-skill");

        Assert.True(await nextChange.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, changes.Current);
        Assert.Empty((await ReadSkillProjectionAsync(client.QueryAsync())).Skills);
    }

    [Fact]
    public async Task SubagentsOwnerRpc_UsesExactProviderForContentBackedUnaryAndStreamingCalls()
    {
        using var scope = RegressionTestPackageScope.Create();
        var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
        await using var catalog = new RegressionTestExtensionCatalog(
            new RegressionTestRpcClient(state, "younics.ai.agent.subagents"));
        var store = new SubagentStore(scope.Context);
        store.Activate();
        var service = new SubagentService(store);
        await using var management = await catalog.CreateSubagentRuntimeManagementAsync(service);
        AddSubagentsProvider(state, management.Service, "subagents.unrelated");
        AddSubagentsProvider(state, management.Service, "subagents.management");
        var client = Assert.Single((await catalog.Subagents()
            .Providers<SubagentManagementServices.ISubagentsManagementService>()
            .ListAsync()).Providers, provider => provider.ProviderId == "subagents.management").Service;

        Assert.Empty((await ReadSubagentProjectionAsync(client.ManagementAsync())).Subagents ?? []);
        await using var subscription = client.ChangesAsync(0).GetAsyncEnumerator();
        await MoveToSubagentChangeAsync(subscription, AppSubagentChangeKind.Connected);

        var projection = await ReadSubagentProjectionAsync(client.CreateAsync("RPC Subagent"));

        Assert.Equal("RPC Subagent", projection.Subagent?.DisplayName);
        await MoveToSubagentChangeAsync(subscription, AppSubagentChangeKind.Subagents);
        Assert.Equal(
            "RPC Subagent",
            Assert.Single((await ReadSubagentProjectionAsync(client.ManagementAsync())).Subagents ?? []).DisplayName);
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
        await using var subscription = boundary.Client.ChangesAsync(0)
            .GetAsyncEnumerator();
        var connected = await MoveToSubagentChangeAsync(subscription, AppSubagentChangeKind.Connected);
        var authorityFailure = new SunderRpcException(
            new SunderRpcError(errorKind, errorCode, "Authenticated Host retirement."),
            hostAuthenticated: true);
        boundary.State.FailNextResponseContentRegistration(authorityFailure);

        boundary.Service.NotifySubagentsImported();

        Assert.False(await subscription.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        await using var replay = boundary.Client.ChangesAsync(connected.Revision)
            .GetAsyncEnumerator();
        var replayed = await MoveToSubagentChangeAsync(replay, AppSubagentChangeKind.Subagents);
        Assert.Equal(connected.Revision + 1, replayed.Revision);
        Assert.Empty((await ReadSubagentProjectionAsync(boundary.Client.ManagementAsync())).Subagents ?? []);
    }

    [Fact]
    public async Task SubagentsOwnerRpc_StaleContentAuthorityCompletesAndResnapshotsWhenReplayExpired()
    {
        using var boundary = new SubagentOwnerBoundary();
        for (var index = 0; index < 300; index++)
        {
            boundary.Service.NotifySubagentsImported();
        }
        var authorityFailure = new SunderRpcException(
            new SunderRpcError(
                SunderRpcErrorKind.Unavailable,
                "rpc.content.authority-revoked",
                "The invocation content authority retired."),
            hostAuthenticated: true);
        boundary.State.FailNextResponseContentRegistration(authorityFailure);

        await using (var stale = boundary.Client.ChangesAsync(0).GetAsyncEnumerator())
        {
            Assert.False(await stale.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        }

        await using var reconnect = boundary.Client.ChangesAsync(0).GetAsyncEnumerator();
        var resnapshot = await MoveToSubagentChangeAsync(
            reconnect,
            AppSubagentChangeKind.ResnapshotRequired);
        Assert.Equal(boundary.Changes.Revision, resnapshot.Revision);
    }

    [Fact]
    public async Task SubagentsOwnerRpc_OtherContentRegistrationFailuresStillProviderFault()
    {
        const string canary = "subagents-content-registration-secret-canary";
        using var boundary = new SubagentOwnerBoundary();
        boundary.State.FailNextResponseContentRegistration(new InvalidOperationException(canary));
        await using var subscription = boundary.Client.ChangesAsync(0)
            .GetAsyncEnumerator();

        var exception = await Assert.ThrowsAsync<SunderRpcException>(async () =>
            _ = await subscription.MoveNextAsync());

        Assert.Equal(SunderRpcErrorKind.ProviderFaulted, exception.Error.Kind);
        Assert.Equal("rpc.provider.handler-fault", exception.Error.Code);
        Assert.DoesNotContain(canary, exception.Error.Message, StringComparison.Ordinal);
        Assert.Empty((await ReadSubagentProjectionAsync(boundary.Client.ManagementAsync())).Subagents ?? []);
    }

    [Fact]
    public async Task SubagentsChangeStream_IgnoresUnrelatedCatalogTransitions()
    {
        using var boundary = new SubagentOwnerBoundary();
        await using var subscription = boundary.Client.ChangesAsync(0)
            .GetAsyncEnumerator();
        _ = await MoveToSubagentChangeAsync(subscription, AppSubagentChangeKind.Connected);
        var revision = boundary.Changes.Revision;
        boundary.Catalog.AddProvider(
            RegressionTestBindings.SessionCleaners,
            new NoOpSessionDataCleaner());

        Assert.Equal(revision, boundary.Changes.Revision);
    }

    private static async Task<AppSubagentChanged> MoveToSubagentChangeAsync(
        IAsyncEnumerator<SunderContent> subscription,
        AppSubagentChangeKind expected)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            Assert.True(await subscription.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
            var change = await AppSubagentProjectionContent.ReadAsync<AppSubagentChanged>(
                ValueTask.FromResult(subscription.Current),
                CancellationToken.None);
            if (change.Kind == expected)
            {
                return change;
            }
        }

        Assert.Fail($"Subagent change '{expected}' was not observed.");
        throw new InvalidOperationException("Unreachable.");
    }

    private static void AddSkillsProvider(
        RegressionTestRpcState state,
        SkillRuntimeHandler provider,
        string providerId)
        => state.AddGenerated(
            SkillConstants.PackageId,
            provider,
            provider.CreateSunderRpcProviderRegistration(),
            new NoOpDisposable(),
            providerId);

    private static ValueTask<SkillProjection> ReadSkillProjectionAsync(ValueTask<SunderContent> content)
        => SkillProjectionContent.ReadAsync(content, CancellationToken.None);

    private static void AddSubagentsProvider(
        RegressionTestRpcState state,
        SubagentsManagementService provider,
        string providerId)
        => state.AddGenerated(
            "younics.ai.agent.subagents",
            provider,
            provider.CreateSunderRpcProviderRegistration(),
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
        private readonly RegressionTestExtensionCatalog _catalog;
        private readonly RegressionSubagentManagement _management;

        public SubagentOwnerBoundary()
        {
            State = new RegressionTestRpcState(workerStyleProviderBoundary: true);
            _catalog = new RegressionTestExtensionCatalog(
                new RegressionTestRpcClient(State, "younics.ai.agent.subagents"));
            var store = new SubagentStore(_scope.Context);
            store.Activate();
            Service = new SubagentService(store);
            _management = _catalog.CreateSubagentRuntimeManagementAsync(Service).AsTask().GetAwaiter().GetResult();
            Changes = _management.Changes;
            AddSubagentsProvider(State, _management.Service, "subagents.management");
            Client = Assert.Single(_catalog.Subagents()
                .Providers<SubagentManagementServices.ISubagentsManagementService>()
                .ListAsync().AsTask().GetAwaiter().GetResult().Providers).Service;
        }

        public SubagentService Service { get; }

        public SubagentRuntimeChangeStream Changes { get; }

        public RegressionTestExtensionCatalog Catalog => _catalog;

        public RegressionTestRpcState State { get; }

        public SubagentManagementServices.ISubagentsManagementService Client { get; }

        public void Dispose()
        {
            _management.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _catalog.Dispose();
            _scope.Dispose();
        }
    }

    private static ValueTask<AppSubagentProjection> ReadSubagentProjectionAsync(
        ValueTask<SunderContent> content)
        => AppSubagentProjectionContent.ReadAsync<AppSubagentProjection>(content, CancellationToken.None);

    private sealed class NoOpSessionDataCleaner : IAgentSessionDataCleaner
    {
        public string CleanerId => "test.unrelated.cleaner";

        public void DeleteSessionData(Guid sessionId)
        {
        }
    }
}
