namespace Sunder.Package.Agent.Contracts.Models;

public sealed record AgentMcpServerDescriptor(
    string ServerId,
    string DisplayName,
    string? Description = null,
    string? StatusText = null);
