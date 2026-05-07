using System.Text;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Memory.Semantic.Services;

namespace Sunder.Package.Agent.Memory.Semantic;

public sealed class MemorySemanticFeature(
    MemoryLocalStore store,
    SemanticMemoryRecallService recallService,
    SemanticMemoryPromotionService promotionService,
    MemoryWorkingSummaryBuilder workingSummaryBuilder) : IAgentProfileCapabilityConsumer, IAgentPromptContextContributor, IAgentLifecycleObserver, IAgentSessionDataCleaner
{
    private readonly MemoryLocalStore _store = store;
    private readonly SemanticMemoryRecallService _recallService = recallService;
    private readonly SemanticMemoryPromotionService _promotionService = promotionService;
    private readonly MemoryWorkingSummaryBuilder _workingSummaryBuilder = workingSummaryBuilder;

    public string FeatureId => "memory.semantic";

    public string DisplayName => "Semantic Memory";

    public string ConsumerId => FeatureId;

    public string ContributorId => FeatureId;

    public string ObserverId => FeatureId;

    public string CleanerId => FeatureId;

    public IReadOnlyList<AgentProfileCapabilityConsumerDescriptor> ListConsumedCapabilities()
        =>
        [
            new(
                AgentModelCapabilityKinds.Embedding,
                "Embeddings",
                "Use the selected embedding provider and model for semantic memory indexing and retrieval."),
        ];

    public ValueTask<AgentMemoryRecallResult?> RecallAsync(
        AgentMemoryRecallRequest request,
        CancellationToken cancellationToken = default)
        => _recallService.RecallAsync(request, cancellationToken);

    public async ValueTask<AgentPromptContextContribution?> ContributeContextAsync(
        AgentPromptContextRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!request.ContextPlan.ShouldContribute)
        {
            return null;
        }

        var recallResult = await RecallAsync(
            new AgentMemoryRecallRequest(
                request.Session,
                request.Run,
                request.Turn,
                request.Turns,
                request.RecentLiveBufferTurns,
                ToMemoryRecallPlan(request.ContextPlan)),
            cancellationToken);
        if (recallResult is null || recallResult.Entries.Count == 0)
        {
            return null;
        }

        return new AgentPromptContextContribution(
        [
            new AgentPromptContextBlock(
                "Recalled Session Context",
                BuildRecallContextBlock(recallResult),
                Priority: 100,
                SourceId: FeatureId),
        ]);
    }

    public async ValueTask<AgentLifecycleObserverResult?> HandleLifecycleEventAsync(
        AgentLifecycleEvent lifecycleEvent,
        CancellationToken cancellationToken = default)
    {
        await _promotionService.PromoteDurableMemoriesAsync(lifecycleEvent, cancellationToken);
        var workingSummary = _workingSummaryBuilder.Build(lifecycleEvent);

        return string.IsNullOrWhiteSpace(workingSummary)
            ? null
            : new AgentLifecycleObserverResult(workingSummary);
    }

    public void DeleteSessionData(Guid sessionId)
        => _store.DeleteSessionData(sessionId);

    private static AgentMemoryRecallPlan ToMemoryRecallPlan(AgentPromptContextPlan plan)
        => Enum.TryParse<AgentMemoryRecallIntent>(plan.Intent, ignoreCase: true, out var intent)
            ? new AgentMemoryRecallPlan(intent, plan.QueryText, plan.Reason, plan.PreferredCategories, plan.MaxEntryCount, plan.MaxChars)
            : AgentMemoryRecallPlan.None(plan.Reason);

    private static string BuildRecallContextBlock(AgentMemoryRecallResult recallResult)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Use this context when it is relevant. Prefer direct current-turn user instructions if there is a conflict.");
        foreach (var entry in recallResult.Entries.OrderByDescending(item => item.Score).ThenBy(item => item.Category, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append("- [").Append(entry.Category).Append(" | ").Append(entry.TrustState).Append("] ").AppendLine(entry.Content.Trim());
            if (!string.IsNullOrWhiteSpace(entry.EvidenceText))
            {
                builder.Append("  Evidence: ").AppendLine(entry.EvidenceText.Trim());
            }

            if (entry.SourceTurnId is Guid sourceTurnId)
            {
                builder.Append("  Source turn: `").Append(sourceTurnId).AppendLine("`");
            }

            if (entry.MatchReasons is { Count: > 0 })
            {
                builder.Append("  Why recalled: ")
                    .AppendLine(string.Join("; ", entry.MatchReasons.Select(reason => reason.Description.Trim())));
            }
        }

        return builder.ToString().Trim();
    }
}
