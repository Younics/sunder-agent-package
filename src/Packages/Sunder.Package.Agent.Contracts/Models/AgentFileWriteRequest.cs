namespace Sunder.Package.Agent.Contracts.Models;

public sealed record AgentFileWriteRequest(
    string Path,
    string Content,
    bool Overwrite = true);
