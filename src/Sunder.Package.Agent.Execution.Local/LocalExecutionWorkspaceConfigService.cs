using System.Text.Json;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Execution.Local;

public sealed class LocalExecutionWorkspaceConfigService(IPackageContext packageContext) : IAgentWorkspacePathMigrationContributor
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public string ContributorId => "sunder.package.agent.execution.local.workspace-path-migration";

    public LocalExecutionWorkspaceConfig GetConfig(string bindingId)
    {
        var json = packageContext.Storage.State.GetValue(BuildKey(bindingId));
        if (string.IsNullOrWhiteSpace(json))
        {
            return new LocalExecutionWorkspaceConfig(null, []);
        }

        try
        {
            var config = JsonSerializer.Deserialize<LocalExecutionWorkspaceConfig>(json, JsonOptions);
            return Normalize(config ?? new LocalExecutionWorkspaceConfig(null, []));
        }
        catch
        {
            return new LocalExecutionWorkspaceConfig(null, []);
        }
    }

    public void SaveConfig(string bindingId, LocalExecutionWorkspaceConfig config)
    {
        var normalized = Normalize(config);
        packageContext.Storage.State.SetValueAsync(BuildKey(bindingId), JsonSerializer.Serialize(normalized, JsonOptions)).GetAwaiter().GetResult();
    }

    private static LocalExecutionWorkspaceConfig Normalize(LocalExecutionWorkspaceConfig config)
    {
        var pathEntries = (config.PathEntries ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.GetFullPath(ExpandPath(path.Trim())))
            .Distinct(PathStringComparer)
            .ToArray();

        return new LocalExecutionWorkspaceConfig(
            string.IsNullOrWhiteSpace(config.SelectedShellId) ? null : config.SelectedShellId.Trim(),
            pathEntries);
    }

    public bool CanMigrate(AgentWorkspacePathMigrationContext context)
        => string.Equals(context.Binding.ContributionId, "local", StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<AgentWorkspacePathMigrationItem> GetLegacyWorkspacePaths(AgentWorkspacePathMigrationContext context)
    {
        var json = packageContext.Storage.State.GetValue(BuildKey(context.Binding.BindingId));
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("AllowedRoots", out var rootsElement) || rootsElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var defaultWorkingDirectory = TryGetString(root, "DefaultWorkingDirectory");
            var normalizedDefault = string.IsNullOrWhiteSpace(defaultWorkingDirectory)
                ? null
                : Path.GetFullPath(ExpandPath(defaultWorkingDirectory.Trim()));
            var items = new List<AgentWorkspacePathMigrationItem>();
            var index = 0;
            foreach (var item in rootsElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var value = item.GetString();
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                var hostPath = Path.GetFullPath(ExpandPath(value.Trim()));
                var isDefault = normalizedDefault is not null && IsSameOrChildPath(normalizedDefault, hostPath);
                items.Add(new AgentWorkspacePathMigrationItem(hostPath, isDefault, SortOrder: index++));
            }

            return items;
        }
        catch
        {
            return [];
        }
    }

    public void CompleteWorkspacePathMigration(AgentWorkspacePathMigrationContext context)
        => SaveConfig(context.Binding.BindingId, GetConfig(context.Binding.BindingId));

    internal static string ExpandPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        if (path == "~")
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        return path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..])
            : Environment.ExpandEnvironmentVariables(path);
    }

    internal static bool IsSameOrChildPath(string candidatePath, string rootPath)
    {
        var candidate = Path.GetFullPath(candidatePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var root = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var comparison = PathStringComparison;
        return string.Equals(candidate, root, comparison)
               || candidate.StartsWith(root + Path.DirectorySeparatorChar, comparison);
    }

    internal static StringComparer PathStringComparer
        => OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    private static StringComparison PathStringComparison
        => OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private static string BuildKey(string bindingId) => $"workspace-bindings:{bindingId}:config";

    private static string? TryGetString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
}
