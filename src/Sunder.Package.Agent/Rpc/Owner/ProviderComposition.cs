using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.HistorySearch;
using Sunder.Package.Agent.Services;
using Sunder.Package.Agent.Services.BehaviorLoops;
using Sunder.Sdk.Stacks;

namespace Sunder.Package.Agent.Rpc.Owner
{
    public sealed partial class AgentProfileStackProvider
    {
        [ActivatorUtilitiesConstructor]
        internal AgentProfileStackProvider(AgentProfileStackContributor contributor) : this((IPackageStackContributor)contributor) { }
    }

    public sealed partial class AgentWorkspaceStackProvider
    {
        [ActivatorUtilitiesConstructor]
        internal AgentWorkspaceStackProvider(AgentWorkspaceStackContributor contributor) : this((IPackageStackContributor)contributor) { }
    }
}

namespace Sunder.Package.Agent.Rpc.Public
{
    public sealed partial class BehaviorLoopService
    {
        [ActivatorUtilitiesConstructor]
        internal BehaviorLoopService(DefaultAgentBehaviorLoop loop, Protocol.AgentRpcCatalog catalog) : this((IAgentBehaviorLoop)loop, catalog) { }
    }

    public sealed partial class PromptContextContributorService
    {
        [ActivatorUtilitiesConstructor]
        internal PromptContextContributorService(WorkspaceDocumentationContextService contributor, Protocol.AgentRpcCatalog catalog)
            : this((IAgentPromptContextContributor)contributor, catalog) { }
    }

    public partial class SessionCleanerService
    {
        [ActivatorUtilitiesConstructor]
        internal SessionCleanerService(AgentAttachmentService cleaner) : this((IAgentSessionDataCleaner)cleaner) { }
    }

    public sealed partial class HistorySessionCleanerProvider
    {
        [ActivatorUtilitiesConstructor]
        internal HistorySessionCleanerProvider(HistorySearchIndexingService cleaner) : this((IAgentSessionDataCleaner)cleaner) { }
    }
}
