using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Memory.Semantic.Services;
using Sunder.Sdk.Abstractions;
using Xunit;

namespace Sunder.Package.Agent.Memory.Semantic.Tests;

public sealed class SemanticModelRuntimeResolverTests
{
    [Fact]
    public async Task ResolveForProfile_TracksProviderRemovalAndReplacement()
    {
        var catalog = new MutableExtensionCatalog();
        catalog.RuntimeCatalogs.Add(new TestRuntimeCatalog());
        var original = new TestEmbeddingProvider("original");
        var replacement = new TestEmbeddingProvider("replacement");
        catalog.EmbeddingProviders.Add(original);
        var resolver = new SemanticModelRuntimeResolver(catalog, null!);

        var first = await resolver.ResolveForProfileAsync("profile-1");
        catalog.EmbeddingProviders.Clear();
        var removed = await resolver.ResolveForProfileAsync("profile-1");
        catalog.EmbeddingProviders.Add(replacement);
        var reinstalled = await resolver.ResolveForProfileAsync("profile-1");

        Assert.Same(original, first?.Provider);
        Assert.Null(removed);
        Assert.Same(replacement, reinstalled?.Provider);
    }

    private sealed class MutableExtensionCatalog : IPackageExtensionCatalog
    {
        public List<IAgentRuntimeCatalog> RuntimeCatalogs { get; } = [];
        public List<IAgentEmbeddingProvider> EmbeddingProviders { get; } = [];

        public IReadOnlyList<T> GetExtensions<T>(PackageExtensionPoint<T> extensionPoint)
        {
            if (extensionPoint.Id == PackageExtensionPoints.RuntimeCatalogs.Id)
            {
                return RuntimeCatalogs.Cast<T>().ToArray();
            }

            return extensionPoint.Id == PackageExtensionPoints.EmbeddingProviders.Id
                ? EmbeddingProviders.Cast<T>().ToArray()
                : [];
        }

        public IReadOnlyList<PackageExtensionContribution<T>> GetExtensionContributions<T>(PackageExtensionPoint<T> extensionPoint)
            => GetExtensions(extensionPoint)
                .Select(extension => new PackageExtensionContribution<T>("test.package", extension))
                .ToArray();
    }

    private sealed class TestEmbeddingProvider(string instanceName) : IAgentEmbeddingProvider
    {
        public AgentEmbeddingProviderDescriptor Descriptor { get; } = new("embedding", instanceName, []);

        public ValueTask<IReadOnlyList<AgentEmbeddingModelDescriptor>> GetAvailableModelsAsync(
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<AgentEmbeddingModelDescriptor>>([]);

        public ValueTask<AgentEmbeddingProviderReadiness> GetReadinessAsync(
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentEmbeddingProviderReadiness(
                Descriptor.ProviderId,
                AgentProviderReadinessStatus.Ready,
                "Ready."));

        public ValueTask<AgentEmbeddingGenerationResult?> GenerateEmbeddingAsync(
            string modelId,
            string text,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<AgentEmbeddingGenerationResult?>(null);

        public ValueTask<IReadOnlyList<AgentEmbeddingGenerationResult?>> GenerateEmbeddingsAsync(
            string modelId,
            IReadOnlyList<string> texts,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<AgentEmbeddingGenerationResult?>>([]);
    }

    private sealed class TestRuntimeCatalog : IAgentRuntimeCatalog
    {
        public event Action<Guid>? SessionChanged { add { } remove { } }
        public event Action<Guid, AgentTurnRecord>? TurnChanged { add { } remove { } }
        public event Action<string>? ProfileChanged { add { } remove { } }

        public IReadOnlyList<AgentSessionRecord> ListSessions() => [];
        public IReadOnlyList<AgentSessionRecord> ListSessionsForProfile(string profileId) => [];
        public IReadOnlyList<AgentSessionRecord> ListSessionsForWorkspace(string workspaceId) => [];
        public AgentSessionRecord? GetSession(Guid sessionId) => null;
        public IReadOnlyList<AgentWorkspaceRecord> ListWorkspaces() => [];
        public AgentWorkspaceRecord? GetWorkspace(string workspaceId) => null;
        public AgentProfileRecord? GetSessionProfile(Guid sessionId) => null;
        public AgentWorkingSummaryRecord? GetWorkingSummary(Guid sessionId) => null;
        public AgentSessionContextCheckpointRecord? GetLatestSessionContextCheckpoint(Guid sessionId) => null;
        public AgentRunCheckpointRecord? GetLatestCheckpoint(Guid sessionId) => null;
        public IReadOnlyList<AgentTurnRecord> ListRecentTurns(Guid sessionId, int limit) => [];
        public IReadOnlyList<AgentTurnRecord> ListTurnsBefore(
            Guid sessionId,
            DateTimeOffset beforeCreatedAtUtc,
            Guid beforeTurnId,
            int limit) => [];
        public IReadOnlyList<AgentTurnRecord> ListTurnsAfter(
            Guid sessionId,
            DateTimeOffset afterCreatedAtUtc,
            Guid afterTurnId,
            int limit) => [];
        public IReadOnlyList<AgentProfileRecord> ListProfiles() => [];
        public AgentProfileRecord? GetProfile(string profileId) => null;
        public AgentProfileModelBindingRecord? GetSessionModelBinding(Guid sessionId, string capabilityKind) => null;
        public AgentProfileModelBindingRecord? GetModelBinding(string profileId, string capabilityKind)
            => profileId == "profile-1" && capabilityKind == AgentModelCapabilityKinds.Embedding
                ? new AgentProfileModelBindingRecord(
                    profileId,
                    capabilityKind,
                    "embedding",
                    "embedding/model",
                    null,
                    DateTimeOffset.UtcNow)
                : null;
    }
}
