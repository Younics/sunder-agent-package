using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.Protocol.Generated.ToolSource;

_ = typeof(IToolSourceProvider);
_ = typeof(ToolSourceClient);
_ = typeof(Sunder.Package.Agent.Protocol.Generated.ToolSource.ToolInvocationRequest);

var descriptor = AgentRpcContractDescriptors.Get(AgentRpcContractIds.ExecutionTarget);
if (descriptor.Services.SelectMany(static service => service.Methods)
    .Any(static method => method.MethodId == "legacy-file-search"))
{
    throw new InvalidOperationException("The packed execution-target descriptor retained legacy file search.");
}

var protocolAssembly = typeof(AgentRpcContractIds).Assembly;
var removedTypes = new[]
{
    "Sunder.Package.Agent.Contracts.Contracts.IAgentFileSearchExecutionTarget",
    "Sunder.Package.Agent.Contracts.Models.AgentFileSearchProcessRequest",
    "Sunder.Package.Agent.Contracts.Models.AgentFileSearchProcessResult",
    "Sunder.Package.Agent.Protocol.Generated.ExecutionTarget.AgentFileSearchProcessRequest",
    "Sunder.Package.Agent.Protocol.Generated.ExecutionTarget.AgentFileSearchProcessResult",
};
foreach (var removedType in removedTypes)
{
    if (protocolAssembly.GetType(removedType, throwOnError: false) is not null)
    {
        throw new InvalidOperationException($"The packed Protocol retained obsolete type '{removedType}'.");
    }
}

Console.WriteLine($"Verified packed {protocolAssembly.GetName().Name} {protocolAssembly.GetName().Version}.");
