using Sunder.Package.Agent.Skills.Protocol;
using Sunder.Package.Agent.Skills.Runtime;
using Sunder.Package.Agent.Skills.Services;
using Sunder.Package.Agent.Subagents.Protocol;
using Sunder.Package.Agent.Subagents.Runtime;
using Sunder.Package.Agent.Subagents.Services;
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

    private static async Task MoveToSubagentChangeAsync(
        IAsyncEnumerator<SubagentChanged> subscription,
        SubagentChangeKind expected)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            Assert.True(await subscription.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
            if (subscription.Current.Kind == expected)
            {
                return;
            }
        }

        Assert.Fail($"Subagent change '{expected}' was not observed.");
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
}
