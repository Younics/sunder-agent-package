using System.Globalization;
using System.Runtime.CompilerServices;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Memory.Semantic.Services;
using Sunder.Sdk.Rpc;
using Wire = Sunder.Package.Agent.Memory.Semantic.Protocol.Generated;

namespace Sunder.Package.Agent.Memory.Semantic.Runtime;

public static class MemoryInspectorRpc
{
    private const string OperationFailedCode = "memory.semantic.operation-failed";

    public static ISunderRpcServiceHandler CreateHandler(IMemoryInspectorRpcService service)
        => new Wire.SunderAgentMemorySemanticInspectorRpcHandler(new ProviderAdapter(service));

    public static MemoryInspectorRpcClient CreateClient(
        ISunderRpcCallScope scope,
        SunderRpcEndpointReference endpoint)
        => new(new Wire.MemoryInspectorClient(scope, endpoint));

    private sealed class ProviderAdapter(IMemoryInspectorRpcService service) : Wire.IMemoryInspectorProvider
    {
        public async ValueTask<Wire.MemoryProjection> QueryAsync(
            SunderRpcInvocationContext context,
            Wire.MemoryQuery request,
            CancellationToken cancellationToken = default)
        {
            try
            {
                return MemoryInspectorRpcMapper.ToWire(await service.QueryAsync(
                    context,
                    MemoryInspectorRpcMapper.FromWire(request),
                    cancellationToken).ConfigureAwait(false));
            }
            catch (Exception exception) when (ShouldMap(exception))
            {
                throw Map((MemoryInspectorOperationException)exception);
            }
        }

        public async ValueTask<Wire.MemoryProjection> CommandAsync(
            SunderRpcInvocationContext context,
            Wire.MemoryCommand request,
            CancellationToken cancellationToken = default)
        {
            try
            {
                return MemoryInspectorRpcMapper.ToWire(await service.CommandAsync(
                    context,
                    MemoryInspectorRpcMapper.FromWire(request),
                    cancellationToken).ConfigureAwait(false));
            }
            catch (Exception exception) when (ShouldMap(exception))
            {
                throw Map((MemoryInspectorOperationException)exception);
            }
        }

        public async IAsyncEnumerable<Wire.MemoryChanged> ChangesAsync(
            SunderRpcInvocationContext context,
            Wire.MemoryChangeSubscription request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await using var enumerator = service.ChangesAsync(
                context,
                new MemoryChangeSubscription(),
                cancellationToken).GetAsyncEnumerator(cancellationToken);
            while (await MoveNextAsync(enumerator, cancellationToken).ConfigureAwait(false))
            {
                yield return MemoryInspectorRpcMapper.ToWire(enumerator.Current);
            }
        }

        private static async ValueTask<bool> MoveNextAsync<T>(
            IAsyncEnumerator<T> enumerator,
            CancellationToken cancellationToken)
        {
            try
            {
                return await enumerator.MoveNextAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (ShouldMap(exception))
            {
                throw Map((MemoryInspectorOperationException)exception);
            }
        }
    }

    internal static bool ShouldMap(Exception exception)
        => exception is MemoryInspectorOperationException;

    private static SunderRpcException Map(MemoryInspectorOperationException exception)
    {
        var message = string.IsNullOrWhiteSpace(exception.Message)
            ? "The requested semantic memory operation failed."
            : exception.Message;
        if (message.Length > 2_048)
        {
            message = message[..2_048];
        }

        return new SunderRpcException(new SunderRpcError(
            SunderRpcErrorKind.Domain,
            OperationFailedCode,
            message));
    }
}

public sealed class MemoryInspectorRpcClient(Wire.MemoryInspectorClient client)
{
    public async ValueTask<MemoryProjection> QueryAsync(
        MemoryQuery request,
        CancellationToken cancellationToken = default)
        => MemoryInspectorRpcMapper.FromWire(
            await client.QueryAsync(MemoryInspectorRpcMapper.ToWire(request), cancellationToken).ConfigureAwait(false));

