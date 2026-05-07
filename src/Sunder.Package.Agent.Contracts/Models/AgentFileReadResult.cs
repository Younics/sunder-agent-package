namespace Sunder.Package.Agent.Contracts.Models;

public sealed record AgentFileReadResult(
    string Path,
    string Content,
    bool IsDirectory = false,
    bool WasTruncated = false);
