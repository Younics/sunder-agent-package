using System.Text.Json;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Subagents.Models;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Stacks;

namespace Sunder.Package.Agent.Subagents.Services;

internal sealed class SubagentStackContributor(
    SubagentService subagentService,
    IPackageContext packageContext,
    IPackageExtensionCatalog extensionCatalog) : IPackageStackContributor, IPackageStackImportAppliedHandler
{
    private const string SchemaId = "sunder.package.agent.subagents/subagent";
    private const string DetailDescription = "description";
    private const string DetailInstructions = "instructions";
    private const string DetailProvider = "provider";
    private const string DetailModel = "model";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public string ContributorId => "sunder.package.agent.subagents.subagents";

    public string DisplayName => "Subagents";

    public ValueTask<IReadOnlyList<StackExportItemDescriptor>> ListExportItemsAsync(
        StackExportDiscoveryContext context,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult<IReadOnlyList<StackExportItemDescriptor>>(subagentService.ListSubagents()
            .OrderBy(subagent => subagent.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(subagent => new StackExportItemDescriptor(
                subagent.SubagentId,
                subagent.DisplayName,
                "subagent",
                Description: null,
                DefaultSelected: true,
                Sensitivities: BuildSensitivities(subagent),
                Details: BuildExportDetails(subagent)))
            .ToArray());

    public ValueTask<StackExportContribution> ExportAsync(
        StackExportRequest request,
        CancellationToken cancellationToken = default)
    {
        var selectedIds = request.ItemIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var subagents = subagentService.ListSubagents()
            .Where(subagent => selectedIds.Contains(subagent.SubagentId))
            .ToArray();
        var payloads = subagents
            .Select(subagent => SubagentStackPayload.FromSubagent(subagent, request))
            .ToArray();
        var fragments = subagents
            .Zip(payloads, (subagent, payload) => new StackFragmentExport(
                FragmentId: "subagent." + SanitizeIdentifier(subagent.SubagentId),
                ContributorId,
                SchemaId,
                SchemaVersion: 1,
                DisplayName: subagent.DisplayName,
                JsonPayload: JsonSerializer.Serialize(payload, JsonOptions),
                Description: payload.Description,
                DefaultSelected: true,
                SourceItemId: subagent.SubagentId))
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
        foreach (var fragment in request.Fragments)
        {
            if (!TryReadPayload(fragment, warnings, out var payload) || payload is null)
            {
                continue;
            }

            var existing = subagentService.GetSubagent(payload.SubagentId);
            actions.Add(new StackImportAction(
                BuildActionId(fragment.FragmentId, payload.SubagentId),
                existing is null ? $"Create subagent '{payload.DisplayName}'" : $"Update subagent '{payload.DisplayName}'",
                existing is null ? StackImportActionKind.Create : StackImportActionKind.Update,
                DefaultSelected: true,
                Description: payload.Description));
        }

        return ValueTask.FromResult(new StackImportPreview(actions, [], [], warnings));
    }

    public ValueTask<StackImportResult> ImportAsync(
        StackImportRequest request,
        CancellationToken cancellationToken = default)
    {
        var selectedActionIds = request.SelectedActionIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();
        var errors = new List<string>();
        var selectedPayloads = new List<SubagentStackPayload>();
        foreach (var fragment in request.Fragments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryReadPayload(fragment, warnings, out var payload) || payload is null)
            {
                var actionPrefix = $"subagent:{fragment.FragmentId}:";
                if (selectedActionIds.Any(actionId => actionId.StartsWith(actionPrefix, StringComparison.OrdinalIgnoreCase)))
                {
                    errors.Add($"Selected subagent fragment '{fragment.FragmentId}' is invalid. No subagents were imported.");
                }

                continue;
            }

            var actionId = BuildActionId(fragment.FragmentId, payload.SubagentId);
            if (!selectedActionIds.Contains(actionId))
            {
                continue;
            }

            selectedPayloads.Add(payload);
        }

        if (errors.Count > 0)
        {
            return ValueTask.FromResult(new StackImportResult(StackImportOutcome.Failed, [], new Dictionary<string, string>(), warnings, errors));
        }

        var duplicateId = selectedPayloads
            .GroupBy(payload => payload.SubagentId, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1)?.Key;
        if (duplicateId is not null)
        {
            errors.Add($"The selected stack fragments contain duplicate subagent id '{duplicateId}'. No subagents were imported.");
            return ValueTask.FromResult(new StackImportResult(StackImportOutcome.Failed, [], new Dictionary<string, string>(), warnings, errors));
        }

        try
        {
            var saved = subagentService.ImportSubagents(selectedPayloads.Select(payload => payload.ToSubagent()).ToArray());
            var imported = saved
                .Select(subagent => new StackImportedItem(subagent.SubagentId, subagent.DisplayName, "subagent"))
                .ToArray();
            return ValueTask.FromResult(new StackImportResult(StackImportOutcome.Completed, imported, new Dictionary<string, string>(), warnings, []));
        }
        catch (Exception ex)
        {
            errors.Add($"Subagent import failed before the atomic store update completed: {ex.Message}");
            return ValueTask.FromResult(new StackImportResult(StackImportOutcome.Failed, [], new Dictionary<string, string>(), warnings, errors));
        }
    }

    public ValueTask OnStackImportAppliedAsync(
        StackImportAppliedContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (context.ImportedItems.Count > 0)
        {
            subagentService.NotifySubagentsImported();
        }

        return ValueTask.CompletedTask;
    }

    private StackPackageRequirement CreatePackageRequirement()
        => new(SubagentConstants.PackageId, CreatedWithVersion: packageContext.Version.ToString(), MinimumVersion: "1.0.0");

    private IReadOnlyList<StackPackageRequirement> BuildPackageRequirements(IReadOnlyList<SubagentStackPayload> payloads)
    {
        var packageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { SubagentConstants.PackageId };
        var providerIds = payloads
            .Select(payload => payload.ChatProviderId)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (providerIds.Count > 0)
        {
            foreach (var contribution in extensionCatalog.GetExtensionContributions(PackageExtensionPoints.ChatProviders))
            {
                if (providerIds.Contains(contribution.Contribution.Descriptor.ProviderId))
                {
                    AddPackageId(packageIds, contribution.PackageId);
                }
            }
        }

        var sourceIds = payloads
            .SelectMany(payload => payload.SelectableCapabilityAssignments ?? [])
            .Select(assignment => assignment.SourceId)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (sourceIds.Count > 0)
        {
            foreach (var contribution in extensionCatalog.GetExtensionContributions(PackageExtensionPoints.ProfileSelectableCapabilityProviders))
            {
                if (sourceIds.Contains(contribution.Contribution.ProviderId))
                {
                    AddPackageId(packageIds, contribution.PackageId);
                }
            }
        }

        return packageIds
            .OrderBy(packageId => string.Equals(packageId, SubagentConstants.PackageId, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(packageId => packageId, StringComparer.OrdinalIgnoreCase)
            .Select(packageId => string.Equals(packageId, SubagentConstants.PackageId, StringComparison.OrdinalIgnoreCase)
                ? CreatePackageRequirement()
                : new StackPackageRequirement(packageId))
            .ToArray();
    }

    private static void AddPackageId(ISet<string> packageIds, string? packageId)
    {
        if (!string.IsNullOrWhiteSpace(packageId))
        {
            packageIds.Add(packageId.Trim());
        }
    }

    private static IReadOnlyList<StackValueSensitivity> BuildSensitivities(SubagentRecord subagent)
    {
        return [StackValueSensitivity.Public];
    }

    private static IReadOnlyList<StackExportItemDetail> BuildExportDetails(SubagentRecord subagent)
    {
        var details = new List<StackExportItemDetail>();
        if (!string.IsNullOrWhiteSpace(subagent.Description))
        {
            details.Add(new StackExportItemDetail(
                "Subagent description",
                subagent.Description.Trim(),
                StackValueSensitivity.Public,
                ValueWhenExcluded: "Not exported",
                DetailId: DetailDescription));
        }

        if (!string.IsNullOrWhiteSpace(subagent.Instructions))
        {
            details.Add(new StackExportItemDetail(
                "Subagent instructions",
                subagent.Instructions.Trim(),
                StackValueSensitivity.Public,
                ValueWhenExcluded: "Not exported",
                DetailId: DetailInstructions));
        }

        if (!string.IsNullOrWhiteSpace(subagent.ChatProviderId))
        {
            details.Add(new StackExportItemDetail(
                "Provider connection",
                subagent.ChatProviderId,
                StackValueSensitivity.Public,
                ValueWhenExcluded: "Not exported",
                DetailId: DetailProvider));
        }

        if (!string.IsNullOrWhiteSpace(subagent.ChatModelId))
        {
            details.Add(new StackExportItemDetail(
                "Model choice",
                subagent.ChatModelId,
                StackValueSensitivity.Public,
                ValueWhenExcluded: "Not exported",
                DetailId: DetailModel));
        }

        return details.Count == 0
            ? [new StackExportItemDetail("Subagent settings", "No additional subagent content", StackValueSensitivity.Public)]
            : details;
    }

    private static bool TryReadPayload(StackFragmentImport fragment, ICollection<string> warnings, out SubagentStackPayload? payload)
    {
        payload = null;
        if (!string.Equals(fragment.SchemaId, SchemaId, StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add($"Stack fragment '{fragment.FragmentId}' uses unsupported schema '{fragment.SchemaId}'.");
            return false;
        }

        if (fragment.SchemaVersion != 1)
        {
            warnings.Add($"Stack fragment '{fragment.FragmentId}' uses unsupported subagent schema version {fragment.SchemaVersion}.");
            return false;
        }

        try
        {
            payload = JsonSerializer.Deserialize<SubagentStackPayload>(fragment.JsonPayload, JsonOptions);
            if (payload is null || string.IsNullOrWhiteSpace(payload.SubagentId) || string.IsNullOrWhiteSpace(payload.DisplayName))
            {
                warnings.Add($"Stack fragment '{fragment.FragmentId}' does not contain a valid subagent payload.");
                payload = null;
                return false;
            }

            return true;
        }
        catch (JsonException ex)
        {
            warnings.Add($"Stack fragment '{fragment.FragmentId}' subagent payload could not be parsed: {ex.Message}");
            return false;
        }
    }

    private static string BuildActionId(string fragmentId, string subagentId)
        => $"subagent:{fragmentId}:{subagentId}";

    private static string SanitizeIdentifier(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length);
        var pendingSeparator = false;
        foreach (var character in value.Trim().ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(character))
            {
                builder.Append(character);
                pendingSeparator = false;
                continue;
            }

            if (builder.Length > 0 && !pendingSeparator)
            {
                builder.Append('-');
                pendingSeparator = true;
            }
        }

        var sanitized = builder.ToString().Trim('-');
        return string.IsNullOrWhiteSpace(sanitized) ? "subagent" : sanitized;
    }

    private sealed record SubagentStackPayload(
        string SubagentId,
        string DisplayName,
        string? Description,
        string? Instructions,
        string? ChatProviderId,
        string? ChatModelId,
        IReadOnlyList<Sunder.Package.Agent.Contracts.Models.AgentProfileSelectableCapabilityAssignmentRecord>? SelectableCapabilityAssignments,
        string? ChatModelSettingsJson)
    {
        public static SubagentStackPayload FromSubagent(SubagentRecord subagent, StackExportRequest request)
            => new(
                subagent.SubagentId,
                subagent.DisplayName,
                request.IsDetailSelected(subagent.SubagentId, DetailDescription) ? request.GetDetailValue(subagent.SubagentId, DetailDescription, subagent.Description ?? string.Empty) : null,
                request.IsDetailSelected(subagent.SubagentId, DetailInstructions) ? request.GetDetailValue(subagent.SubagentId, DetailInstructions, subagent.Instructions ?? string.Empty) : null,
                request.IsDetailSelected(subagent.SubagentId, DetailProvider) && request.IsDetailSelected(subagent.SubagentId, DetailModel) ? subagent.ChatProviderId : null,
                request.IsDetailSelected(subagent.SubagentId, DetailProvider) && request.IsDetailSelected(subagent.SubagentId, DetailModel) ? subagent.ChatModelId : null,
                subagent.SelectableCapabilityAssignments,
                request.IsDetailSelected(subagent.SubagentId, DetailModel) ? subagent.ChatModelSettingsJson : null);

        public SubagentRecord ToSubagent()
        {
            var now = DateTimeOffset.UtcNow;
            return new SubagentRecord(
                SubagentId,
                DisplayName,
                Description,
                Instructions,
                ChatProviderId,
                ChatModelId,
                SelectableCapabilityAssignments ?? [],
                now,
                now,
                ChatModelSettingsJson);
        }
    }
}
