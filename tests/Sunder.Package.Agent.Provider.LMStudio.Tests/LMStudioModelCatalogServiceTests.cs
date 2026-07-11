using System.Net;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Provider.TestSupport;
using Xunit;

namespace Sunder.Package.Agent.Provider.LMStudio.Tests;

public sealed class LMStudioModelCatalogServiceTests
{
    [Fact]
    public async Task Catalog_DistinguishesValidEmptyFromFailure()
    {
        var emptyHandler = new LMStudioTestHttpHandler((_, _, _) => LMStudioTestHttpHandler.Json("{\"data\":[]}"));
        using var emptyConnection = new LMStudioConnection(CreateContext(), emptyHandler);
        var empty = await new LMStudioModelCatalogService(emptyConnection).GetCatalogAsync();

        var failureHandler = new LMStudioTestHttpHandler((_, _, _) =>
            LMStudioTestHttpHandler.Json("{\"error\":\"offline\"}", HttpStatusCode.ServiceUnavailable));
        using var failureConnection = new LMStudioConnection(CreateContext(), failureHandler);
        var failure = await new LMStudioModelCatalogService(failureConnection).GetCatalogAsync();

        Assert.True(empty.IsSuccess);
        Assert.Empty(empty.Models);
        Assert.Null(empty.Failure);
        Assert.False(failure.IsSuccess);
        Assert.Empty(failure.Models);
        Assert.Equal(LMStudioCatalogFailureKind.Http, failure.Failure?.Kind);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failure.Failure?.StatusCode);
    }

    [Fact]
    public async Task Catalog_RefreshesAfterShortTtl()
    {
        var timeProvider = new MutableTimeProvider(new DateTimeOffset(2026, 7, 10, 0, 0, 0, TimeSpan.Zero));
        var handler = new LMStudioTestHttpHandler((_, requestNumber, _) => LMStudioTestHttpHandler.Json(
            $"{{\"data\":[{{\"id\":\"model-{requestNumber}\"}}]}}"));
        using var connection = new LMStudioConnection(CreateContext(), handler);
        var catalog = new LMStudioModelCatalogService(connection, TimeSpan.FromSeconds(10), timeProvider);

        var first = await catalog.GetCatalogAsync();
        var cached = await catalog.GetCatalogAsync();
        timeProvider.Advance(TimeSpan.FromSeconds(11));
        var refreshed = await catalog.GetCatalogAsync();

        Assert.Equal("model-1", Assert.Single(first.Models).Id);
        Assert.Equal("model-1", Assert.Single(cached.Models).Id);
        Assert.Equal("model-2", Assert.Single(refreshed.Models).Id);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task ConcurrentModelAndReadinessCalls_ShareColdFetchAndCancelWaitersIndependently()
    {
        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new LMStudioTestHttpHandler(async (_, _, cancellationToken) =>
        {
            requestStarted.TrySetResult();
            return await response.Task.WaitAsync(cancellationToken);
        });
        var context = CreateContext();
        using var connection = new LMStudioConnection(context, handler);
        var catalog = new LMStudioModelCatalogService(connection);
        using var provider = new LMStudioAgentProvider(context, connection, catalog);
        using var canceledWaiter = new CancellationTokenSource();

        var modelsTask = provider.GetAvailableModelsAsync(canceledWaiter.Token).AsTask();
        await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var readinessTask = provider.GetReadinessAsync().AsTask();
        canceledWaiter.Cancel();

        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => modelsTask);
        }
        finally
        {
            response.TrySetResult(await LMStudioTestHttpHandler.Json(
                "{\"data\":[{\"id\":\"loaded-model\",\"type\":\"llm\"}]}"));
        }

        var readiness = await readinessTask;

        Assert.Equal(AgentProviderReadinessStatus.Ready, readiness.Status);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Catalog_AllCanceledWaitersCancelSharedFetchAndAllowRetry()
    {
        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requestCanceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new LMStudioTestHttpHandler(async (_, requestNumber, cancellationToken) =>
        {
            if (requestNumber > 1)
            {
                return await LMStudioTestHttpHandler.Json("{\"data\":[{\"id\":\"retry-model\"}]}");
            }

            requestStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("The canceled request unexpectedly completed.");
            }
            finally
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    requestCanceled.TrySetResult();
                }
            }
        });
        using var connection = new LMStudioConnection(CreateContext(), handler);
        var catalog = new LMStudioModelCatalogService(connection);
        using var firstCancellation = new CancellationTokenSource();
        using var secondCancellation = new CancellationTokenSource();

        var first = catalog.GetCatalogAsync(firstCancellation.Token).AsTask();
        await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = catalog.GetCatalogAsync(secondCancellation.Token).AsTask();
        firstCancellation.Cancel();
        secondCancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        await requestCanceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var retry = await catalog.GetCatalogAsync();

        Assert.Equal("retry-model", Assert.Single(retry.Models).Id);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Catalog_KeyChurnSharesOriginalKeyAndRejectsStaleCompletion()
    {
        var context = CreateContext("http://a.test/v1");
        var firstAStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var aResponse = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bResponse = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var aRequests = 0;
        var handler = new LMStudioTestHttpHandler(async (request, _, cancellationToken) =>
        {
            if (request.Uri.Host == "a.test")
            {
                if (Interlocked.Increment(ref aRequests) > 1)
                {
                    return await LMStudioTestHttpHandler.Json("{\"data\":[{\"id\":\"unexpected-a\"}]}");
                }

                firstAStarted.TrySetResult();
                return await aResponse.Task.WaitAsync(cancellationToken);
            }

            bStarted.TrySetResult();
            return await bResponse.Task.WaitAsync(cancellationToken);
        });
        using var connection = new LMStudioConnection(context, handler);
        var catalog = new LMStudioModelCatalogService(connection);

        var firstA = catalog.GetCatalogAsync().AsTask();
        await firstAStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await context.Storage.State.SetValueAsync(LMStudioProviderConfiguration.BaseUrlKey, "http://b.test/v1");
        var b = catalog.GetCatalogAsync().AsTask();
        await bStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await context.Storage.State.SetValueAsync(LMStudioProviderConfiguration.BaseUrlKey, "http://a.test/v1");
        var secondA = catalog.GetCatalogAsync().AsTask();

        aResponse.SetResult(await LMStudioTestHttpHandler.Json("{\"data\":[{\"id\":\"model-a\"}]}"));
        Assert.Equal("model-a", Assert.Single((await firstA).Models).Id);
        Assert.Equal("model-a", Assert.Single((await secondA).Models).Id);

        bResponse.SetResult(await LMStudioTestHttpHandler.Json("{\"data\":[{\"id\":\"model-b\"}]}"));
        Assert.Equal("model-b", Assert.Single((await b).Models).Id);
        Assert.Equal("model-a", Assert.Single((await catalog.GetCatalogAsync()).Models).Id);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(1, aRequests);
    }

    [Fact]
    public async Task Classification_UsesExplicitCapabilitiesAndConservativeEmbeddingIdSignals()
    {
        const string payload = """
            {
              "data": [
                { "id": "chat-model", "type": "llm", "max_context_length": 4096 },
                { "id": "embedding-model", "type": "embeddings", "dimensions": 768 },
                { "id": "both-model", "capabilities": { "chat": true, "embeddings": true } },
                { "id": "mystery-model", "object": "model" },
                { "id": "nomic-embed-text-v1", "object": "model" }
              ]
            }
            """;
        var context = CreateContext();
        var handler = new LMStudioTestHttpHandler((_, _, _) => LMStudioTestHttpHandler.Json(payload));
        using var connection = new LMStudioConnection(context, handler);
        var catalog = new LMStudioModelCatalogService(connection);
        var result = await catalog.GetCatalogAsync();
        var chatModels = await new LMStudioAgentProvider(context, connection, catalog).GetAvailableModelsAsync();
        var embeddingModels = await new LMStudioEmbeddingProvider(context, connection, catalog).GetAvailableModelsAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(
            LMStudioModelCapability.Unknown,
            result.Models.Single(model => model.Id == "mystery-model").Capability);
        Assert.Contains(chatModels, model => model.ModelId == "lmstudio/chat-model");
        Assert.Contains(chatModels, model => model.ModelId == "lmstudio/both-model");
        Assert.Contains(chatModels, model => model.ModelId == "lmstudio/mystery-model");
        Assert.DoesNotContain(chatModels, model => model.ModelId == "lmstudio/embedding-model");
        Assert.DoesNotContain(chatModels, model => model.ModelId == "lmstudio/nomic-embed-text-v1");
        Assert.Contains(embeddingModels, model => model.ModelId == "lmstudio/embedding-model" && model.Dimensions == 768);
        Assert.Contains(embeddingModels, model => model.ModelId == "lmstudio/both-model");
        Assert.DoesNotContain(embeddingModels, model => model.ModelId == "lmstudio/mystery-model");
        Assert.Contains(embeddingModels, model => model.ModelId == "lmstudio/nomic-embed-text-v1");
        Assert.DoesNotContain(embeddingModels, model => model.ModelId == "lmstudio/chat-model");
    }

    [Fact]
    public async Task Readiness_RequiresAUsableCandidateForEachProviderKind()
    {
        var context = CreateContext();
        var handler = new LMStudioTestHttpHandler((_, _, _) => LMStudioTestHttpHandler.Json("{\"data\":[]}"));
        using var connection = new LMStudioConnection(context, handler);
        var catalog = new LMStudioModelCatalogService(connection);
        var chatReadiness = await new LMStudioAgentProvider(context, connection, catalog).GetReadinessAsync();
        var embeddingReadiness = await new LMStudioEmbeddingProvider(context, connection, catalog).GetReadinessAsync();

        Assert.Equal(AgentProviderReadinessStatus.NeedsConfiguration, chatReadiness.Status);
        Assert.Contains("no chat model", chatReadiness.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(AgentProviderReadinessStatus.NeedsConfiguration, embeddingReadiness.Status);
        Assert.Contains("no embedding model", embeddingReadiness.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static ProviderTestPackageContext CreateContext(
        string baseUrl = "http://lmstudio.test/v1")
        => new(
            "sunder.package.agent.provider.lmstudio",
            new Dictionary<string, string>
            {
                [LMStudioProviderConfiguration.BaseUrlKey] = baseUrl,
            });

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration) => _utcNow += duration;
    }
}
