using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Services;

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
public sealed record AgentToolCatalogEntry(
    AgentToolDescriptor Descriptor,
    AgentToolReadiness Readiness);
