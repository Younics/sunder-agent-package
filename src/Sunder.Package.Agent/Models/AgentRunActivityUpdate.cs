using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Models;

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
public sealed record AgentRunActivityUpdate(
    [property: Sunder.Sdk.Services.SunderValueConstraints(Minimum = "0")] long RunRevision,
    AgentRunActivityKind Kind,
    string Text,
    DateTimeOffset CreatedAtUtc,
    AgentRunRetryState? Retry = null,
    string? ReasoningText = null,
    [property: Sunder.Sdk.Services.SunderValueConstraints(Minimum = "0")] long Sequence = 0);
