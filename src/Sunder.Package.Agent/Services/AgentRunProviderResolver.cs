using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Services;

public sealed class AgentRunProviderResolver(
    AgentProfileService profileService,
    IPackageExtensionCatalog extensionCatalog)
{
    private readonly AgentProfileService _profileService = profileService;
    private readonly IPackageExtensionCatalog _extensionCatalog = extensionCatalog;

    public AgentRunProviderSelection ResolveChatProvider(AgentProfileRecord profile)
    {
        var chatBinding = _profileService.GetChatBinding(profile.ProfileId);
        var provider = _extensionCatalog.GetExtensions(PackageExtensionPoints.ChatProviders)
            .FirstOrDefault(x => string.Equals(x.Descriptor.ProviderId, chatBinding?.ProviderId, StringComparison.OrdinalIgnoreCase));

        return new AgentRunProviderSelection(chatBinding, provider);
    }

    public async ValueTask<AgentRunProviderMetadata> ResolveRunMetadataAsync(
        IAgentChatProvider provider,
        AgentProfileModelBindingRecord chatBinding,
        CancellationToken cancellationToken)
    {
        var runCapabilities = await provider.GetRunCapabilitiesAsync(chatBinding.ModelId, cancellationToken).ConfigureAwait(false);
        var model = await ResolveModelDescriptorAsync(provider, chatBinding.ModelId, cancellationToken).ConfigureAwait(false);
        runCapabilities = EnrichRunCapabilities(runCapabilities, model);
        var modelVariant = ResolveModelVariant(model, chatBinding);
        return new AgentRunProviderMetadata(runCapabilities, modelVariant);
    }

    public async ValueTask<AgentProviderRunCapabilities> ResolveRunCapabilitiesAsync(
        IAgentChatProvider provider,
        AgentProfileModelBindingRecord chatBinding,
        CancellationToken cancellationToken)
        => (await ResolveRunMetadataAsync(provider, chatBinding, cancellationToken).ConfigureAwait(false)).RunCapabilities;

    private static async ValueTask<AgentModelDescriptor?> ResolveModelDescriptorAsync(
        IAgentChatProvider provider,
        string? modelId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return null;
        }

        try
        {
            var models = await provider.GetAvailableModelsAsync(cancellationToken).ConfigureAwait(false);
            return models.FirstOrDefault(candidate =>
                string.Equals(candidate.ModelId, modelId, StringComparison.OrdinalIgnoreCase));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static AgentProviderRunCapabilities EnrichRunCapabilities(
        AgentProviderRunCapabilities runCapabilities,
        AgentModelDescriptor? model)
        => model is null
            ? runCapabilities
            : runCapabilities with
            {
                ContextWindowTokens = runCapabilities.ContextWindowTokens ?? model.ContextWindow,
                MaxOutputTokens = runCapabilities.MaxOutputTokens ?? model.MaxOutputTokens,
            };

    private static AgentModelVariantDescriptor? ResolveModelVariant(
        AgentModelDescriptor? model,
        AgentProfileModelBindingRecord chatBinding)
    {
        var settings = AgentChatModelSettingsJson.Parse(chatBinding.SettingsJson);
        if (string.IsNullOrWhiteSpace(settings.ReasoningVariantId))
        {
            return null;
        }

        return model?.Variants?.FirstOrDefault(variant =>
            string.Equals(variant.VariantId, settings.ReasoningVariantId, StringComparison.OrdinalIgnoreCase));
    }
}

public sealed record AgentRunProviderSelection(
    AgentProfileModelBindingRecord? ChatBinding,
    IAgentChatProvider? Provider);

public sealed record AgentRunProviderMetadata(
    AgentProviderRunCapabilities RunCapabilities,
    AgentModelVariantDescriptor? ModelVariant);