    public async ValueTask<MemoryProjection> CommandAsync(
        MemoryCommand request,
        CancellationToken cancellationToken = default)
        => MemoryInspectorRpcMapper.FromWire(
            await client.CommandAsync(MemoryInspectorRpcMapper.ToWire(request), cancellationToken).ConfigureAwait(false));

    public async IAsyncEnumerable<MemoryChanged> ChangesAsync(
        MemoryChangeSubscription request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var change in client.ChangesAsync(
                           new Wire.MemoryChangeSubscription(),
                           cancellationToken).ConfigureAwait(false))
        {
            yield return MemoryInspectorRpcMapper.FromWire(change);
        }
    }
}

internal static class MemoryInspectorRpcMapper
{
    public static Wire.MemoryQuery ToWire(MemoryQuery value)
        => new()
        {
            Kind = (long)value.Kind,
            SessionId = value.SessionId,
            MemoryId = value.MemoryId,
            SearchText = value.SearchText,
            IncludeInactive = value.IncludeInactive,
            ProfileId = value.ProfileId,
            Memory = value.Memory is null ? null : ToWire(value.Memory),
            SemanticContext = value.SemanticContext is null ? null : ToWire(value.SemanticContext),
        };

    public static MemoryQuery FromWire(Wire.MemoryQuery value)
        => new(
            EnumValue<MemoryQueryKind>(value.Kind, nameof(value.Kind)),
            value.SessionId,
            value.MemoryId,
            value.SearchText,
            value.IncludeInactive,
            value.ProfileId,
            value.Memory is null ? null : FromWire(value.Memory),
            value.SemanticContext is null ? null : FromWire(value.SemanticContext));

    public static Wire.MemoryCommand ToWire(MemoryCommand value)
        => new()
        {
            Kind = (long)value.Kind,
            MemoryId = value.MemoryId,
            Category = value.Category,
            Content = value.Content,
            Note = value.Note,
            IsPinned = value.IsPinned,
            ProfileId = value.ProfileId,
        };

    public static MemoryCommand FromWire(Wire.MemoryCommand value)
        => new(
            EnumValue<MemoryCommandKind>(value.Kind, nameof(value.Kind)),
            value.MemoryId,
            value.Category,
            value.Content,
            value.Note,
            value.IsPinned,
            value.ProfileId);

    public static Wire.MemoryProjection ToWire(MemoryProjection value)
        => new()
        {
            Sessions = value.Sessions?.Select(ToWire).ToArray(),
            Checkpoint = value.Checkpoint is null ? null : ToWire(value.Checkpoint),
            WorkingSummary = value.WorkingSummary is null ? null : ToWire(value.WorkingSummary),
            Memories = value.Memories?.Select(ToWire).ToArray(),
            Evidence = value.Evidence?.Select(ToWire).ToArray(),
            Memory = value.Memory is null ? null : ToWire(value.Memory),
            SupersedingMemory = value.SupersedingMemory is null ? null : ToWire(value.SupersedingMemory),
            SupersededMemories = value.SupersededMemories?.Select(ToWire).ToArray(),
            CorrectionLineage = value.CorrectionLineage?.Select(ToWire).ToArray(),
            Correction = value.Correction is null ? null : ToWire(value.Correction),
            IndexStatus = value.IndexStatus is null ? null : ToWire(value.IndexStatus),
            SemanticState = value.SemanticState is null ? null : ToWire(value.SemanticState),
            ReindexResult = value.ReindexResult is null ? null : ToWire(value.ReindexResult),
            WorkerStatus = value.WorkerStatus is null ? null : ToWire(value.WorkerStatus),
            Metrics = value.Metrics is null ? null : ToWire(value.Metrics),
        };

    public static MemoryProjection FromWire(Wire.MemoryProjection value)
        => new(
            value.Sessions?.Select(FromWire).ToArray(),
            value.Checkpoint is null ? null : FromWire(value.Checkpoint),
            value.WorkingSummary is null ? null : FromWire(value.WorkingSummary),
            value.Memories?.Select(FromWire).ToArray(),
            value.Evidence?.Select(FromWire).ToArray(),
            value.Memory is null ? null : FromWire(value.Memory),
            value.SupersedingMemory is null ? null : FromWire(value.SupersedingMemory),
            value.SupersededMemories?.Select(FromWire).ToArray(),
            value.CorrectionLineage?.Select(FromWire).ToArray(),
            value.Correction is null ? null : FromWire(value.Correction),
            value.IndexStatus is null ? null : FromWire(value.IndexStatus),
            value.SemanticState is null ? null : FromWire(value.SemanticState),
            value.ReindexResult is null ? null : FromWire(value.ReindexResult),
            value.WorkerStatus is null ? null : FromWire(value.WorkerStatus),
            value.Metrics is null ? null : FromWire(value.Metrics));

