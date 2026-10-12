using System.Text.Json.Serialization;

namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Identifies the payload shape of a persisted turn item.
/// </summary>
[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
public enum AgentTurnItemKind
{
    /// <summary>Natural-language or plain-text content in <see cref="AgentTurnItemRecord.TextContent"/>.</summary>
    [JsonStringEnumMemberName("text")] Text = 0,

    /// <summary>A tool request carrying call, tool, and arguments fields.</summary>
    [JsonStringEnumMemberName("tool-call")] ToolCall = 1,

    /// <summary>A tool outcome carrying correlation, result, structured payload, provenance, and error fields.</summary>
    [JsonStringEnumMemberName("tool-result")] ToolResult = 2,

    /// <summary>An attachment reference whose structured payload contains <see cref="AgentAttachmentMetadata"/>.</summary>
    [JsonStringEnumMemberName("attachment")] Attachment = 3,
}
