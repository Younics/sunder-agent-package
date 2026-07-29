using Sunder.Agent.Execution.Common;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Execution.Local;

internal static class LocalScopedInstructionDiscovery
{
    public static ValueTask<AgentScopedInstructionDiscoveryResult> DiscoverAsync(
        LocalExecutionRuntimeConfig config,
        AgentScopedInstructionDiscoveryRequest request,
        CancellationToken cancellationToken,
        ILocalSecureFileSystemHooks? hooks = null)
    {
        ValidateRequest(request);
        var mounts = config.WorkspacePaths
            .Select(Path.GetFullPath)
            .Distinct(LocalSecurePathEngine.PathComparer)
            .Select(path => new HostScopedInstructionMount(path, path))
            .ToArray();
        var probes = request.Probes
            .Select(probe =>
            {
                var path = LocalSecurePathEngine.ResolveLexicalPath(config, probe.Path);
                return new HostScopedInstructionProbe(probe.Path, path, path, probe.IsDirectory);
            })
            .ToArray();
        return HostScopedInstructionDiscovery.DiscoverAsync(
            mounts,
            probes,
            cancellationToken,
            hooks);
    }

    private static void ValidateRequest(AgentScopedInstructionDiscoveryRequest request)
    {
        if (request.Probes is null
            || request.Probes.Count == 0
            || request.Probes.Count > 64
            || request.Probes.Any(probe => probe is null || string.IsNullOrWhiteSpace(probe.Path))
            || request.Probes
                .Select(probe => (probe.Path, probe.IsDirectory, probe.FollowFinalSymbolicLink))
                .Distinct()
                .Count() != request.Probes.Count)
        {
            throw new InvalidOperationException(
                "Local scoped-instruction discovery requires 1 to 64 unique, non-empty probes.");
        }
    }
}
