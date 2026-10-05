using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.HistorySearch;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.Runtime;
using Sunder.Package.Agent.Services;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Notifications;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.PackageViews
{
    // Models and the workspace view context remain owned by each view constructor.
    public partial class AgentChatView
    {
        [ActivatorUtilitiesConstructor]
        internal AgentChatView(AgentAppRuntimeGateway gateway, AgentChatSelectionStateService selection,
            AgentToolPresentationService tools, IPackageShellViewService shell, IPackageNotificationService notifications, IPackageContext context)
            : this(gateway, gateway, gateway, gateway, gateway, selection, tools, gateway, shell, gateway, notifications, context) { }
    }

    public partial class AgentWorkspacesView
    {
        [ActivatorUtilitiesConstructor]
        internal AgentWorkspacesView(AgentAppRuntimeGateway gateway, AgentAppRpcCatalog catalog, IPackageSettingsNavigationService navigation)
            : this(gateway, gateway, catalog, navigation) { }
    }

    public partial class AgentProfilesView
    {
        [ActivatorUtilitiesConstructor]
        internal AgentProfilesView(AgentAppRuntimeGateway gateway, IPackageSettingsNavigationService navigation)
            : this((IAgentProfileGateway)gateway, navigation) { }
    }

    public partial class AgentHistorySearchView
    {
        [ActivatorUtilitiesConstructor]
        internal AgentHistorySearchView(AgentAppRuntimeGateway gateway, AgentChatSelectionStateService selection, IPackageShellViewService shell)
            : this((IAgentHistorySearchGateway)gateway, selection, shell) { }
    }

    public partial class AgentPermissionsView
    {
        [ActivatorUtilitiesConstructor]
        internal AgentPermissionsView(AgentAppRuntimeGateway gateway, IPackageContext context, IPackageNotificationService notifications)
            : this((IAgentPermissionGateway)gateway, context, notifications) { }
    }
}

namespace Sunder.Package.Agent.Runtime
{
    internal sealed partial class AgentAppRuntimeGateway
    {
        [ActivatorUtilitiesConstructor]
        internal AgentAppRuntimeGateway(AgentOwnerRpcClient owner) : this((IAgentOwnerClient)owner) { }
    }
}

namespace Sunder.Package.Agent.Services
{
    // App catalog observation starts with its first consumer, as in the canonical App.
    internal sealed class AgentAppRpcCatalog(ISunderRpcClient rpc) : AgentRpcCatalog(rpc);

    public sealed partial class AgentToolPresentationService
    {
        [ActivatorUtilitiesConstructor]
        internal AgentToolPresentationService(AgentAppRpcCatalog catalog) : this((AgentRpcCatalog)catalog) { }
    }
}
