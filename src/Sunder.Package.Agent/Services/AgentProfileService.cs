using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Contracts.Services;
using Sunder.Package.Agent.Storage;
using Sunder.Package.Agent.Runtime;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Services;

public sealed class AgentProfileService : IDisposable, IAgentProfileGateway
{
    private readonly AgentLocalStore _store;
    private readonly AgentToolService _toolService;
    private readonly AgentRpcCatalog _rpcCatalog;
    private readonly AgentRpcProviderService<IAgentBehaviorLoop> _behaviorLoops;
    private readonly AgentProfileSelectableCapabilityChangeObserver _capabilityChangeObserver;
    private bool _disposed;

    public AgentProfileService(
        AgentLocalStore store,
        AgentToolService toolService,
        AgentRpcCatalog rpcCatalog,
        AgentRpcProviderService<IAgentBehaviorLoop> behaviorLoops)
    {
        _store = store;
        _toolService = toolService;
        _rpcCatalog = rpcCatalog;
        _behaviorLoops = behaviorLoops;
        _capabilityChangeObserver = new AgentProfileSelectableCapabilityChangeObserver(rpcCatalog);
        _capabilityChangeObserver.Changed += OnSelectableCapabilitiesChanged;
    }

    public event Action<string>? ProfileChanged;

    public event Action? SelectableCapabilitiesChanged;

    public IReadOnlyList<AgentProfileRecord> ListProfiles() => _store.ListProfiles();

    public AgentProfileRecord? GetProfile(string profileId) => _store.GetProfile(profileId);

    public async Task<AgentProfileRecord> CreateProfileAsync(string displayName, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var chatProvider = GetChatProviderReferences(cancellationToken)
            .OrderBy(provider => provider.Metadata.DisplayName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        var orderedChatModels = chatProvider is null
            ? []
            : (await AgentRpcInvocation.InvokeAsync(
                    chatProvider,
                    cancellationToken,
                    static (provider, token) => provider.GetAvailableModelsAsync(token))
                .ConfigureAwait(false))
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
            chatProvider?.Metadata.ProviderId,
            chatModel?.ModelId,
            null,
            null,
            now,
            now,
            BuildModelBindings(
                profileId,
                chatProvider?.Metadata.ProviderId,
                chatModel?.ModelId,
                chatSettingsJson: null,
                embeddingProviderId: null,
                embeddingModelId: null,
                embeddingSettingsJson: null,
                now),
            []
        );

        _store.SaveProfile(record);
        ProfileChanged?.Invoke(record.ProfileId);
        return record;
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
        var existing = _store.GetProfile(profileId) ?? throw new InvalidOperationException($"Profile '{profileId}' was not found.");
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
        ProfileChanged?.Invoke(profileId);
    }

