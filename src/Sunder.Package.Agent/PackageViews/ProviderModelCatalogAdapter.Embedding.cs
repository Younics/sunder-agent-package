using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Shared.Presentation;

internal sealed partial class ProviderModelCatalogAdapter
{
    public static ProviderModelCatalogOption ToCatalogOption(AgentEmbeddingModelDescriptor model)
        => new(model.ModelId, model.DisplayName);
}
