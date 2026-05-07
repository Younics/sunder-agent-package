namespace Sunder.Package.Agent.Contracts.Models;

public sealed record AgentExecutionScopeDescriptor(
    string DisplayName,
    IReadOnlyList<string> AllowedRoots,
    string? DefaultWorkingDirectory = null,
    string? PathStyleDescription = null);
