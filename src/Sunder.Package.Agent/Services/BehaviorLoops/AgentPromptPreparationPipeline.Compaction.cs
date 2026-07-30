using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Services.BehaviorLoops;

internal sealed partial class AgentPromptPreparationPipeline
{
    public async Task<bool> CompactForRequestPressureAsync(
        IAgentBehaviorLoopRuntime host,
        AgentBehaviorLoopContext context,
        AgentPromptPreparation preparation,
        int requestedReductionTokens,
        bool requireHistoricalAttachmentEviction,
        CancellationToken cancellationToken)
    {
        var previousProjection = preparation.Projection;
        var pressureOverhead = (int)Math.Min(
            int.MaxValue,
            Math.Max(0L, preparation.PromptOverheadTokens) + Math.Max(1L, requestedReductionTokens));
        var projection = await BuildPromptProjectionAsync(
            host,
            context,
            pressureOverhead,
            cancellationToken,
            requireHistoricalAttachmentEviction && previousProjection.ContextCheckpoint is null ? 1 : 0)
            .ConfigureAwait(false);
        preparation.Projection = projection;
        var instructionContext = await host.BuildInstructionContextAsync(cancellationToken);
        preparation.PromptRequest = preparation.PromptRequest with { Turns = projection.PromptTurns };
        preparation.SystemInstructions = await _promptComposer.ComposeAsync(
            preparation.PromptRequest,
            instructionContext.SystemInstructions,
            cancellationToken);
        preparation.SupplementaryContextBlocks = instructionContext.SupplementaryContextBlocks ?? [];
        preparation.PromptOverheadTokens = EstimatePromptOverheadTokens(
            preparation.SystemInstructions,
            preparation.SupplementaryContextBlocks,
            preparation.AvailableTools);
        return projection.OmittedHistoricalTurnCount > previousProjection.OmittedHistoricalTurnCount
               || projection.ContextCheckpoint?.ContextCheckpointId
               != previousProjection.ContextCheckpoint?.ContextCheckpointId
               || EstimateProjectionCharacters(projection.PromptTurns)
               < EstimateProjectionCharacters(previousProjection.PromptTurns);
    }

    private static long EstimateProjectionCharacters(IReadOnlyList<AgentTurnRecord> turns)
        => turns.SelectMany(turn => turn.Items).Sum(item =>
            (long)(item.TextContent?.Length ?? 0)
            + (item.ArgumentsJson?.Length ?? 0)
            + (item.ResultSummary?.Length ?? 0)
            + (item.StructuredPayloadJson?.Length ?? 0)
            + (item.SourcesJson?.Length ?? 0));
}
