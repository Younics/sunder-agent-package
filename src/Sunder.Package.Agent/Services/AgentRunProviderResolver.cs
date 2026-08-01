using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;

namespace Sunder.Package.Agent.Services;

public sealed class AgentRunProviderResolver(
    AgentProfileService profileService,
    AgentRpcCatalog rpcCatalog)
{
    private readonly AgentProfileService _profileService = profileService;
    private readonly AgentRpcCatalog _rpcCatalog = rpcCatalog;

    public AgentRunProviderSelection ResolveChatProvider(AgentProfileRecord profile)
    {
        var chatBinding = _profileService.GetChatBinding(profile.ProfileId);
        if (string.IsNullOrWhiteSpace(chatBinding?.ProviderId))
        {
            return new AgentRunProviderSelection(chatBinding);
        }

        foreach (var reference in _rpcCatalog.GetServiceReferences(AgentRpcServices.ChatProviders))
        {
            if (!reference.TryAcquire(out var lease))
            {
                continue;
            }

            var descriptor = SnapshotDescriptor(lease.Service.Descriptor);
            if (!string.Equals(
                    descriptor.ProviderId,
                    chatBinding.ProviderId,
                    StringComparison.OrdinalIgnoreCase)
                || lease.RetirementToken.IsCancellationRequested)
            {
                lease.Dispose();
                continue;
            }

            return new AgentRunProviderSelection(
                chatBinding,
                reference,
                lease,
                descriptor);
        }

        return new AgentRunProviderSelection(chatBinding);
    }

    public async ValueTask<AgentRunProviderMetadata> ResolveRunMetadataAsync(
        AgentRunProviderSelection selection,
        CancellationToken cancellationToken)
    {
        var chatBinding = selection.ChatBinding
            ?? throw new InvalidOperationException("The selected chat provider has no model binding.");
        return await selection.InvokeAsync(
            cancellationToken,
            async (provider, invocationToken) =>
            {
                var runCapabilities = await provider
                    .GetRunCapabilitiesAsync(chatBinding.ModelId, invocationToken)
                    .ConfigureAwait(false);
                var model = await ResolveModelDescriptorAsync(
                    provider,
                    chatBinding.ModelId,
                    invocationToken).ConfigureAwait(false);
                runCapabilities = EnrichRunCapabilities(runCapabilities, model);
                var settings = AgentChatModelSettingsJson.Parse(chatBinding.SettingsJson);
                var modelModeOption = ResolveModelModeOption(model, settings);
                var modelVariant = modelModeOption?.DisablesReasoning == true
                    ? null
                    : ResolveModelVariant(model, settings);
                var modelSpeedOption = ResolveModelSpeedOption(model, settings)
                                       ?? ResolveLegacyFastSpeedOption(model, chatBinding.ModelId);
                return new AgentRunProviderMetadata(
                    runCapabilities,
                    modelVariant,
                    modelSpeedOption,
                    modelModeOption);
            }).ConfigureAwait(false);
    }

    public async ValueTask<AgentProviderRunCapabilities> ResolveRunCapabilitiesAsync(
        AgentRunProviderSelection selection,
        CancellationToken cancellationToken)
        => (await ResolveRunMetadataAsync(selection, cancellationToken).ConfigureAwait(false)).RunCapabilities;

    private static AgentProviderDescriptor SnapshotDescriptor(AgentProviderDescriptor descriptor)
        => descriptor with { SupportedAuthModes = descriptor.SupportedAuthModes.ToArray() };

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

public sealed class AgentRunProviderSelection : IDisposable
{
    private AgentRpcLease<IAgentChatProvider>? _lease;

    internal AgentRunProviderSelection(
        AgentProfileModelBindingRecord? chatBinding,
        AgentRpcReference<IAgentChatProvider>? reference = null,
        AgentRpcLease<IAgentChatProvider>? lease = null,
        AgentProviderDescriptor? descriptor = null)
    {
        ChatBinding = chatBinding;
        Reference = reference;
        _lease = lease;
        Descriptor = descriptor;
        OwnerPackageId = lease?.PackageId;
        RetirementToken = lease?.RetirementToken ?? CancellationToken.None;
    }

    public AgentProfileModelBindingRecord? ChatBinding { get; }

    public AgentProviderDescriptor? Descriptor { get; }

    public string? OwnerPackageId { get; }

    public bool IsAvailable => Volatile.Read(ref _lease) is not null;

    internal AgentRpcReference<IAgentChatProvider>? Reference { get; }

    internal CancellationToken RetirementToken { get; }

    internal bool IsRetiring => RetirementToken.IsCancellationRequested;

    internal IAgentChatProvider Provider
        => Volatile.Read(ref _lease)?.Service
           ?? throw new ObjectDisposedException(nameof(AgentRunProviderSelection));

    internal AgentRunProviderSelection? TryRetain()
    {
        var reference = Reference;
        if (reference is null || !reference.TryAcquire(out var lease))
        {
            return null;
        }

        if (lease.RetirementToken.IsCancellationRequested)
        {
            lease.Dispose();
            return null;
        }

        return new AgentRunProviderSelection(
            ChatBinding,
            reference,
            lease,
            Descriptor);
    }

    internal bool CanAcquireExactOwner()
    {
        var reference = Reference;
        if (reference is null || !reference.TryAcquire(out var lease))
        {
            return false;
        }

        using (lease)
        {
            return !lease.RetirementToken.IsCancellationRequested;
        }
    }

    internal async ValueTask<TResult> InvokeAsync<TResult>(
        CancellationToken cancellationToken,
        Func<IAgentChatProvider, CancellationToken, ValueTask<TResult>> callback)
    {
        var lease = Volatile.Read(ref _lease)
            ?? throw new ObjectDisposedException(nameof(AgentRunProviderSelection));
        using var invocation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            RetirementToken);
        try
        {
            var result = await callback(lease.Service, invocation.Token).ConfigureAwait(false);
            if (IsRetiring && !cancellationToken.IsCancellationRequested)
            {
                throw AgentRpcInvocation.Unavailable(OwnerPackageId!);
            }

            return result;
        }
        catch (AgentPackageUnavailableException)
        {
            throw;
        }
        catch (OperationCanceledException exception) when (
            IsRetiring
            && !cancellationToken.IsCancellationRequested)
        {
            throw AgentRpcInvocation.Unavailable(OwnerPackageId!, exception);
        }
        catch (Exception exception) when (
            IsRetiring
            && !cancellationToken.IsCancellationRequested)
        {
            throw AgentRpcInvocation.Unavailable(OwnerPackageId!, exception);
        }
    }

    public void Dispose()
        => Interlocked.Exchange(ref _lease, null)?.Dispose();
}

public sealed record AgentRunProviderMetadata(
    AgentProviderRunCapabilities RunCapabilities,
    AgentModelVariantDescriptor? ModelVariant,
    AgentModelSpeedOptionDescriptor? ModelSpeedOption,
    AgentModelModeOptionDescriptor? ModelModeOption);
