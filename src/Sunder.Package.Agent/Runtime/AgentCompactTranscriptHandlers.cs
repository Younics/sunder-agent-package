using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Shared.PackageViews;

namespace Sunder.Package.Agent.Runtime;

internal sealed partial class AgentTranscriptPageHandler
{
    internal ValueTask<AgentCompactTranscriptPage> HandleAsync(
        AgentCompactTranscriptRequest request, CancellationToken cancellationToken)
        => ValueTask.FromResult(TranscriptCompactPayload.Fit(sessions.Store.ReadCompactTranscript(request, cancellationToken),
            request.Direction, AgentRuntimePayloadLimits.GetResponsePayloadBytes(_transportBudgets.Current)));

    internal ValueTask<AgentTranscriptActivityMembersPage> HandleAsync(
        AgentTranscriptActivityMembersRequest request, CancellationToken cancellationToken)
        => ValueTask.FromResult(TranscriptCompactPayload.Fit(sessions.Store.ReadActivityMembers(request, cancellationToken),
            AgentRuntimePayloadLimits.GetResponsePayloadBytes(_transportBudgets.Current)));
}
