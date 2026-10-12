using System.Text.Json.Serialization;

namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Classifies the semantic purpose of a persisted transcript turn.
/// </summary>
[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
public enum AgentTurnKind
{
    /// <summary>A conversational message whose items can include text and attachments.</summary>
    [JsonStringEnumMemberName("message")] Message = 0,

    /// <summary>A request by an assistant/provider to invoke a tool.</summary>
    [JsonStringEnumMemberName("tool-call")] ToolCall = 1,

    /// <summary>The persisted outcome correlated to an earlier tool call.</summary>
    [JsonStringEnumMemberName("tool-result")] ToolResult = 2,
}
