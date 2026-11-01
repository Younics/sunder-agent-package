using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Presentation.Tests;

internal static class RenderedSubsessionFixture
{
    internal static PackageViewNavigationContext CreateSubsessionNavigation(
        Guid sessionId,
        Guid turnId,
        Guid itemId,
        string anchorKind,
        DateTimeOffset? createdAtUtc = null,
        string? callId = null)
        => new(
            SubagentConstants.SubsessionsViewId,
            new Dictionary<string, string?>
            {
                ["sessionId"] = sessionId.ToString("D"),
                ["turnId"] = turnId.ToString("D"),
                ["itemId"] = itemId.ToString("D"),
                ["callId"] = callId,
                ["anchorKind"] = anchorKind,
                ["createdAtUtc"] = (createdAtUtc ?? DateTimeOffset.UtcNow).ToString("O"),
            });

    internal static AgentTurnRecord CreateTextTurn(
        Guid sessionId,
        DateTimeOffset createdAtUtc,
        string content)
    {
        var turnId = Guid.NewGuid();
        return new AgentTurnRecord(
            turnId,
            sessionId,
            AgentMessageRole.Assistant,
            AgentTurnKind.Message,
            [new AgentTurnItemRecord(
                Guid.NewGuid(),
                turnId,
                0,
                AgentTurnItemKind.Text,
                content,
                null,
                null,
                null,
                null,
                null,
                null,
                false,
                false,
                null,
                null)],
            createdAtUtc,
            createdAtUtc);
    }
}
