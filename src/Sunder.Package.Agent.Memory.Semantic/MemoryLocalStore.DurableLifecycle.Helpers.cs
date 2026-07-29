using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Memory.Semantic;

public sealed partial class MemoryLocalStore
{
    private static InboxIdentity? ReadInboxIdentity(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string eventId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EventType, PayloadHash FROM SemanticLifecycleInbox WHERE EventId = $eventId;";
        command.Parameters.AddWithValue("$eventId", eventId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? new InboxIdentity(reader.GetString(0), reader.GetString(1)) : null;
    }

    private static void ValidateEnvelopeHash(AgentDurableLifecycleEventEnvelope lifecycleEvent)
    {
        var actualHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(lifecycleEvent.PayloadJson))).ToLowerInvariant();
        if (!string.Equals(actualHash, lifecycleEvent.PayloadHash, StringComparison.Ordinal))
        {
            throw new AgentDurableLifecycleIntegrityException(
                $"Semantic Memory received lifecycle event '{lifecycleEvent.EventId}' with a payload hash mismatch.");
        }
        if (lifecycleEvent.Payload.ContentErased != (lifecycleEvent.OriginalPayloadHash is not null))
        {
            throw new AgentDurableLifecycleIntegrityException(
                $"Semantic Memory received lifecycle event '{lifecycleEvent.EventId}' with inconsistent erasure metadata.");
        }
    }

    private static void ValidateDeletionManifest(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AgentDurableLifecycleEventEnvelope lifecycleEvent)
    {
        var payload = lifecycleEvent.Payload;
        if (payload.DeletionChunkReceipts.Count == 0)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(payload.DeletionBatchId)
            || payload.DeletionChunkIndex is not null
            || payload.DeletionChunkCount != payload.DeletionChunkReceipts.Count
            || payload.DeletionChunkReceipts.Select(receipt => receipt.EventId).Distinct(StringComparer.Ordinal).Count()
               != payload.DeletionChunkReceipts.Count)
        {
            throw new AgentDurableLifecycleIntegrityException(
                $"Semantic Memory received malformed deletion manifest '{lifecycleEvent.EventId}'.");
        }

        foreach (var receipt in payload.DeletionChunkReceipts)
        {
            var existing = ReadInboxIdentity(connection, transaction, receipt.EventId);
            if (existing is null
                || !string.Equals(existing.EventType, lifecycleEvent.Kind.ToString(), StringComparison.Ordinal)
                || !string.Equals(existing.PayloadHash, receipt.PayloadHash, StringComparison.Ordinal))
            {
                throw new AgentDurableLifecycleIntegrityException(
                    $"Semantic Memory cannot acknowledge deletion manifest '{lifecycleEvent.EventId}' before all exact chunks are received.");
            }
        }
    }

    private static string BuildContributionId(
        string eventId,
        Guid sessionId,
        Guid sourceTurnId,
        string category,
        string normalizedContent)
        => "contribution_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"semantic-memory-contribution-v1\n{eventId}\n{sessionId:N}\n{sourceTurnId:N}\n{category}\n{normalizedContent}"))).ToLowerInvariant();

    private static Guid BuildContributionEvidenceId(string contributionId)
        => new(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"semantic-memory-evidence-v1\n{contributionId}"))[..16]);

    private static string? ChooseRicher(IEnumerable<string?> values)
        => values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .OrderByDescending(value => value!.Length)
            .ThenBy(value => value, StringComparer.Ordinal)
            .FirstOrDefault();

    private sealed record InboxIdentity(string EventType, string PayloadHash);

    private sealed record StoredContribution(
        string ContributionId,
        string ProfileId,
        Guid SourceTurnId,
        string Category,
        string Content,
        string? EvidenceText,
        float Importance,
        float Confidence,
        bool IsPinned,
        DateTimeOffset CreatedAtUtc);
}

internal sealed record SemanticLifecycleProcessingResult(
    bool IsDuplicate,
    int CandidateCount,
    int PromotedCount,
    IReadOnlyList<SemanticMemoryIndexRequest> IndexRequests)
{
    public static SemanticLifecycleProcessingResult DuplicateReceipt { get; } = new(true, 0, 0, []);
}

internal sealed record SemanticMemoryIndexRequest(Guid MemoryId, string ProfileId);
