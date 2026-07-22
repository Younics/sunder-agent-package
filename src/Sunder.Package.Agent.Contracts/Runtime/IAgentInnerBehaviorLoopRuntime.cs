using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Contracts.Contracts;

/// <summary>
/// Allows a behavior-loop decorator to delegate execution to the host's undecorated default loop.
/// </summary>
/// <remarks>This optional capability avoids rediscovering or recursively invoking the selected outer loop.</remarks>
public interface IAgentInnerBehaviorLoopRuntime
{
    /// <summary>Executes the built-in inner loop for the same immutable run context.</summary>
    /// <param name="context">The run context supplied to the outer behavior loop.</param>
    /// <param name="cancellationToken">A token that requests prompt termination of inner-loop work.</param>
    /// <returns>The inner loop's durable checkpoint and completion classification.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is canceled and the inner loop propagates cancellation.</exception>
    ValueTask<AgentBehaviorLoopResult> RunDefaultLoopAsync(
        AgentBehaviorLoopContext context,
        CancellationToken cancellationToken = default);
}
