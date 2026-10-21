using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Storage;
using Sunder.Package.Agent.Runtime;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;
using AgentServices = Sunder.Generated.Packages.Younics_Ai_Agent_Services;

namespace Sunder.Package.Agent.Services;

[SunderService]
[SunderServiceError("agent.owner.operation-failed", Description = "The requested Agent operation could not be completed.")]
public sealed partial class AgentProfileService : IDisposable, IAsyncDisposable, IAgentProfileGateway
{
    private const string ProviderUnavailableMessage =
        "The selected package became unavailable while the operation was running.";
    private readonly AgentLocalStore _store;
    private readonly AgentToolService _toolService;
    private readonly IServiceProviders<AgentServices.IChatProviderService> _chatProviders;
    private readonly IServiceProviders<AgentServices.ISelectableCapabilityProviderService> _selectableProviders;
    private readonly IServiceProviders<AgentServices.IBehaviorLoopService> _behaviorLoops;
    private readonly IServiceProviders<AgentServices.IEmbeddingProviderService> _embeddingProviders;
    private readonly IServiceProviders<AgentServices.IProfileCapabilityConsumerService> _capabilityConsumers;
    private readonly IServiceProviders<AgentServices.IToolSourceService> _toolSources;
    private readonly IServiceProviders<AgentServices.IExecutionTargetService> _executionTargets;
    private bool _disposed;

    public AgentProfileService(
        AgentLocalStore store,
        AgentToolService toolService,
        IServiceProviders<AgentServices.IChatProviderService> chatProviders,
        IServiceProviders<AgentServices.IBehaviorLoopService> behaviorLoops,
        IServiceProviders<AgentServices.IEmbeddingProviderService> embeddingProviders,
        IServiceProviders<AgentServices.IProfileCapabilityConsumerService> capabilityConsumers,
        IServiceProviders<AgentServices.ISelectableCapabilityProviderService> selectableProviders,
        IServiceProviders<AgentServices.IToolSourceService> toolSources,
        IServiceProviders<AgentServices.IExecutionTargetService> executionTargets)
    {
        _store = store;
        _toolService = toolService;
        _chatProviders = chatProviders;
        _selectableProviders = selectableProviders;
        _behaviorLoops = behaviorLoops;
        _embeddingProviders = embeddingProviders;
        _capabilityConsumers = capabilityConsumers;
        _toolSources = toolSources;
        _executionTargets = executionTargets;
    }

    public event Action<string>? ProfileChanged;

    public event Action? SelectableCapabilitiesChanged;

    public IReadOnlyList<AgentProfileRecord> ListProfiles() => _store.ListProfiles();

    public AgentProfileRecord? GetProfile(string profileId) => _store.GetProfile(profileId);

