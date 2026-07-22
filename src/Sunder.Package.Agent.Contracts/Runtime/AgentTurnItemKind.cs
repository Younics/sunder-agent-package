namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Identifies the payload shape of a persisted turn item.
/// </summary>
public enum AgentTurnItemKind
{
    /// <summary>Natural-language or plain-text content in <see cref="AgentTurnItemRecord.TextContent"/>.</summary>
    Text = 0,

    /// <summary>A tool request carrying call, tool, and arguments fields.</summary>
    ToolCall = 1,

    /// <summary>A tool outcome carrying correlation, result, structured payload, provenance, and error fields.</summary>
    ToolResult = 2,

    /// <summary>An attachment reference whose structured payload contains <see cref="AgentAttachmentMetadata"/>.</summary>
    Attachment = 3,
}
