using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Provider.Shared;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Provider.LMStudio;

public sealed class LMStudioEmbeddingProvider : IAgentEmbeddingProvider, IDisposable
{
    private readonly LMStudioConnection _connection;
    private readonly LMStudioModelCatalogService _catalog;
    private readonly bool _ownsConnection;

    public LMStudioEmbeddingProvider(IPackageContext packageContext)
    {
        _connection = new LMStudioConnection(packageContext);
        _catalog = new LMStudioModelCatalogService(_connection);
        _ownsConnection = true;
        Descriptor = CreateDescriptor(packageContext.PackageId);
    }

    internal LMStudioEmbeddingProvider(
        IPackageContext packageContext,
        LMStudioConnection connection,
        LMStudioModelCatalogService catalog)
    {
        _connection = connection;
        _catalog = catalog;
        Descriptor = CreateDescriptor(packageContext.PackageId);
    }

    public AgentEmbeddingProviderDescriptor Descriptor { get; }

    public async ValueTask<IReadOnlyList<AgentEmbeddingModelDescriptor>> GetAvailableModelsAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await _catalog.GetCatalogAsync(cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? result.Models
                .Where(model => model.IsEmbeddingCandidate)
                .Select(model => new AgentEmbeddingModelDescriptor(
                    $"lmstudio/{model.Id}",
                    model.DisplayName,
                    model.Dimensions))
                .ToArray()
            : [];
    }

    public async ValueTask<AgentEmbeddingProviderReadiness> GetReadinessAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var connection = await _connection.GetOptionsAsync(cancellationToken);
        if (connection.Options is null)
        {
            return new AgentEmbeddingProviderReadiness(
                Descriptor.ProviderId,
                AgentProviderReadinessStatus.NeedsConfiguration,
                $"The LM Studio base URL is invalid: {connection.ValidationError}");
        }

        var result = await _catalog.GetCatalogAsync(cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return new AgentEmbeddingProviderReadiness(
                Descriptor.ProviderId,
                AgentProviderReadinessStatus.Failed,
                result.Failure!.Message);
        }

        var hasEmbeddingModel = result.Models.Any(model => model.IsEmbeddingCandidate);
        return new AgentEmbeddingProviderReadiness(
            Descriptor.ProviderId,
            hasEmbeddingModel
                ? AgentProviderReadinessStatus.Ready
                : AgentProviderReadinessStatus.NeedsConfiguration,
            hasEmbeddingModel
                ? "LM Studio is reachable and ready for embeddings."
                : "LM Studio is reachable, but no embedding model is loaded or discoverable.");
    }

    public async ValueTask<AgentEmbeddingGenerationResult?> GenerateEmbeddingAsync(
        string modelId,
        string text,
        CancellationToken cancellationToken = default)
    {
        var results = await GenerateEmbeddingsAsync(modelId, [text], cancellationToken).ConfigureAwait(false);
        return results.Count == 0 ? null : results[0];
    }

    public async ValueTask<IReadOnlyList<AgentEmbeddingGenerationResult?>> GenerateEmbeddingsAsync(
        string modelId,
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var connectionOptions = await _connection.GetRequiredOptionsAsync(cancellationToken);
        var results = ProviderEmbeddingBatch.CreateResultBuffer(texts, out var validTexts);
        if (validTexts.Count == 0)
        {
            return results;
        }

        var client = _connection.CreateEmbeddingClient(modelId, connectionOptions);
        var response = await client.GenerateEmbeddingsAsync(
            validTexts.Select(item => item.Text).ToArray(),
            options: null,
            cancellationToken).ConfigureAwait(false);
        var embeddings = response.Value
            .Select(embedding => new ProviderIndexedEmbedding(
                embedding.Index,
                new AgentEmbeddingGenerationResult(modelId, embedding.ToFloats().ToArray())))
            .ToArray();

        ProviderEmbeddingBatch.ApplyIndexedResults(results, validTexts, embeddings);
        return results;
    }

    public void Dispose()
    {
        if (_ownsConnection)
        {
            _connection.Dispose();
        }
    }

    private static AgentEmbeddingProviderDescriptor CreateDescriptor(string packageId)
        => new("lmstudio", "LM Studio", [])
        {
            PackageId = packageId,
        };
}
