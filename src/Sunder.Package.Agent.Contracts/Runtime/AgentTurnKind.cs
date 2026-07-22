namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Classifies the semantic purpose of a persisted transcript turn.
/// </summary>
public enum AgentTurnKind
{
    /// <summary>A conversational message whose items can include text and attachments.</summary>
    Message = 0,

    /// <summary>A request by an assistant/provider to invoke a tool.</summary>
    ToolCall = 1,

    /// <summary>The persisted outcome correlated to an earlier tool call.</summary>
    ToolResult = 2,
}
