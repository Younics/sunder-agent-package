using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Memory.Semantic.Services;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.Tests;
using Xunit;

namespace Sunder.Package.Agent.Memory.Semantic.Tests;

public sealed class SemanticModelRuntimeResolverTests
{
    [Fact]
    public async Task ResolveForProfile_TracksProviderRemovalAndReplacement()
    {
        using var catalog = new RegressionTestExtensionCatalog();
        catalog.AddProvider(AgentRpcServices.RuntimeCatalogs, new TestRuntimeCatalog());
        var original = new TestEmbeddingProvider("original");
        var replacement = new TestEmbeddingProvider("replacement");
        catalog.AddProvider(AgentRpcServices.EmbeddingProviders, original);
        using var resolver = new SemanticModelRuntimeResolver(catalog, null!);

        var first = await resolver.ResolveForProfileAsync("profile-1");
        await catalog.RetireProviderAsync(original);
        var removed = await resolver.ResolveForProfileAsync("profile-1");
        catalog.AddProvider(AgentRpcServices.EmbeddingProviders, replacement);
        var reinstalled = await resolver.ResolveForProfileAsync("profile-1");

        Assert.Equal("original", first?.ProviderDisplayName);
        Assert.Equal(1, original.ReadinessCallCount);
        Assert.Null(removed);
        Assert.Equal("replacement", reinstalled?.ProviderDisplayName);
        Assert.Equal(1, replacement.ReadinessCallCount);
    }

    private sealed class TestEmbeddingProvider(string instanceName) : IAgentEmbeddingProvider
    {
        public AgentEmbeddingProviderDescriptor Descriptor { get; } = new("embedding", instanceName, []);
        public int ReadinessCallCount { get; private set; }

        public ValueTask<IReadOnlyList<AgentEmbeddingModelDescriptor>> GetAvailableModelsAsync(
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<AgentEmbeddingModelDescriptor>>([]);

        public ValueTask<AgentEmbeddingProviderReadiness> GetReadinessAsync(
            CancellationToken cancellationToken = default)
        {
            ReadinessCallCount++;
            return ValueTask.FromResult(new AgentEmbeddingProviderReadiness(
                Descriptor.ProviderId,
                AgentProviderReadinessStatus.Ready,
                "Ready."));
        }

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
