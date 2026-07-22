namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Carries a selected tool invocation from the host to an individual tool or source.
/// </summary>
/// <remarks>
/// Both fields originate from provider output and remain untrusted. The host verifies that the tool
/// was advertised, but the implementation owns schema, value, path, command, and backend validation.
/// </remarks>
/// <param name="ToolId">The requested tool identifier from the ready catalog.</param>
/// <param name="ArgumentsJson">The raw JSON object arguments, subject to shared payload limits and tool-specific validation.</param>
public sealed record AgentToolRequest(
    string ToolId,
    string ArgumentsJson);
