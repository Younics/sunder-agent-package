extern alias MemoryWorker;
extern alias SkillsWorker;

using System.Runtime.CompilerServices;
using System.Text.Json;
using MemoryWorker::Sunder.Package.Agent.Memory.Semantic.Services;
using MemoryWorker::Sunder.Package.Agent.Memory.Semantic;
using SkillsWorker::Sunder.Package.Agent.Skills.Services;
using Sunder.Package.Agent.Tests;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Rpc;
using IEmbeddingProviderService = MemoryWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Services.CYounics_Ai_Agent_Embedding_Provider.IEmbeddingProviderService;
using IRuntimeCatalogService = MemoryWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Services.CYounics_Ai_Agent_Runtime_Catalog.IRuntimeCatalogService;

namespace Sunder.Package.Agent.Presentation.Tests;

internal static class RenderedExtensionFixture
{
    internal static MemoryInspectorService CreateMemoryInspector(
        IPackageContext context,
        RegressionTestExtensionCatalog extensionCatalog)
    {
        var store = new MemoryLocalStore(context);
        var settings = new MemorySemanticSettingsService(context);
        var metrics = new SemanticMemoryMetricsService();
        var resolver = new SemanticModelRuntimeResolver(
            extensionCatalog.Memory().Providers<IRuntimeCatalogService>(),
            extensionCatalog.Memory().Providers<IEmbeddingProviderService>(),
            settings);
        var retrieval = new SemanticMemoryRetrievalBackend(store, resolver, settings);
        var worker = new SemanticMemoryIndexingBackgroundService(store, resolver, settings, retrieval, metrics);
        return new MemoryInspectorService(store, retrieval, worker, resolver, metrics);
    }

    internal sealed class NoOpGitHubSkillClient : IGitHubSkillClient
    {
        public Task<string?> TryGetDefaultBranchAsync(
            string owner,
            string repo,
            CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);

        public Task<GitHubSkillFolder?> TryGetFolderAsync(
            GitHubSkillFolderRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult<GitHubSkillFolder?>(null);

        public Task<GitHubSkillFolder?> TryGetSkillFolderAsync(
            GitHubSkillFolderRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult<GitHubSkillFolder?>(null);

        public Task<IReadOnlyList<GitHubSkillFile>> ListFilesAsync(
            GitHubSkillFolder folder,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<GitHubSkillFile>>([]);

        public Task<byte[]> ReadFileAsync(
            GitHubSkillFolder folder,
            GitHubSkillFile file,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Array.Empty<byte>());
    }

    internal sealed class ThrowingRpcClient : ISunderRpcClient
    {
        public ValueTask<SunderRpcProviderSnapshot?> GetProviderAsync(
            SunderRpcEndpointReference endpoint,
            CancellationToken cancellationToken = default)
            => ValueTask.FromException<SunderRpcProviderSnapshot?>(Failure());

        public ValueTask<bool> TryReportInvariantViolationAsync(
            SunderRpcEndpointReference endpoint,
            Exception exception,
            CancellationToken cancellationToken = default)
            => ValueTask.FromException<bool>(Failure());

        public ValueTask<SunderRpcCatalogSnapshot> DiscoverAsync(
            string contractId,
            CancellationToken cancellationToken = default)
            => ValueTask.FromException<SunderRpcCatalogSnapshot>(Failure());

        public async IAsyncEnumerable<SunderRpcCatalogEvent> WatchAsync(
            long afterRevision,
            long afterSequence,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }

        public ValueTask<JsonElement> InvokeAsync(
            SunderRpcEndpointReference endpoint,
            string serviceId,
            string methodId,
            JsonElement request,
            SunderRpcCallOptions? options = null,
            CancellationToken cancellationToken = default)
            => ValueTask.FromException<JsonElement>(Failure());

        public async IAsyncEnumerable<JsonElement> SubscribeAsync(
            SunderRpcEndpointReference endpoint,
            string serviceId,
            string methodId,
            JsonElement request,
            SunderRpcCallOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            if (!cancellationToken.IsCancellationRequested)
            {
                throw Failure();
            }
            yield break;
        }

        private static InvalidOperationException Failure()
            => new("Injected catalog failure.");
    }
}
