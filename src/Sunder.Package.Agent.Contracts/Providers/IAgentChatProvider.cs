using Microsoft.Extensions.AI;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Contracts.Contracts;

/// <summary>
/// Supplies chat model discovery, readiness, capabilities, and clients for one provider identity.
/// </summary>
/// <remarks>
/// Implementations resolve credentials through host secret or authorization services and must not
/// expose them through descriptors, readiness messages, client context logging, or exceptions.
/// Returned catalogs are read-only snapshots that must remain valid after the call. Caller-requested
/// cancellation is propagated as <see cref="OperationCanceledException" /> rather than readiness or
/// provider failure. The host owns the provider lifetime.
/// </remarks>
public interface IAgentChatProvider
{
    /// <summary>Gets immutable provider identity and provider-wide capability metadata.</summary>
    AgentProviderDescriptor Descriptor { get; }

    /// <summary>Lists models currently selectable through this provider and its selected authentication mode.</summary>
    /// <param name="cancellationToken">A token that cancels configuration or remote catalog discovery.</param>
    /// <returns>A stable read-only snapshot whose model identifiers are unique within the provider.</returns>
    ValueTask<IReadOnlyList<AgentModelDescriptor>> GetAvailableModelsAsync(CancellationToken cancellationToken = default);

    /// <summary>Checks whether the provider has the configuration and authorization required to attempt chat requests.</summary>
    /// <param name="cancellationToken">A token that cancels configuration, secret, authorization, or connectivity checks.</param>
    /// <returns>A point-in-time readiness report for <see cref="Descriptor" />.</returns>
    ValueTask<AgentProviderReadiness> GetReadinessAsync(CancellationToken cancellationToken = default);

    /// <summary>Resolves effective tool, streaming, input, and token-limit capabilities for a run.</summary>
    /// <param name="modelId">The selected model identifier, or <see langword="null" /> to request provider-level defaults.</param>
    /// <param name="cancellationToken">A token that cancels capability resolution.</param>
    /// <returns>The capabilities the runtime may rely on when constructing the request.</returns>
    ValueTask<AgentProviderRunCapabilities> GetRunCapabilitiesAsync(string? modelId, CancellationToken cancellationToken = default);

    /// <summary>Creates a chat client bound to the provider and model in <paramref name="context" />.</summary>
    /// <remarks>
    /// The implementation should validate or normalize the selected model and resolve secrets
    /// internally. The caller owns the returned client and disposes it after the provider cycle.
    /// Cancellation stops client creation; cancellation of later chat operations is passed directly
    /// to the returned client.
    /// </remarks>
    /// <param name="context">The selected provider/model identity and optional diagnostic sink.</param>
    /// <param name="cancellationToken">A token that cancels authorization and client creation.</param>
    /// <returns>A caller-owned client configured for the selected identity.</returns>
    ValueTask<IChatClient> CreateChatClientAsync(AgentChatClientContext context, CancellationToken cancellationToken = default);
}
