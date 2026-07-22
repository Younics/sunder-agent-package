using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Contracts.Contracts;

/// <summary>
/// Defines one installed-package tool with its own descriptor, readiness, and execution behavior.
/// </summary>
/// <remarks>
/// Arguments originate from a model and are untrusted even when they match the advertised schema.
/// Implementations must validate arguments and enforce resource boundaries again at execution time.
/// Expected failures should be returned as <see cref="AgentToolResult" /> errors; caller cancellation
/// is propagated. Cancellation does not guarantee rollback of mutations already performed.
/// </remarks>
public interface IAgentTool
{
    /// <summary>Gets the stable identity, schema, mutation, and scheduling metadata for the tool.</summary>
    AgentToolDescriptor Descriptor { get; }

    /// <summary>Checks whether the tool may currently be advertised.</summary>
    /// <param name="cancellationToken">A token that cancels configuration, secret, or connectivity checks.</param>
    /// <returns>A point-in-time readiness report whose tool identity matches <see cref="Descriptor" />.</returns>
    ValueTask<AgentToolReadiness> GetReadinessAsync(CancellationToken cancellationToken = default);

    /// <summary>Executes one invocation after host catalog and permission checks.</summary>
    /// <param name="context">The host-verified execution and correlation context.</param>
    /// <param name="request">The selected tool identity and untrusted JSON arguments.</param>
    /// <param name="cancellationToken">A token that requests cancellation without promising mutation rollback.</param>
    /// <returns>A bounded result, including an error result for expected tool or backend failures.</returns>
    ValueTask<AgentToolResult> ExecuteAsync(
        AgentToolExecutionContext context,
        AgentToolRequest request,
        CancellationToken cancellationToken = default);
}
