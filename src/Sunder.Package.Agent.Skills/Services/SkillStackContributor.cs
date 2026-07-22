using System.Text.Json;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Stacks;

namespace Sunder.Package.Agent.Skills.Services;

internal sealed class SkillStackContributor(
    SkillStore store,
    SkillImportService importService,
    IPackageContext packageContext) : IPackageStackExporter, IPackageStackImporter, IPackageStackImportAppliedHandler
{
    private const string SchemaId = "sunder.package.agent.skills/github-skill";
    private const string DetailSource = "github-url";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public string ContributorId => "sunder.package.agent.skills.github-skills";

    public string DisplayName => "Agent Skills";

    public ValueTask<IReadOnlyList<StackExportItemDescriptor>> ListExportItemsAsync(
        StackExportDiscoveryContext context,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult<IReadOnlyList<StackExportItemDescriptor>>(store.ListSkills()
            .Where(IsGitHubSkill)
            .Select(skill => new StackExportItemDescriptor(
                skill.SkillId,
                SkillStore.ResolveDisplayName(skill),
                "agent-skill",
                skill.Description,
                DefaultSelected: true,
                Details:
                [
                    new StackExportItemDetail(
                        "GitHub URL",
                        skill.SourceUri!.Trim(),
                        StackValueSensitivity.Public,
                        ValueWhenExcluded: "Not exported",
                        DetailId: DetailSource,
                        IsEditable: false),
                ]))
            .ToArray());

    public ValueTask<StackExportContribution> ExportAsync(
        StackExportRequest request,
        CancellationToken cancellationToken = default)
    {
        var selectedIds = request.ItemSelections
            .Select(selection => selection.ItemId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var fragments = new List<StackFragmentExport>();
        var warnings = new List<string>();
        foreach (var skill in store.ListSkills().Where(skill => selectedIds.Contains(skill.SkillId)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsGitHubSkill(skill))
            {
                continue;
            }

            if (!request.IsDetailSelected(skill.SkillId, DetailSource))
            {
                warnings.Add($"Skipped skill '{SkillStore.ResolveDisplayName(skill)}' because its GitHub URL was not selected.");
                continue;
            }

            var githubUrl = skill.SourceUri!.Trim();
            fragments.Add(new StackFragmentExport(
                FragmentId: "github-skill." + SanitizeIdentifier(skill.SkillId),
                SchemaId: SchemaId,
                SchemaVersion: 1,
                DisplayName: SkillStore.ResolveDisplayName(skill),
                JsonPayload: JsonSerializer.Serialize(new GitHubSkillStackPayload(githubUrl), JsonOptions),
                Description: skill.Description,
                DefaultSelected: true,
                SourceItemId: skill.SkillId));
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
        var warnings = new List<string>();
        foreach (var fragment in request.Fragments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryReadPayload(fragment, warnings, out var payload) || payload is null)
            {
                continue;
            }

            var alreadyInstalled = store.ListSkills().Any(skill =>
                IsGitHubSkill(skill)
                && string.Equals(skill.SourceUri!.Trim(), payload.GitHubUrl, StringComparison.OrdinalIgnoreCase));
            actions.Add(new StackImportAction(
                BuildActionId(fragment.FragmentId),
                alreadyInstalled
                    ? $"Update GitHub skill '{fragment.DisplayName}'"
                    : $"Import GitHub skill '{fragment.DisplayName}'",
                alreadyInstalled ? StackImportActionKind.Update : StackImportActionKind.Create,
                DefaultSelected: true,
                Description: payload.GitHubUrl));
        }

        return ValueTask.FromResult(new StackImportPreview(actions, [], [], warnings));
    }

    public async ValueTask<StackImportResult> ImportAsync(
        StackImportRequest request,
        CancellationToken cancellationToken = default)
    {
        var selectedActionIds = request.SelectedActionIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var imported = new List<StackImportedItem>();
        var warnings = new List<string>();
        var errors = new List<string>();
        foreach (var fragment in request.Fragments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!selectedActionIds.Contains(BuildActionId(fragment.FragmentId)))
            {
                continue;
            }

            if (!TryReadPayload(fragment, warnings, out var payload) || payload is null)
            {
                continue;
            }

            try
            {
                var record = await importService.ImportGitHubFolderAsync(payload.GitHubUrl, cancellationToken);
                imported.Add(new StackImportedItem(record.SkillId, SkillStore.ResolveDisplayName(record), "agent-skill"));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                errors.Add($"Failed to import GitHub skill '{fragment.DisplayName}': {ex.Message}");
            }
        }

        var outcome = errors.Count == 0
            ? StackImportOutcome.Completed
            : imported.Count == 0 ? StackImportOutcome.Failed : StackImportOutcome.Partial;
        return new StackImportResult(outcome, imported, new Dictionary<string, string>(), warnings, errors);
    }

    public ValueTask OnStackImportAppliedAsync(
        StackImportAppliedContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (context.ImportedItems.Count > 0)
        {
            store.NotifySkillsImported();
        }

        return ValueTask.CompletedTask;
    }

    private StackPackageRequirement CreatePackageRequirement()
        => new(SkillConstants.PackageId, CreatedWithVersion: packageContext.Version.ToString(), MinimumVersion: "1.1.0");

    private static bool IsGitHubSkill(InstalledSkillRecord skill)
        => string.Equals(skill.SourceKind, "github", StringComparison.OrdinalIgnoreCase)
           && !string.IsNullOrWhiteSpace(skill.SourceUri);

    private static bool TryReadPayload(
        StackFragmentImport fragment,
        ICollection<string> warnings,
        out GitHubSkillStackPayload? payload)
    {
        payload = null;
        try
        {
            if (fragment.JsonPayload.Length > 64 * 1024)
            {
                warnings.Add($"Stack fragment '{fragment.FragmentId}' GitHub skill payload exceeds the size limit.");
                return false;
            }

            using var document = JsonDocument.Parse(fragment.JsonPayload, new JsonDocumentOptions { MaxDepth = 8 });
            var propertyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || document.RootElement.EnumerateObject().Any(property => !propertyNames.Add(property.Name)))
            {
                warnings.Add($"Stack fragment '{fragment.FragmentId}' GitHub skill payload contains duplicate or case-colliding properties.");
                return false;
            }

            payload = document.RootElement.Deserialize<GitHubSkillStackPayload>(JsonOptions);
            if (payload is null || string.IsNullOrWhiteSpace(payload.GitHubUrl))
            {
                warnings.Add($"Stack fragment '{fragment.FragmentId}' does not contain a valid GitHub skill URL.");
                payload = null;
                return false;
            }

            payload = payload with { GitHubUrl = payload.GitHubUrl.Trim() };
            return true;
        }
        catch (JsonException ex)
        {
            warnings.Add($"Stack fragment '{fragment.FragmentId}' GitHub skill payload could not be parsed: {ex.Message}");
            return false;
        }
    }

    private static string BuildActionId(string fragmentId)
        => "github-skill:" + fragmentId;

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
        return string.IsNullOrWhiteSpace(sanitized) ? "skill" : sanitized;
    }

    private sealed record GitHubSkillStackPayload(string GitHubUrl);
}
