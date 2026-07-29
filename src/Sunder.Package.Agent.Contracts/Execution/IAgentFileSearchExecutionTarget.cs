using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Contracts.Contracts;

/// <summary>Executes legacy Files search processes against a target-owned canonical path binding.</summary>
/// <remarks>
/// Implementations must resolve and authorize <see cref="AgentFileSearchProcessRequest.Path"/> immediately before execution, insert only the
/// resulting canonical absolute path into the command, and require an exact approved resource reference for an outside-scope path when supported.
/// </remarks>
public interface IAgentFileSearchExecutionTarget : IAgentExecutionTarget
{
    /// <summary>Executes one search process while retaining the target resources used to canonicalize its path.</summary>
    /// <param name="context">The workspace binding and exact outside-scope resource approvals for this operation.</param>
    /// <param name="request">The path and structured command template to bind.</param>
    /// <param name="cancellationToken">Signals cancellation of path resolution or process execution.</param>
    /// <returns>The process result and canonical-to-expected path mapping roots.</returns>
    ValueTask<AgentFileSearchProcessResult> ExecuteFileSearchProcessAsync(
        AgentExecutionTargetContext context,
        AgentFileSearchProcessRequest request,
        CancellationToken cancellationToken = default);
}
