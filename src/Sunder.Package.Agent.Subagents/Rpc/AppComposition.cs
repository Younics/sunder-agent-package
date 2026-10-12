using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Shared.Presentation;
using Sunder.Package.Agent.Subagents.Runtime;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Notifications;
using Sunder.Generated.Packages.Sunder_Package_Agent_Subagents_Services;

namespace Sunder.Package.Agent.Subagents.PackageViews
{
    public sealed partial class SubagentsViewModel
    {
        [ActivatorUtilitiesConstructor]
        internal SubagentsViewModel(SubagentAppRuntimeGateway gateway, IPackageSettingsNavigationService navigation)
            : this(gateway, navigation, PresentationDispatcher.Capture()) { }
    }

    public sealed partial class SubsessionsViewModel
    {
        [ActivatorUtilitiesConstructor]
        internal SubsessionsViewModel(SubagentAppRuntimeGateway gateway, IPackageContext context, IPackageNotificationService notifications)
            : this(gateway, gateway, gateway, gateway, PresentationDispatcher.Capture(),
                new PresentationOperationObserver(context, notifications, "Subsessions")) { }
    }
}

namespace Sunder.Package.Agent.Subagents.Runtime
{
    internal sealed partial class SubagentAppRuntimeGateway
    {
        [ActivatorUtilitiesConstructor]
        internal SubagentAppRuntimeGateway(ISubagentsManagementService client, IPackageContext context) : this(client, context.Logging.LoggerFactory) { }
    }
}
