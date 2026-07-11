using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Execution.Local;

internal static class LocalResourceResolver
{
    public static IReadOnlyList<AgentResolvedExecutionResource> ResolveResources(IReadOnlyList<AgentExecutionResourceDescriptor> resources)
        => resources
            .Where(resource => !string.IsNullOrWhiteSpace(resource.HostPath))
            .Select(resource => new AgentResolvedExecutionResource(
                resource.ResourceId,
                resource.ResourceKind,
                resource.SourceId,
                resource.DisplayName,
                resource.HostPath,
                resource.HostPath,
                resource.AccessMode,
                resource.Metadata))
            .ToArray();

    public static AgentResolvedResource ResolveFileResource(LocalExecutionRuntimeConfig config, string path, bool allowOutsideConfiguredScope)
    {
        var resolved = LocalPathResolver.ResolvePath(config, path, allowOutsideConfiguredScope);
        var physicalPath = LocalPathResolver.ResolvePhysicalPath(resolved);
        var boundary = LocalPathResolver.IsInsidePhysicalWorkspacePath(config, physicalPath)
            ? AgentPermissionBoundaryIds.ConfiguredScope
            : AgentPermissionBoundaryIds.OutsideConfiguredScope;
        return new AgentResolvedResource(
            "file",
            resolved,
            physicalPath,
            boundary,
            File.Exists(physicalPath) || Directory.Exists(physicalPath));
    }

    public static AgentExecutionPathMapping MapToHostPath(LocalExecutionRuntimeConfig config, string executionPath)
    {
        var resolved = LocalPathResolver.ResolvePath(config, executionPath, allowOutsideConfiguredScope: false);
        var hostPath = LocalPathResolver.ResolveFileSystemPath(config, executionPath, allowOutsideConfiguredScope: false);
        return new AgentExecutionPathMapping(resolved, hostPath, IsInsideAllowedRoot: true);
    }
}
