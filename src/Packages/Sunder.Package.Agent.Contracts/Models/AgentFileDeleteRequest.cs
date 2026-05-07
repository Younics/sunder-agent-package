namespace Sunder.Package.Agent.Contracts.Models;

public sealed record AgentFileDeleteRequest(string Path, bool Recursive = false);
