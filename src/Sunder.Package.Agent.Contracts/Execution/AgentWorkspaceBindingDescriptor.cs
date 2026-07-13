namespace Sunder.Package.Agent.Contracts.Models;

public sealed record AgentWorkspaceBindingDescriptor(
    string ExtensionPointId,
    string ContributionId,
    string Role,
    string DisplayName,
    string? Description);
