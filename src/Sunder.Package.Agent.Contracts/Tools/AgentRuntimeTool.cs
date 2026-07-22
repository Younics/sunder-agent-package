using Microsoft.Extensions.AI;

namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Pairs host security and catalog metadata with the function declaration sent to a chat provider.
/// </summary>
/// <remarks>
/// <paramref name="Descriptor" /> remains authoritative for selection, permission, mutation,
/// priority, and concurrency. <paramref name="Declaration" /> must use the same tool identifier and
/// an equivalent argument schema; it is not invoked directly by the host.
/// </remarks>
/// <param name="Descriptor">The host-facing tool descriptor.</param>
/// <param name="Declaration">The model-facing function declaration that remains valid for the provider run.</param>
public sealed record AgentRuntimeTool(
    AgentToolDescriptor Descriptor,
    AIFunctionDeclaration Declaration);
