namespace Sunder.Package.Agent.Execution.Local.Rpc;

internal sealed partial class ManagementService
{
    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    internal ManagementService(LocalExecutionPresentationRpcHandler operations) : this((ILocalExecutionPresentationClient)operations) { }
}
