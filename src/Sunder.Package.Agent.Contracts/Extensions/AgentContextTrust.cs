namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Classifies the user-authority level of supplementary prompt context.
/// </summary>
/// <remarks>
/// Trust is independent of <see cref="AgentContextProvenance"/> and is a declarative label, not a cryptographic
/// assertion or authorization decision. All supplementary context is rendered as user-role reference data and
/// remains outside the privileged system prompt. Producers must choose the least-trusting applicable value, and
/// consumers must tolerate future enum values.
/// </remarks>
public enum AgentContextTrust
{
    /// <summary>
    /// The content is reference data only and must not be followed as an instruction.
    /// </summary>
    /// <remarks>
    /// Use this for assistant claims, transcript summaries, durable recall, tool output, remote content, and
    /// extension-generated data unless the current user directly supplied the exact content.
    /// </remarks>
    Untrusted = 0,

    /// <summary>
    /// The exact content was supplied directly by the current user in the current trust context.
    /// </summary>
    /// <remarks>
    /// User provenance does not grant system authority, validate factual claims, authorize unrelated actions,
    /// or make embedded third-party text safe. Do not use this value for summaries or recalled paraphrases.
    /// </remarks>
    UserProvided = 1,
}
