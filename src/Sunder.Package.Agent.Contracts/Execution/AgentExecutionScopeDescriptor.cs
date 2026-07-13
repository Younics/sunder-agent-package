namespace Sunder.Package.Agent.Contracts.Models;

public sealed record AgentExecutionScopeDescriptor(
    string DisplayName,
    IReadOnlyList<string> WorkspacePaths,
    string? DefaultWorkingDirectory = null,
    string? PathStyleDescription = null);
