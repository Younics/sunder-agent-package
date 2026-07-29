using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Contracts.Contracts;

/// <summary>Optionally performs a bounded, non-dispatching safety preflight before a tool source is executed.</summary>
/// <remarks>
/// The host invokes this capability before marking durable execution as started. A non-null result must be an error
/// result that is safe to record as a known skipped/failed execution; the host will not call
/// <see cref="IAgentToolSource.ExecuteAsync"/> for that invocation.
/// </remarks>
public interface IAgentToolExecutionPreflightSource
{
    /// <summary>Returns a recorded skip/failure result, or <see langword="null"/> when normal dispatch may proceed.</summary>
    ValueTask<AgentToolResult?> PreflightExecutionAsync(
        AgentToolExecutionContext context,
        AgentToolRequest request,
        CancellationToken cancellationToken = default);
}
