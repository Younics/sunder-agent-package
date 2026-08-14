using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Services;

public sealed record AgentToolCatalogEntry(
    AgentToolDescriptor Descriptor,
    AgentToolReadiness Readiness);
