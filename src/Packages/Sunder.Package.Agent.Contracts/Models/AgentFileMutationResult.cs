namespace Sunder.Package.Agent.Contracts.Models;

public sealed record AgentFileMutationResult(
    string Path,
    string Summary,
    bool IsError = false,
    string? ErrorCode = null);
