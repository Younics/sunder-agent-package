using System.Text.Json;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Execution.Local;

public sealed class LocalExecutionWorkspaceConfigService(IPackageContext packageContext)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public LocalExecutionWorkspaceConfig GetConfig(string bindingId)
    {
        var json = packageContext.Storage.State.GetValue(BuildKey(bindingId));
        if (string.IsNullOrWhiteSpace(json))
        {
            return new LocalExecutionWorkspaceConfig([], null, null);
        }

        try
        {
            var config = JsonSerializer.Deserialize<LocalExecutionWorkspaceConfig>(json, JsonOptions);
            return Normalize(config ?? new LocalExecutionWorkspaceConfig([], null, null));
        }
        catch
        {
            return new LocalExecutionWorkspaceConfig([], null, null);
        }
    }

    public void SaveConfig(string bindingId, LocalExecutionWorkspaceConfig config)
    {
        var normalized = Normalize(config);
        packageContext.Storage.State.SetValueAsync(BuildKey(bindingId), JsonSerializer.Serialize(normalized, JsonOptions)).GetAwaiter().GetResult();
    }

    private static LocalExecutionWorkspaceConfig Normalize(LocalExecutionWorkspaceConfig config)
    {
        var roots = config.AllowedRoots
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(root => Path.GetFullPath(ExpandPath(root.Trim())))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var defaultWorkingDirectory = string.IsNullOrWhiteSpace(config.DefaultWorkingDirectory)
            ? roots.FirstOrDefault()
            : Path.GetFullPath(ExpandPath(config.DefaultWorkingDirectory.Trim()));

        if (defaultWorkingDirectory is not null
            && !roots.Any(root => IsSameOrChildPath(defaultWorkingDirectory, root)))
        {
            defaultWorkingDirectory = roots.FirstOrDefault();
        }

        return new LocalExecutionWorkspaceConfig(roots, defaultWorkingDirectory, string.IsNullOrWhiteSpace(config.SelectedShellId) ? null : config.SelectedShellId.Trim());
    }

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
        return string.Equals(candidate, root, StringComparison.OrdinalIgnoreCase)
               || candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildKey(string bindingId) => $"workspace-bindings:{bindingId}:config";
}
