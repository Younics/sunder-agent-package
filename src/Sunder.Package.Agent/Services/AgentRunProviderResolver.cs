using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;
using AgentServices = Sunder.Generated.Packages.Younics_Ai_Agent_Services;

namespace Sunder.Package.Agent.Services;

public sealed class AgentRunProviderResolver(
    AgentProfileService profileService,
    IServiceProviders<AgentServices.IChatProviderService> providers)
{
    private readonly AgentProfileService _profileService = profileService;
    public async ValueTask<AgentRunProviderSelection> ResolveChatProviderAsync(
        AgentProfileRecord profile, CancellationToken cancellationToken = default)
    {
        var chatBinding = _profileService.GetChatBinding(profile.ProfileId);
        if (string.IsNullOrWhiteSpace(chatBinding?.ProviderId))
        {
            return new AgentRunProviderSelection(chatBinding);
        }

        var catalog = await providers.ListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var provider in catalog.Providers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (provider.State != ServiceProviderState.Active) continue;
            try
            {
                var descriptor = await provider.Service.DescribeAsync(cancellationToken).ConfigureAwait(false);
                if (!string.Equals(
                    descriptor.ProviderId,
                    chatBinding.ProviderId,
                    StringComparison.OrdinalIgnoreCase)) continue;
                return new AgentRunProviderSelection(chatBinding, provider, SnapshotDescriptor(descriptor));
            }
            catch (SunderRpcException exception) when (!cancellationToken.IsCancellationRequested
                && exception.Error.Kind is SunderRpcErrorKind.StaleEndpoint or SunderRpcErrorKind.Unavailable)
            {
                // A withdrawn candidate cannot satisfy this profile's selection.
            }
        }

        return new AgentRunProviderSelection(chatBinding);
    }

    public async ValueTask<AgentRunProviderMetadata> ResolveRunMetadataAsync(
        AgentRunProviderSelection selection,
        CancellationToken cancellationToken)
    {
        var chatBinding = selection.ChatBinding
            ?? throw new InvalidOperationException("The selected chat provider has no model binding.");
        var provider = selection.Provider?.Service
            ?? throw new InvalidOperationException("The selected chat provider is unavailable.");
        var runCapabilities = await provider.GetCapabilitiesAsync(chatBinding.ModelId, cancellationToken).ConfigureAwait(false);
        var model = await ResolveModelDescriptorAsync(provider, chatBinding.ModelId, cancellationToken).ConfigureAwait(false);
        await selection.Provider!.EnsureCurrentAsync(cancellationToken).ConfigureAwait(false);
        runCapabilities = EnrichRunCapabilities(runCapabilities, model);
        var settings = AgentChatModelSettingsJson.Parse(chatBinding.SettingsJson);
        var modelModeOption = ResolveModelModeOption(model, settings);
        var modelVariant = modelModeOption?.DisablesReasoning == true ? null : ResolveModelVariant(model, settings);
        var modelSpeedOption = ResolveModelSpeedOption(model, settings);
        return new AgentRunProviderMetadata(runCapabilities, modelVariant, modelSpeedOption, modelModeOption);
    }

    public async ValueTask<AgentProviderRunCapabilities> ResolveRunCapabilitiesAsync(
        AgentRunProviderSelection selection,
        CancellationToken cancellationToken)
        => (await ResolveRunMetadataAsync(selection, cancellationToken).ConfigureAwait(false)).RunCapabilities;

    private static AgentProviderDescriptor SnapshotDescriptor(AgentProviderDescriptor descriptor)
        => descriptor with { SupportedAuthModes = descriptor.SupportedAuthModes.ToArray() };

    private static async ValueTask<AgentModelDescriptor?> ResolveModelDescriptorAsync(
        AgentServices.IChatProviderService provider,
        string? modelId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return null;
        }

        try
        {
            var models = await provider.ListModelsAsync(cancellationToken).ConfigureAwait(false);
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

}

public sealed record AgentRunProviderSelection(
    AgentProfileModelBindingRecord? ChatBinding,
    ServiceProvider<AgentServices.IChatProviderService>? Provider = null,
    AgentProviderDescriptor? Descriptor = null)
{
    public string? OwnerPackageId => Provider?.PackageId;
    public bool IsAvailable => Provider is not null && !IsRetiring;
    internal CancellationToken RetirementToken => Provider?.RetirementToken ?? CancellationToken.None;
    internal bool IsRetiring => RetirementToken.IsCancellationRequested;
}

public sealed record AgentRunProviderMetadata(
    AgentProviderRunCapabilities RunCapabilities,
    AgentModelVariantDescriptor? ModelVariant,
    AgentModelSpeedOptionDescriptor? ModelSpeedOption,
    AgentModelModeOptionDescriptor? ModelModeOption);
