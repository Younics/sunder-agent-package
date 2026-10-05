using Sunder.Package.Agent.Skills.Runtime;
using Sunder.Package.Agent.Skills.Services;
using Sunder.Sdk.Stacks;

namespace Sunder.Package.Agent.Skills.Rpc;

public sealed partial class SkillStackProvider
{
    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    internal SkillStackProvider(SkillStackContributor contributor) : this((IPackageStackContributor)contributor) { }
}

internal sealed partial class ManagementService
{
    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    internal ManagementService(SkillRuntimeOwnerClient operations) : this((ISkillOwnerClient)operations) { }
}
