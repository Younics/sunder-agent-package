using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Contracts.Services;
using Sunder.Package.Agent.Storage;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Services;

public sealed class AgentProfileService : IDisposable
{
    private readonly AgentLocalStore _store;
    private readonly AgentToolService _toolService;
    private readonly IPackageExtensionCatalog _extensionCatalog;
    private readonly AgentProfileSelectableCapabilityChangeObserver _capabilityChangeObserver;
    private bool _disposed;

    public AgentProfileService(
        AgentLocalStore store,
        AgentToolService toolService,
        IPackageExtensionCatalog extensionCatalog)
    {
        _store = store;
        _toolService = toolService;
        _extensionCatalog = extensionCatalog;
        _capabilityChangeObserver = new AgentProfileSelectableCapabilityChangeObserver(extensionCatalog);
        _capabilityChangeObserver.Changed += OnSelectableCapabilitiesChanged;
    }

    public event Action<string>? ProfileChanged;

    public event Action? SelectableCapabilitiesChanged;

    public IReadOnlyList<AgentProfileRecord> ListProfiles() => _store.ListProfiles();

    public AgentProfileRecord? GetProfile(string profileId) => _store.GetProfile(profileId);

    public async Task<AgentProfileRecord> CreateProfileAsync(string displayName, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var chatProviders = ListChatProviders();
        var chatProvider = chatProviders.FirstOrDefault();
        var chatModel = chatProvider is null
            ? null
            : (await chatProvider.GetAvailableModelsAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault();
        var profileId = Guid.NewGuid().ToString("N");

        var record = new AgentProfileRecord(
            profileId,
            displayName,
            null,
            null,
            chatProvider?.Descriptor.ProviderId,
            chatModel?.ModelId,
            null,
            null,
            now,
            now,
            BuildModelBindings(
                profileId,
                chatProvider?.Descriptor.ProviderId,
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

    public IReadOnlyList<IAgentBehaviorLoop> ListBehaviorLoops()
        => _extensionCatalog.GetExtensions(PackageExtensionPoints.BehaviorLoops)
            .OrderBy(loop => loop.Descriptor.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public AgentProfileModelBindingRecord? GetModelBinding(string profileId, string capabilityKind)
        => _store.GetProfileModelBinding(profileId, capabilityKind);

    public AgentProfileModelBindingRecord? GetChatBinding(string profileId)
        => GetModelBinding(profileId, AgentModelCapabilityKinds.Chat);

    public AgentProfileModelBindingRecord? GetEmbeddingBinding(string profileId)
        => GetModelBinding(profileId, AgentModelCapabilityKinds.Embedding);

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

    public IReadOnlyList<IAgentChatProvider> ListChatProviders()
        => _extensionCatalog.GetExtensions(PackageExtensionPoints.ChatProviders)
            .OrderBy(provider => provider.Descriptor.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public IReadOnlyList<IAgentEmbeddingProvider> ListEmbeddingProviders()
        => _extensionCatalog.GetExtensions(PackageExtensionPoints.EmbeddingProviders)
            .OrderBy(provider => provider.Descriptor.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public bool HasProfileCapabilityConsumers(string capabilityKind)
        => !string.IsNullOrWhiteSpace(capabilityKind)
           && _extensionCatalog.GetExtensions(PackageExtensionPoints.ProfileCapabilityConsumers)
               .Any(consumer => consumer.ListConsumedCapabilities()
                   .Any(capability => string.Equals(capability.CapabilityKind, capabilityKind, StringComparison.OrdinalIgnoreCase)));

    public async Task<IReadOnlyList<AgentProfileSelectableCapabilityDescriptor>> ListSelectableProfileCapabilitiesAsync(
        AgentProfileRecord? profile = null,
        CancellationToken cancellationToken = default)
    {
        _capabilityChangeObserver.RefreshProviderSubscriptions();
        var request = new AgentProfileSelectableCapabilityRequest(profile);
        var capabilities = new List<AgentProfileSelectableCapabilityDescriptor>();
        foreach (var provider in _extensionCatalog.GetExtensions(PackageExtensionPoints.ProfileSelectableCapabilityProviders)
                     .OrderBy(provider => provider.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            capabilities.AddRange(await provider.ListCapabilitiesAsync(request, cancellationToken).ConfigureAwait(false));
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

        var provider = ListChatProviders().FirstOrDefault(x => string.Equals(x.Descriptor.ProviderId, providerId, StringComparison.OrdinalIgnoreCase));
        return provider is null
            ? []
            : (await provider.GetAvailableModelsAsync(cancellationToken).ConfigureAwait(false))
                .OrderBy(model => model.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
    }

    public async Task<IReadOnlyList<AgentEmbeddingModelDescriptor>> ListEmbeddingModelsAsync(string? providerId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            return [];
        }

        var provider = ListEmbeddingProviders().FirstOrDefault(x => string.Equals(x.Descriptor.ProviderId, providerId, StringComparison.OrdinalIgnoreCase));
        return provider is null
            ? []
            : (await provider.GetAvailableModelsAsync(cancellationToken).ConfigureAwait(false))
                .OrderBy(model => model.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
    }

    public async Task<AgentProviderReadiness?> GetChatProviderReadinessAsync(string? providerId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            return null;
        }

        var provider = ListChatProviders().FirstOrDefault(x => string.Equals(x.Descriptor.ProviderId, providerId, StringComparison.OrdinalIgnoreCase));
        return provider is null
            ? null
            : await provider.GetReadinessAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<AgentEmbeddingProviderReadiness?> GetEmbeddingProviderReadinessAsync(string? providerId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            return null;
        }

        var provider = ListEmbeddingProviders().FirstOrDefault(x => string.Equals(x.Descriptor.ProviderId, providerId, StringComparison.OrdinalIgnoreCase));
        return provider is null
            ? null
            : await provider.GetReadinessAsync(cancellationToken).ConfigureAwait(false);
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
