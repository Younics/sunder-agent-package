namespace Sunder.Package.Agent.Contracts.Models;

public sealed record AgentPermissionRequest(
    string ActionId,
    string BoundaryId,
    string Summary,
    string? ToolId = null,
    string? Command = null,
    string? Path = null,
    string? WorkspaceId = null,
    string? BindingId = null,
    string? ResourceDisplayName = null,
    string? ResourceReference = null,
    bool IsMutation = false);
