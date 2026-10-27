using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Runtime;

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal interface IAgentCompactTranscriptGateway
{
    Task<AgentCompactTranscriptPage> LoadCompactTranscriptAsync(
        AgentCompactTranscriptRequest request, CancellationToken cancellationToken = default);

    Task<AgentTranscriptActivityMembersPage> LoadActivityMembersAsync(
        AgentTranscriptActivityMembersRequest request, CancellationToken cancellationToken = default);
}
