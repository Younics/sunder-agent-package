using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Contracts.Contracts;

/// <summary>
/// Manages binding-scoped executable search-path entries in an execution target's path namespace.
/// </summary>
/// <remarks>
/// These are configured additions used by future shell or process invocations, not necessarily the target's complete inherited <c>PATH</c>.
/// Implementations own normalization, deduplication, persistence, and safe coordination with active operations. Adding a directory does not
/// grant file permission; callers must trust its contents because executables found there can run with the target's authority. The contract
/// does not make a read-modify-write update atomic relative to other callers.
/// </remarks>
public interface IAgentExecutionPathEnvironment
{
    /// <summary>
    /// Lists the configured search-path additions for a workspace binding.
    /// </summary>
    /// <param name="context">The workspace binding whose target environment is queried.</param>
    /// <param name="cancellationToken">Signals that loading the binding-scoped environment should be canceled.</param>
    /// <returns>A read-only snapshot of normalized target-visible directory paths in application order.</returns>
    /// <exception cref="OperationCanceledException">The operation was canceled.</exception>
    ValueTask<IReadOnlyList<string>> ListPathEntriesAsync(
        AgentExecutionTargetContext context,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a directory to the binding's configured executable search path for subsequent invocations.
    /// </summary>
    /// <param name="context">The workspace binding whose target environment is updated.</param>
    /// <param name="executionPath">
    /// A directory in the execution target's path syntax. Implementations normalize and deduplicate the value; blank values may be ignored.
    /// </param>
    /// <param name="cancellationToken">Signals that validation or persistence should be canceled.</param>
    /// <returns>A value task that completes when the target has persisted or otherwise applied the entry.</returns>
    /// <exception cref="OperationCanceledException">The operation was canceled.</exception>
    ValueTask AddPathEntryAsync(
        AgentExecutionTargetContext context,
        string executionPath,
        CancellationToken cancellationToken = default);
}
