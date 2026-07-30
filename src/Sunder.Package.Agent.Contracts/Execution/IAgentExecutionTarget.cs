using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Contracts.Contracts;

/// <summary>
/// Defines a Runtime-side backend that executes shell and filesystem operations for a selected workspace binding.
/// </summary>
/// <remarks>
/// The Runtime extension catalog owns target instances and may share one instance across workspaces and overlapping requests. Implementations
/// must be thread-safe or serialize access to shared backend state, must not require a UI thread, and own all processes, command streams,
/// temporary files, mounts, and backend leases they create. Permission planning remains caller-owned, while the target owns final path
/// canonicalization and containment checks immediately before each operation.
///
/// Configured workspace paths constrain target file APIs and requested working directories; they do not make arbitrary shell commands a
/// sandbox. A command can exercise every capability available to the selected backend, so callers must authorize shell execution separately.
/// </remarks>
public interface IAgentExecutionTarget
{
    /// <summary>
    /// Gets immutable discovery metadata and the stable identity used by workspace bindings.
    /// </summary>
    AgentExecutionTargetDescriptor Descriptor { get; }

    /// <summary>
    /// Gets an opaque generation for all target-owned mutable configuration that can affect workspace execution.
    /// </summary>
    /// <remarks>
    /// Implementations with target-owned workspace settings must return the same value for semantically equivalent normalized configuration
    /// and a different value when execution behavior can change. Operations receiving an expected generation in
    /// <see cref="AgentExecutionTargetContext.ExpectedConfigurationGeneration"/> must reject the operation before side effects when the
    /// current generation differs. Targets without mutable target-owned configuration may use the default <see langword="null"/> result.
    /// </remarks>
    /// <param name="context">The workspace and execution binding whose target-owned configuration is requested.</param>
    /// <param name="cancellationToken">Signals that configuration loading should be canceled.</param>
    /// <returns>An opaque stable generation, or <see langword="null"/> when no target-owned mutable configuration applies.</returns>
    ValueTask<string?> GetConfigurationGenerationAsync(
        AgentExecutionTargetContext context,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult<string?>(null);

    /// <summary>
    /// Assesses required binding configuration and backend availability for a workspace.
    /// </summary>
    /// <param name="context">The workspace and execution binding to assess.</param>
    /// <param name="cancellationToken">Signals that configuration loading or backend probing should be canceled.</param>
    /// <returns>
    /// A point-in-time status. Expected missing configuration and backend failures should be represented as readiness values when practical;
    /// cancellation and failures that prevent an assessment may be raised as exceptions.
    /// </returns>
    /// <exception cref="OperationCanceledException">The readiness check was canceled.</exception>
    ValueTask<AgentExecutionTargetReadiness> GetReadinessAsync(
        AgentExecutionTargetContext context,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the command interpreter selected by the binding and the syntax callers must use for shell requests.
    /// </summary>
    /// <param name="context">The workspace binding whose shell configuration is requested.</param>
    /// <param name="cancellationToken">Signals that shell configuration discovery should be canceled.</param>
    /// <returns>A descriptor in the target's path and command-language namespace; no process is started or transferred to the caller.</returns>
    /// <exception cref="NotSupportedException"><see cref="AgentExecutionTargetDescriptor.SupportsShell"/> is <see langword="false"/>.</exception>
    /// <exception cref="OperationCanceledException">The operation was canceled.</exception>
    ValueTask<AgentExecutionShellDescriptor> GetShellAsync(
        AgentExecutionTargetContext context,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Canonicalizes a file path and classifies its permission boundary without reading or mutating file contents.
    /// </summary>
    /// <remarks>
    /// This method is used before permission approval, so implementations may perform only the minimum metadata access needed to canonicalize
    /// the path and determine existence. The result is advisory and must be recomputed or revalidated when a later operation executes.
    /// </remarks>
    /// <param name="context">The workspace roots and binding configuration used to resolve the path.</param>
    /// <param name="path">A target-visible absolute path or a path relative to the configured default working directory.</param>
    /// <param name="cancellationToken">Signals that configuration loading or canonicalization should be canceled.</param>
    /// <returns>The canonical resource reference, configured-scope classification, and best-effort existence state.</returns>
    /// <exception cref="InvalidOperationException">The target cannot canonicalize or classify the path.</exception>
    /// <exception cref="OperationCanceledException">The operation was canceled.</exception>
    ValueTask<AgentResolvedResource> ResolveFileResourceAsync(
        AgentExecutionTargetContext context,
        string path,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs an opaque command through the binding's selected shell and waits for completion, timeout, or cancellation.
    /// </summary>
    /// <param name="context">The workspace binding and caller authorization context for the invocation.</param>
    /// <param name="request">The shell command, target-visible working directory, and optional target-bounded timeout.</param>
    /// <param name="cancellationToken">
    /// Signals cancellation. The target must attempt to terminate its owned command and then throw <see cref="OperationCanceledException"/>.
    /// </param>
    /// <returns>
    /// The exit status and bounded, buffered output after the target has released command streams and per-operation resources. A nonzero exit
    /// or timeout is represented in the result rather than by cancellation.
    /// </returns>
    /// <exception cref="InvalidOperationException">The binding is invalid, the backend is unavailable, or the requested working directory is not allowed.</exception>
    /// <exception cref="OperationCanceledException">The caller canceled the command.</exception>
    ValueTask<AgentShellCommandResult> ExecuteShellAsync(
        AgentExecutionTargetContext context,
        AgentShellCommandRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads bounded text content or a bounded directory listing through the selected execution backend.
    /// </summary>
    /// <param name="context">The workspace roots, binding configuration, and outside-scope authorization for the read.</param>
    /// <param name="request">The target-visible path and optional one-based line range.</param>
    /// <param name="cancellationToken">Signals that path resolution or I/O should be canceled.</param>
    /// <returns>
    /// Buffered content and truncation or range metadata. Expected file, range, size, containment, and I/O failures should be returned as a
    /// structured <see cref="AgentFileReadResult.IsError"/> result; the caller owns no stream.
    /// </returns>
    /// <exception cref="OperationCanceledException">The read was canceled.</exception>
    ValueTask<AgentFileReadResult> ReadFileAsync(
        AgentExecutionTargetContext context,
        AgentFileReadRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates or replaces a file through the selected execution backend after final containment and concurrency checks.
    /// </summary>
    /// <param name="context">The workspace roots, binding configuration, and outside-scope authorization for the mutation.</param>
    /// <param name="request">The target-visible destination, complete text content, overwrite policy, and optional expected-content hash.</param>
    /// <param name="cancellationToken">Signals that path resolution or the mutation should be canceled.</param>
    /// <returns>A structured mutation result describing the canonical target path and any expected operational error.</returns>
    /// <remarks>
    /// Implementations own temporary files and must clean them up on failure or cancellation. They should prevent lost updates when an
    /// expected-content hash is supplied, but callers must not assume atomic replacement on backends that cannot provide it.
    /// </remarks>
    /// <exception cref="OperationCanceledException">The mutation was canceled; the target must make a best effort not to publish partial content.</exception>
    ValueTask<AgentFileMutationResult> WriteFileAsync(
        AgentExecutionTargetContext context,
        AgentFileWriteRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a file or, when explicitly requested, a directory tree through the selected execution backend.
    /// </summary>
    /// <param name="context">The workspace roots, binding configuration, and outside-scope authorization for the mutation.</param>
    /// <param name="request">The target-visible path, recursive-delete choice, and optional expected-content hash.</param>
    /// <param name="cancellationToken">Signals that path resolution or deletion should be canceled.</param>
    /// <returns>A structured mutation result describing the canonical target path and any expected operational error.</returns>
    /// <remarks>
    /// Targets must revalidate physical containment after acquiring any mutation lock and immediately before deletion. Cancellation can race
    /// with an irreversible backend operation, so callers must not infer from cancellation alone that no item was deleted.
    /// </remarks>
    /// <exception cref="OperationCanceledException">The operation was canceled.</exception>
    ValueTask<AgentFileMutationResult> DeleteFileAsync(
        AgentExecutionTargetContext context,
        AgentFileDeleteRequest request,
        CancellationToken cancellationToken = default);
}
