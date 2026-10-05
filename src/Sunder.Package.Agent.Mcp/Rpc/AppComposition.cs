using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Mcp.Runtime;
using Sunder.Package.Agent.Shared.Presentation;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Mcp
{
    public sealed partial class AgentMcpSettingsViewModel
    {
        [ActivatorUtilitiesConstructor]
        internal AgentMcpSettingsViewModel(McpAppRuntimeGateway gateway) : this((IMcpManagementGateway)gateway, PresentationDispatcher.Capture()) { }
    }
}

namespace Sunder.Package.Agent.Mcp.Runtime
{
    internal sealed partial class McpAppRuntimeGateway
    {
        [ActivatorUtilitiesConstructor]
        internal McpAppRuntimeGateway(McpOwnerManagementRpcClient client, IPackageContext context)
            : this((IMcpOwnerManagementClient)client, context.Callbacks, context.Logging.LoggerFactory) { }
    }
}
