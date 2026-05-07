using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Execution.Docker;

public sealed class DockerExecutionWorkspaceConfigService(IPackageContext packageContext)
{
    internal const string DefaultImageReference = "mcr.microsoft.com/dotnet/sdk:10.0";
    internal const string DefaultContainerRoot = "/workspace";
    internal const string DefaultShellPath = "/bin/sh";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public DockerExecutionWorkspaceConfig GetConfig(string bindingId)
    {
        var json = packageContext.Storage.State.GetValue(BuildKey(bindingId));
        if (string.IsNullOrWhiteSpace(json))
        {
            return Normalize(bindingId, new DockerExecutionWorkspaceConfig(DefaultImageReference, [DefaultContainerRoot], DefaultContainerRoot, null, DefaultShellPath));
        }

        try
        {
            return Normalize(bindingId, JsonSerializer.Deserialize<DockerExecutionWorkspaceConfig>(json, JsonOptions)
                                        ?? new DockerExecutionWorkspaceConfig(null, [], null, null, null));
        }
        catch
        {
            return Normalize(bindingId, new DockerExecutionWorkspaceConfig(DefaultImageReference, [DefaultContainerRoot], DefaultContainerRoot, null, DefaultShellPath));
        }
    }

    public void SaveConfig(string bindingId, DockerExecutionWorkspaceConfig config)
    {
        var normalized = Normalize(bindingId, config);
        EnsureHostRoots(normalized);
        packageContext.Storage.State.SetValueAsync(BuildKey(bindingId), JsonSerializer.Serialize(normalized, JsonOptions)).GetAwaiter().GetResult();
    }

    public IReadOnlyList<DockerExecutionMount> ResolveMounts(DockerExecutionWorkspaceConfig config)
    {
        var normalized = Normalize("mount-resolution", config);
        return normalized.AllowedRoots
            .Select(root => new DockerExecutionMount(ResolveHostPath(root), root))
            .ToArray();
    }

    public void EnsureHostRoots(DockerExecutionWorkspaceConfig config)
    {
        foreach (var mount in ResolveMounts(config))
        {
            Directory.CreateDirectory(mount.HostPath);
        }
    }

    private static DockerExecutionWorkspaceConfig Normalize(string bindingId, DockerExecutionWorkspaceConfig config)
    {
        var roots = (config.AllowedRoots.Count == 0 ? [DefaultContainerRoot] : config.AllowedRoots)
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(root => NormalizeContainerPath(root, allowRoot: false))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (roots.Length == 0)
        {
            roots = [DefaultContainerRoot];
        }

        ValidateNoNestedRoots(roots);

        var defaultWorkingDirectory = string.IsNullOrWhiteSpace(config.DefaultWorkingDirectory)
            ? roots.FirstOrDefault()
            : NormalizeContainerPath(config.DefaultWorkingDirectory, allowRoot: false);
        if (defaultWorkingDirectory is not null && !roots.Any(root => IsSameOrChildPath(defaultWorkingDirectory, root)))
        {
            defaultWorkingDirectory = roots.FirstOrDefault();
        }

        var shellPath = string.IsNullOrWhiteSpace(config.ShellPath)
            ? DefaultShellPath
            : NormalizeContainerPath(config.ShellPath, allowRoot: false);

        return new DockerExecutionWorkspaceConfig(
            string.IsNullOrWhiteSpace(config.ImageReference) ? DefaultImageReference : config.ImageReference.Trim(),
            roots,
            defaultWorkingDirectory,
            ResolveContainerName(bindingId, config.ContainerName),
            shellPath);
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

    private string ResolveHostPath(string containerRoot)
    {
        var relativePath = ToFileStoreRelativePath(containerRoot);
        return Path.GetFullPath(packageContext.Storage.Files.GetPath(relativePath));
    }

    internal static string ToFileStoreRelativePath(string containerRoot)
    {
        var normalized = NormalizeContainerPath(containerRoot, allowRoot: false);
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return Path.Combine(segments);
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
            throw new InvalidOperationException("Docker workspace roots cannot be '/'. Configure a subdirectory such as /workspace.");
        }

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Any(segment => segment is "." or ".."))
        {
            throw new InvalidOperationException("Docker container paths cannot contain '.' or '..' segments.");
        }
    }

    private static void ValidateNoNestedRoots(IReadOnlyList<string> roots)
    {
        foreach (var root in roots)
        {
            foreach (var other in roots)
            {
                if (string.Equals(root, other, StringComparison.Ordinal))
                {
                    continue;
                }

                if (IsSameOrChildPath(other, root))
                {
                    throw new InvalidOperationException($"Docker workspace root '{other}' is nested inside '{root}'. Configure non-overlapping roots.");
                }
            }
        }
    }

    private static string ResolveContainerName(string bindingId, string? configuredName)
    {
        if (!string.IsNullOrWhiteSpace(configuredName) && IsValidContainerName(configuredName.Trim()))
        {
            return configuredName.Trim();
        }

        return BuildContainerName(bindingId);
    }

    internal static string BuildContainerName(string bindingId)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(bindingId))).ToLowerInvariant();
        return $"sunder-agent-{hash[..16]}";
    }

    private static bool IsValidContainerName(string containerName)
        => containerName.Length > 0
           && char.IsLetterOrDigit(containerName[0])
           && containerName.All(ch => char.IsLetterOrDigit(ch) || ch is '_' or '.' or '-');

    private static string BuildKey(string bindingId) => $"workspace-bindings:{bindingId}:config";
}