    public IReadOnlyList<AgentBehaviorLoopDescriptor> ListBehaviorLoopDescriptors()
        => AgentRpcInvocation.Snapshot(
                _rpcCatalog,
                _behaviorLoops,
                static loop => loop.Descriptor with
                {
                    FeatureKinds = loop.Descriptor.FeatureKinds?.ToArray(),
                },
                omitUnavailable: true)
            .Select(static loop => loop.Metadata)
            .OrderBy(loop => loop.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public AgentProfileModelBindingRecord? GetModelBinding(string profileId, string capabilityKind)
        => _store.GetProfileModelBinding(profileId, capabilityKind);

    public AgentProfileModelBindingRecord? GetChatBinding(string profileId)
        => GetModelBinding(profileId, AgentModelCapabilityKinds.Chat);

    public void DeleteProfile(string profileId)
    {
        _store.DeleteProfile(profileId);
        ProfileChanged?.Invoke(profileId);
    }

    public void SaveRuntimeProfile(AgentProfileRecord profile)
    {
        _store.SaveProfile(profile with { IsInternal = true });
        ProfileChanged?.Invoke(profile.ProfileId);
    }

    public void ImportProfile(AgentProfileRecord profile)
    {
        _store.SaveProfile(profile with { IsInternal = false });
        ProfileChanged?.Invoke(profile.ProfileId);
    }

    public void NotifyProfileImported(string profileId)
    {
        if (!string.IsNullOrWhiteSpace(profileId))
        {
            ProfileChanged?.Invoke(profileId);
        }
    }

    public IReadOnlyList<AgentProviderDescriptor> ListChatProviderDescriptors()
        => GetChatProviderReferences(omitUnavailable: true)
            .Select(static provider => provider.Metadata)
            .OrderBy(provider => provider.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public IReadOnlyList<AgentEmbeddingProviderDescriptor> ListEmbeddingProviderDescriptors()
        => GetEmbeddingProviderReferences(omitUnavailable: true)
            .Select(static provider => provider.Metadata)
            .OrderBy(provider => provider.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public bool HasProfileCapabilityConsumers(string capabilityKind)
    {
        if (string.IsNullOrWhiteSpace(capabilityKind))
        {
            return false;
        }

        foreach (var consumer in AgentRpcInvocation.Snapshot(
                     _rpcCatalog,
                     AgentRpcServices.ProfileCapabilityConsumers,
                     static instance => instance.ListConsumedCapabilities().ToArray(),
                     omitUnavailable: true))
        {
            if (consumer.Metadata.Any(capability => string.Equals(
                    capability.CapabilityKind,
                    capabilityKind,
                    StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    public async Task<IReadOnlyList<AgentProfileSelectableCapabilityDescriptor>> ListSelectableProfileCapabilitiesAsync(
        AgentProfileRecord? profile = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _capabilityChangeObserver.RefreshProviderSubscriptions();
        }
        catch (SunderRpcException exception) when (exception.Error.Kind == SunderRpcErrorKind.Cancelled)
        {
            throw AgentRpcInvocation.Cancelled(exception, cancellationToken);
        }
        var request = new AgentProfileSelectableCapabilityRequest(profile);
        var capabilities = new List<AgentProfileSelectableCapabilityDescriptor>();
        var providers = AgentRpcInvocation.Snapshot(
            _rpcCatalog,
            AgentRpcServices.SelectableCapabilityProviders,
            static provider => provider.DisplayName,
            cancellationToken,
            omitUnavailable: true);
        foreach (var provider in providers
                     .OrderBy(provider => provider.Metadata, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                capabilities.AddRange(await AgentRpcInvocation.InvokeAsync(
                    provider,
                    cancellationToken,
                    (instance, token) => instance.ListCapabilitiesAsync(request, token)).ConfigureAwait(false));
            }
            catch (AgentPackageUnavailableException)
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
        SelectableCapabilitiesChanged?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _capabilityChangeObserver.Changed -= OnSelectableCapabilitiesChanged;
        _capabilityChangeObserver.Dispose();
    }

    public Task<IReadOnlyList<AgentToolCatalogEntry>> ListInstalledLocalToolsAsync(CancellationToken cancellationToken = default)
        => _toolService.ListInstalledLocalToolsAsync(cancellationToken);

    public async Task<IReadOnlyList<AgentModelDescriptor>> ListChatModelsAsync(string? providerId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            return [];
        }

        var provider = GetChatProviderReferences(cancellationToken, omitUnavailable: true).FirstOrDefault(x => string.Equals(
            x.Metadata.ProviderId,
            providerId,
            StringComparison.OrdinalIgnoreCase));
        if (provider is null)
        {
            return [];
        }

        try
        {
            return (await AgentRpcInvocation.InvokeAsync(
                    provider,
                    cancellationToken,
                    static (instance, token) => instance.GetAvailableModelsAsync(token))
                .ConfigureAwait(false))
                .OrderNewestFirst()
                .ToArray();
        }
        catch (AgentPackageUnavailableException)
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

        var provider = GetEmbeddingProviderReferences(cancellationToken, omitUnavailable: true).FirstOrDefault(x => string.Equals(
            x.Metadata.ProviderId,
            providerId,
            StringComparison.OrdinalIgnoreCase));
        if (provider is null)
        {
            return [];
        }

        try
        {
            return (await AgentRpcInvocation.InvokeAsync(
                    provider,
                    cancellationToken,
                    static (instance, token) => instance.GetAvailableModelsAsync(token))
                .ConfigureAwait(false))
                .OrderBy(model => model.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (AgentPackageUnavailableException)
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

        var provider = GetChatProviderReferences(cancellationToken, omitUnavailable: true).FirstOrDefault(x => string.Equals(
            x.Metadata.ProviderId,
            providerId,
            StringComparison.OrdinalIgnoreCase));
        if (provider is null)
        {
            return null;
        }

        try
        {
            return await AgentRpcInvocation.InvokeAsync(
                provider,
                cancellationToken,
                static (instance, token) => instance.GetReadinessAsync(token)).ConfigureAwait(false);
        }
        catch (AgentPackageUnavailableException)
        {
            return new AgentProviderReadiness(
                provider.Metadata.ProviderId,
                AgentProviderReadinessStatus.Failed,
                AgentRpcInvocation.PackageUnavailableMessage);
        }
    }

    public async Task<AgentEmbeddingProviderReadiness?> GetEmbeddingProviderReadinessAsync(string? providerId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            return null;
        }

        var provider = GetEmbeddingProviderReferences(cancellationToken, omitUnavailable: true).FirstOrDefault(x => string.Equals(
            x.Metadata.ProviderId,
            providerId,
            StringComparison.OrdinalIgnoreCase));
        if (provider is null)
        {
            return null;
        }

        try
        {
            return await AgentRpcInvocation.InvokeAsync(
                provider,
                cancellationToken,
                static (instance, token) => instance.GetReadinessAsync(token)).ConfigureAwait(false);
        }
        catch (AgentPackageUnavailableException)
        {
            return new AgentEmbeddingProviderReadiness(
                provider.Metadata.ProviderId,
                AgentProviderReadinessStatus.Failed,
                AgentRpcInvocation.PackageUnavailableMessage);
        }
    }

    private IReadOnlyList<AgentRpcOwnedReference<IAgentChatProvider, AgentProviderDescriptor>>
        GetChatProviderReferences(
            CancellationToken cancellationToken = default,
            bool omitUnavailable = false)
        => AgentRpcInvocation.Snapshot(
            _rpcCatalog,
            AgentRpcServices.ChatProviders,
            static provider => provider.Descriptor with
            {
                SupportedAuthModes = provider.Descriptor.SupportedAuthModes.ToArray(),
            },
            cancellationToken,
            omitUnavailable);

    private IReadOnlyList<AgentRpcOwnedReference<IAgentEmbeddingProvider, AgentEmbeddingProviderDescriptor>>
        GetEmbeddingProviderReferences(
            CancellationToken cancellationToken = default,
            bool omitUnavailable = false)
        => AgentRpcInvocation.Snapshot(
            _rpcCatalog,
            AgentRpcServices.EmbeddingProviders,
            static provider => provider.Descriptor,
            cancellationToken,
            omitUnavailable);

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
