using Xunit;

namespace Sunder.Package.Agent.Tests;

extern alias MemoryWorker;
using MemoryWorker::Sunder.Package.Agent.Contracts.Models;
using MemoryWorker::Sunder.Package.Agent.Memory.Semantic;

public sealed class SemanticMemoryLimitsTests
{
    [Fact]
    public void ListRecallableMemories_BoundsPerSessionResults()
    {
        using var scope = RegressionTestPackageScope.Create();
        var store = new MemoryLocalStore(scope.Context);
        var sessionId = Guid.NewGuid();

        for (var index = 0; index <= MemoryLocalStore.MaxRecallableMemoriesPerSession; index++)
        {
            var content = $"Bounded memory {index}.";
            store.UpsertMemory(new MemoryUpsertRequest(
                sessionId,
                "remembered-fact",
                content,
                content.ToLowerInvariant(),
                "User-provided evidence.",
                Guid.NewGuid(),
                false,
                0.5f,
                0.8f,
                AgentMemoryProvenance.User));
        }

        Assert.Equal(MemoryLocalStore.MaxRecallableMemoriesPerSession, store.ListRecallableMemories(sessionId).Count);
        Assert.Equal(MemoryLocalStore.MaxRecallableMemoriesPerSession + 1, store.ListMemories(sessionId).Count);
    }
}
