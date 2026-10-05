using Sunder.Package.Agent.Mcp.Services;
using Sunder.Package.Agent.Mcp.Runtime;
using Sunder.Sdk.Stacks;

namespace Sunder.Package.Agent.Mcp.Rpc;

internal sealed partial class ManagementService
{
    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    internal ManagementService(McpRuntimeManagementClient operations) : this((IMcpOwnerManagementClient)operations) { }
}

public sealed partial class McpStackProvider
{
    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    internal McpStackProvider(McpServerStackContributor contributor) : this((IPackageStackContributor)contributor) { }
}