    [SunderMethod(Idempotency = SunderServiceIdempotency.Idempotent)]
    public Task<IReadOnlyList<AgentProfileRecord>> ListProfilesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_store.ListProfiles());
    }

    [SunderMethod(Idempotency = SunderServiceIdempotency.Idempotent)]
    public Task<AgentProfileRecord?> GetProfileAsync(string profileId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_store.GetProfile(profileId));
    }

    public async Task<AgentProfileRecord> CreateProfileAsync(string displayName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = DateTimeOffset.UtcNow;
        var chatProvider = (await ListChatProvidersAsync(cancellationToken).ConfigureAwait(false))
            .OrderBy(provider => provider.Descriptor.DisplayName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        var orderedChatModels = chatProvider.Provider is null
            ? []
            : (await chatProvider.Provider.Service.ListModelsAsync(cancellationToken).ConfigureAwait(false))
                .OrderNewestFirst()
                .ToArray();
        var chatModel = orderedChatModels.FirstOrDefault(model => model.IsRecommended)
                        ?? orderedChatModels.FirstOrDefault();
        var profileId = Guid.NewGuid().ToString("N");

        var record = new AgentProfileRecord(
            profileId,
            displayName,
            null,
            null,
            chatProvider.Descriptor?.ProviderId,
            chatModel?.ModelId,
            null,
            null,
            now,
            now,
            BuildModelBindings(
                profileId,
                chatProvider.Descriptor?.ProviderId,
                chatModel?.ModelId,
                chatSettingsJson: null,
                embeddingProviderId: null,
                embeddingModelId: null,
                embeddingSettingsJson: null,
                now),
            []
        );

        _store.SaveProfile(record);
        NotifyProfileChanged(record.ProfileId);
        return record;
    }

    [SunderMethod(Errors = ["agent.owner.operation-failed"])]
    public Task SaveProfileAsync(
        string profileId, string displayName, string? description, string? instructions,
        string? chatProviderId, string? chatModelId, string? embeddingProviderId, string? embeddingModelId,
        IReadOnlyList<AgentProfileSelectableCapabilityAssignmentRecord>? selectableCapabilityAssignments = null,
        string? behaviorLoopId = null, string? behaviorLoopSourceId = null,
        string? behaviorLoopSettingsJson = null, string? chatModelSettingsJson = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireProfileId(profileId);
        try
        {
            SaveProfile(profileId, displayName, description, instructions, chatProviderId, chatModelId,
                embeddingProviderId, embeddingModelId, selectableCapabilityAssignments, behaviorLoopId,
                behaviorLoopSourceId, behaviorLoopSettingsJson, chatModelSettingsJson);
            return Task.CompletedTask;
        }
        catch (AgentProfileOperationException exception)
        {
            var message = exception.Message;
            throw new SunderRpcException(new(SunderRpcErrorKind.Domain, "agent.owner.operation-failed",
                message.Length > 2048 ? message[..2048] : message));
        }
    }

    [SunderMethod(Errors = ["agent.owner.operation-failed"])]
    public Task DeleteProfileAsync(string profileId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireProfileId(profileId);
        _store.DeleteProfile(profileId);
        NotifyProfileChanged(profileId);
        return Task.CompletedTask;
    }

    private static void RequireProfileId(string profileId)
    {
        if (string.IsNullOrWhiteSpace(profileId))
            throw new SunderRpcException(new(SunderRpcErrorKind.Domain, "agent.owner.operation-failed", "Profile id is required."));
    }

    public void SaveProfile(
        string profileId,
        string displayName,
        string? description,
        string? instructions,
        string? chatProviderId,
        string? chatModelId,
        string? embeddingProviderId,
        string? embeddingModelId,
        IReadOnlyList<AgentProfileSelectableCapabilityAssignmentRecord>? selectableCapabilityAssignments = null,
        string? behaviorLoopId = null,
        string? behaviorLoopSourceId = null,
        string? behaviorLoopSettingsJson = null,
        string? chatModelSettingsJson = null)
    {
        var existing = _store.GetProfile(profileId)
                       ?? throw new AgentProfileOperationException($"Profile '{profileId}' was not found.");
        var now = DateTimeOffset.UtcNow;
        var existingChatBinding = FindModelBinding(existing, AgentModelCapabilityKinds.Chat);
        var existingEmbeddingBinding = FindModelBinding(existing, AgentModelCapabilityKinds.Embedding);
        var effectiveChatSettingsJson = chatModelSettingsJson is null
            ? existingChatBinding?.SettingsJson
            : NormalizeNullable(chatModelSettingsJson);

        _store.SaveProfile(existing with
        {
            DisplayName = displayName,
            Description = description,
            Instructions = instructions,
            ChatProviderId = chatProviderId,
            ChatModelId = chatModelId,
            EmbeddingProviderId = embeddingProviderId,
            EmbeddingModelId = embeddingModelId,
            UpdatedAtUtc = now,
            SelectableCapabilityAssignments = selectableCapabilityAssignments ?? [],
            ModelBindings = BuildModelBindings(
                profileId,
                chatProviderId,
                chatModelId,
                effectiveChatSettingsJson,
                embeddingProviderId,
                embeddingModelId,
                existingEmbeddingBinding?.SettingsJson,
                now),
            BehaviorLoopId = behaviorLoopId is null ? existing.BehaviorLoopId : NormalizeNullable(behaviorLoopId),
            BehaviorLoopSourceId = behaviorLoopSourceId is null ? existing.BehaviorLoopSourceId : NormalizeNullable(behaviorLoopSourceId),
            BehaviorLoopSettingsJson = behaviorLoopSettingsJson is null ? existing.BehaviorLoopSettingsJson : NormalizeNullable(behaviorLoopSettingsJson),
        });
        NotifyProfileChanged(profileId);
    }

    [SunderMethod(Idempotency = SunderServiceIdempotency.Idempotent)]
    public async Task<IReadOnlyList<AgentBehaviorLoopDescriptor>> ListBehaviorLoopDescriptorsAsync(CancellationToken cancellationToken = default)
        => (await ListBehaviorProvidersAsync(cancellationToken, omitUnavailable: true).ConfigureAwait(false))
            .Select(static loop => loop.Descriptor)
            .OrderBy(loop => loop.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    internal async Task<IReadOnlyList<(ServiceProvider<AgentServices.IBehaviorLoopService> Provider, AgentBehaviorLoopDescriptor Descriptor)>>
        ListBehaviorProvidersAsync(CancellationToken cancellationToken, bool omitUnavailable = false)
    {
        ServiceProviderCatalog<AgentServices.IBehaviorLoopService> catalog;
        try
        {
            catalog = await _behaviorLoops.ListAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SunderRpcException exception) when (!cancellationToken.IsCancellationRequested
            && exception.Error.Kind == SunderRpcErrorKind.Unavailable)
        {
            return [];
        }
        var result = new List<(ServiceProvider<AgentServices.IBehaviorLoopService>, AgentBehaviorLoopDescriptor)>();
        foreach (var provider in catalog.Providers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (provider.State != ServiceProviderState.Active) continue;
            try
            {
                var descriptor = await provider.Service.DescribeAsync(cancellationToken).ConfigureAwait(false);
                result.Add((provider, descriptor));
            }
            catch (SunderRpcException exception) when (omitUnavailable && !cancellationToken.IsCancellationRequested
                && exception.Error.Kind is SunderRpcErrorKind.StaleEndpoint or SunderRpcErrorKind.Unavailable)
            {
            }
        }
        return result;
    }

    public AgentProfileModelBindingRecord? GetModelBinding(string profileId, string capabilityKind)
        => _store.GetProfileModelBinding(profileId, capabilityKind);

    public AgentProfileModelBindingRecord? GetChatBinding(string profileId)
        => GetModelBinding(profileId, AgentModelCapabilityKinds.Chat);

    public void DeleteProfile(string profileId)
    {
        _store.DeleteProfile(profileId);
        NotifyProfileChanged(profileId);
    }

    public void SaveRuntimeProfile(AgentProfileRecord profile)
    {
        _store.SaveProfile(profile with { IsInternal = true });
        NotifyProfileChanged(profile.ProfileId);
    }

    public void ImportProfile(AgentProfileRecord profile)
    {
        _store.SaveProfile(profile with { IsInternal = false });
        NotifyProfileChanged(profile.ProfileId);
    }

    public void NotifyProfileImported(string profileId)
    {
        if (!string.IsNullOrWhiteSpace(profileId))
        {
            NotifyProfileChanged(profileId);
        }
    }

    [SunderMethod(Idempotency = SunderServiceIdempotency.Idempotent)]
    public async Task<IReadOnlyList<AgentProviderDescriptor>> ListChatProviderDescriptorsAsync(CancellationToken cancellationToken = default)
        => (await ListChatProvidersAsync(cancellationToken, omitUnavailable: true).ConfigureAwait(false))
            .Select(static provider => provider.Descriptor)
            .OrderBy(provider => provider.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    [SunderMethod(Idempotency = SunderServiceIdempotency.Idempotent)]
    public async Task<IReadOnlyList<AgentEmbeddingProviderDescriptor>> ListEmbeddingProviderDescriptorsAsync(CancellationToken cancellationToken = default)
        => (await ListEmbeddingProvidersAsync(cancellationToken, omitUnavailable: true).ConfigureAwait(false))
            .Select(static provider => provider.Descriptor)
            .OrderBy(provider => provider.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    [SunderMethod(Idempotency = SunderServiceIdempotency.Idempotent)]
    public async Task<bool> HasProfileCapabilityConsumersAsync(string capabilityKind, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(capabilityKind))
        {
            return false;
        }

        ServiceProviderCatalog<AgentServices.IProfileCapabilityConsumerService> catalog;
        try
        {
            catalog = await _capabilityConsumers.ListAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SunderRpcException exception) when (!cancellationToken.IsCancellationRequested
            && exception.Error.Kind == SunderRpcErrorKind.Unavailable)
        {
            return false;
        }
        foreach (var consumer in catalog.Providers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (consumer.State != ServiceProviderState.Active) continue;
            try
            {
                var descriptor = await consumer.Service.DescribeAsync(cancellationToken).ConfigureAwait(false);
                if (descriptor.Capabilities.Any(capability => string.Equals(capability.CapabilityKind, capabilityKind, StringComparison.OrdinalIgnoreCase)))
                    return true;
            }
            catch (SunderRpcException exception) when (!cancellationToken.IsCancellationRequested
                && exception.Error.Kind is SunderRpcErrorKind.StaleEndpoint or SunderRpcErrorKind.Unavailable)
            {
            }
        }

        return false;
    }

    public async Task<IReadOnlyList<AgentProfileSelectableCapabilityDescriptor>> ListSelectableProfileCapabilitiesAsync(
        AgentProfileRecord? profile = null,
        CancellationToken cancellationToken = default)
    {
        StartCapabilityObservation();
        var capabilities = new List<AgentProfileSelectableCapabilityDescriptor>();
        var providers = await ListSelectableProvidersAsync(cancellationToken).ConfigureAwait(false);
        foreach (var provider in providers
                     .OrderBy(provider => provider.Descriptor.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                capabilities.AddRange(await provider.Provider.Service.ListAsync(profile, cancellationToken).ConfigureAwait(false));
            }
            catch (SunderRpcException exception) when (!cancellationToken.IsCancellationRequested
                && exception.Error.Kind is SunderRpcErrorKind.StaleEndpoint or SunderRpcErrorKind.Unavailable)
            {
                continue;
            }
        }

        return capabilities
            .Where(capability => !string.IsNullOrWhiteSpace(capability.Kind)
                                 && !string.IsNullOrWhiteSpace(capability.CapabilityId)
                                 && !string.IsNullOrWhiteSpace(capability.DisplayName))
            .GroupBy(capability => string.Concat(
                    capability.Kind.Trim(), "\n",
                    capability.SourceId?.Trim() ?? string.Empty, "\n",
                    capability.CapabilityId.Trim()),
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(capability => capability.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private void OnSelectableCapabilitiesChanged()
    {
        var handlers = SelectableCapabilitiesChanged;
        if (handlers is null)
        {
            return;
        }

        foreach (Action handler in handlers.GetInvocationList())
        {
            try
            {
                handler();
            }
            catch (Exception exception) when (!AgentServiceExceptionPolicy.IsFatal(exception))
            {
                // Capability listeners are independent refresh hints.
            }
        }
    }

    private void NotifyProfileChanged(string profileId)
    {
        var handlers = ProfileChanged;
        if (handlers is null)
        {
            return;
        }

        foreach (Action<string> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(profileId);
            }
            catch (Exception exception) when (!AgentServiceExceptionPolicy.IsFatal(exception))
            {
                // Listeners cannot invalidate an already committed profile mutation.
            }
        }
    }

    public Task<IReadOnlyList<AgentToolCatalogEntry>> ListInstalledLocalToolsAsync(CancellationToken cancellationToken = default)
        => _toolService.ListInstalledLocalToolsAsync(cancellationToken);

    public async Task<IReadOnlyList<AgentModelDescriptor>> ListChatModelsAsync(string? providerId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            return [];
        }

        var provider = (await ListChatProvidersAsync(cancellationToken, omitUnavailable: true).ConfigureAwait(false)).FirstOrDefault(x => string.Equals(
            x.Descriptor.ProviderId,
            providerId,
            StringComparison.OrdinalIgnoreCase));
        return await ListChatModelsAsync(provider.Provider, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<AgentModelDescriptor>> ListChatModelsAsync(
        ServiceProvider<AgentServices.IChatProviderService>? provider,
        CancellationToken cancellationToken)
    {
        if (provider is null)
        {
            return [];
        }

        try
        {
            return (await provider.Service.ListModelsAsync(cancellationToken).ConfigureAwait(false))
                .OrderNewestFirst()
                .ToArray();
        }
        catch (SunderRpcException exception) when (!cancellationToken.IsCancellationRequested
            && exception.Error.Kind is SunderRpcErrorKind.StaleEndpoint or SunderRpcErrorKind.Unavailable)
        {
            return [];
        }
    }

    public async Task<IReadOnlyList<AgentEmbeddingModelDescriptor>> ListEmbeddingModelsAsync(string? providerId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            return [];
        }

        var provider = (await ListEmbeddingProvidersAsync(cancellationToken, omitUnavailable: true).ConfigureAwait(false)).FirstOrDefault(x => string.Equals(
            x.Descriptor.ProviderId,
            providerId,
            StringComparison.OrdinalIgnoreCase));
        return await ListEmbeddingModelsAsync(provider.Provider, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<AgentEmbeddingModelDescriptor>> ListEmbeddingModelsAsync(
        ServiceProvider<AgentServices.IEmbeddingProviderService>? provider,
        CancellationToken cancellationToken)
    {
        if (provider is null)
        {
            return [];
        }

        try
        {
            return (await provider.Service.ListModelsAsync(cancellationToken).ConfigureAwait(false))
                .OrderBy(model => model.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (SunderRpcException exception) when (
            !cancellationToken.IsCancellationRequested
            && exception.Error.Kind is SunderRpcErrorKind.StaleEndpoint or SunderRpcErrorKind.Unavailable)
        {
            return [];
        }
    }

    public async Task<AgentProviderReadiness?> GetChatProviderReadinessAsync(string? providerId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            return null;
        }

        var provider = (await ListChatProvidersAsync(cancellationToken, omitUnavailable: true).ConfigureAwait(false)).FirstOrDefault(x => string.Equals(
            x.Descriptor.ProviderId,
            providerId,
            StringComparison.OrdinalIgnoreCase));
        return await GetChatProviderReadinessAsync(provider.Provider, provider.Descriptor?.ProviderId, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<AgentProviderReadiness?> GetChatProviderReadinessAsync(
        ServiceProvider<AgentServices.IChatProviderService>? provider,
        string? providerId,
        CancellationToken cancellationToken)
    {
        if (provider is null)
        {
            return null;
        }

        try
        {
            return await provider.Service.GetReadinessAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SunderRpcException exception) when (!cancellationToken.IsCancellationRequested
            && exception.Error.Kind is SunderRpcErrorKind.StaleEndpoint or SunderRpcErrorKind.Unavailable)
        {
            return new AgentProviderReadiness(
                providerId!,
                AgentProviderReadinessStatus.Failed,
                ProviderUnavailableMessage);
        }
    }

    public async Task<AgentEmbeddingProviderReadiness?> GetEmbeddingProviderReadinessAsync(string? providerId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            return null;
        }

        var provider = (await ListEmbeddingProvidersAsync(cancellationToken, omitUnavailable: true).ConfigureAwait(false)).FirstOrDefault(x => string.Equals(
            x.Descriptor.ProviderId,
            providerId,
            StringComparison.OrdinalIgnoreCase));
        return await GetEmbeddingProviderReadinessAsync(provider.Provider, provider.Descriptor?.ProviderId, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<AgentEmbeddingProviderReadiness?> GetEmbeddingProviderReadinessAsync(
        ServiceProvider<AgentServices.IEmbeddingProviderService>? provider,
        string? providerId,
        CancellationToken cancellationToken)
    {
        if (provider is null)
        {
            return null;
        }

        try
        {
            return await provider.Service.GetReadinessAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SunderRpcException exception) when (
            !cancellationToken.IsCancellationRequested
            && exception.Error.Kind is SunderRpcErrorKind.StaleEndpoint or SunderRpcErrorKind.Unavailable)
        {
            return new AgentEmbeddingProviderReadiness(
                providerId!,
                AgentProviderReadinessStatus.Failed,
                ProviderUnavailableMessage);
        }
    }

    internal async Task<IReadOnlyList<(ServiceProvider<AgentServices.IChatProviderService> Provider, AgentProviderDescriptor Descriptor)>>
        ListChatProvidersAsync(
            CancellationToken cancellationToken = default,
            bool omitUnavailable = false)
    {
        var result = new List<(ServiceProvider<AgentServices.IChatProviderService>, AgentProviderDescriptor)>();
        var catalog = await _chatProviders.ListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var provider in catalog.Providers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (provider.State != ServiceProviderState.Active) continue;
            try
            {
                var descriptor = await provider.Service.DescribeAsync(cancellationToken).ConfigureAwait(false);
                result.Add((provider, descriptor with { SupportedAuthModes = descriptor.SupportedAuthModes.ToArray() }));
            }
            catch (SunderRpcException exception) when (omitUnavailable && !cancellationToken.IsCancellationRequested
                && exception.Error.Kind is SunderRpcErrorKind.StaleEndpoint or SunderRpcErrorKind.Unavailable) { }
        }
        return result;
    }

    internal async Task<IReadOnlyList<(ServiceProvider<AgentServices.ISelectableCapabilityProviderService> Provider, AgentServiceIdentity Descriptor)>>
        ListSelectableProvidersAsync(CancellationToken cancellationToken)
    {
        var result = new List<(ServiceProvider<AgentServices.ISelectableCapabilityProviderService>, AgentServiceIdentity)>();
        var catalog = await _selectableProviders.ListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var provider in catalog.Providers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (provider.State != ServiceProviderState.Active) continue;
            try { result.Add((provider, await provider.Service.DescribeAsync(cancellationToken).ConfigureAwait(false))); }
            catch (SunderRpcException exception) when (!cancellationToken.IsCancellationRequested
                && exception.Error.Kind is SunderRpcErrorKind.StaleEndpoint or SunderRpcErrorKind.Unavailable) { }
        }
        return result;
    }

    internal async Task<IReadOnlyList<(ServiceProvider<AgentServices.IEmbeddingProviderService> Provider, AgentEmbeddingProviderDescriptor Descriptor)>>
        ListEmbeddingProvidersAsync(CancellationToken cancellationToken, bool omitUnavailable = false)
    {
        ServiceProviderCatalog<AgentServices.IEmbeddingProviderService> catalog;
        try
        {
            catalog = await _embeddingProviders.ListAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SunderRpcException exception) when (
            !cancellationToken.IsCancellationRequested
            && exception.Error.Kind == SunderRpcErrorKind.Unavailable)
        {
            return [];
        }

        var providers = new List<(ServiceProvider<AgentServices.IEmbeddingProviderService>, AgentEmbeddingProviderDescriptor)>();
        foreach (var provider in catalog.Providers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (provider.State != ServiceProviderState.Active) continue;
            try
            {
                var descriptor = await provider.Service.DescribeAsync(cancellationToken).ConfigureAwait(false);
                providers.Add((provider, descriptor));
            }
            catch (SunderRpcException exception) when (omitUnavailable
                && !cancellationToken.IsCancellationRequested
                && exception.Error.Kind is SunderRpcErrorKind.StaleEndpoint or SunderRpcErrorKind.Unavailable)
            {
                // Optional catalog entries that retired during discovery are omitted.
            }
        }
        return providers;
    }

    private static IReadOnlyList<AgentProfileModelBindingRecord> BuildModelBindings(
        string? profileId,
        string? chatProviderId,
        string? chatModelId,
        string? chatSettingsJson,
        string? embeddingProviderId,
        string? embeddingModelId,
        string? embeddingSettingsJson,
        DateTimeOffset updatedAtUtc)
        => new[]
            {
                BuildModelBinding(profileId, AgentModelCapabilityKinds.Chat, chatProviderId, chatModelId, chatSettingsJson, updatedAtUtc),
                BuildModelBinding(profileId, AgentModelCapabilityKinds.Embedding, embeddingProviderId, embeddingModelId, embeddingSettingsJson, updatedAtUtc),
            }
            .Where(binding => binding is not null)
            .Select(binding => binding!)
            .ToArray();

    private static AgentProfileModelBindingRecord? BuildModelBinding(
        string? profileId,
        string capabilityKind,
        string? providerId,
        string? modelId,
        string? settingsJson,
        DateTimeOffset updatedAtUtc)
        => string.IsNullOrWhiteSpace(providerId) && string.IsNullOrWhiteSpace(modelId)
            ? null
            : new(
                profileId ?? string.Empty,
                capabilityKind,
                string.IsNullOrWhiteSpace(providerId) ? null : providerId.Trim(),
                string.IsNullOrWhiteSpace(modelId) ? null : modelId.Trim(),
                SettingsJson: NormalizeNullable(settingsJson),
                updatedAtUtc);

    private static AgentProfileModelBindingRecord? FindModelBinding(AgentProfileRecord profile, string capabilityKind)
    {
        var binding = profile.ModelBindings?.FirstOrDefault(candidate =>
            string.Equals(candidate.CapabilityKind, capabilityKind, StringComparison.OrdinalIgnoreCase));
        if (binding is not null)
        {
            return binding;
        }

        return string.Equals(capabilityKind, AgentModelCapabilityKinds.Chat, StringComparison.OrdinalIgnoreCase)
            ? BuildModelBinding(profile.ProfileId, capabilityKind, profile.ChatProviderId, profile.ChatModelId, settingsJson: null, profile.UpdatedAtUtc)
            : string.Equals(capabilityKind, AgentModelCapabilityKinds.Embedding, StringComparison.OrdinalIgnoreCase)
                ? BuildModelBinding(profile.ProfileId, capabilityKind, profile.EmbeddingProviderId, profile.EmbeddingModelId, settingsJson: null, profile.UpdatedAtUtc)
                : null;
    }

    private static string? NormalizeNullable(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

internal sealed class AgentProfileOperationException(string message) : InvalidOperationException(message);
