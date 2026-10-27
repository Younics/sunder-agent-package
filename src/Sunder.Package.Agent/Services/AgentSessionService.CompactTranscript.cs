using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Runtime;
using Sunder.Package.Agent.Shared.Presentation;

namespace Sunder.Package.Agent.Services;

public sealed partial class AgentSessionService : IAgentCompactTranscriptGateway
{
    Task<AgentCompactTranscriptPage> IAgentCompactTranscriptGateway.LoadCompactTranscriptAsync(
        AgentCompactTranscriptRequest request, CancellationToken cancellationToken)
        => ReadCompactAsync(() => _store.ReadCompactTranscript(request, cancellationToken), cancellationToken);

    Task<AgentTranscriptActivityMembersPage> IAgentCompactTranscriptGateway.LoadActivityMembersAsync(
        AgentTranscriptActivityMembersRequest request, CancellationToken cancellationToken)
        => ReadCompactAsync(() => _store.ReadActivityMembers(request, cancellationToken), cancellationToken);

    private static Task<T> ReadCompactAsync<T>(Func<T> read, CancellationToken token)
        => Task.Run(() =>
        {
            try { return read(); }
            catch (InvalidOperationException error) { throw new PresentationOperationException(error.Message, error); }
        }, token);
}
