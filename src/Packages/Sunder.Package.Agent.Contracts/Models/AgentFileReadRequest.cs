namespace Sunder.Package.Agent.Contracts.Models;

public sealed record AgentFileReadRequest(
    string Path,
    int? Offset = null,
    int? Limit = null);
