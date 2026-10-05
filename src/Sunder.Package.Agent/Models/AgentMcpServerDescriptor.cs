namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Provides a point-in-time description of a configured Model Context Protocol server.
/// </summary>
/// <remarks>
/// The descriptor contains catalog and diagnostic data only; it does not represent a live connection or
/// grant trust to tools exposed by the server.
/// </remarks>
/// <param name="ServerId">The opaque server identifier used by profile capability assignments.</param>
/// <param name="DisplayName">The user-facing server name.</param>
/// <param name="Description">An optional explanation of the server's purpose.</param>
/// <param name="StatusText">An optional best-effort connection or readiness message captured when the descriptor was created.</param>
public sealed record AgentMcpServerDescriptor(
    string ServerId,
    string DisplayName,
    string? Description = null,
    string? StatusText = null);
