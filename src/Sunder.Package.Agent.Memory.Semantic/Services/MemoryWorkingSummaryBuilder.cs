using System.Text;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Memory.Semantic.Services;

public sealed class MemoryWorkingSummaryBuilder(MemoryLocalStore store)
{
    private const int MaxWorkingSummaryLength = 1400;

    private readonly MemoryLocalStore _store = store;

    public string Build(AgentLifecycleEvent lifecycleEvent)
    {
        var recentTurns = lifecycleEvent.RecentLiveBufferTurns.Count > 0
            ? lifecycleEvent.RecentLiveBufferTurns
            : lifecycleEvent.Turns;
        var builder = new StringBuilder();
        builder.Append("Session focus: ").AppendLine(SemanticMemoryTextHelpers.Truncate(lifecycleEvent.Turn.UserMessage, 280));

        var latestAssistant = recentTurns
            .Where(turn => turn.Role == AgentMessageRole.Assistant && turn.Kind == AgentTurnKind.Message)
            .OrderByDescending(turn => turn.CreatedAtUtc)
            .FirstOrDefault();
        if (latestAssistant is not null)
        {
            builder.Append("Latest assistant state: ").AppendLine(SemanticMemoryTextHelpers.Truncate(SemanticMemoryTextHelpers.RenderTurnText(latestAssistant), 320));
        }

        var latestToolResult = recentTurns
            .Where(turn => turn.Kind == AgentTurnKind.ToolResult)
            .OrderByDescending(turn => turn.CreatedAtUtc)
            .FirstOrDefault();
        if (latestToolResult is not null && !string.IsNullOrWhiteSpace(SemanticMemoryTextHelpers.RenderToolResultText(latestToolResult)))
        {
            builder.Append("Latest tool outcome: ").AppendLine(SemanticMemoryTextHelpers.Truncate(SemanticMemoryTextHelpers.RenderToolResultText(latestToolResult), 260));
        }

        if (!string.IsNullOrWhiteSpace(lifecycleEvent.Checkpoint?.Summary))
        {
            builder.Append("Latest run status: ").AppendLine(SemanticMemoryTextHelpers.Truncate(lifecycleEvent.Checkpoint.Summary!, 220));
        }

        var memories = _store.ListPriorityMemories(lifecycleEvent.Session.SessionId, limit: 4);
        if (memories.Count > 0)
        {
            builder.AppendLine("Remembered context:");
            foreach (var memory in memories)
            {
                builder.Append("- [").Append(memory.Category).Append("] ").AppendLine(SemanticMemoryTextHelpers.Truncate(memory.Content, 180));
            }
        }

        return SemanticMemoryTextHelpers.Truncate(builder.ToString().Trim(), MaxWorkingSummaryLength);
    }
}
