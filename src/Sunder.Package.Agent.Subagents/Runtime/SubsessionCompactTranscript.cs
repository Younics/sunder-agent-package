using Sunder.Package.Agent.Shared.PackageViews;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;

namespace Sunder.Package.Agent.Subagents.Runtime;

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal interface ISubsessionCompactTranscriptReader
{
    Task<AgentCompactTranscriptPage> LoadCompactTranscriptAsync(AgentCompactTranscriptRequest request, CancellationToken cancellationToken = default);
    Task<AgentTranscriptActivityMembersPage> LoadActivityMembersAsync(AgentTranscriptActivityMembersRequest request, CancellationToken cancellationToken = default);
}

internal sealed partial class SubagentManagementService
{
    [SunderMethod(Idempotency = SunderServiceIdempotency.Idempotent, Errors = ["subagents.owner.operation-failed"])]
    public ValueTask<SunderContent> CompactTranscriptAsync(AgentCompactTranscriptRequest request, CancellationToken token = default)
        => ProjectAsync(() => WithRuntimeAsync(async (runtime, cancellation) => new SubagentProjection(
            CompactTranscript: TranscriptCompactPayload.Fit(await runtime.ReadCompactTranscriptAsync(request, cancellation).ConfigureAwait(false),
                request.Direction, SubsessionAroundTurnPayload.MaximumResponseBytes)), token));

    [SunderMethod(Idempotency = SunderServiceIdempotency.Idempotent, Errors = ["subagents.owner.operation-failed"])]
    public ValueTask<SunderContent> ActivityMembersAsync(AgentTranscriptActivityMembersRequest request, CancellationToken token = default)
        => ProjectAsync(() => WithRuntimeAsync(async (runtime, cancellation) => new SubagentProjection(
            ActivityMembers: TranscriptCompactPayload.Fit(await runtime.ReadActivityMembersAsync(request, cancellation).ConfigureAwait(false),
                SubsessionAroundTurnPayload.MaximumResponseBytes)), token));
}

#if SUNDER_SUBAGENTS_APP
internal sealed partial class SubagentAppRuntimeGateway : ISubsessionCompactTranscriptReader
{
    public async Task<AgentCompactTranscriptPage> LoadCompactTranscriptAsync(AgentCompactTranscriptRequest request, CancellationToken cancellationToken = default)
        => (await InvokeAsync(_client.CompactTranscriptAsync(request, cancellationToken), cancellationToken).ConfigureAwait(false)).CompactTranscript
            ?? throw new InvalidOperationException("Runtime did not return the Compact transcript page.");

    public async Task<AgentTranscriptActivityMembersPage> LoadActivityMembersAsync(AgentTranscriptActivityMembersRequest request, CancellationToken cancellationToken = default)
        => (await InvokeAsync(_client.ActivityMembersAsync(request, cancellationToken), cancellationToken).ConfigureAwait(false)).ActivityMembers
            ?? throw new InvalidOperationException("Runtime did not return the activity headers.");
}
#endif