    public static Wire.MemoryChanged ToWire(MemoryChanged value)
        => new()
        {
            Kind = (long)value.Kind,
            SessionId = value.SessionId,
        };

    public static MemoryChanged FromWire(Wire.MemoryChanged value)
        => new(EnumValue<MemoryChangeKind>(value.Kind, nameof(value.Kind)), value.SessionId);

    private static Wire.StoredMemoryRecord ToWire(StoredMemoryRecord value)
        => new()
        {
            MemoryId = value.MemoryId,
            SessionId = value.SessionId,
            Category = value.Category,
            Content = value.Content,
            EvidenceText = value.EvidenceText,
            SourceTurnId = value.SourceTurnId,
            Importance = value.Importance,
            Confidence = value.Confidence,
            IsPinned = value.IsPinned,
            State = value.State,
            SupersededByMemoryId = value.SupersededByMemoryId,
            CreatedAtUtc = Format(value.CreatedAtUtc),
            UpdatedAtUtc = Format(value.UpdatedAtUtc),
            LastAccessedAtUtc = Format(value.LastAccessedAtUtc),
            AccessCount = value.AccessCount,
            Provenance = (long)value.Provenance,
            MemoryRevision = value.MemoryRevision,
            IsManual = value.IsManual,
        };

    private static StoredMemoryRecord FromWire(Wire.StoredMemoryRecord value)
        => new(
            value.MemoryId,
            value.SessionId,
            value.Category,
            value.Content,
            value.EvidenceText,
            value.SourceTurnId,
            checked((float)value.Importance),
            checked((float)value.Confidence),
            value.IsPinned,
            value.State,
            value.SupersededByMemoryId,
            Parse(value.CreatedAtUtc),
            Parse(value.UpdatedAtUtc),
            ParseNullable(value.LastAccessedAtUtc),
            checked((int)value.AccessCount),
            EnumValue<AgentMemoryProvenance>(value.Provenance, nameof(value.Provenance)))
        {
            MemoryRevision = value.MemoryRevision,
            IsManual = value.IsManual,
        };

    private static Wire.StoredMemoryEvidenceRecord ToWire(StoredMemoryEvidenceRecord value)
        => new()
        {
            EvidenceId = value.EvidenceId,
            MemoryId = value.MemoryId,
            SessionId = value.SessionId,
            SourceTurnId = value.SourceTurnId,
            EvidenceText = value.EvidenceText,
            CreatedAtUtc = Format(value.CreatedAtUtc),
            ContributionId = value.ContributionId,
        };

    private static StoredMemoryEvidenceRecord FromWire(Wire.StoredMemoryEvidenceRecord value)
        => new(
            value.EvidenceId,
            value.MemoryId,
            value.SessionId,
            value.SourceTurnId,
            value.EvidenceText,
            Parse(value.CreatedAtUtc))
        {
            ContributionId = value.ContributionId,
        };

    private static Wire.MemoryCorrectionResult ToWire(MemoryCorrectionResult value)
        => new()
        {
            CorrectedMemory = ToWire(value.CorrectedMemory),
            UpdatedSourceMemory = ToWire(value.UpdatedSourceMemory),
            CreatedNewMemory = value.CreatedNewMemory,
        };

    private static MemoryCorrectionResult FromWire(Wire.MemoryCorrectionResult value)
        => new(
            FromWire(value.CorrectedMemory),
            FromWire(value.UpdatedSourceMemory),
            value.CreatedNewMemory);

