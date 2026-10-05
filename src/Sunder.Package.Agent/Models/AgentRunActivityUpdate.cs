using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Models;

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
public sealed record AgentRunActivityUpdate(
    long RunRevision,
    AgentRunActivityKind Kind,
    string Text,
    DateTimeOffset CreatedAtUtc);
