namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>Reports host-reserved instruction hashes that survived final prompt serialization.</summary>
/// <param name="SessionId">The session for which the prompt was serialized.</param>
/// <param name="RunId">The run receiving the serialized prompt.</param>
/// <param name="TranscriptEpoch">The transcript epoch used to build the prompt.</param>
/// <param name="Blocks">The exact fully retained host-reserved instruction blocks.</param>
public sealed record AgentPromptContextReceipt(
    Guid SessionId,
    Guid RunId,
    long TranscriptEpoch,
    IReadOnlyList<AgentPromptContextReceiptBlock> Blocks);

/// <summary>Identifies one fully retained host-reserved instruction block.</summary>
/// <param name="HostIdentity">The opaque host-reserved authority identity.</param>
/// <param name="ContextIdentity">The scope's session/target/root binding.</param>
/// <param name="DocumentPath">The canonical target-namespace document path.</param>
/// <param name="ContentHash">The exact document hash serialized into the prompt.</param>
public sealed record AgentPromptContextReceiptBlock(
    string HostIdentity,
    string ContextIdentity,
    string DocumentPath,
    string ContentHash);
