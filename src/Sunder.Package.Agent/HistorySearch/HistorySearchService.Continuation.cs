using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;

namespace Sunder.Package.Agent.HistorySearch;

internal sealed partial class HistorySearchService
{
    internal static string CreateRequestFingerprint(HistorySearchRequest request)
    {
        var plan = HistoryFtsQueryPlan.Create(request.Query);
        return HashJson(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("QueryPlanVersion", HistorySearchVersions.QueryPlanVersion);
            writer.WriteBoolean("IsRecentRequest", plan.IsRecentRequest);
            writer.WriteString("FacetQuery", plan.FacetQuery);
            writer.WriteBoolean("FacetPrefixEligible", plan.FacetPrefixEligible);
            writer.WriteString("RequiredPhraseMatch", plan.RequiredPhraseMatch);
            writer.WriteStartArray("UnquotedTokens");
            foreach (var token in plan.UnquotedTokens)
            {
                writer.WriteStartObject();
                writer.WriteString("Value", token.Value);
                writer.WriteBoolean("IsPrefix", token.IsPrefix);
                writer.WriteBoolean("IsAutomaticPrefix", token.IsAutomaticPrefix);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartArray("RequiredPhrases");
            foreach (var phrase in plan.RequiredPhrases)
            {
                writer.WriteStartArray();
                foreach (var token in phrase.Tokens)
                {
                    writer.WriteStringValue(token);
                }
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
            writer.WriteString("WorkspaceId", request.WorkspaceId);
            writer.WriteString("SessionId", request.SessionId?.ToString("D"));
            writer.WriteString("ProfileId", request.ProfileId);
            writer.WriteNumber("Role", (int)request.Role);
            writer.WriteNumber("Activity", (int)request.Activity);
            writer.WriteString("FromUtc", request.FromUtc?.ToUniversalTime().ToString("O"));
            writer.WriteString("ToUtc", request.ToUtc?.ToUniversalTime().ToString("O"));
            writer.WriteBoolean("IncludeChildSessions", request.IncludeChildSessions);
            writer.WriteEndObject();
        });
    }

    private static string CreateRankIdentity(
        IReadOnlyList<RankedDocument> values)
        => HashJson(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("RankingVersion", HistorySearchVersions.RankingVersion);
            writer.WriteStartArray("Documents");
            foreach (var value in values)
            {
                writer.WriteStartObject();
                writer.WriteString("DocumentId", value.Document.DocumentId);
                writer.WriteNumber("SourceContentRevision", value.Document.SourceContentRevision);
                writer.WriteString("ProjectionHash", value.Document.ProjectionHash);
                writer.WriteNumber("Tier", value.Ranking.Tier);
                writer.WriteNumber("Score", value.Ranking.Score);
                writer.WriteString("TimestampUtc", value.Ranking.TimestampUtc.ToUniversalTime().ToString("O"));
                writer.WriteStartArray("LaneRanks");
                foreach (var lane in value.Ranking.LaneRanks)
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("Lane", (int)lane.Lane);
                    writer.WriteNumber("Rank", lane.Rank);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        });

    private static string EncodeContinuation(
        int offset,
        string requestFingerprint,
        string rankIdentity,
        HistoryProjectionPin pin,
        string? lastDocumentId)
    {
        var data = new ContinuationData(
            Version: 2,
            HistorySearchVersions.QueryPlanVersion,
            HistorySearchVersions.RankingVersion,
            offset,
            requestFingerprint,
            rankIdentity,
            pin.TextGenerationId,
            pin.EmbeddingGenerationId,
            pin.ConfigurationRevision,
            lastDocumentId);
        return Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(data));
    }

    private static int DecodeContinuation(
        string? continuation,
        string requestFingerprint,
        string rankIdentity,
        HistoryProjectionPin pin,
        IReadOnlyList<RankedDocument> values,
        out bool restarted)
    {
        restarted = false;
        if (string.IsNullOrWhiteSpace(continuation))
        {
            return 0;
        }
        try
        {
            var data = JsonSerializer.Deserialize<ContinuationData>(Convert.FromBase64String(continuation));
            if (data is null
                || data.Version != 2
                || data.QueryPlanVersion != HistorySearchVersions.QueryPlanVersion
                || data.RankingVersion != HistorySearchVersions.RankingVersion
                || data.Offset is < 0 or > HistorySearchLimits.CandidateLimit
                || data.Offset > values.Count
                || data.TextGenerationId != pin.TextGenerationId
                || data.EmbeddingGenerationId != pin.EmbeddingGenerationId
                || data.ConfigurationRevision != pin.ConfigurationRevision
                || !string.Equals(data.RequestFingerprint, requestFingerprint, StringComparison.Ordinal)
                || !string.Equals(data.RankIdentity, rankIdentity, StringComparison.Ordinal)
                || data.Offset > 0 && !string.Equals(
                    data.LastDocumentId,
                    values[data.Offset - 1].Document.DocumentId,
                    StringComparison.Ordinal))
            {
                restarted = true;
                return 0;
            }
            return data.Offset;
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            restarted = true;
            return 0;
        }
    }

    private static string HashJson(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            write(writer);
        }
        return Convert.ToHexString(SHA256.HashData(buffer.WrittenSpan)).ToLowerInvariant();
    }

    private sealed record ContinuationData(
        int Version,
        int QueryPlanVersion,
        int RankingVersion,
        int Offset,
        string RequestFingerprint,
        string RankIdentity,
        long TextGenerationId,
        long? EmbeddingGenerationId,
        long ConfigurationRevision,
        string? LastDocumentId);
}
