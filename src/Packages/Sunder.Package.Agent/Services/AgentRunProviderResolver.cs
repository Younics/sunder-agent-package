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
        var modelVariant = await ResolveModelVariantAsync(provider, chatBinding, cancellationToken).ConfigureAwait(false);
        return new AgentRunProviderMetadata(runCapabilities, modelVariant);
    }

    public ValueTask<AgentProviderRunCapabilities> ResolveRunCapabilitiesAsync(
        IAgentChatProvider provider,
        AgentProfileModelBindingRecord chatBinding,
        CancellationToken cancellationToken)
        => provider.GetRunCapabilitiesAsync(chatBinding.ModelId, cancellationToken);

    private static async ValueTask<AgentModelVariantDescriptor?> ResolveModelVariantAsync(
        IAgentChatProvider provider,
        AgentProfileModelBindingRecord chatBinding,
        CancellationToken cancellationToken)
    {
        var settings = AgentChatModelSettingsJson.Parse(chatBinding.SettingsJson);
        if (string.IsNullOrWhiteSpace(settings.ReasoningVariantId))
        {
            return null;
        }

        try
        {
            var models = await provider.GetAvailableModelsAsync(cancellationToken).ConfigureAwait(false);
            var model = models.FirstOrDefault(candidate =>
                string.Equals(candidate.ModelId, chatBinding.ModelId, StringComparison.OrdinalIgnoreCase));
            return model?.Variants?.FirstOrDefault(variant =>
                string.Equals(variant.VariantId, settings.ReasoningVariantId, StringComparison.OrdinalIgnoreCase));
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
}

public sealed record AgentRunProviderSelection(
    AgentProfileModelBindingRecord? ChatBinding,
    IAgentChatProvider? Provider);

public sealed record AgentRunProviderMetadata(
    AgentProviderRunCapabilities RunCapabilities,
    AgentModelVariantDescriptor? ModelVariant);
