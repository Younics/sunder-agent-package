using System.Text.Json;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Stacks;

namespace Sunder.Package.Agent.Skills.Services;

internal sealed class SkillStackContributor(
    SkillStore store,
    SkillImportService importService,
    IPackageContext packageContext) : IPackageStackContributor
{
    private const string SchemaId = "sunder.package.agent.skills/skill";
    private const string DetailFiles = "files";
    private const string DetailDescription = "description";
    private const string DetailSource = "source";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public string ContributorId => "sunder.package.agent.skills.skills";

    public string DisplayName => "Agent Skills";

    public ValueTask<IReadOnlyList<StackExportItemDescriptor>> ListExportItemsAsync(
        StackExportDiscoveryContext context,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult<IReadOnlyList<StackExportItemDescriptor>>(store.ListSkills()
            .Select(skill => new StackExportItemDescriptor(
                skill.SkillId,
                SkillStore.ResolveDisplayName(skill),
                "agent-skill",
                Description: null,
                DefaultSelected: true,
                Sensitivities: BuildSensitivities(skill),
                Details: BuildExportDetails(skill)))
            .ToArray());

    public ValueTask<StackExportContribution> ExportAsync(
        StackExportRequest request,
        CancellationToken cancellationToken = default)
    {
        var selectedIds = request.ItemIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var fragments = new List<StackFragmentExport>();
        var warnings = new List<string>();
        if (!request.Options.IncludePrivateText)
        {
            return ValueTask.FromResult(new StackExportContribution(
                [],
                [],
                ["Skipped skills because skill markdown and resources are text content."]));
        }

        foreach (var skill in store.ListSkills().Where(skill => selectedIds.Contains(skill.SkillId)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!request.IsDetailSelected(skill.SkillId, DetailFiles))
            {
                warnings.Add($"Skipped skill '{SkillStore.ResolveDisplayName(skill)}' because its files were not selected.");
                continue;
            }

            var fragmentId = "agent-skill." + SanitizeIdentifier(skill.SkillId);
            var rootPath = store.GetSkillRootPath(skill);
            var files = BuildPayloadFiles(rootPath, warnings).ToArray();
            if (files.Length == 0)
            {
                warnings.Add($"Skipped skill '{SkillStore.ResolveDisplayName(skill)}' because no files were found.");
                continue;
            }

            var payload = SkillStackPayload.FromSkill(
                skill,
                files.Select(file => file.RelativePath).ToArray(),
                request.Options,
                request);
            fragments.Add(new StackFragmentExport(
                FragmentId: fragmentId,
                OwnerPackageId: SkillConstants.PackageId,
                ContributorId,
                SchemaId,
                SchemaVersion: 1,
                DisplayName: SkillStore.ResolveDisplayName(skill),
                JsonPayload: JsonSerializer.Serialize(payload, JsonOptions),
                Safety: BuildSafety(payload),
                Description: payload.Description,
                DefaultSelected: true,
                RequiresPackages: [CreatePackageRequirement()],
                Files: files,
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
            if (!TryReadPayload(fragment, warnings, out var payload) || payload is null)
            {
                continue;
            }

            var existing = store.GetSkill(payload.SkillId);
            actions.Add(new StackImportAction(
                BuildActionId(fragment.FragmentId, payload.SkillId),
                existing is null ? $"Create skill '{payload.DisplayName}'" : $"Update skill '{payload.DisplayName}'",
                existing is null ? StackImportActionKind.Create : StackImportActionKind.Update,
                DefaultSelected: true,
                Description: payload.Description));
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
            if (!TryReadPayload(fragment, warnings, out var payload) || payload is null)
            {
                continue;
            }

            var actionId = BuildActionId(fragment.FragmentId, payload.SkillId);
            if (!selectedActionIds.Contains(actionId))
            {
                continue;
            }

            var stagingRoot = CreateImportStagingRoot();
            try
            {
                CopyFragmentFiles(fragment.Files ?? [], stagingRoot);
                var record = await importService.ImportStackFolderAsync(stagingRoot, cancellationToken);
                imported.Add(new StackImportedItem(record.SkillId, SkillStore.ResolveDisplayName(record), "agent-skill"));
                if (!string.Equals(record.SkillId, payload.SkillId, StringComparison.OrdinalIgnoreCase))
                {
                    warnings.Add($"Imported skill '{payload.DisplayName}' as '{record.SkillId}' because the skill markdown resolves to that id.");
                }
            }
            catch (Exception ex)
            {
                errors.Add($"Failed to import skill '{payload.DisplayName}': {ex.Message}");
            }
            finally
            {
                TryDeleteDirectory(stagingRoot);
            }
        }

        return new StackImportResult(errors.Count == 0, imported, new Dictionary<string, string>(), warnings, errors);
    }

    private StackPackageRequirement CreatePackageRequirement()
        => new(SkillConstants.PackageId, CreatedWithVersion: packageContext.Version.ToString(), MinimumVersion: "1.0.0");

    private static IReadOnlyList<StackValueSensitivity> BuildSensitivities(InstalledSkillRecord skill)
    {
        var sensitivities = new List<StackValueSensitivity> { StackValueSensitivity.PrivateText };
        if (string.Equals(skill.SourceKind, "local", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(skill.SourceUri))
        {
            sensitivities.Add(StackValueSensitivity.LocalPath);
        }
        else if (!string.IsNullOrWhiteSpace(skill.SourceUri))
        {
            sensitivities.Add(StackValueSensitivity.NetworkEndpoint);
        }

        return sensitivities;
    }

    private IReadOnlyList<StackExportItemDetail> BuildExportDetails(InstalledSkillRecord skill)
    {
        var details = new List<StackExportItemDetail>
        {
            new(
                "Skill markdown and resources",
                BuildSkillFileSummary(skill),
                StackValueSensitivity.PrivateText,
                ValueWhenExcluded: "Not exported",
                DetailId: DetailFiles),
        };

        if (!string.IsNullOrWhiteSpace(skill.Description))
        {
            details.Add(new StackExportItemDetail(
                "Skill description",
                skill.Description.Trim(),
                StackValueSensitivity.PrivateText,
                ValueWhenExcluded: "Not exported",
                DetailId: DetailDescription));
        }

        if (!string.IsNullOrWhiteSpace(skill.SourceUri))
        {
            details.Add(new StackExportItemDetail(
                "Original source",
                skill.SourceUri,
                string.Equals(skill.SourceKind, "local", StringComparison.OrdinalIgnoreCase) ? StackValueSensitivity.LocalPath : StackValueSensitivity.NetworkEndpoint,
                ValueWhenExcluded: "Not exported",
                DetailId: DetailSource));
        }

        return details;
    }

    private string BuildSkillFileSummary(InstalledSkillRecord skill)
    {
        var rootPath = store.GetSkillRootPath(skill);
        if (!Directory.Exists(rootPath))
        {
            return "Skill files";
        }

        var files = Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(rootPath, path).Replace('\\', '/'))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .ToArray();
        return files.Length == 0 ? "No files" : string.Join(Environment.NewLine, files);
    }

    private static StackSafetyDescriptor BuildSafety(SkillStackPayload payload)
        => new(
            ContainsSecrets: false,
            ContainsSecretReferences: false,
            ContainsLocalPaths: payload.SourceKind == "local" && !string.IsNullOrWhiteSpace(payload.SourceUri),
            ContainsPrivateText: true,
            ContainsExecutableCommands: false,
            ContainsNetworkEndpoints: payload.SourceKind != "local" && !string.IsNullOrWhiteSpace(payload.SourceUri),
            ContainsMachineSpecificValues: payload.SourceKind == "local" && !string.IsNullOrWhiteSpace(payload.SourceUri));

    private static IEnumerable<StackPayloadFile> BuildPayloadFiles(string rootPath, ICollection<string> warnings)
    {
        if (!Directory.Exists(rootPath))
        {
            yield break;
        }

        foreach (var file in Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories)
                     .OrderBy(path => Path.GetRelativePath(rootPath, path), StringComparer.OrdinalIgnoreCase))
        {
            if (File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint))
            {
                warnings.Add($"Skipped symlinked skill file: {Path.GetRelativePath(rootPath, file)}");
                continue;
            }

            var relativePath = Path.GetRelativePath(rootPath, file).Replace('\\', '/');
            if (!IsSafeRelativePath(relativePath))
            {
                warnings.Add($"Skipped unsafe skill file path: {relativePath}");
                continue;
            }

            yield return new StackPayloadFile(relativePath, file);
        }
    }

    private static void CopyFragmentFiles(IReadOnlyList<StackPayloadFile> files, string targetRoot)
    {
        if (files.Count == 0)
        {
            throw new InvalidOperationException("Skill Stack fragment does not contain skill files.");
        }

        foreach (var file in files)
        {
            if (!IsSafeRelativePath(file.RelativePath) || !File.Exists(file.SourcePath))
            {
                continue;
            }

            var destinationPath = Path.Combine(targetRoot, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(file.SourcePath, destinationPath, overwrite: true);
        }

        if (!File.Exists(Path.Combine(targetRoot, "SKILL.md")))
        {
            throw new InvalidOperationException("Skill Stack fragment is missing SKILL.md.");
        }
    }

    private string CreateImportStagingRoot()
    {
        var path = Path.Combine(packageContext.Storage.CacheRootPath, "skill-stack-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static bool TryReadPayload(StackFragmentImport fragment, ICollection<string> warnings, out SkillStackPayload? payload)
    {
        payload = null;
        try
        {
            payload = JsonSerializer.Deserialize<SkillStackPayload>(fragment.JsonPayload, JsonOptions);
            if (payload is null || string.IsNullOrWhiteSpace(payload.SkillId) || string.IsNullOrWhiteSpace(payload.DisplayName))
            {
                warnings.Add($"Stack fragment '{fragment.FragmentId}' does not contain a valid skill payload.");
                payload = null;
                return false;
            }

            return true;
        }
        catch (JsonException ex)
        {
            warnings.Add($"Stack fragment '{fragment.FragmentId}' skill payload could not be parsed: {ex.Message}");
            return false;
        }
    }

    private static bool IsSafeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path))
        {
            return false;
        }

        return !path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(segment => segment is "." or "..");
    }

    private static string BuildActionId(string fragmentId, string skillId)
        => $"agent-skill:{fragmentId}:{skillId}";

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

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Temporary Stack skill import cleanup is best effort.
        }
    }

    private sealed record SkillStackPayload(
        string SkillId,
        string DisplayName,
        string? Description,
        string? Version,
        string? Author,
        string SourceKind,
        string? SourceUri,
        string? SourceRef,
        string? ResolvedCommitSha,
        string ContentHash,
        IReadOnlyDictionary<string, string> Metadata,
        IReadOnlyList<string> Files)
    {
        public static SkillStackPayload FromSkill(
            InstalledSkillRecord skill,
            IReadOnlyList<string> files,
            StackExportOptions options,
            StackExportRequest request)
            => new(
                skill.SkillId,
                SkillStore.ResolveDisplayName(skill),
                request.IsDetailSelected(skill.SkillId, DetailDescription) ? request.GetDetailValue(skill.SkillId, DetailDescription, skill.Description ?? string.Empty) : null,
                skill.Version,
                skill.Author,
                skill.SourceKind,
                ShouldIncludeSourceUri(skill, options, request) ? request.GetDetailValue(skill.SkillId, DetailSource, skill.SourceUri ?? string.Empty) : null,
                ShouldIncludeSourceUri(skill, options, request) ? skill.SourceRef : null,
                ShouldIncludeSourceUri(skill, options, request) ? skill.ResolvedCommitSha : null,
                skill.ContentHash,
                skill.Metadata,
                files);

        private static bool ShouldIncludeSourceUri(InstalledSkillRecord skill, StackExportOptions options, StackExportRequest request)
        {
            if (string.IsNullOrWhiteSpace(skill.SourceUri))
            {
                return false;
            }

            if (!request.IsDetailSelected(skill.SkillId, DetailSource))
            {
                return false;
            }

            if (request.GetItemSelection(skill.SkillId)?.Details is not null)
            {
                return true;
            }

            return string.Equals(skill.SourceKind, "local", StringComparison.OrdinalIgnoreCase)
                ? options.IncludeMachineSpecificValues
                : options.IncludeNetworkEndpoints;
        }
    }
}
