namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Represents one native tool call emitted by a chat provider.
/// </summary>
/// <remarks>The tool identifier and arguments are untrusted model output and must match the ready catalog before execution.</remarks>
/// <param name="CallId">The provider-generated identifier used to correlate the call, approval, and result.</param>
/// <param name="ToolId">The requested tool identifier.</param>
/// <param name="ArgumentsJson">The raw JSON object arguments, subject to shared payload limits and tool-specific validation.</param>
public sealed record AgentToolCallRequest(
    string CallId,
    string ToolId,
    string ArgumentsJson);
