using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Runtime;

internal sealed partial class AgentAppRuntimeGateway : IAgentCompactTranscriptGateway
{
    public async Task<AgentCompactTranscriptPage> LoadCompactTranscriptAsync(
        AgentCompactTranscriptRequest request, CancellationToken cancellationToken = default)
    {
        var page = await InvokeAsync(_owner.GetCompactTranscriptPageAsync, request, cancellationToken).ConfigureAwait(false);
        CacheTurns(page.Entries.Select(entry => entry.Message).OfType<AgentTurnRecord>());
        return page;
    }

    public async Task<AgentTranscriptActivityMembersPage> LoadActivityMembersAsync(
        AgentTranscriptActivityMembersRequest request, CancellationToken cancellationToken = default)
        => await InvokeAsync(_owner.GetActivityMembersAsync, request, cancellationToken).ConfigureAwait(false);
}
