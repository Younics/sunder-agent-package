using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Contracts.Contracts;

public interface IAgentTool
{
    AgentToolDescriptor Descriptor { get; }

    ValueTask<AgentToolReadiness> GetReadinessAsync(CancellationToken cancellationToken = default);

    ValueTask<AgentToolResult> ExecuteAsync(
        AgentToolExecutionContext context,
        AgentToolRequest request,
        CancellationToken cancellationToken = default);
}
