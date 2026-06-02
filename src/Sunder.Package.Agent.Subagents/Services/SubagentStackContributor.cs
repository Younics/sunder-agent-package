using System.Text.Json;
using Sunder.Package.Agent.Subagents.Models;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Stacks;

namespace Sunder.Package.Agent.Subagents.Services;

internal sealed class SubagentStackContributor(
    SubagentService subagentService,
    IPackageContext packageContext) : IPackageStackContributor
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
        if (!request.Options.IncludePrivateText)
        {
            return ValueTask.FromResult(new StackExportContribution(
                [],
                [],
                ["Skipped subagents because subagent descriptions and instructions are text content."]));
        }

        var selectedIds = request.ItemIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var fragments = subagentService.ListSubagents()
            .Where(subagent => selectedIds.Contains(subagent.SubagentId))
            .Select(subagent => new StackFragmentExport(
                FragmentId: "subagent." + SanitizeIdentifier(subagent.SubagentId),
                OwnerPackageId: SubagentConstants.PackageId,
                ContributorId,
                SchemaId,
                SchemaVersion: 1,
                DisplayName: subagent.DisplayName,
                JsonPayload: JsonSerializer.Serialize(SubagentStackPayload.FromSubagent(subagent, request), JsonOptions),
                Safety: BuildSafety(subagent, request),
                Description: request.IsDetailSelected(subagent.SubagentId, DetailDescription)
                    ? request.GetDetailValue(subagent.SubagentId, DetailDescription, subagent.Description ?? string.Empty)
                    : null,
                DefaultSelected: true,
                RequiresPackages: [CreatePackageRequirement()],
                SourceItemId: subagent.SubagentId))
            .ToArray();

        return ValueTask.FromResult(new StackExportContribution(
            fragments,
            fragments.Length == 0 ? [] : [CreatePackageRequirement()],
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
        var imported = new List<StackImportedItem>();
        var warnings = new List<string>();
        var errors = new List<string>();
        foreach (var fragment in request.Fragments)
        {
            if (!TryReadPayload(fragment, warnings, out var payload) || payload is null)
            {
                continue;
            }

            var actionId = BuildActionId(fragment.FragmentId, payload.SubagentId);
            if (!selectedActionIds.Contains(actionId))
            {
                continue;
            }

            try
            {
                var saved = subagentService.ImportSubagent(payload.ToSubagent());
                imported.Add(new StackImportedItem(saved.SubagentId, saved.DisplayName, "subagent"));
            }
            catch (Exception ex)
            {
                errors.Add($"Failed to import subagent '{payload.DisplayName}': {ex.Message}");
            }
        }

        return ValueTask.FromResult(new StackImportResult(errors.Count == 0, imported, new Dictionary<string, string>(), warnings, errors));
    }

    private StackPackageRequirement CreatePackageRequirement()
        => new(SubagentConstants.PackageId, CreatedWithVersion: packageContext.Version.ToString(), MinimumVersion: "1.0.0");

    private static IReadOnlyList<StackValueSensitivity> BuildSensitivities(SubagentRecord subagent)
    {
        var sensitivities = new List<StackValueSensitivity> { StackValueSensitivity.PrivateText };
        if (!string.IsNullOrWhiteSpace(subagent.ChatProviderId) || !string.IsNullOrWhiteSpace(subagent.ChatModelId))
        {
            sensitivities.Add(StackValueSensitivity.NetworkEndpoint);
        }

        return sensitivities;
    }

    private static IReadOnlyList<StackExportItemDetail> BuildExportDetails(SubagentRecord subagent)
    {
        var details = new List<StackExportItemDetail>();
        if (!string.IsNullOrWhiteSpace(subagent.Description))
        {
            details.Add(new StackExportItemDetail(
                "Subagent description",
                subagent.Description.Trim(),
                StackValueSensitivity.PrivateText,
                ValueWhenExcluded: "Not exported",
                DetailId: DetailDescription));
        }

        if (!string.IsNullOrWhiteSpace(subagent.Instructions))
        {
            details.Add(new StackExportItemDetail(
                "Subagent instructions",
                subagent.Instructions.Trim(),
                StackValueSensitivity.PrivateText,
                ValueWhenExcluded: "Not exported",
                DetailId: DetailInstructions));
        }

        if (!string.IsNullOrWhiteSpace(subagent.ChatProviderId))
        {
            details.Add(new StackExportItemDetail(
                "Provider connection",
                subagent.ChatProviderId,
                StackValueSensitivity.NetworkEndpoint,
                ValueWhenExcluded: "Not exported",
                DetailId: DetailProvider));
        }

        if (!string.IsNullOrWhiteSpace(subagent.ChatModelId))
        {
            details.Add(new StackExportItemDetail(
                "Model choice",
                subagent.ChatModelId,
                StackValueSensitivity.NetworkEndpoint,
                ValueWhenExcluded: "Not exported",
                DetailId: DetailModel));
        }

        return details.Count == 0
            ? [new StackExportItemDetail("Subagent settings", "No additional subagent content", StackValueSensitivity.Public)]
            : details;
    }

    private static StackSafetyDescriptor BuildSafety(SubagentRecord subagent, StackExportRequest request)
        => new(
            ContainsSecrets: false,
            ContainsSecretReferences: false,
            ContainsLocalPaths: false,
            ContainsPrivateText: request.IsDetailSelected(subagent.SubagentId, DetailDescription) && !string.IsNullOrWhiteSpace(subagent.Description)
                                 || request.IsDetailSelected(subagent.SubagentId, DetailInstructions) && !string.IsNullOrWhiteSpace(subagent.Instructions),
            ContainsExecutableCommands: false,
            ContainsNetworkEndpoints: (request.IsDetailSelected(subagent.SubagentId, DetailProvider) || request.IsDetailSelected(subagent.SubagentId, DetailModel))
                                      && (!string.IsNullOrWhiteSpace(subagent.ChatProviderId) || !string.IsNullOrWhiteSpace(subagent.ChatModelId)),
            ContainsMachineSpecificValues: false);

    private static bool TryReadPayload(StackFragmentImport fragment, ICollection<string> warnings, out SubagentStackPayload? payload)
    {
        payload = null;
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
