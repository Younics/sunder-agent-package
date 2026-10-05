using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Skills.Runtime;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Skills.PackageViews
{
    public sealed partial class SkillSettingsViewModel
    {
        [ActivatorUtilitiesConstructor]
        internal SkillSettingsViewModel(SkillAppRuntimeGateway gateway) : this((ISkillManagementGateway)gateway) { }
    }
}

namespace Sunder.Package.Agent.Skills.Runtime
{
    internal sealed partial class SkillAppRuntimeGateway
    {
        [ActivatorUtilitiesConstructor]
        internal SkillAppRuntimeGateway(SkillOwnerClient client, IPackageContext context) : this((ISkillOwnerClient)client, context) { }
    }
}
