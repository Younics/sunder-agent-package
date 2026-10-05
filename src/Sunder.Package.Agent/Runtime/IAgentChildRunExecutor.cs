using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Contracts.Contracts;

/// <summary>
/// Creates or reuses a correlated child session and executes one delegated task within it.
/// </summary>
public interface IAgentChildRunExecutor
{
    /// <summary>Runs a child task and returns the persisted child-session outcome.</summary>
    /// <remarks>
    /// Cancellation stops waiting/execution but does not imply that already committed child-session, turn, or
    /// checkpoint data is rolled back. The base executor throws <see cref="InvalidOperationException"/> for a missing
    /// parent session; provider, model, and workspace preparation failures normally produce a failed child checkpoint
    /// and result instead of an exception.
    /// </remarks>
    /// <param name="request">The parent correlation, child profile snapshot, workspace, and task payload.</param>
    /// <param name="cancellationToken">A token that cancels preparation and child execution.</param>
    /// <returns>The child session identifier, durable status, summary, and latest assistant content.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is canceled before the operation completes.</exception>
    ValueTask<AgentChildRunResult> RunChildAsync(
        AgentChildRunRequest request,
        CancellationToken cancellationToken = default);
}
