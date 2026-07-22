using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Contracts.Contracts;

/// <summary>
/// Defines a selectable strategy that drives provider and tool cycles for one Agent run.
/// </summary>
public interface IAgentBehaviorLoop
{
    /// <summary>Gets the stable descriptor used for profile selection and capability discovery.</summary>
    AgentBehaviorLoopDescriptor Descriptor { get; }

    /// <summary>
    /// Executes the loop for the supplied run generation until it completes, suspends, stops, or is interrupted.
    /// </summary>
    /// <remarks>
    /// Implementations must use <paramref name="runtime"/> for fenced transcript and checkpoint mutations, periodically
    /// test current-run ownership, and stop promptly on cancellation. The host converts an otherwise unhandled error
    /// into a failed run checkpoint; cancellation or stale ownership is reconciled as interruption.
    /// </remarks>
    /// <param name="context">The immutable host-selected session, profile, model, workspace, and run snapshot.</param>
    /// <param name="runtime">The bounded host operations for the same run generation.</param>
    /// <param name="cancellationToken">A token that requests prompt termination of provider, tool, and persistence work.</param>
    /// <returns>The durable checkpoint and completion classification observed when execution returns.</returns>
    /// <exception cref="OperationCanceledException">The implementation may propagate cancellation requested by <paramref name="cancellationToken"/>.</exception>
    ValueTask<AgentBehaviorLoopResult> RunAsync(
        AgentBehaviorLoopContext context,
        IAgentBehaviorLoopRuntime runtime,
        CancellationToken cancellationToken = default);
}
