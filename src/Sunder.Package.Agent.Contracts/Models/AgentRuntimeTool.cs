using Microsoft.Extensions.AI;

namespace Sunder.Package.Agent.Contracts.Models;

public sealed record AgentRuntimeTool(
    AgentToolDescriptor Descriptor,
    AIFunctionDeclaration Declaration);
