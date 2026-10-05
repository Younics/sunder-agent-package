using Microsoft.Extensions.DependencyInjection;

namespace Sunder.Package.Agent.Builder;

public sealed partial class BuilderWorkspaceExecutionService
{
    [ActivatorUtilitiesConstructor]
    internal BuilderWorkspaceExecutionService(BuilderRpcGateway rpc) : this((IBuilderRpcGateway)rpc) { }
}
