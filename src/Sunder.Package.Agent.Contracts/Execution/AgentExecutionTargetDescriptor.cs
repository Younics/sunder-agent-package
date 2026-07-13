namespace Sunder.Package.Agent.Contracts.Models;

public sealed record AgentExecutionTargetDescriptor(
    string TargetKind,
    string TargetId,
    string DisplayName,
    string? Description,
    bool SupportsShell,
    bool SupportsFiles,
    bool SupportsSearch);
