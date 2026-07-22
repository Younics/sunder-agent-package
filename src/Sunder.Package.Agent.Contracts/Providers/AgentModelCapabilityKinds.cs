namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Defines stable capability identifiers used to bind an agent profile to provider models.
/// </summary>
public static class AgentModelCapabilityKinds
{
    /// <summary>Identifies a model binding used for conversational generation.</summary>
    public const string Chat = "model.chat";

    /// <summary>Identifies a model binding used to generate vector embeddings.</summary>
    public const string Embedding = "model.embedding";
}
