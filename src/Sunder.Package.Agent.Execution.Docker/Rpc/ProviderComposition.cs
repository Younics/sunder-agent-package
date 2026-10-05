using Sunder.Sdk.Stacks;

namespace Sunder.Package.Agent.Execution.Docker.Rpc;

internal sealed partial class ManagementService
{
    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    internal ManagementService(DockerExecutionPresentationRpcHandler operations) : this((IDockerExecutionPresentationClient)operations) { }
}

public sealed partial class DockerStackProvider
{
    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    internal DockerStackProvider(DockerImageStackContributor contributor) : this((IPackageStackContributor)contributor) { }
}
