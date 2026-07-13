using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Contracts.Contracts;

public interface IAgentChildRunExecutor
{
    ValueTask<AgentChildRunResult> RunChildAsync(
        AgentChildRunRequest request,
        CancellationToken cancellationToken = default);
}
