using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Abstractions;
using Sunder.Package.Agent.Runtime;

namespace Sunder.Package.Agent.Services;

public sealed class AgentExecutionTargetService(IPackageExtensionCatalog extensionCatalog) : IAgentExecutionGateway
{
    private readonly IPackageExtensionInvocationCatalog _invocationCatalog =
        AgentExtensionInvocation.Require(extensionCatalog);

    public IReadOnlyList<AgentExecutionTargetDescriptor> ListTargets()
        => GetTargetReferences()
            .Select(target => target.Metadata)
            .OrderBy(target => target.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(target => target.TargetKind, StringComparer.OrdinalIgnoreCase)
            .ThenBy(target => target.TargetId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    internal AgentExtensionReference<IAgentExecutionTarget, AgentExecutionTargetDescriptor>?
        ResolveTargetReference(AgentWorkspaceBindingRecord? binding)
    {
        if (binding is null || !binding.IsEnabled)
        {
            return null;
        }

        return GetTargetReferences()
            .FirstOrDefault(target => string.Equals(
                                          target.Metadata.TargetId,
                                          binding.ContributionId,
                                          StringComparison.OrdinalIgnoreCase)
                                      || string.Equals(
                                          target.Metadata.TargetKind,
                                          binding.ContributionId,
                                          StringComparison.OrdinalIgnoreCase));
    }

    private IReadOnlyList<AgentExtensionReference<IAgentExecutionTarget, AgentExecutionTargetDescriptor>>
        GetTargetReferences()
        => AgentExtensionInvocation.Snapshot(
            _invocationCatalog,
            PackageExtensionPoints.ExecutionTargets,
            static target => target.Descriptor);

    public Task<AgentExecutionTargetWarmupResult> WarmWorkspaceAsync(
        AgentWorkspaceRecord workspace,
        CancellationToken cancellationToken = default)
        => Task.FromResult(AgentExecutionTargetWarmupResult.Skipped(
            "Warmup requires the Runtime workspace service."));
}
