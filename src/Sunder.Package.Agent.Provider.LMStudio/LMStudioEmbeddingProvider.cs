using Sunder.Generated.Packages.Sunder_Package_Agent_Services;
using Sunder.Generated.Packages.Sunder_Package_Agent_Models;
using Sunder.Package.Agent.Provider.Shared;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Services;

namespace Sunder.Package.Agent.Provider.LMStudio;

[SunderServiceProvider(typeof(IEmbeddingProviderService), ProviderId = "lmstudio.embedding")]
public sealed partial class LMStudioEmbeddingProvider :
    IEmbeddingProviderService,
    IDisposable
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

    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
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

    public ValueTask<AgentEmbeddingProviderDescriptor> DescribeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Descriptor);
    }

    public async ValueTask<string> SpaceIdentityAsync(
        string modelId,
        CancellationToken cancellationToken = default)
    {
        var options = await _connection.GetRequiredOptionsAsync(cancellationToken).ConfigureAwait(false);
        return options.NormalizedBaseUrl;
    }

    public async ValueTask<IReadOnlyList<AgentEmbeddingModelDescriptor>> ListModelsAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await _catalog.GetCatalogAsync(cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? result.Models
                .Where(model => model.IsEmbeddingCandidate)
                .Select(model => new AgentEmbeddingModelDescriptor
                {
                    ModelId = $"lmstudio/{model.Id}",
                    DisplayName = model.DisplayName,
                    Dimensions = model.Dimensions,
                    IsRecommended = false,
                })
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
            return new AgentEmbeddingProviderReadiness
            {
                ProviderId = Descriptor.ProviderId,
                Status = AgentProviderReadinessStatus.NeedsConfiguration,
                Message = $"The LM Studio base URL is invalid: {connection.ValidationError}",
            };
        }

        var result = await _catalog.GetCatalogAsync(cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return new AgentEmbeddingProviderReadiness
            {
                ProviderId = Descriptor.ProviderId,
                Status = AgentProviderReadinessStatus.Failed,
                Message = result.Failure!.Message,
            };
        }

        var hasEmbeddingModel = result.Models.Any(model => model.IsEmbeddingCandidate);
        return new AgentEmbeddingProviderReadiness
        {
            ProviderId = Descriptor.ProviderId,
            Status = hasEmbeddingModel
                ? AgentProviderReadinessStatus.Ready
                : AgentProviderReadinessStatus.NeedsConfiguration,
            Message = hasEmbeddingModel
                ? "LM Studio is reachable and ready for embeddings."
                : "LM Studio is reachable, but no embedding model is loaded or discoverable.",
        };
    }

    public async ValueTask<AgentEmbeddingGenerationResult?> GenerateAsync(
        CSunder_Agent_Embedding_Provider_EmbeddingProviderGenerateRequest request,
        CancellationToken cancellationToken = default)
    {
        var results = await GenerateBatchAsync(new() { ModelId = request.ModelId, Texts = [request.Text] }, cancellationToken).ConfigureAwait(false);
        return results.Count == 0 ? null : results[0];
    }

    public async ValueTask<IReadOnlyList<AgentEmbeddingGenerationResult?>> GenerateBatchAsync(
        CSunder_Agent_Embedding_Provider_EmbeddingProviderGenerateBatchRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var connectionOptions = await _connection.GetRequiredOptionsAsync(cancellationToken);
        var results = ProviderEmbeddingBatch.CreateResultBuffer<AgentEmbeddingGenerationResult>(request.Texts, out var validTexts);
        if (validTexts.Count == 0)
        {
            return results;
        }

        var client = _connection.CreateEmbeddingClient(request.ModelId, connectionOptions);
        var response = await client.GenerateEmbeddingsAsync(
            validTexts.Select(item => item.Text).ToArray(),
            options: null,
            cancellationToken).ConfigureAwait(false);
        var embeddings = response.Value
            .Select(embedding => new ProviderIndexedEmbedding<AgentEmbeddingGenerationResult>(
                embedding.Index,
                new AgentEmbeddingGenerationResult
                {
                    ModelId = request.ModelId,
                    Values = embedding.ToFloats().ToArray().Select(static value => (double)value).ToArray(),
                }))
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
        => new()
        {
            ProviderId = "lmstudio",
            DisplayName = "LM Studio",
            SupportedAuthModes = [],
            PackageId = packageId,
        };
}
