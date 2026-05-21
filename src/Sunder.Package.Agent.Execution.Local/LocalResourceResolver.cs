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

    public static AgentResolvedResource ResolveFileResource(LocalExecutionWorkspaceConfig config, string path, bool allowOutsideConfiguredScope)
    {
        var resolved = LocalPathResolver.ResolvePath(config, path, allowOutsideConfiguredScope);
        var boundary = LocalPathResolver.IsInsideAllowedRoot(config, resolved)
            ? AgentPermissionBoundaryIds.ConfiguredScope
            : AgentPermissionBoundaryIds.OutsideConfiguredScope;
        return new AgentResolvedResource(
            "file",
            resolved,
            resolved,
            boundary,
            File.Exists(resolved) || Directory.Exists(resolved));
    }

    public static AgentExecutionPathMapping MapToHostPath(LocalExecutionWorkspaceConfig config, string executionPath)
    {
        var resolved = LocalPathResolver.ResolvePath(config, executionPath, allowOutsideConfiguredScope: false);
        return new AgentExecutionPathMapping(resolved, resolved, LocalPathResolver.IsInsideAllowedRoot(config, resolved));
    }
}
