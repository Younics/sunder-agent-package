using Sunder.Agent.Execution.Common;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Execution.Docker;

internal static class DockerPathResolver
{
    internal const string StructuredBindRequiredErrorCode = "docker-structured-bind-required";
    internal const string StructuredBindRequiredMessage =
        "Secure Docker structured file operations require the path to map to a configured workspace bind mount.";
    internal const string StructuredRootDeleteErrorCode = "docker-structured-root-delete-forbidden";
    internal const string StructuredRootDeleteMessage =
        "Secure Docker structured file operations cannot delete a configured workspace bind root or its ancestor.";

    public static string ResolvePath(DockerExecutionRuntimeConfig config, string path, bool allowOutsideConfiguredScope)
    {
        var normalized = ResolveRuntimePath(path, ResolveDefaultBaseDirectory(config));

        if (!allowOutsideConfiguredScope && !IsInsideWorkspacePath(config, normalized))
        {
            throw new InvalidOperationException($"Path '{path}' is outside the configured Docker workspace paths.");
        }

        return normalized;
    }

    public static string ResolveWorkingDirectory(
        DockerExecutionRuntimeConfig config,
        string? requestedWorkingDirectory,
        bool allowOutsideConfiguredScope)
        => string.IsNullOrWhiteSpace(requestedWorkingDirectory)
            ? ResolveDefaultBaseDirectory(config)
            : ResolvePath(config, requestedWorkingDirectory, allowOutsideConfiguredScope);

    public static string ResolveDefaultBaseDirectory(DockerExecutionRuntimeConfig config)
        => string.IsNullOrWhiteSpace(config.DefaultWorkingDirectory)
            ? config.Mounts.FirstOrDefault()?.ContainerPath
              ?? throw new InvalidOperationException("The Docker execution binding has no workspace paths configured.")
            : config.DefaultWorkingDirectory;

    public static bool IsInsideWorkspacePath(DockerExecutionRuntimeConfig config, string candidate)
        => config.Mounts.Any(mount => IsSameOrChildNormalized(candidate, mount.ContainerPath));

    public static AgentExecutionPathMapping MapToHostPath(
        DockerExecutionRuntimeConfig config,
        string executionPath)
    {
        var binding = ResolveHostBinding(config, executionPath);
        _ = HostSecurePathEngine.Probe([binding.Mount.HostPath], binding.HostPath);
        return new AgentExecutionPathMapping(binding.ContainerPath, binding.HostPath, IsInsideAllowedRoot: true);
    }

    public static DockerHostPathBinding ResolveHostBinding(
        DockerExecutionRuntimeConfig config,
        string executionPath)
    {
        var normalizedPath = ResolvePath(config, executionPath, allowOutsideConfiguredScope: true);
        var mount = config.Mounts
            .Where(mount => IsSameOrChildNormalized(normalizedPath, mount.ContainerPath))
            .OrderByDescending(mount => mount.ContainerPath.Length)
            .FirstOrDefault()
            ?? throw new DockerStructuredBindRequiredException(normalizedPath);
        var relative = normalizedPath[mount.ContainerPath.Length..].TrimStart('/');
        var hostRoot = Path.GetFullPath(mount.HostPath);
        var hostPath = hostRoot;
        if (relative.Length > 0)
        {
            foreach (var segment in relative.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                ValidateHostComponent(segment);
                hostPath = Path.Combine(hostPath, segment);
            }
        }
        hostPath = Path.GetFullPath(hostPath);
        if (!HostSecurePathEngine.IsSameOrChild(hostPath, hostRoot))
        {
            throw new DockerStructuredBindRequiredException(normalizedPath);
        }
        return new DockerHostPathBinding(normalizedPath, hostPath, mount with { HostPath = hostRoot });
    }

    public static bool IsConfiguredMountRootOrAncestor(
        DockerExecutionRuntimeConfig config,
        string normalizedContainerPath)
        => config.Mounts.Any(mount => IsSameOrChildNormalized(mount.ContainerPath, normalizedContainerPath));

    public static bool IsConfiguredHostRootOrAncestor(
        DockerExecutionRuntimeConfig config,
        string hostPath)
    {
        var candidate = Path.GetFullPath(hostPath);
        return config.Mounts.Any(mount =>
            HostSecurePathEngine.IsSameOrChild(Path.GetFullPath(mount.HostPath), candidate));
    }

    private static string ResolveRuntimePath(string path, string baseDirectory)
    {
        if (path.Contains('\0'))
        {
            throw new InvalidOperationException("Docker container paths cannot contain NUL characters.");
        }
        var candidate = string.IsNullOrEmpty(path)
            ? baseDirectory
            : path;
        if (!candidate.StartsWith("/", StringComparison.Ordinal))
        {
            candidate = DockerExecutionWorkspaceConfigService.NormalizeContainerPath(baseDirectory) + "/" + candidate;
        }

        var segments = new List<string>();
        foreach (var segment in candidate.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            switch (segment)
            {
                case ".":
                    continue;
                case ".." when segments.Count > 0:
                    segments.RemoveAt(segments.Count - 1);
                    continue;
                case "..":
                    throw new InvalidOperationException($"Path '{path}' cannot resolve above the container root.");
                default:
                    segments.Add(segment);
                    break;
            }
        }

        if (segments.Count > HostSecurePathEngine.MaxPathDepth)
        {
            throw new InvalidOperationException(
                $"Docker container paths support at most {HostSecurePathEngine.MaxPathDepth} components.");
        }
        return "/" + string.Join("/", segments);
    }

    private static bool IsSameOrChildNormalized(string candidatePath, string rootPath)
    {
        var root = DockerExecutionWorkspaceConfigService.NormalizeContainerPath(rootPath);
        return string.Equals(candidatePath, root, StringComparison.Ordinal)
               || (root == "/"
                   ? candidatePath.Length > 0 && candidatePath[0] == '/'
                   : candidatePath.StartsWith(root + "/", StringComparison.Ordinal));
    }

    private static void ValidateHostComponent(string segment)
    {
        if (segment.Length == 0 || segment is "." or ".." || segment.Contains('\0'))
        {
            throw new DockerStructuredBindRequiredException();
        }
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        if (segment.IndexOfAny(['\\', ':']) >= 0
            || Path.IsPathRooted(segment)
            || segment.EndsWith(' ')
            || segment.EndsWith('.'))
        {
            throw new DockerStructuredBindRequiredException();
        }

        var stem = segment.Split('.', 2)[0];
        if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            || (stem.Length == 4
                && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                    || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
                && stem[3] is >= '1' and <= '9'))
        {
            throw new DockerStructuredBindRequiredException();
        }
    }
}

internal sealed record DockerHostPathBinding(
    string ContainerPath,
    string HostPath,
    DockerExecutionMount Mount);

internal sealed class DockerStructuredBindRequiredException(string? containerPath = null)
    : InvalidOperationException(DockerPathResolver.StructuredBindRequiredMessage)
{
    public string? ContainerPath { get; } = containerPath;

    public string ErrorCode => DockerPathResolver.StructuredBindRequiredErrorCode;
}
