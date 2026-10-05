using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Contracts.Contracts;

/// <summary>
/// Extends an execution target with structured executable-and-argument process invocation.
/// </summary>
/// <remarks>
/// This capability avoids requiring callers to construct a command in the selected shell's syntax. The target owns process or remote-command
/// lifetime and all standard streams, and returns only bounded, buffered output. Implementations must remain safe under overlapping calls or
/// explicitly serialize access to a shared backend.
/// </remarks>
public interface IAgentProcessExecutionTarget : IAgentExecutionTarget
{
    /// <summary>
    /// Starts a program in the selected target environment and waits for it to exit, time out, or be canceled.
    /// </summary>
    /// <param name="context">The workspace binding, correlation identities, and outside-scope authorization for the invocation.</param>
    /// <param name="request">The executable, unquoted argument list, target-visible working directory, and optional timeout.</param>
    /// <param name="cancellationToken">
    /// Signals cancellation. Implementations must attempt to terminate the owned operation and then throw <see cref="OperationCanceledException"/>.
    /// </param>
    /// <returns>
    /// The exit code and bounded combined output. Ordinary nonzero exits, start failures, and timeouts are represented in the result when the
    /// backend can report them; invalid configuration or path containment failures may be raised as exceptions.
    /// </returns>
    /// <exception cref="OperationCanceledException">The caller canceled the invocation.</exception>
    ValueTask<AgentShellCommandResult> ExecuteProcessAsync(
        AgentExecutionTargetContext context,
        AgentProcessCommandRequest request,
        CancellationToken cancellationToken = default);
}