    private static Wire.SemanticEmbeddingContext ToWire(SemanticEmbeddingContext value)
        => new()
        {
            StatusLabel = value.StatusLabel,
            StatusText = value.StatusText,
            IsReady = value.IsReady,
            ProviderId = value.ProviderId,
            ModelId = value.ModelId,
            ProviderDisplayName = value.ProviderDisplayName,
            ConfigurationFingerprint = value.ConfigurationFingerprint,
        };

    private static SemanticEmbeddingContext FromWire(Wire.SemanticEmbeddingContext value)
        => new(
            value.StatusLabel,
            value.StatusText,
            value.IsReady,
            value.ProviderId,
            value.ModelId,
            value.ProviderDisplayName,
            value.ConfigurationFingerprint);

    private static Wire.AgentSessionRecord ToWire(AgentSessionRecord value)
        => new()
        {
            SessionId = value.SessionId,
            Title = value.Title,
            State = (long)value.State,
            CreatedAtUtc = Format(value.CreatedAtUtc),
            UpdatedAtUtc = Format(value.UpdatedAtUtc),
            ParentSessionId = value.ParentSessionId,
            RootSessionId = value.RootSessionId,
            ParentRunId = value.ParentRunId,
            ParentRunRevision = value.ParentRunRevision,
            ParentToolCallId = value.ParentToolCallId,
            TaskId = value.TaskId,
            ProfileId = value.ProfileId,
            BehaviorLoopId = value.BehaviorLoopId,
            AgentKind = value.AgentKind,
            WorkspaceId = value.WorkspaceId,
        };

    private static AgentSessionRecord FromWire(Wire.AgentSessionRecord value)
        => new(
            value.SessionId,
            value.Title,
            EnumValue<AgentSessionState>(value.State, nameof(value.State)),
            Parse(value.CreatedAtUtc),
            Parse(value.UpdatedAtUtc),
            value.ParentSessionId,
            value.RootSessionId,
            value.ParentRunId,
            value.ParentRunRevision,
            value.ParentToolCallId,
            value.TaskId,
            value.ProfileId,
            value.BehaviorLoopId,
            value.AgentKind,
            value.WorkspaceId);

    private static Wire.AgentSessionContextCheckpointRecord ToWire(AgentSessionContextCheckpointRecord value)
        => new()
        {
            ContextCheckpointId = value.ContextCheckpointId,
            SessionId = value.SessionId,
            FirstOmittedTurnId = value.FirstOmittedTurnId,
            LastOmittedTurnId = value.LastOmittedTurnId,
            OmittedTurnCount = value.OmittedTurnCount,
            SummaryText = value.SummaryText,
            DetailsJson = value.DetailsJson,
            CreatedAtUtc = Format(value.CreatedAtUtc),
        };

    private static AgentSessionContextCheckpointRecord FromWire(Wire.AgentSessionContextCheckpointRecord value)
        => new(
            value.ContextCheckpointId,
            value.SessionId,
            value.FirstOmittedTurnId,
            value.LastOmittedTurnId,
            checked((int)value.OmittedTurnCount),
            value.SummaryText,
            value.DetailsJson,
            Parse(value.CreatedAtUtc));

    private static Wire.AgentWorkingSummaryRecord ToWire(AgentWorkingSummaryRecord value)
        => new()
        {
            SessionId = value.SessionId,
            SummaryText = value.SummaryText,
            UpdatedAtUtc = Format(value.UpdatedAtUtc),
        };

    private static AgentWorkingSummaryRecord FromWire(Wire.AgentWorkingSummaryRecord value)
        => new(value.SessionId, value.SummaryText, Parse(value.UpdatedAtUtc));

    private static Wire.SemanticMemoryStatusRecord ToWire(SemanticMemoryStatusRecord value)
        => new() { StatusText = value.StatusText, CanReindex = value.CanReindex };

    private static SemanticMemoryStatusRecord FromWire(Wire.SemanticMemoryStatusRecord value)
        => new(value.StatusText, value.CanReindex);

    private static Wire.SemanticMemorySessionStateRecord ToWire(SemanticMemorySessionStateRecord value)
        => new() { Context = ToWire(value.Context), Status = ToWire(value.Status) };

