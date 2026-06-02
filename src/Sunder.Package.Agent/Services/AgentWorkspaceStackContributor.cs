using System.Text.Json;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Stacks;

namespace Sunder.Package.Agent.Services;

public sealed class AgentWorkspaceStackContributor(
    AgentWorkspaceService workspaceService,
    IPackageContext packageContext) : IPackageStackContributor
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
                Sensitivities: BuildSensitivities(workspace),
                Details: BuildExportDetails(workspace, workspaceService.ListBindings(workspace.WorkspaceId))))
            .ToArray();
        return ValueTask.FromResult<IReadOnlyList<StackExportItemDescriptor>>(workspaces);
    }

    public ValueTask<StackExportContribution> ExportAsync(
        StackExportRequest request,
        CancellationToken cancellationToken = default)
    {
        var selectedIds = request.ItemIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var fragments = new List<StackFragmentExport>();
        var warnings = new List<string>();
        var exportedPathPrompts = false;
        foreach (var workspace in workspaceService.ListWorkspaces()
                     .Where(workspace => selectedIds.Contains(workspace.WorkspaceId)
                                         && !string.Equals(workspace.WorkspaceId, AgentWorkspaceService.UnassignedSessionsWorkspaceId, StringComparison.OrdinalIgnoreCase)))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var bindings = workspaceService.ListBindings(workspace.WorkspaceId);
            var payload = AgentWorkspaceStackPayload.FromWorkspace(workspace, bindings, request.Options, request);
            var requiredInputs = BuildRequiredInputs(payload).ToArray();
            exportedPathPrompts |= requiredInputs.Length > 0;
            fragments.Add(new StackFragmentExport(
                FragmentId: "agent-workspace." + SanitizeIdentifier(workspace.WorkspaceId),
                OwnerPackageId: PackageId,
                ContributorId,
                SchemaId,
                SchemaVersion: 1,
                DisplayName: workspace.DisplayName,
                JsonPayload: JsonSerializer.Serialize(payload, JsonOptions),
                Safety: BuildSafety(payload),
                Description: payload.Description,
                DefaultSelected: true,
                RequiresPackages: [CreatePackageRequirement()],
                RequiredInputs: requiredInputs,
                SourceItemId: workspace.WorkspaceId));
        }

        if (exportedPathPrompts)
        {
            warnings.Add("Workspace local paths were exported as import prompts because machine-specific values are excluded.");
        }

        return ValueTask.FromResult(new StackExportContribution(
            fragments,
            fragments.Count == 0 ? [] : [CreatePackageRequirement()],
            warnings));
    }

    public ValueTask<StackImportPreview> PreviewImportAsync(
        StackImportPreviewRequest request,
        CancellationToken cancellationToken = default)
    {
        var actions = new List<StackImportAction>();
        var requiredInputs = new List<StackRequiredInputDescriptor>();
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
            requiredInputs.AddRange(BuildRequiredInputs(payload));
        }

        if (requiredInputs.Count > 0)
        {
            warnings.Add("Workspace Stack exports do not include local paths by default. Provide local paths before import or those workspace paths will be skipped.");
        }

        return ValueTask.FromResult(new StackImportPreview(actions, requiredInputs, [], warnings));
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

        return ValueTask.FromResult(new StackImportResult(errors.Count == 0, imported, idRemaps, warnings, errors));
    }

    private StackPackageRequirement CreatePackageRequirement()
        => new(PackageId, CreatedWithVersion: packageContext.Version.ToString(), MinimumVersion: "1.0.0");

    private static IReadOnlyList<StackValueSensitivity> BuildSensitivities(AgentWorkspaceRecord workspace)
    {
        var sensitivities = new List<StackValueSensitivity>();
        if (!string.IsNullOrWhiteSpace(workspace.Description))
        {
            sensitivities.Add(StackValueSensitivity.PrivateText);
        }

        if (workspace.Paths.Count > 0 || workspace.Documents.Count > 0)
        {
            sensitivities.Add(StackValueSensitivity.LocalPath);
            sensitivities.Add(StackValueSensitivity.MachineSpecific);
        }

        return sensitivities.Count == 0 ? [StackValueSensitivity.Public] : sensitivities.Distinct().ToArray();
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
                StackValueSensitivity.PrivateText,
                ValueWhenExcluded: "Not exported",
                DetailId: DetailDescription));
        }

        if (workspace.Paths.Count > 0)
        {
            details.Add(new StackExportItemDetail(
                "Workspace folders",
                string.Join(Environment.NewLine, workspace.Paths.OrderBy(path => path.SortOrder).Select(path => path.HostPath)),
                StackValueSensitivity.LocalPath,
                ValueWhenExcluded: "Prompt on import",
                DetailId: DetailPaths));
        }

        if (workspace.Documents.Count > 0)
        {
            details.Add(new StackExportItemDetail(
                "Documentation files",
                string.Join(Environment.NewLine, workspace.Documents.OrderBy(document => document.SortOrder).Select(document => document.FilePath)),
                StackValueSensitivity.LocalPath,
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

    private static StackSafetyDescriptor BuildSafety(AgentWorkspaceStackPayload payload)
    {
        var includesMachineValues = (payload.Paths?.Any(path => !string.IsNullOrWhiteSpace(path.HostPath)) == true)
                                    || (payload.Documents?.Any(document => !string.IsNullOrWhiteSpace(document.FilePath)) == true);
        return new StackSafetyDescriptor(
            ContainsSecrets: false,
            ContainsSecretReferences: false,
            ContainsLocalPaths: includesMachineValues,
            ContainsPrivateText: !string.IsNullOrWhiteSpace(payload.Description),
            ContainsExecutableCommands: false,
            ContainsNetworkEndpoints: false,
            ContainsMachineSpecificValues: includesMachineValues);
    }

    private static IReadOnlyList<StackRequiredInputDescriptor> BuildRequiredInputs(AgentWorkspaceStackPayload payload)
    {
        var inputs = new List<StackRequiredInputDescriptor>();
        if (payload.Paths is not null)
        {
            foreach (var path in payload.Paths.Where(path => string.IsNullOrWhiteSpace(path.HostPath) && !string.IsNullOrWhiteSpace(path.InputId)))
            {
                inputs.Add(new StackRequiredInputDescriptor(
                    path.InputId!,
                    StackRequiredInputKind.LocalPath,
                    $"{payload.DisplayName} workspace path {path.SortOrder + 1}",
                    Required: false,
                    Description: "Provide a local folder for this workspace path. Leave blank to skip this path."));
            }
        }

        if (payload.Documents is not null)
        {
            foreach (var document in payload.Documents.Where(document => string.IsNullOrWhiteSpace(document.FilePath) && !string.IsNullOrWhiteSpace(document.InputId)))
            {
                inputs.Add(new StackRequiredInputDescriptor(
                    document.InputId!,
                    StackRequiredInputKind.LocalPath,
                    $"{payload.DisplayName} documentation file {document.SortOrder + 1}",
                    Required: false,
                    Description: "Provide a local documentation file for this workspace. Leave blank to skip this document."));
            }
        }

        return inputs;
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
            StackExportOptions options,
            StackExportRequest request)
        {
            var hasExplicitDetails = request.GetItemSelection(workspace.WorkspaceId)?.Details is not null;
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
                        .Select(path => WorkspacePathStackEntry.FromPath(workspace.WorkspaceId, path, hasExplicitDetails || options.IncludeMachineSpecificValues))
                        .ToArray()
                    : [],
                request.IsDetailSelected(workspace.WorkspaceId, DetailDocuments)
                    ? workspace.Documents
                        .OrderBy(document => document.SortOrder)
                        .Select(document => WorkspaceDocumentStackEntry.FromDocument(workspace.WorkspaceId, document, hasExplicitDetails || options.IncludeMachineSpecificValues))
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
        public static WorkspacePathStackEntry FromPath(
            string workspaceId,
            AgentWorkspacePathRecord path,
            bool includeMachineSpecificValues)
            => new(
                path.PathId,
                path.IsDefault,
                path.SortOrder,
                includeMachineSpecificValues ? path.HostPath : null,
                includeMachineSpecificValues ? null : BuildInputId(workspaceId, "path", path.PathId, path.SortOrder));
    }

    private sealed record WorkspaceDocumentStackEntry(
        string DocumentId,
        int SortOrder,
        string? FilePath,
        string? InputId)
    {
        public static WorkspaceDocumentStackEntry FromDocument(
            string workspaceId,
            AgentWorkspaceDocumentRecord document,
            bool includeMachineSpecificValues)
            => new(
                document.DocumentId,
                document.SortOrder,
                includeMachineSpecificValues ? document.FilePath : null,
                includeMachineSpecificValues ? null : BuildInputId(workspaceId, "document", document.DocumentId, document.SortOrder));
    }

    private sealed record WorkspaceBindingStackEntry(string ContributionId);
}
