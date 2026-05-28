using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Execution.Docker;

public sealed class DockerExecutionWorkspaceConfigService(IPackageContext packageContext, DockerImageCatalogService? imageCatalogService = null)
    : IAgentWorkspacePathMigrationContributor
{
    internal const string DefaultImageReference = "agent0ai/agent-zero:latest";
    internal const string DefaultContainerRoot = "/workspace";
    internal const string DefaultShellPath = "/bin/sh";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly char[] AliasSeparators = [' ', '.', '_'];
    private readonly DockerImageCatalogService _imageCatalogService = imageCatalogService ?? new DockerImageCatalogService(packageContext);

    public string ContributorId => "sunder.package.agent.execution.docker.workspace-path-migration";

    public DockerExecutionWorkspaceConfig GetConfig(string bindingId)
    {
        var json = packageContext.Storage.State.GetValue(BuildKey(bindingId));
        if (string.IsNullOrWhiteSpace(json))
        {
            return Normalize(bindingId, new DockerExecutionWorkspaceConfig(null, null, DefaultShellPath, []));
        }

        try
        {
            return Normalize(bindingId, JsonSerializer.Deserialize<DockerExecutionWorkspaceConfig>(json, JsonOptions)
                                         ?? new DockerExecutionWorkspaceConfig(null, null, null, []));
        }
        catch
        {
            return Normalize(bindingId, new DockerExecutionWorkspaceConfig(null, null, DefaultShellPath, []));
        }
    }

    public void SaveConfig(string bindingId, DockerExecutionWorkspaceConfig config)
    {
        var normalized = Normalize(bindingId, config);
        packageContext.Storage.State.SetValueAsync(BuildKey(bindingId), JsonSerializer.Serialize(normalized, JsonOptions)).GetAwaiter().GetResult();
    }

    internal DockerExecutionRuntimeConfig BuildRuntimeConfig(
        string bindingId,
        AgentWorkspaceRecord workspace,
        DockerExecutionWorkspaceConfig config)
    {
        var normalized = Normalize(bindingId, config);
        var mounts = BuildMounts(workspace.Paths);
        var defaultHostPath = workspace.Paths
            .OrderBy(path => path.SortOrder)
            .FirstOrDefault(path => path.IsDefault && !string.IsNullOrWhiteSpace(path.HostPath))
            ?.HostPath;
        var defaultWorkingDirectory = ResolveDefaultContainerPath(mounts, defaultHostPath) ?? mounts.FirstOrDefault()?.ContainerPath;
        return new DockerExecutionRuntimeConfig(
            normalized.ImageReference,
            normalized.ContainerName,
            normalized.ShellPath,
            normalized.PathEntries,
            mounts,
            defaultWorkingDirectory);
    }

    internal IReadOnlyList<DockerExecutionMount> ResolveMounts(DockerExecutionRuntimeConfig config)
        => config.Mounts;

    internal string ResolveHostPath(DockerExecutionRuntimeConfig config, string containerRoot)
    {
        var root = NormalizeContainerPath(containerRoot, allowRoot: false);
        return config.Mounts.FirstOrDefault(mount => string.Equals(mount.ContainerPath, root, StringComparison.Ordinal))?.HostPath
               ?? throw new InvalidOperationException($"Docker workspace path is not mounted: {root}");
    }

    public string ResolveDefaultHostPath(string containerRoot)
    {
        var relativePath = ToFileStoreRelativePath(containerRoot);
        return ValidateHostPath(Path.GetFullPath(packageContext.Storage.Files.GetPath(relativePath)));
    }

    internal void EnsureHostMountPaths(DockerExecutionRuntimeConfig config)
    {
        foreach (var mount in ResolveMounts(config))
        {
            if (File.Exists(mount.HostPath))
            {
                throw new InvalidOperationException($"Docker host mount path points to a file: {mount.HostPath}");
            }

            if (!Directory.Exists(mount.HostPath))
            {
                throw new InvalidOperationException($"Workspace path does not exist: {mount.HostPath}");
            }
        }
    }

    public bool CanMigrate(AgentWorkspacePathMigrationContext context)
        => string.Equals(context.Binding.ContributionId, "docker", StringComparison.OrdinalIgnoreCase);

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

            var hostRoots = ReadLegacyHostRoots(root);
            var defaultWorkingDirectory = TryGetString(root, "DefaultWorkingDirectory");
            var normalizedDefault = string.IsNullOrWhiteSpace(defaultWorkingDirectory)
                ? null
                : NormalizeContainerPath(defaultWorkingDirectory, allowRoot: false);
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

                var containerRoot = NormalizeContainerPath(value, allowRoot: false);
                var hostPath = hostRoots.TryGetValue(containerRoot, out var configuredHostPath)
                    ? configuredHostPath
                    : ResolveDefaultHostPath(containerRoot);
                var isDefault = normalizedDefault is not null && IsSameOrChildPath(normalizedDefault, containerRoot);
                items.Add(new AgentWorkspacePathMigrationItem(hostPath, isDefault, index++));
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

    private DockerExecutionWorkspaceConfig Normalize(string bindingId, DockerExecutionWorkspaceConfig config)
    {
        var shellPath = string.IsNullOrWhiteSpace(config.ShellPath)
            ? DefaultShellPath
            : NormalizeContainerPath(config.ShellPath, allowRoot: false);
        var pathEntries = (config.PathEntries ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => NormalizeContainerPath(path.Trim(), allowRoot: false))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return new DockerExecutionWorkspaceConfig(
            string.IsNullOrWhiteSpace(config.ImageReference) ? _imageCatalogService.GetDefaultImageReference() : DockerImageCatalogService.NormalizeImageReference(config.ImageReference),
            ResolveContainerName(bindingId, config.ContainerName),
            shellPath,
            pathEntries);
    }

    internal static string NormalizeContainerPath(string path)
        => NormalizeContainerPath(path, allowRoot: true);

    private static string NormalizeContainerPath(string path, bool allowRoot)
    {
        var trimmed = path.Trim().Replace('\\', '/');
        if (!trimmed.StartsWith('/'))
        {
            trimmed = "/" + trimmed;
        }

        while (trimmed.Contains("//", StringComparison.Ordinal))
        {
            trimmed = trimmed.Replace("//", "/", StringComparison.Ordinal);
        }

        var normalized = trimmed.Length > 1 ? trimmed.TrimEnd('/') : trimmed;
        ValidateContainerPath(normalized, allowRoot);
        return normalized;
    }

    internal static bool IsSameOrChildPath(string candidatePath, string rootPath)
    {
        var candidate = NormalizeContainerPath(candidatePath);
        var root = NormalizeContainerPath(rootPath);
        return string.Equals(candidate, root, StringComparison.Ordinal)
               || candidate.StartsWith(root + "/", StringComparison.Ordinal);
    }

    internal static string NormalizeHostPath(string hostPath)
        => ValidateHostPath(Path.GetFullPath(Environment.ExpandEnvironmentVariables(hostPath.Trim())));

    internal static string ToFileStoreRelativePath(string containerRoot)
    {
        var normalized = NormalizeContainerPath(containerRoot, allowRoot: false);
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return Path.Combine(segments);
    }

    internal static string BuildContainerName(string bindingId)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(bindingId))).ToLowerInvariant();
        return $"sunder-agent-{hash[..16]}";
    }

    private static IReadOnlyList<DockerExecutionMount> BuildMounts(IReadOnlyList<AgentWorkspacePathRecord> workspacePaths)
    {
        var mounts = new List<DockerExecutionMount>();
        foreach (var workspacePath in workspacePaths.OrderBy(path => path.SortOrder))
        {
            if (string.IsNullOrWhiteSpace(workspacePath.HostPath))
            {
                continue;
            }

            var hostPath = NormalizeHostPath(workspacePath.HostPath);
            if (mounts.Any(mount => string.Equals(mount.HostPath, hostPath, GetHostPathStringComparison())))
            {
                continue;
            }

            mounts.Add(new DockerExecutionMount(hostPath, BuildContainerPath(hostPath)));
        }

        return mounts;
    }

    private static string? ResolveDefaultContainerPath(IReadOnlyList<DockerExecutionMount> mounts, string? defaultHostPath)
    {
        if (string.IsNullOrWhiteSpace(defaultHostPath))
        {
            return null;
        }

        var normalized = NormalizeHostPath(defaultHostPath);
        return mounts.FirstOrDefault(mount => string.Equals(mount.HostPath, normalized, GetHostPathStringComparison()))?.ContainerPath;
    }

    private static string BuildContainerPath(string hostPath)
    {
        var normalized = Path.GetFullPath(hostPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var leaf = Path.GetFileName(normalized);
        if (string.IsNullOrWhiteSpace(leaf))
        {
            leaf = "workspace";
        }

        var slug = BuildAliasSlug(leaf);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant()[..8];
        return $"{DefaultContainerRoot}/{slug}-{hash}";
    }

    private static string BuildAliasSlug(string value)
    {
        var builder = new StringBuilder(value.Length);
        var previousDash = false;
        foreach (var ch in value.Trim().ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(ch))
            {
                builder.Append(ch);
                previousDash = false;
                continue;
            }

            if ((AliasSeparators.Contains(ch) || ch == '-') && !previousDash && builder.Length > 0)
            {
                builder.Append('-');
                previousDash = true;
            }
        }

        return builder.ToString().Trim('-') is { Length: > 0 } slug ? slug : "workspace";
    }

    private static IReadOnlyDictionary<string, string> ReadLegacyHostRoots(JsonElement root)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!root.TryGetProperty("HostRoots", out var hostRootsElement) || hostRootsElement.ValueKind != JsonValueKind.Object)
        {
            return result;
        }

        foreach (var property in hostRootsElement.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var value = property.Value.GetString();
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            result[NormalizeContainerPath(property.Name, allowRoot: false)] = NormalizeHostPath(value);
        }

        return result;
    }

    private static void ValidateContainerPath(string normalized, bool allowRoot)
    {
        if (string.IsNullOrWhiteSpace(normalized) || !normalized.StartsWith('/'))
        {
            throw new InvalidOperationException("Docker container paths must be absolute POSIX paths.");
        }

        if (normalized.Contains(',', StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Docker container paths cannot contain commas.");
        }

        if (!allowRoot && string.Equals(normalized, "/", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Docker workspace paths cannot be '/'. Configure a subdirectory such as /workspace.");
        }

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Any(segment => segment is "." or ".."))
        {
            throw new InvalidOperationException("Docker container paths cannot contain '.' or '..' segments.");
        }
    }

    private static string ValidateHostPath(string hostPath)
    {
        if (hostPath.Contains(',', StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Docker host mount paths cannot contain commas.");
        }

        return hostPath;
    }

    private static string ResolveContainerName(string bindingId, string? configuredName)
    {
        if (!string.IsNullOrWhiteSpace(configuredName) && IsValidContainerName(configuredName.Trim()))
        {
            return configuredName.Trim();
        }

        return BuildContainerName(bindingId);
    }

    private static bool IsValidContainerName(string containerName)
        => containerName.Length > 0
           && char.IsLetterOrDigit(containerName[0])
           && containerName.All(ch => char.IsLetterOrDigit(ch) || ch is '_' or '.' or '-');

    private static string? TryGetString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static StringComparison GetHostPathStringComparison()
        => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private static string BuildKey(string bindingId) => $"workspace-bindings:{bindingId}:config";
}
