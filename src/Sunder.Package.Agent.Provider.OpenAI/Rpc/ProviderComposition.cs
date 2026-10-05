namespace Sunder.Package.Agent.Provider.OpenAI.Rpc;

internal sealed partial class ManagementService
{
    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    internal ManagementService(OpenAiRuntimeManagementClient operations) : this((IOpenAiOwnerManagementClient)operations) { }
}
