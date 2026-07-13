using System.Text.Json;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Stacks;

namespace Sunder.Package.Agent.Services;

public sealed class AgentProfileStackContributor(
    AgentProfileService profileService,
    IPackageContext packageContext,
    IPackageExtensionCatalog extensionCatalog) : IPackageStackContributor, IPackageStackImportAppliedHandler
{
    private const string PackageId = "sunder.package.agent";
    private const string SchemaId = "sunder.package.agent/profile";
    private const string DetailDescription = "description";
    private const string DetailInstructions = "instructions";
    private const string DetailProviders = "provider-connections";
    private const string DetailModels = "model-choices";
    private const string DetailBehaviorLoop = "behavior-loop";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public string ContributorId => "sunder.package.agent.profiles";

    public string DisplayName => "Agent Profiles";

    public ValueTask<IReadOnlyList<StackExportItemDescriptor>> ListExportItemsAsync(
        StackExportDiscoveryContext context,
        CancellationToken cancellationToken = default)
    {
        var profiles = profileService.ListProfiles()
            .Where(profile => !profile.IsInternal)
            .OrderBy(profile => profile.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(profile => new StackExportItemDescriptor(
                profile.ProfileId,
                profile.DisplayName,
                "agent-profile",
                Description: null,
                DefaultSelected: true,
                Sensitivities: [StackValueSensitivity.Public],
                Details: BuildExportDetails(profile)))
            .ToArray();
        return ValueTask.FromResult<IReadOnlyList<StackExportItemDescriptor>>(profiles);
    }

    public ValueTask<StackExportContribution> ExportAsync(
        StackExportRequest request,
        CancellationToken cancellationToken = default)
    {
        var selectedIds = request.ItemIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var profiles = profileService.ListProfiles()
            .Where(profile => selectedIds.Contains(profile.ProfileId) && !profile.IsInternal)
            .ToArray();
        var payloads = profiles
            .Select(profile => AgentProfileStackPayload.FromProfile(profile, request))
            .ToArray();
        var fragments = profiles
            .Zip(payloads, (profile, payload) => new StackFragmentExport(
                FragmentId: "agent-profile." + SanitizeId(profile.ProfileId),
                ContributorId,
                SchemaId,
                SchemaVersion: 1,
                DisplayName: profile.DisplayName,
                JsonPayload: JsonSerializer.Serialize(payload, JsonOptions),
                Description: payload.Description,
                DefaultSelected: true,
                SourceItemId: profile.ProfileId))
            .ToArray();
        return ValueTask.FromResult(new StackExportContribution(
            fragments,
            fragments.Length == 0 ? [] : BuildPackageRequirements(payloads),
            []));
    }

    public ValueTask<StackImportPreview> PreviewImportAsync(
        StackImportPreviewRequest request,
        CancellationToken cancellationToken = default)
    {
        var actions = new List<StackImportAction>();
        var warnings = new List<string>();
        var conflicts = new List<StackImportConflict>();
        foreach (var fragment in request.Fragments)
        {
            if (!TryReadPayload(fragment, warnings, out var payload) || payload is null)
            {
                continue;
            }

            var existing = profileService.GetProfile(payload.ProfileId);
            var actionKind = existing is null ? StackImportActionKind.Create : StackImportActionKind.Update;
            actions.Add(new StackImportAction(
                BuildActionId(fragment.FragmentId, payload.ProfileId),
                existing is null ? $"Create agent profile '{payload.DisplayName}'" : $"Update agent profile '{payload.DisplayName}'",
                actionKind,
                DefaultSelected: true,
                Description: payload.Description));
        }

        return ValueTask.FromResult(new StackImportPreview(actions, [], conflicts, warnings));
    }

    public ValueTask<StackImportResult> ImportAsync(
        StackImportRequest request,
        CancellationToken cancellationToken = default)
    {
        var selectedActionIds = request.SelectedActionIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var imported = new List<StackImportedItem>();
        var warnings = new List<string>();
        var errors = new List<string>();
        var idRemaps = new Dictionary<string, string>(request.IdRemaps, StringComparer.OrdinalIgnoreCase);
        foreach (var fragment in request.Fragments)
        {
            if (!TryReadPayload(fragment, warnings, out var payload) || payload is null)
            {
                continue;
            }

            var actionId = BuildActionId(fragment.FragmentId, payload.ProfileId);
            if (!selectedActionIds.Contains(actionId))
            {
                continue;
            }

            try
            {
                var profileId = ResolveProfileId(payload.ProfileId, idRemaps);
                var now = DateTimeOffset.UtcNow;
                var profile = payload.ToProfile(profileId, now);
                profileService.ImportProfile(profile);
                idRemaps[payload.ProfileId] = profileId;
                imported.Add(new StackImportedItem(profileId, profile.DisplayName, "agent-profile"));
            }
            catch (Exception ex)
            {
                errors.Add($"Failed to import agent profile '{payload.DisplayName}': {ex.Message}");
            }
        }

        var outcome = errors.Count == 0
            ? StackImportOutcome.Completed
            : imported.Count == 0 ? StackImportOutcome.Failed : StackImportOutcome.Partial;
        return ValueTask.FromResult(new StackImportResult(outcome, imported, idRemaps, warnings, errors));
    }

    public ValueTask OnStackImportAppliedAsync(
        StackImportAppliedContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var item in context.ImportedItems)
        {
            profileService.NotifyProfileImported(item.ItemId);
        }

        return ValueTask.CompletedTask;
    }

    private static bool TryReadPayload(StackFragmentImport fragment, ICollection<string> warnings, out AgentProfileStackPayload? payload)
    {
        try
        {
            payload = JsonSerializer.Deserialize<AgentProfileStackPayload>(fragment.JsonPayload, JsonOptions);
            if (payload is null || string.IsNullOrWhiteSpace(payload.ProfileId) || string.IsNullOrWhiteSpace(payload.DisplayName))
            {
                warnings.Add($"Stack fragment '{fragment.FragmentId}' does not contain a valid agent profile payload.");
                payload = null;
                return false;
            }

            return true;
        }
        catch (JsonException ex)
        {
            warnings.Add($"Stack fragment '{fragment.FragmentId}' profile payload could not be parsed: {ex.Message}");
            payload = null;
            return false;
        }
    }

    private static string BuildActionId(string fragmentId, string profileId)
        => $"agent-profile:{fragmentId}:{profileId}";

    private IReadOnlyList<StackPackageRequirement> BuildPackageRequirements(IReadOnlyList<AgentProfileStackPayload> payloads)
    {
        var packageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { PackageId };
        var providerIds = payloads
            .SelectMany(payload => new[]
            {
                payload.ChatProviderId,
                payload.EmbeddingProviderId,
            }.Concat(payload.ModelBindings?.Select(binding => binding.ProviderId) ?? []))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        AddProviderPackages(packageIds, providerIds);

        var behaviorLoopIds = payloads
            .Select(payload => payload.BehaviorLoopId)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var behaviorLoopSourceIds = payloads
            .Select(payload => payload.BehaviorLoopSourceId)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var contribution in extensionCatalog.GetExtensionContributions(PackageExtensionPoints.BehaviorLoops))
        {
            var descriptor = contribution.Contribution.Descriptor;
            if (behaviorLoopIds.Contains(descriptor.LoopId)
                || (!string.IsNullOrWhiteSpace(descriptor.SourceId) && behaviorLoopSourceIds.Contains(descriptor.SourceId)))
            {
                AddPackageId(packageIds, contribution.PackageId);
            }
        }

        AddSelectableCapabilityPackages(packageIds, payloads.SelectMany(payload => payload.SelectableCapabilityAssignments ?? []));

        return packageIds
            .OrderBy(packageId => string.Equals(packageId, PackageId, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(packageId => packageId, StringComparer.OrdinalIgnoreCase)
            .Select(packageId => string.Equals(packageId, PackageId, StringComparison.OrdinalIgnoreCase)
                ? new StackPackageRequirement(PackageId, CreatedWithVersion: packageContext.Version.ToString(), MinimumVersion: "1.0.0")
                : new StackPackageRequirement(packageId))
            .ToArray();
    }

    private void AddProviderPackages(ISet<string> packageIds, ISet<string> providerIds)
    {
        if (providerIds.Count == 0)
        {
            return;
        }

        foreach (var contribution in extensionCatalog.GetExtensionContributions(PackageExtensionPoints.ChatProviders))
        {
            if (providerIds.Contains(contribution.Contribution.Descriptor.ProviderId))
            {
                AddPackageId(packageIds, contribution.PackageId);
            }
        }

        foreach (var contribution in extensionCatalog.GetExtensionContributions(PackageExtensionPoints.EmbeddingProviders))
        {
            if (providerIds.Contains(contribution.Contribution.Descriptor.ProviderId))
            {
                AddPackageId(packageIds, contribution.PackageId);
            }
        }
    }

    private void AddSelectableCapabilityPackages(
        ISet<string> packageIds,
        IEnumerable<AgentProfileSelectableCapabilityAssignmentRecord> assignments)
    {
        var sourceIds = assignments
            .Select(assignment => assignment.SourceId)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (sourceIds.Count == 0)
        {
            return;
        }

        foreach (var contribution in extensionCatalog.GetExtensionContributions(PackageExtensionPoints.ProfileSelectableCapabilityProviders))
        {
            if (sourceIds.Contains(contribution.Contribution.ProviderId))
            {
                AddPackageId(packageIds, contribution.PackageId);
            }
        }
    }

    private static void AddPackageId(ISet<string> packageIds, string? packageId)
    {
        if (!string.IsNullOrWhiteSpace(packageId))
        {
            packageIds.Add(packageId.Trim());
        }
    }

    private static string ResolveProfileId(string profileId, IReadOnlyDictionary<string, string> idRemaps)
        => idRemaps.TryGetValue(profileId, out var remappedId) && !string.IsNullOrWhiteSpace(remappedId)
            ? remappedId
            : profileId;

    private static bool HasProviderBindings(AgentProfileRecord profile)
        => !string.IsNullOrWhiteSpace(profile.ChatProviderId)
           || !string.IsNullOrWhiteSpace(profile.ChatModelId)
           || !string.IsNullOrWhiteSpace(profile.EmbeddingProviderId)
           || !string.IsNullOrWhiteSpace(profile.EmbeddingModelId)
           || (profile.ModelBindings?.Any(binding => !string.IsNullOrWhiteSpace(binding.ProviderId) || !string.IsNullOrWhiteSpace(binding.ModelId)) == true);

    private static string SanitizeId(string value)
        => string.Concat(value.Select(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' ? char.ToLowerInvariant(character) : '-'));

    private static IReadOnlyList<StackExportItemDetail> BuildExportDetails(AgentProfileRecord profile)
    {
        var details = new List<StackExportItemDetail>();
        if (!string.IsNullOrWhiteSpace(profile.Description))
        {
            details.Add(new StackExportItemDetail(
                "Profile description",
                profile.Description.Trim(),
                StackValueSensitivity.Public,
                ValueWhenExcluded: "Not exported",
                DetailId: DetailDescription));
        }

        if (!string.IsNullOrWhiteSpace(profile.Instructions))
        {
            details.Add(new StackExportItemDetail(
                "Custom instructions",
                profile.Instructions.Trim(),
                StackValueSensitivity.Public,
                ValueWhenExcluded: "Not exported",
                DetailId: DetailInstructions));
        }

        var providers = new[]
            {
                profile.ChatProviderId,
                profile.EmbeddingProviderId,
            }
            .Concat(profile.ModelBindings?.Select(binding => binding.ProviderId) ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (providers.Length > 0)
        {
            details.Add(new StackExportItemDetail(
                "Provider connections",
                string.Join(", ", providers),
                StackValueSensitivity.Public,
                ValueWhenExcluded: "Not exported",
                DetailId: DetailProviders));
        }

        var models = new[]
            {
                profile.ChatModelId,
                profile.EmbeddingModelId,
            }
            .Concat(profile.ModelBindings?.Select(binding => binding.ModelId) ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (models.Length > 0)
        {
            details.Add(new StackExportItemDetail(
                "Model choices",
                string.Join(", ", models),
                StackValueSensitivity.Public,
                ValueWhenExcluded: "Not exported",
                DetailId: DetailModels));
        }

        if (!string.IsNullOrWhiteSpace(profile.BehaviorLoopId))
        {
            details.Add(new StackExportItemDetail(
                "Behavior loop",
                string.IsNullOrWhiteSpace(profile.BehaviorLoopSettingsJson)
                    ? profile.BehaviorLoopId
                    : $"{profile.BehaviorLoopId}\n{profile.BehaviorLoopSettingsJson}",
                StackValueSensitivity.Public,
                ValueWhenExcluded: "Not exported",
                DetailId: DetailBehaviorLoop));
        }

        return details.Count == 0
            ? [new StackExportItemDetail("Profile settings", "No additional profile content", StackValueSensitivity.Public)]
            : details;
    }

    private sealed record AgentProfileStackPayload(
        string ProfileId,
        string DisplayName,
        string? Description,
        string? Instructions,
        string? ChatProviderId,
        string? ChatModelId,
        string? EmbeddingProviderId,
        string? EmbeddingModelId,
        IReadOnlyList<AgentProfileModelBindingRecord>? ModelBindings,
        IReadOnlyList<AgentProfileSelectableCapabilityAssignmentRecord>? SelectableCapabilityAssignments,
        string? BehaviorLoopId,
        string? BehaviorLoopSourceId,
        string? BehaviorLoopSettingsJson)
    {
        public static AgentProfileStackPayload FromProfile(AgentProfileRecord profile, StackExportRequest request)
            => new(
                profile.ProfileId,
                profile.DisplayName,
                request.IsDetailSelected(profile.ProfileId, DetailDescription) ? request.GetDetailValue(profile.ProfileId, DetailDescription, profile.Description ?? string.Empty) : null,
                request.IsDetailSelected(profile.ProfileId, DetailInstructions) ? request.GetDetailValue(profile.ProfileId, DetailInstructions, profile.Instructions ?? string.Empty) : null,
                request.IsDetailSelected(profile.ProfileId, DetailProviders) && request.IsDetailSelected(profile.ProfileId, DetailModels) ? profile.ChatProviderId : null,
                request.IsDetailSelected(profile.ProfileId, DetailProviders) && request.IsDetailSelected(profile.ProfileId, DetailModels) ? profile.ChatModelId : null,
                request.IsDetailSelected(profile.ProfileId, DetailProviders) && request.IsDetailSelected(profile.ProfileId, DetailModels) ? profile.EmbeddingProviderId : null,
                request.IsDetailSelected(profile.ProfileId, DetailProviders) && request.IsDetailSelected(profile.ProfileId, DetailModels) ? profile.EmbeddingModelId : null,
                request.IsDetailSelected(profile.ProfileId, DetailProviders) && request.IsDetailSelected(profile.ProfileId, DetailModels) ? profile.ModelBindings : [],
                profile.SelectableCapabilityAssignments,
                request.IsDetailSelected(profile.ProfileId, DetailBehaviorLoop) ? profile.BehaviorLoopId : null,
                request.IsDetailSelected(profile.ProfileId, DetailBehaviorLoop) ? profile.BehaviorLoopSourceId : null,
                request.IsDetailSelected(profile.ProfileId, DetailBehaviorLoop) ? profile.BehaviorLoopSettingsJson : null);

        public AgentProfileRecord ToProfile(string profileId, DateTimeOffset now)
            => new(
                profileId,
                DisplayName,
                Description,
                Instructions,
                ChatProviderId,
                ChatModelId,
                EmbeddingProviderId,
                EmbeddingModelId,
                now,
                now,
                ModelBindings?.Select(binding => binding with { ProfileId = profileId, UpdatedAtUtc = now }).ToArray(),
                SelectableCapabilityAssignments,
                BehaviorLoopId,
                BehaviorLoopSourceId,
                BehaviorLoopSettingsJson,
                IsInternal: false);
    }
}
