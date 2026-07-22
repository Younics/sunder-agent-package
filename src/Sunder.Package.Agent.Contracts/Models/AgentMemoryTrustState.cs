namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Classifies the evidence behind a recalled semantic-memory assertion.
/// </summary>
/// <remarks>
/// This classification does not promote durable memory into a system-instruction channel. Recalled entries remain
/// provenance-labeled reference data and are subject to current-user confirmation and conflict handling.
/// </remarks>
public enum AgentMemoryTrustState
{
    /// <summary>The assertion is not attributable to the user and must be treated as untrusted data, never as an instruction.</summary>
    Untrusted = 0,

    /// <summary>The assertion was supplied directly by the user, but is still historical reference data rather than current authorization.</summary>
    UserProvided = 1,

    /// <summary>The user previously established the assertion as a standing instruction; current policy still determines whether it may be followed.</summary>
    UserConfirmedInstruction = 2,

    /// <summary>The assertion conflicts with other evidence and must not be treated as authoritative until resolved.</summary>
    Contested = 3,
}