    private static SemanticMemorySessionStateRecord FromWire(Wire.SemanticMemorySessionStateRecord value)
        => new(FromWire(value.Context), FromWire(value.Status));

    private static Wire.SemanticMemoryReindexResult ToWire(SemanticMemoryReindexResult value)
        => new() { Message = value.Message, IndexedMemoryCount = value.IndexedMemoryCount };

    private static SemanticMemoryReindexResult FromWire(Wire.SemanticMemoryReindexResult value)
        => new(value.Message, checked((int)value.IndexedMemoryCount));

    private static Wire.SemanticMemoryWorkerStatusRecord ToWire(SemanticMemoryWorkerStatusRecord value)
        => new()
        {
            StatusText = value.StatusText,
            IsRunning = value.IsRunning,
            PendingItemCount = value.PendingItemCount,
            ProcessedItemCount = value.ProcessedItemCount,
            LastSuccessfulRunAtUtc = Format(value.LastSuccessfulRunAtUtc),
            LastFailureAtUtc = Format(value.LastFailureAtUtc),
            LastFailureMessage = value.LastFailureMessage,
            HasFailure = value.HasFailure,
        };

    private static SemanticMemoryWorkerStatusRecord FromWire(Wire.SemanticMemoryWorkerStatusRecord value)
        => new(
            value.StatusText,
            value.IsRunning,
            checked((int)value.PendingItemCount),
            value.ProcessedItemCount,
            ParseNullable(value.LastSuccessfulRunAtUtc),
            ParseNullable(value.LastFailureAtUtc),
            value.LastFailureMessage,
            value.HasFailure);

    private static Wire.SemanticMemoryMetricsSnapshot ToWire(SemanticMemoryMetricsSnapshot value)
        => new()
        {
            PromotionCandidateCount = value.PromotionCandidateCount,
            PromotionWriteCount = value.PromotionWriteCount,
            RecallRequestCount = value.RecallRequestCount,
            RecallEntryCount = value.RecallEntryCount,
            CorrectionCount = value.CorrectionCount,
            WorkerFailureCount = value.WorkerFailureCount,
        };

    private static SemanticMemoryMetricsSnapshot FromWire(Wire.SemanticMemoryMetricsSnapshot value)
        => new(
            value.PromotionCandidateCount,
            value.PromotionWriteCount,
            value.RecallRequestCount,
            value.RecallEntryCount,
            value.CorrectionCount,
            value.WorkerFailureCount);

    private static Wire.MemorySemanticIndexStatusRecord ToWire(MemorySemanticIndexStatusRecord value)
        => new()
        {
            StatusLabel = value.StatusLabel,
            StatusText = value.StatusText,
            IndexState = value.IndexState is null ? null : (long)value.IndexState.Value,
            ProviderDisplayName = value.ProviderDisplayName,
            ModelId = value.ModelId,
        };

    private static MemorySemanticIndexStatusRecord FromWire(Wire.MemorySemanticIndexStatusRecord value)
        => new(
            value.StatusLabel,
            value.StatusText,
            value.IndexState is null
                ? null
                : EnumValue<SemanticMemoryEntryIndexState>(value.IndexState.Value, nameof(value.IndexState)),
            value.ProviderDisplayName,
            value.ModelId);

    private static TEnum EnumValue<TEnum>(long value, string fieldName)
        where TEnum : struct, Enum
    {
        if (value is < int.MinValue or > int.MaxValue)
        {
            throw new InvalidOperationException($"RPC field '{fieldName}' is outside the supported enum range.");
        }
        var result = (TEnum)Enum.ToObject(typeof(TEnum), checked((int)value));
        return Enum.IsDefined(result)
            ? result
            : throw new InvalidOperationException($"RPC field '{fieldName}' has an unknown value.");
    }

    private static string Format(DateTimeOffset value)
        => value.ToUniversalTime().ToString(
            "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",
            CultureInfo.InvariantCulture);

    private static string? Format(DateTimeOffset? value)
        => value is null ? null : Format(value.Value);

    private static DateTimeOffset Parse(string value)
        => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static DateTimeOffset? ParseNullable(string? value)
        => value is null ? null : Parse(value);
}
