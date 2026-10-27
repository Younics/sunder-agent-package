using IRuntimeCatalogService = Sunder.Generated.Packages.Younics_Ai_Agent_Services.CYounics_Ai_Agent_Runtime_Catalog.IRuntimeCatalogService;
using Sunder.Sdk.Rpc;
using Sunder.Package.Agent.Shared.PackageViews;

namespace Sunder.Package.Agent.Subagents.Runtime;

internal static class SubsessionTranscriptReader
{
    internal static async ValueTask<SubsessionTranscriptPage> RecentAsync(IRuntimeCatalogService runtime, Guid sessionId, int limit, CancellationToken token)
        => Page(await runtime.ListRecentTranscriptHeadersAsync(new() { SessionId = sessionId, Limit = Math.Clamp(limit, 1, 500) + 1 }, token).ConfigureAwait(false),
            limit, SubsessionPageDirection.Recent);

    internal static async ValueTask<SubsessionTranscriptPage> BeforeAsync(IRuntimeCatalogService runtime, Guid sessionId, SunderRpcOffsetDateTime created, Guid turnId, int limit, CancellationToken token)
        => Page(await runtime.ListTranscriptHeadersBeforeAsync(new() { SessionId = sessionId, CreatedAtUtc = created, TurnId = turnId, Limit = Math.Clamp(limit, 1, 500) + 1 }, token).ConfigureAwait(false),
            limit, SubsessionPageDirection.Before);

    internal static async ValueTask<SubsessionTranscriptPage> AfterAsync(IRuntimeCatalogService runtime, Guid sessionId, SunderRpcOffsetDateTime created, Guid turnId, int limit, CancellationToken token)
        => Page(await runtime.ListTranscriptHeadersAfterAsync(new() { SessionId = sessionId, CreatedAtUtc = created, TurnId = turnId, Limit = Math.Clamp(limit, 1, 500) + 1 }, token).ConfigureAwait(false),
            limit, SubsessionPageDirection.After);

    internal static async ValueTask<SubsessionAroundTurnPage> AroundAsync(IRuntimeCatalogService runtime, Guid sessionId, Guid turnId,
        DateTimeOffset createdAtUtc, Guid itemId, int beforeLimit, int afterLimit, CancellationToken token)
    {
        var session = await runtime.GetSessionAsync(sessionId, token).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The selected sub-session is no longer available.");
        if (session.ParentSessionId is null) throw new InvalidOperationException("Subsession transcript navigation requires a child session.");
        var predecessor = (await runtime.ListTranscriptHeadersBeforeAsync(new()
        { SessionId = sessionId, CreatedAtUtc = Timestamp(createdAtUtc), TurnId = turnId, Limit = 1 }, token).ConfigureAwait(false)).SingleOrDefault();
        var anchor = (await runtime.ListTranscriptHeadersAfterAsync(new()
        {
            SessionId = sessionId,
            CreatedAtUtc = predecessor?.CreatedAtUtc ?? Timestamp(createdAtUtc == DateTimeOffset.MinValue ? createdAtUtc : createdAtUtc.AddTicks(-1)),
            TurnId = predecessor?.TurnId ?? Guid.Empty, Limit = 1,
        }, token).ConfigureAwait(false)).SingleOrDefault();
        if (anchor is null) throw new InvalidOperationException("The transcript anchor is no longer available.");
        if (anchor.TurnId != turnId)
        {
            // Header pages use canonical invocation positions. Prove that the
            // exact requested receipt/item still resolves in this session, then
            // present that logical slot under the requested navigation identity.
            // The proof and projection are header-only; no detail payload is read.
            _ = await runtime.ReadCompactTranscriptAsync(new()
            {
                SessionId = sessionId, Direction = TranscriptCompactDirections.Around, Limit = 1,
                AnchorTurnId = turnId, AnchorItemId = itemId,
            }, token).ConfigureAwait(false);
            if (anchor.Items.Count != 1 || anchor.Items[0].ToolExecutionId is null
                || anchor.Kind is not (AgentTurnKind.ToolCall or AgentTurnKind.ToolResult))
                throw new InvalidOperationException("The transcript anchor is no longer available.");
            anchor = anchor with
            {
                TurnId = turnId, Kind = AgentTurnKind.ToolResult, Role = AgentMessageRole.Tool,
                CreatedAtUtc = Timestamp(createdAtUtc),
                Items = [anchor.Items[0] with { TurnId = turnId, ItemId = itemId, Kind = AgentTurnItemKind.ToolResult }],
            };
        }
        if (anchor.SessionId != sessionId) throw new InvalidOperationException("The transcript anchor does not belong to the selected sub-session.");
        var beforeCount = Math.Clamp(beforeLimit, 0, 30);
        var afterCount = Math.Clamp(afterLimit, 0, 30);
        var before = await runtime.ListTranscriptHeadersBeforeAsync(new()
        { SessionId = sessionId, CreatedAtUtc = anchor.CreatedAtUtc, TurnId = turnId, Limit = beforeCount + 1 }, token).ConfigureAwait(false);
        var after = await runtime.ListTranscriptHeadersAfterAsync(new()
        { SessionId = sessionId, CreatedAtUtc = anchor.CreatedAtUtc, TurnId = turnId, Limit = afterCount + 1 }, token).ConfigureAwait(false);
        var hasOlder = before.Count > beforeCount;
        var hasNewer = after.Count > afterCount;
        if (hasOlder) before = before.Skip(before.Count - beforeCount).ToArray();
        if (hasNewer) after = after.Take(afterCount).ToArray();
        return SubsessionAroundTurnPayload.Fit(new([.. before, anchor, .. after], hasOlder, hasNewer, turnId), itemId);
    }

    private static SubsessionTranscriptPage Page(IReadOnlyList<AgentTurnRecord> turns, int limit, SubsessionPageDirection direction)
    {
        var bounded = Math.Clamp(limit, 1, 500);
        var more = turns.Count > bounded;
        var selected = !more ? turns : direction is SubsessionPageDirection.Recent or SubsessionPageDirection.Before
            ? turns.Skip(turns.Count - bounded).ToArray() : turns.Take(bounded).ToArray();
        return SubsessionAroundTurnPayload.FitPage(new(selected, more), direction);
    }

    internal static SunderRpcOffsetDateTime Timestamp(DateTimeOffset value)
        => new(value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", System.Globalization.CultureInfo.InvariantCulture));
}
