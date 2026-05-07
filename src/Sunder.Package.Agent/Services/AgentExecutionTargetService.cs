using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Services;

public sealed class AgentExecutionTargetService(IPackageExtensionCatalog extensionCatalog)
{
    private readonly IPackageExtensionCatalog _extensionCatalog = extensionCatalog;

    public IReadOnlyList<AgentExecutionTargetDescriptor> ListTargets()
        => _extensionCatalog.GetExtensions(PackageExtensionPoints.ExecutionTargets)
            .Select(target => target.Descriptor)
            .OrderBy(target => target.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(target => target.TargetKind, StringComparer.OrdinalIgnoreCase)
            .ThenBy(target => target.TargetId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public IAgentExecutionTarget? ResolveTarget(AgentWorkspaceBindingRecord? binding)
    {
        if (binding is null || !binding.IsEnabled)
        {
            return null;
        }

        return _extensionCatalog.GetExtensions(PackageExtensionPoints.ExecutionTargets)
            .FirstOrDefault(target => string.Equals(target.Descriptor.TargetId, binding.ContributionId, StringComparison.OrdinalIgnoreCase)
                                      || string.Equals(target.Descriptor.TargetKind, binding.ContributionId, StringComparison.OrdinalIgnoreCase));
    }
}
