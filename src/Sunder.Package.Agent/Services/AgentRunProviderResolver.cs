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
        var settings = AgentChatModelSettingsJson.Parse(chatBinding.SettingsJson);
        var modelModeOption = ResolveModelModeOption(model, settings);
        var modelVariant = modelModeOption?.DisablesReasoning == true
            ? null
            : ResolveModelVariant(model, settings);
        var modelSpeedOption = ResolveModelSpeedOption(model, settings)
                               ?? ResolveLegacyFastSpeedOption(model, chatBinding.ModelId);
        return new AgentRunProviderMetadata(runCapabilities, modelVariant, modelSpeedOption, modelModeOption);
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
            var model = models.FirstOrDefault(candidate =>
                string.Equals(candidate.ModelId, modelId, StringComparison.OrdinalIgnoreCase));
            if (model is not null || !modelId.EndsWith("-fast", StringComparison.OrdinalIgnoreCase))
            {
                return model;
            }

            var baseModelId = modelId[..^"-fast".Length];
            return models.FirstOrDefault(candidate =>
                string.Equals(candidate.ModelId, baseModelId, StringComparison.OrdinalIgnoreCase));
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
        AgentChatModelSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.ReasoningVariantId))
        {
            return null;
        }

        return model?.Variants?.FirstOrDefault(variant =>
            string.Equals(variant.VariantId, settings.ReasoningVariantId, StringComparison.OrdinalIgnoreCase));
    }

    private static AgentModelSpeedOptionDescriptor? ResolveModelSpeedOption(
        AgentModelDescriptor? model,
        AgentChatModelSettings settings)
        => string.IsNullOrWhiteSpace(settings.SpeedOptionId)
            ? null
            : model?.SpeedOptions?.FirstOrDefault(option =>
                string.Equals(option.SpeedOptionId, settings.SpeedOptionId, StringComparison.OrdinalIgnoreCase));

    private static AgentModelModeOptionDescriptor? ResolveModelModeOption(
        AgentModelDescriptor? model,
        AgentChatModelSettings settings)
        => string.IsNullOrWhiteSpace(settings.ModeOptionId)
            ? null
            : model?.ModeOptions?.FirstOrDefault(option =>
                string.Equals(option.ModeOptionId, settings.ModeOptionId, StringComparison.OrdinalIgnoreCase));

    private static AgentModelSpeedOptionDescriptor? ResolveLegacyFastSpeedOption(
        AgentModelDescriptor? model,
        string? modelId)
        => !string.IsNullOrWhiteSpace(modelId)
           && modelId.EndsWith("-fast", StringComparison.OrdinalIgnoreCase)
            ? model?.SpeedOptions?.FirstOrDefault(option =>
                string.Equals(option.SpeedOptionId, "fast", StringComparison.OrdinalIgnoreCase))
            : null;
}

public sealed record AgentRunProviderSelection(
    AgentProfileModelBindingRecord? ChatBinding,
    IAgentChatProvider? Provider);

public sealed record AgentRunProviderMetadata(
    AgentProviderRunCapabilities RunCapabilities,
    AgentModelVariantDescriptor? ModelVariant,
    AgentModelSpeedOptionDescriptor? ModelSpeedOption,
    AgentModelModeOptionDescriptor? ModelModeOption);
