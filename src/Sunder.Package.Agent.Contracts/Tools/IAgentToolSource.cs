using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Contracts.Contracts;

/// <summary>
/// Discovers and executes a related set of tools under one stable source identity.
/// </summary>
/// <remarks>
/// Source and tool identifiers participate in persisted profile assignments and security
/// fingerprints and must remain stable. Arguments originate from a model and remain untrusted even
/// when they match an advertised schema. Implementations must validate them, enforce resource scope
/// at execution time, return bounded outputs, and avoid exposing credentials in any catalog or result.
/// Expected backend failures should be returned as error results; caller cancellation is propagated.
/// Cancellation does not roll back side effects already performed.
/// </remarks>
public interface IAgentToolSource
{
    /// <summary>Gets the stable source identifier used for ownership, assignment, and routing.</summary>
    string SourceId { get; }

    /// <summary>Gets the source name shown in catalogs and profile editors.</summary>
    string DisplayName { get; }

    /// <summary>Gets the stable source category, such as <c>workspace</c>, <c>local</c>, or <c>mcp</c>.</summary>
    string SourceKind { get; }

    /// <summary>Lists tool descriptors applicable to the current catalog context.</summary>
    /// <param name="context">The optional session, profile, workspace, and execution-target snapshots.</param>
    /// <param name="cancellationToken">A token that cancels discovery or remote schema loading.</param>
    /// <returns>A stable read-only descriptor snapshot; readiness is evaluated separately.</returns>
    ValueTask<IReadOnlyList<AgentToolDescriptor>> ListToolsAsync(
        AgentToolSourceContext context,
        CancellationToken cancellationToken = default);

    /// <summary>Checks a source-owned tool against the current context before it is advertised.</summary>
    /// <remarks>
    /// The host treats a listed tool with no readiness report as ready. Sources should therefore
    /// return a failed report for every recognized tool that cannot safely execute in this context.
    /// Readiness is a point-in-time check and is not a permission grant.
    /// </remarks>
    /// <param name="toolId">The tool identifier to check.</param>
    /// <param name="context">The current catalog context.</param>
    /// <param name="cancellationToken">A token that cancels configuration, connection, or resource checks.</param>
    /// <returns>The readiness report, or <see langword="null" /> when the source has no report for the identifier.</returns>
    ValueTask<AgentToolReadiness?> GetReadinessAsync(
        string toolId,
        AgentToolSourceContext context,
        CancellationToken cancellationToken = default);

    /// <summary>Executes one source-owned tool after host catalog and permission checks.</summary>
    /// <param name="context">The host-verified execution, authorization-scope, and correlation context.</param>
    /// <param name="request">The selected tool identity and untrusted JSON arguments.</param>
    /// <param name="cancellationToken">A token that requests cancellation without promising mutation rollback.</param>
    /// <returns>A bounded result, including an error result for expected tool or backend failures.</returns>
    ValueTask<AgentToolResult> ExecuteAsync(
        AgentToolExecutionContext context,
        AgentToolRequest request,
        CancellationToken cancellationToken = default);
}
