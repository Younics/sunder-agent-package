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

}
