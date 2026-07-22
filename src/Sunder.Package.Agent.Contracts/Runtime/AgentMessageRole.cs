namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Identifies the author or provider channel represented by a transcript turn.
/// </summary>
public enum AgentMessageRole
{
    /// <summary>Host-authorized system guidance. Untrusted recalled context must not be assigned this role.</summary>
    System = 0,

    /// <summary>User-authored input, including messages that carry attachment items.</summary>
    User = 1,

    /// <summary>Assistant/model output, including streamed text and tool requests.</summary>
    Assistant = 2,

    /// <summary>Tool-produced output correlated to an assistant tool call.</summary>
    Tool = 3,
}
