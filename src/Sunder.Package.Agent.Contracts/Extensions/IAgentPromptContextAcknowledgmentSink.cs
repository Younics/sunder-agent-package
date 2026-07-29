using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Contracts.Contracts;

/// <summary>Consumes receipts for host-reserved prompt context after final serialization.</summary>
/// <remarks>
/// The Runtime invokes this contract only for package-owner-verified required context sources. A receipt is an
/// acknowledgment of exact prompt retention, not a permission grant. Implementations must reject stale session,
/// transcript, target, and hash bindings and must persist acknowledgment before returning.
/// </remarks>
public interface IAgentPromptContextAcknowledgmentSink
{
    /// <summary>Gets a stable package-scoped diagnostic identity.</summary>
    string AcknowledgmentSinkId { get; }

    /// <summary>Persists the exact host-reserved instruction hashes retained in the final prompt.</summary>
    ValueTask AcknowledgePromptContextAsync(
        AgentPromptContextReceipt receipt,
        CancellationToken cancellationToken = default);
}
