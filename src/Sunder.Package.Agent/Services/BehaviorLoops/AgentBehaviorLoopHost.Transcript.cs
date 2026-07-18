using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Models;

namespace Sunder.Package.Agent.Services.BehaviorLoops;

internal sealed partial class AgentBehaviorLoopHost
{
    public AgentTurnRecord UpsertAssistantTurn(AgentTurnRecord? assistantTurn, string content)
    {
        var turn = assistantTurn is null
            ? _sessionService.AppendTextTurn(
                _runLease,
                AgentMessageRole.Assistant,
                content)
            : string.Equals(RenderTextContent(assistantTurn), content, StringComparison.Ordinal)
                ? assistantTurn
                : _sessionService.UpdateTextTurn(_runLease, assistantTurn.TurnId, content);
        if (turn.IsStreaming)
        {
            _openAssistantTurns[turn.TurnId] = turn;
        }
        else
        {
            _openAssistantTurns.Remove(turn.TurnId);
        }
        return turn;
    }

    public AgentTurnRecord CompleteAssistantTurn(AgentTurnRecord assistantTurn)
    {
        var completedTurn = assistantTurn.IsStreaming
            ? _sessionService.CompleteTextTurn(_runLease, assistantTurn.TurnId)
            : assistantTurn;
        _openAssistantTurns.Remove(assistantTurn.TurnId);
        return completedTurn;
    }

    internal void CompleteOpenAssistantTurn()
    {
        foreach (var openTurn in _openAssistantTurns.Values.ToArray())
        {
            CompleteAssistantTurn(openTurn);
        }
    }

    internal void TryCompleteOpenAssistantTurn()
    {
        try
        {
            CompleteOpenAssistantTurn();
        }
        catch (AgentRunTranscriptWriteRejectedException)
        {
            // A stop or replacement owns finalization once the durable lease changes.
        }
    }

    private static string RenderTextContent(AgentTurnRecord turn)
        => string.Concat(turn.Items
            .Where(item => item.Kind == AgentTurnItemKind.Text)
            .OrderBy(item => item.SequenceNumber)
            .Select(item => item.TextContent ?? string.Empty));
}
