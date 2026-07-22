namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Identifies the party that originally supplied a durable semantic-memory assertion.
/// </summary>
/// <remarks>
/// Provenance is evidence for trust evaluation, not an authorization decision. Consumers must also honor
/// <see cref="AgentMemoryTrustState"/> and keep recalled memory out of privileged instruction channels.
/// </remarks>
public enum AgentMemoryProvenance
{
    /// <summary>The original author cannot be established; the assertion must be handled as untrusted data.</summary>
    Unknown = 0,

    /// <summary>The assertion originated in user-authored input, subject to any later contesting or correction.</summary>
    User = 1,

    /// <summary>An assistant model produced the assertion; it must not be treated as a user instruction.</summary>
    Assistant = 2,

    /// <summary>A tool or external system produced the assertion; its payload remains untrusted external data.</summary>
    Tool = 3,
}
