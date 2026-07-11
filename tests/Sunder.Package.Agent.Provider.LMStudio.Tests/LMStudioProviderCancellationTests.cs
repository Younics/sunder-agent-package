using Sunder.Package.Agent.Provider.TestSupport;
using Xunit;

namespace Sunder.Package.Agent.Provider.LMStudio.Tests;

public sealed class LMStudioProviderCancellationTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CatalogAndReadiness_PropagateCancellation(bool embeddings, bool readiness)
    {
        var context = new ProviderTestPackageContext(
            "sunder.package.agent.provider.lmstudio",
            new Dictionary<string, string>
            {
                [LMStudioProviderConfiguration.BaseUrlKey] = "http://lmstudio.test/v1",
            });
        var handler = new LMStudioTestHttpHandler(async (_, _, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        });
        using var connection = new LMStudioConnection(context, handler);
        var catalog = new LMStudioModelCatalogService(connection);
        var chatProvider = new LMStudioAgentProvider(context, connection, catalog);
        var embeddingProvider = new LMStudioEmbeddingProvider(context, connection, catalog);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        Func<Task> operation = (embeddings, readiness) switch
        {
            (false, false) => () => chatProvider.GetAvailableModelsAsync(cancellation.Token).AsTask(),
            (false, true) => () => chatProvider.GetReadinessAsync(cancellation.Token).AsTask(),
            (true, false) => () => embeddingProvider.GetAvailableModelsAsync(cancellation.Token).AsTask(),
            (true, true) => () => embeddingProvider.GetReadinessAsync(cancellation.Token).AsTask(),
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(operation);
    }
}
