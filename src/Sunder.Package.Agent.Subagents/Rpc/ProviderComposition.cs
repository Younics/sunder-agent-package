using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Subagents.Runtime;
using Sunder.Package.Agent.Subagents.Services;
using Sunder.Sdk.Stacks;

namespace Sunder.Package.Agent.Subagents.Rpc;

internal sealed partial class ManagementService
{
    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    internal ManagementService(SubagentRuntimeOwnerClient operations) : this((ISubagentOwnerClient)operations) { }
}

public sealed partial class SubagentStackProvider
{
    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    internal SubagentStackProvider(SubagentStackContributor contributor) : this((IPackageStackContributor)contributor) { }
}

public sealed partial class SelectableCapabilityProvider
{
    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    internal SelectableCapabilityProvider(SubagentFeature feature) : this((IAgentProfileSelectableCapabilityProvider)feature) { }
}
