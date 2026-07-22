using System.Text.Json;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Stacks;

namespace Sunder.Package.Agent.Services;

public sealed class AgentWorkspaceStackContributor(
    AgentWorkspaceService workspaceService,
    IPackageContext packageContext,
    IPackageExtensionCatalog extensionCatalog) : IPackageStackExporter, IPackageStackImporter, IPackageStackImportAppliedHandler
{
    private const string PackageId = "sunder.package.agent";
    private const string SchemaId = "sunder.package.agent/workspace";
    private const string DetailDescription = "description";
    private const string DetailPaths = "paths";
    private const string DetailDocuments = "documents";
    private const string DetailPrimaryExecutionTarget = "primary-execution-target";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public string ContributorId => "sunder.package.agent.workspaces";

    public string DisplayName => "Agent Workspaces";

    public ValueTask<IReadOnlyList<StackExportItemDescriptor>> ListExportItemsAsync(
        StackExportDiscoveryContext context,
        CancellationToken cancellationToken = default)
    {
        var workspaces = workspaceService.ListWorkspaces()
            .Where(workspace => !string.Equals(workspace.WorkspaceId, AgentWorkspaceService.UnassignedSessionsWorkspaceId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(workspace => workspace.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(workspace => new StackExportItemDescriptor(
                workspace.WorkspaceId,
                workspace.DisplayName,
                "agent-workspace",
                Description: null,
                DefaultSelected: true,
                Details: BuildExportDetails(workspace, workspaceService.ListBindings(workspace.WorkspaceId))))
            .ToArray();
        return ValueTask.FromResult<IReadOnlyList<StackExportItemDescriptor>>(workspaces);
    }

    public ValueTask<StackExportContribution> ExportAsync(
        StackExportRequest request,
        CancellationToken cancellationToken = default)
    {
        var selectedIds = request.ItemSelections
            .Select(selection => selection.ItemId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var fragments = new List<StackFragmentExport>();
        var payloads = new List<AgentWorkspaceStackPayload>();
        var warnings = new List<string>();
        foreach (var workspace in workspaceService.ListWorkspaces()
                     .Where(workspace => selectedIds.Contains(workspace.WorkspaceId)
                                         && !string.Equals(workspace.WorkspaceId, AgentWorkspaceService.UnassignedSessionsWorkspaceId, StringComparison.OrdinalIgnoreCase)))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var bindings = workspaceService.ListBindings(workspace.WorkspaceId);
            var payload = AgentWorkspaceStackPayload.FromWorkspace(workspace, bindings, request);
            payloads.Add(payload);
            fragments.Add(new StackFragmentExport(
                FragmentId: "agent-workspace." + SanitizeIdentifier(workspace.WorkspaceId),
                SchemaId: SchemaId,
                SchemaVersion: 1,
                DisplayName: workspace.DisplayName,
                JsonPayload: JsonSerializer.Serialize(payload, JsonOptions),
                Description: payload.Description,
                DefaultSelected: true,
                SourceItemId: workspace.WorkspaceId));
        }

        return ValueTask.FromResult(new StackExportContribution(
            fragments,
            fragments.Count == 0 ? [] : BuildPackageRequirements(payloads),
            warnings));
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

            var existing = workspaceService.GetWorkspace(payload.WorkspaceId);
            actions.Add(new StackImportAction(
                BuildActionId(fragment.FragmentId, payload.WorkspaceId),
                existing is null ? $"Create workspace '{payload.DisplayName}'" : $"Update workspace '{payload.DisplayName}'",
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
        var idRemaps = new Dictionary<string, string>(request.IdRemaps, StringComparer.OrdinalIgnoreCase);
        foreach (var fragment in request.Fragments)
        {
            if (!TryReadPayload(fragment, warnings, out var payload) || payload is null)
            {
                continue;
            }

            var actionId = BuildActionId(fragment.FragmentId, payload.WorkspaceId);
            if (!selectedActionIds.Contains(actionId))
            {
                continue;
            }

            try
            {
                var existing = workspaceService.GetWorkspace(payload.WorkspaceId);
                var paths = ResolvePathRecords(
                    payload,
                    request.InputValues,
                    preserveExistingWhenEmpty: existing is not null,
                    warnings: warnings);
                var documents = ResolveDocumentRecords(
                    payload,
                    request.InputValues,
                    preserveExistingWhenEmpty: existing is not null,
                    warnings: warnings);
                var now = DateTimeOffset.UtcNow;
                workspaceService.ImportWorkspace(
                    new AgentWorkspaceRecord(
                        payload.WorkspaceId,
                        payload.DisplayName,
                        payload.Description,
                        existing?.CreatedAtUtc ?? now,
                        now),
                    paths,
                    documents);

                if (!string.IsNullOrWhiteSpace(payload.PrimaryExecutionBinding?.ContributionId))
                {
                    workspaceService.SavePrimaryExecutionBinding(
                        payload.WorkspaceId,
                        payload.PrimaryExecutionBinding.ContributionId);
                }

                idRemaps[payload.WorkspaceId] = payload.WorkspaceId;
                imported.Add(new StackImportedItem(payload.WorkspaceId, payload.DisplayName, "agent-workspace"));
            }
            catch (Exception ex)
            {
                errors.Add($"Failed to import workspace '{payload.DisplayName}': {ex.Message}");
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
        if (context.ImportedItems.Count > 0)
        {
            workspaceService.NotifyWorkspacesImported();
        }

        return ValueTask.CompletedTask;
    }

    private StackPackageRequirement CreatePackageRequirement()
        => AgentStackPackageRequirements.Create(
            PackageId,
            PackageId,
            packageContext.Version.ToString());

    private IReadOnlyList<StackPackageRequirement> BuildPackageRequirements(IReadOnlyList<AgentWorkspaceStackPayload> payloads)
    {
        var packageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { PackageId };
        var targetIds = payloads
            .Select(payload => payload.PrimaryExecutionBinding?.ContributionId)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (targetIds.Count > 0)
        {
            foreach (var contribution in extensionCatalog.GetExtensionContributions(PackageExtensionPoints.ExecutionTargets))
            {
                var descriptor = contribution.Contribution.Descriptor;
                if (targetIds.Contains(descriptor.TargetId) || targetIds.Contains(descriptor.TargetKind))
                {
                    AddPackageId(packageIds, contribution.PackageId);
                }
            }
        }

        return packageIds
            .OrderBy(packageId => string.Equals(packageId, PackageId, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(packageId => packageId, StringComparer.OrdinalIgnoreCase)
            .Select(packageId => string.Equals(packageId, PackageId, StringComparison.OrdinalIgnoreCase)
                ? CreatePackageRequirement()
                : AgentStackPackageRequirements.Create(
                    packageId,
                    PackageId,
                    packageContext.Version.ToString()))
            .ToArray();
    }

    private static void AddPackageId(ISet<string> packageIds, string? packageId)
    {
        if (!string.IsNullOrWhiteSpace(packageId))
        {
            packageIds.Add(packageId.Trim());
        }
    }

    private static IReadOnlyList<StackExportItemDetail> BuildExportDetails(
        AgentWorkspaceRecord workspace,
        IReadOnlyList<AgentWorkspaceBindingRecord> bindings)
    {
        var details = new List<StackExportItemDetail>();
        if (!string.IsNullOrWhiteSpace(workspace.Description))
        {
            details.Add(new StackExportItemDetail(
                "Workspace description",
                workspace.Description.Trim(),
                StackValueSensitivity.Public,
                ValueWhenExcluded: "Not exported",
                DetailId: DetailDescription));
        }

        if (workspace.Paths.Count > 0)
        {
            details.Add(new StackExportItemDetail(
                "Workspace folders",
                string.Join(Environment.NewLine, workspace.Paths.OrderBy(path => path.SortOrder).Select(path => path.HostPath)),
                StackValueSensitivity.Public,
                ValueWhenExcluded: "Prompt on import",
                DetailId: DetailPaths));
        }

        if (workspace.Documents.Count > 0)
        {
            details.Add(new StackExportItemDetail(
                "Documentation files",
                string.Join(Environment.NewLine, workspace.Documents.OrderBy(document => document.SortOrder).Select(document => document.FilePath)),
                StackValueSensitivity.Public,
                ValueWhenExcluded: "Prompt on import",
                DetailId: DetailDocuments));
        }

        var primaryBinding = bindings.FirstOrDefault(binding => binding.IsEnabled
                                                               && string.Equals(binding.ExtensionPointId, PackageExtensionPoints.ExecutionTargets.Id, StringComparison.OrdinalIgnoreCase)
                                                               && string.Equals(binding.Role, AgentWorkspaceBindingRoles.PrimaryExecutionTarget, StringComparison.OrdinalIgnoreCase));
        if (primaryBinding is not null)
        {
            details.Add(new StackExportItemDetail(
                "Primary execution target",
                primaryBinding.ContributionId,
                StackValueSensitivity.Public,
                DetailId: DetailPrimaryExecutionTarget));
        }

        return details.Count == 0
            ? [new StackExportItemDetail("Workspace metadata", "No additional workspace content", StackValueSensitivity.Public)]
            : details;
    }

    private static IReadOnlyList<AgentWorkspacePathRecord>? ResolvePathRecords(
        AgentWorkspaceStackPayload payload,
        IReadOnlyDictionary<string, string> inputValues,
        bool preserveExistingWhenEmpty,
        ICollection<string> warnings)
    {
        if (payload.Paths is null)
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        var paths = new List<AgentWorkspacePathRecord>();
        var skippedInputs = 0;
        foreach (var path in payload.Paths.OrderBy(path => path.SortOrder))
        {
            var hostPath = ResolveLocalValue(path.HostPath, path.InputId, inputValues);
            if (string.IsNullOrWhiteSpace(hostPath))
            {
                if (!string.IsNullOrWhiteSpace(path.InputId))
                {
                    skippedInputs++;
                }

                continue;
            }

            paths.Add(new AgentWorkspacePathRecord(
                string.IsNullOrWhiteSpace(path.PathId) ? Guid.NewGuid().ToString("N") : path.PathId,
                payload.WorkspaceId,
                hostPath.Trim(),
                path.IsDefault,
                paths.Count,
                now,
                now));
        }

        if (paths.Count == 0 && skippedInputs > 0 && preserveExistingWhenEmpty)
        {
            warnings.Add($"Preserved existing workspace paths for '{payload.DisplayName}' because no replacement local paths were supplied.");
            return null;
        }

        if (skippedInputs > 0)
        {
            warnings.Add($"Skipped {skippedInputs} workspace path{(skippedInputs == 1 ? string.Empty : "s")} for '{payload.DisplayName}' because no local path was supplied.");
        }

        return paths;
    }

    private static IReadOnlyList<AgentWorkspaceDocumentRecord>? ResolveDocumentRecords(
        AgentWorkspaceStackPayload payload,
        IReadOnlyDictionary<string, string> inputValues,
        bool preserveExistingWhenEmpty,
        ICollection<string> warnings)
    {
        if (payload.Documents is null)
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        var documents = new List<AgentWorkspaceDocumentRecord>();
        var skippedInputs = 0;
        foreach (var document in payload.Documents.OrderBy(document => document.SortOrder))
        {
            var filePath = ResolveLocalValue(document.FilePath, document.InputId, inputValues);
            if (string.IsNullOrWhiteSpace(filePath))
            {
                if (!string.IsNullOrWhiteSpace(document.InputId))
                {
                    skippedInputs++;
                }

                continue;
            }

            documents.Add(new AgentWorkspaceDocumentRecord(
                string.IsNullOrWhiteSpace(document.DocumentId) ? Guid.NewGuid().ToString("N") : document.DocumentId,
                payload.WorkspaceId,
                filePath.Trim(),
                documents.Count,
                now,
                now));
        }

        if (documents.Count == 0 && skippedInputs > 0 && preserveExistingWhenEmpty)
        {
            warnings.Add($"Preserved existing workspace documents for '{payload.DisplayName}' because no replacement local document paths were supplied.");
            return null;
        }

        if (skippedInputs > 0)
        {
            warnings.Add($"Skipped {skippedInputs} workspace document{(skippedInputs == 1 ? string.Empty : "s")} for '{payload.DisplayName}' because no local file path was supplied.");
        }

        return documents;
    }

    private static string? ResolveLocalValue(
        string? exportedValue,
        string? inputId,
        IReadOnlyDictionary<string, string> inputValues)
    {
        if (!string.IsNullOrWhiteSpace(exportedValue))
        {
            return exportedValue;
        }

        return !string.IsNullOrWhiteSpace(inputId) && inputValues.TryGetValue(inputId, out var inputValue)
            ? inputValue
            : null;
    }

    private static bool TryReadPayload(StackFragmentImport fragment, ICollection<string> warnings, out AgentWorkspaceStackPayload? payload)
    {
        payload = null;
        try
        {
            payload = JsonSerializer.Deserialize<AgentWorkspaceStackPayload>(fragment.JsonPayload, JsonOptions);
            if (payload is null || string.IsNullOrWhiteSpace(payload.WorkspaceId) || string.IsNullOrWhiteSpace(payload.DisplayName))
            {
                warnings.Add($"Stack fragment '{fragment.FragmentId}' does not contain a valid workspace payload.");
                payload = null;
                return false;
            }

            return true;
        }
        catch (JsonException ex)
        {
            warnings.Add($"Stack fragment '{fragment.FragmentId}' workspace payload could not be parsed: {ex.Message}");
            return false;
        }
    }

    private static string BuildActionId(string fragmentId, string workspaceId)
        => $"agent-workspace:{fragmentId}:{workspaceId}";

    private static string BuildInputId(string workspaceId, string kind, string itemId, int sortOrder)
        => $"agent-workspace.{SanitizeIdentifier(workspaceId)}.{kind}.{SanitizeIdentifier(string.IsNullOrWhiteSpace(itemId) ? sortOrder.ToString() : itemId)}";

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
        return string.IsNullOrWhiteSpace(sanitized) ? "item" : sanitized;
    }

    private sealed record AgentWorkspaceStackPayload(
        string WorkspaceId,
        string DisplayName,
        string? Description,
        IReadOnlyList<WorkspacePathStackEntry>? Paths,
        IReadOnlyList<WorkspaceDocumentStackEntry>? Documents,
        WorkspaceBindingStackEntry? PrimaryExecutionBinding)
    {
        public static AgentWorkspaceStackPayload FromWorkspace(
            AgentWorkspaceRecord workspace,
            IReadOnlyList<AgentWorkspaceBindingRecord> bindings,
            StackExportRequest request)
        {
            var primaryBinding = bindings
                .FirstOrDefault(binding => binding.IsEnabled
                                           && string.Equals(binding.ExtensionPointId, PackageExtensionPoints.ExecutionTargets.Id, StringComparison.OrdinalIgnoreCase)
                                           && string.Equals(binding.Role, AgentWorkspaceBindingRoles.PrimaryExecutionTarget, StringComparison.OrdinalIgnoreCase));
            return new AgentWorkspaceStackPayload(
                workspace.WorkspaceId,
                workspace.DisplayName,
                request.IsDetailSelected(workspace.WorkspaceId, DetailDescription) ? request.GetDetailValue(workspace.WorkspaceId, DetailDescription, workspace.Description ?? string.Empty) : null,
                request.IsDetailSelected(workspace.WorkspaceId, DetailPaths)
                    ? workspace.Paths
                        .OrderBy(path => path.SortOrder)
                        .Select(path => WorkspacePathStackEntry.FromPath(path))
                        .ToArray()
                    : [],
                request.IsDetailSelected(workspace.WorkspaceId, DetailDocuments)
                    ? workspace.Documents
                        .OrderBy(document => document.SortOrder)
                        .Select(WorkspaceDocumentStackEntry.FromDocument)
                        .ToArray()
                    : [],
                primaryBinding is null || !request.IsDetailSelected(workspace.WorkspaceId, DetailPrimaryExecutionTarget) ? null : new WorkspaceBindingStackEntry(primaryBinding.ContributionId));
        }
    }

    private sealed record WorkspacePathStackEntry(
        string PathId,
        bool IsDefault,
        int SortOrder,
        string? HostPath,
        string? InputId)
    {
        public static WorkspacePathStackEntry FromPath(AgentWorkspacePathRecord path)
            => new(
                path.PathId,
                path.IsDefault,
                path.SortOrder,
                path.HostPath,
                null);
    }

    private sealed record WorkspaceDocumentStackEntry(
        string DocumentId,
        int SortOrder,
        string? FilePath,
        string? InputId)
    {
        public static WorkspaceDocumentStackEntry FromDocument(AgentWorkspaceDocumentRecord document)
            => new(
                document.DocumentId,
                document.SortOrder,
                document.FilePath,
                null);
    }

    private sealed record WorkspaceBindingStackEntry(string ContributionId);
}

internal static class AgentStackPackageRequirements
{
    private const string CoordinatedFamilyPrefix = "sunder.package.agent";

    public static StackPackageRequirement Create(
        string packageId,
        string owningPackageId,
        string owningPackageVersion)
        => new(
            packageId,
            CreatedWithVersion: string.Equals(
                packageId,
                owningPackageId,
                StringComparison.OrdinalIgnoreCase)
                ? owningPackageVersion
                : null,
            MinimumVersion: IsCoordinatedFamilyPackage(packageId) ? "1.1.0" : null);

    private static bool IsCoordinatedFamilyPackage(string packageId)
        => string.Equals(packageId, CoordinatedFamilyPrefix, StringComparison.OrdinalIgnoreCase)
           || packageId.StartsWith(
               CoordinatedFamilyPrefix + ".",
               StringComparison.OrdinalIgnoreCase);
}
