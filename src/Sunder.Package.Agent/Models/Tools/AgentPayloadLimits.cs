namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Defines shared parser-security limits for untrusted tool argument JSON.
/// </summary>
/// <remarks>
/// These limits constrain parser complexity, not Agent RPC transport payloads. Runtime's negotiated
/// RPC resource profile remains authoritative for transport size.
/// </remarks>
public static class AgentPayloadLimits
{
    /// <summary>The maximum JSON nesting depth accepted for tool arguments and provider tool-call events.</summary>
    public const int MaxToolArgumentJsonDepth = 64;

    /// <summary>The maximum total number of object properties accepted across a tool argument document.</summary>
    public const int MaxToolArgumentProperties = 4096;

}
