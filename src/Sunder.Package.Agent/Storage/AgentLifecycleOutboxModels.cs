using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Storage;

internal sealed record AgentLifecycleOutboxRecord(
    long Sequence,
    string EventId,
    string SourceKey,
    AgentLifecycleEventKind Kind,
    string OrderingKey,
    string? WorkspaceId,
    Guid? SessionId,
    string PayloadJson,
    string PayloadHash,
    DateTimeOffset CreatedAtUtc,
    bool PayloadErased,
    string? OriginalPayloadHash,
    DateTimeOffset? PayloadErasedAtUtc,
    long? PayloadErasureGeneration)
{
    public AgentDurableLifecycleEventEnvelope ToEnvelope()
    {
        var actualHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(PayloadJson))).ToLowerInvariant();
        if (!string.Equals(actualHash, PayloadHash, StringComparison.Ordinal))
        {
            throw new AgentDurableLifecycleIntegrityException(
                $"Lifecycle event '{EventId}' has an immutable payload hash mismatch.");
        }

        var payload = JsonSerializer.Deserialize<AgentDurableLifecycleEventPayload>(
            PayloadJson,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new AgentDurableLifecycleIntegrityException(
                $"Lifecycle event '{EventId}' has an unreadable payload.");
        if (PayloadErased != payload.ContentErased
            || PayloadErased != (OriginalPayloadHash is not null)
            || PayloadErased != (PayloadErasedAtUtc is not null)
            || PayloadErased != (PayloadErasureGeneration is not null))
        {
            throw new AgentDurableLifecycleIntegrityException(
                $"Lifecycle event '{EventId}' has inconsistent payload-erasure metadata.");
        }
        return new AgentDurableLifecycleEventEnvelope
        {
            EventId = EventId,
            Sequence = Sequence,
            Kind = Kind,
            SourceKey = SourceKey,
            OrderingKey = OrderingKey,
            PayloadHash = PayloadHash,
            OriginalPayloadHash = OriginalPayloadHash,
            PayloadJson = PayloadJson,
            OccurredAtUtc = CreatedAtUtc,
            Payload = payload,
        };
    }
}

internal sealed record AgentLifecycleSubscription(
    string SubscriptionId,
    string PackageId,
    string ObserverId,
    string ContractKind,
    string DisplayName);

internal sealed record AgentLifecycleDeliveryClaim(
    string SubscriptionId,
    string LeaseToken,
    long DeliveryVersion,
    AgentLifecycleOutboxRecord Event);

internal sealed record AgentLifecycleDeliveryFailureResult(
    bool Updated,
    bool Poisoned,
    int AttemptCount,
    DateTimeOffset NextAttemptAtUtc,
    string FailureCode,
    string ExceptionType);

internal sealed record AgentLifecycleDeliveryState(
    string Status,
    int AttemptCount,
    DateTimeOffset NextAttemptAtUtc,
    string? LeaseToken,
    DateTimeOffset? LeaseExpiresAtUtc,
    string? LastError,
    long DeliveryVersion,
    long MembershipGeneration);

internal sealed record AgentSessionDataCleanerIdentity(
    string PackageId,
    string CleanerId);

internal sealed record AgentSessionCleanupJobClaim(
    long JobId,
    string PackageId,
    string CleanerId,
    Guid SessionId,
    string LeaseToken);

internal sealed record AgentSessionCleanupJobState(
    long JobId,
    string PackageId,
    string CleanerId,
    Guid SessionId,
    string Status,
    int AttemptCount,
    DateTimeOffset NextAttemptAtUtc,
    string? LastError);
