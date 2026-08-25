using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Memory.Semantic.Services;
using Sunder.Sdk.Rpc;
using Wire = Sunder.Package.Agent.Memory.Semantic.Protocol.Generated;

namespace Sunder.Package.Agent.Memory.Semantic.Runtime;

public static class MemoryInspectorRpc
{
    private const string OperationFailedCode = "memory.semantic.operation-failed";
    private const string JsonMediaType = "application/json";
    internal const int MaximumProjectionBytes = 16 * 1024 * 1024;
    private static readonly JsonSerializerOptions ProjectionJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower) },
    };

    public static ISunderRpcServiceHandler CreateHandler(IMemoryInspectorRpcService service)
        => new Wire.SunderAgentMemorySemanticInspectorRpcHandler(new ProviderAdapter(service));

    public static MemoryInspectorRpcClient CreateClient(
        ISunderRpcCallScope scope,
        SunderRpcEndpointReference endpoint)
        => new(scope, new Wire.MemoryInspectorClient(scope, endpoint));

    private sealed class ProviderAdapter(IMemoryInspectorRpcService service) : Wire.IMemoryInspectorProvider
    {
        public async ValueTask<Wire.MemoryProjectionContent> QueryAsync(
            SunderRpcInvocationContext context,
            Wire.MemoryQuery request,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var projection = await service.QueryAsync(
                    context,
                    MemoryInspectorRpcMapper.FromWire(request),
                    cancellationToken).ConfigureAwait(false);
                return await WriteProjectionAsync(context, projection, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (ShouldMap(exception))
            {
                throw Map((MemoryInspectorOperationException)exception);
            }
        }

        public async ValueTask<Wire.MemoryProjectionContent> CommandAsync(
            SunderRpcInvocationContext context,
            Wire.MemoryCommand request,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var projection = await service.CommandAsync(
                    context,
                    MemoryInspectorRpcMapper.FromWire(request),
                    cancellationToken).ConfigureAwait(false);
                return await WriteProjectionAsync(context, projection, cancellationToken).ConfigureAwait(false);
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
            while (await MoveNextAsync(enumerator).ConfigureAwait(false))
            {
                yield return MemoryInspectorRpcMapper.ToWire(enumerator.Current);
            }
        }

        private static async ValueTask<bool> MoveNextAsync<T>(IAsyncEnumerator<T> enumerator)
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

    private static async ValueTask<Wire.MemoryProjectionContent> WriteProjectionAsync(
        SunderRpcInvocationContext context,
        MemoryProjection projection,
        CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(projection, ProjectionJson);
        if (bytes.Length > MaximumProjectionBytes)
        {
            throw new MemoryInspectorOperationException(
                "The semantic memory projection exceeds its 16 MiB content limit.");
        }

        await using var stream = new MemoryStream(bytes, writable: false);
        var reference = await context.RegisterContentAsync(
            stream,
            new SunderRpcContentRegistrationOptions(
                JsonMediaType,
                "memory-projection.json",
                bytes.LongLength,
                Repeatability: SunderRpcContentRepeatability.SingleUse,
                MaximumUses: 1),
            cancellationToken).ConfigureAwait(false);
        ValidateReference(reference, context.DeadlineUtc);
        var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (reference.Length != bytes.LongLength
            || !string.Equals(reference.Sha256, digest, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The Host returned inconsistent memory content metadata.");
        }
        return new Wire.MemoryProjectionContent { Content = reference };
    }

    internal static async ValueTask<MemoryProjection> ReadProjectionAsync(
        ISunderRpcCallScope scope,
        Wire.MemoryProjectionContent projection,
        CancellationToken cancellationToken)
    {
        var reference = projection.Content;
        ValidateReference(reference, scope.DeadlineUtc);
        await using var stream = await scope.OpenContentAsync(reference, cancellationToken).ConfigureAwait(false);
        using var content = new MemoryStream(checked((int)reference.Length));
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }
            if (content.Length + read > reference.Length || content.Length + read > MaximumProjectionBytes)
            {
                throw new InvalidDataException("The memory projection exceeds its authenticated length.");
            }
            content.Write(buffer, 0, read);
        }

        var bytes = content.ToArray();
        var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (bytes.LongLength != reference.Length
            || !string.Equals(reference.Sha256, digest, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The memory projection digest or length is invalid.");
        }
        return JsonSerializer.Deserialize<MemoryProjection>(bytes, ProjectionJson)
               ?? throw new InvalidDataException("The semantic memory projection is empty.");
    }

    private static void ValidateReference(SunderRpcContentReference reference, DateTimeOffset deadlineUtc)
    {
        if (reference.Length < 0
            || reference.Length > MaximumProjectionBytes
            || !string.Equals(reference.MediaType, JsonMediaType, StringComparison.OrdinalIgnoreCase)
            || reference.Repeatability != SunderRpcContentRepeatability.SingleUse
            || reference.ExpiresAtUtc <= DateTimeOffset.UtcNow
            || reference.ExpiresAtUtc > deadlineUtc)
        {
            throw new InvalidDataException("The semantic memory content reference is invalid.");
        }
    }
}

public sealed class MemoryInspectorRpcClient
{
    private readonly ISunderRpcCallScope _scope;
    private readonly Wire.MemoryInspectorClient _client;

    internal MemoryInspectorRpcClient(
        ISunderRpcCallScope scope,
        Wire.MemoryInspectorClient client)
    {
        _scope = scope;
        _client = client;
    }

    public async ValueTask<MemoryProjection> QueryAsync(
        MemoryQuery request,
        CancellationToken cancellationToken = default)
        => await MemoryInspectorRpc.ReadProjectionAsync(
            _scope,
            await _client.QueryAsync(MemoryInspectorRpcMapper.ToWire(request), cancellationToken)
                .ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);

    public async ValueTask<MemoryProjection> CommandAsync(
        MemoryCommand request,
        CancellationToken cancellationToken = default)
        => await MemoryInspectorRpc.ReadProjectionAsync(
            _scope,
            await _client.CommandAsync(MemoryInspectorRpcMapper.ToWire(request), cancellationToken)
                .ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);

    public async IAsyncEnumerable<MemoryChanged> ChangesAsync(
        MemoryChangeSubscription request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var change in _client.ChangesAsync(
                           new Wire.MemoryChangeSubscription(),
                           cancellationToken).ConfigureAwait(false))
        {
            yield return MemoryInspectorRpcMapper.FromWire(change);
        }
    }
}

internal static class MemoryInspectorRpcMapper
{
    internal static Wire.MemoryQuery ToWire(MemoryQuery value)
        => new()
        {
            Kind = ToWire(value.Kind),
            SessionId = value.SessionId,
            MemoryId = value.MemoryId,
            SearchText = value.SearchText,
            IncludeInactive = value.IncludeInactive,
            ProfileId = value.ProfileId,
            Memory = value.Memory is null ? null : ToWire(value.Memory),
            SemanticContext = value.SemanticContext is null ? null : ToWire(value.SemanticContext),
        };

    internal static MemoryQuery FromWire(Wire.MemoryQuery value)
        => new(
            FromWire(value.Kind),
            value.SessionId,
            value.MemoryId,
            value.SearchText,
            value.IncludeInactive,
            value.ProfileId,
            value.Memory is null ? null : FromWire(value.Memory),
            value.SemanticContext is null ? null : FromWire(value.SemanticContext));

    internal static Wire.MemoryCommand ToWire(MemoryCommand value)
        => new()
        {
            Kind = ToWire(value.Kind),
            MemoryId = value.MemoryId,
            Category = value.Category,
            Content = value.Content,
            Note = value.Note,
            IsPinned = value.IsPinned,
            ProfileId = value.ProfileId,
        };

    internal static MemoryCommand FromWire(Wire.MemoryCommand value)
        => new(
            FromWire(value.Kind),
            value.MemoryId,
            value.Category,
            value.Content,
            value.Note,
            value.IsPinned,
            value.ProfileId);

    internal static Wire.MemoryChanged ToWire(MemoryChanged value)
        => new()
        {
            Kind = value.Kind switch
            {
                MemoryChangeKind.Session => Wire.MemoryChangeKind.Session,
                MemoryChangeKind.Worker => Wire.MemoryChangeKind.Worker,
                MemoryChangeKind.ResetRequired => Wire.MemoryChangeKind.ResetRequired,
                _ => throw new InvalidDataException("The memory change kind is invalid."),
            },
            SessionId = value.SessionId,
        };

    internal static MemoryChanged FromWire(Wire.MemoryChanged value)
        => new(value.Kind switch
        {
            Wire.MemoryChangeKind.Session => MemoryChangeKind.Session,
            Wire.MemoryChangeKind.Worker => MemoryChangeKind.Worker,
            Wire.MemoryChangeKind.ResetRequired => MemoryChangeKind.ResetRequired,
            _ => throw new InvalidDataException("The memory Runtime returned an unknown change kind."),
        }, value.SessionId);

    private static Wire.StoredMemory ToWire(StoredMemoryRecord value)
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
            State = value.State switch
            {
                MemoryRecordStates.Active => Wire.MemoryState.Active,
                MemoryRecordStates.Contested => Wire.MemoryState.Contested,
                MemoryRecordStates.Forgotten => Wire.MemoryState.Forgotten,
                MemoryRecordStates.Superseded => Wire.MemoryState.Superseded,
                _ => throw new InvalidDataException("The memory state is invalid."),
            },
            SupersededByMemoryId = value.SupersededByMemoryId,
            CreatedAtUtc = Format(value.CreatedAtUtc),
            UpdatedAtUtc = Format(value.UpdatedAtUtc),
            LastAccessedAtUtc = value.LastAccessedAtUtc is { } accessed ? Format(accessed) : null,
            AccessCount = value.AccessCount,
            Provenance = value.Provenance switch
            {
                AgentMemoryProvenance.Unknown => Wire.MemoryProvenance.Unknown,
                AgentMemoryProvenance.User => Wire.MemoryProvenance.User,
                AgentMemoryProvenance.Assistant => Wire.MemoryProvenance.Assistant,
                AgentMemoryProvenance.Tool => Wire.MemoryProvenance.Tool,
                _ => throw new InvalidDataException("The memory provenance is invalid."),
            },
            MemoryRevision = value.MemoryRevision,
            IsManual = value.IsManual,
        };

    private static StoredMemoryRecord FromWire(Wire.StoredMemory value)
        => new(
            value.MemoryId,
            value.SessionId,
            value.Category,
            value.Content,
            value.EvidenceText,
            value.SourceTurnId,
            value.Importance,
            value.Confidence,
            value.IsPinned,
            value.State switch
            {
                Wire.MemoryState.Active => MemoryRecordStates.Active,
                Wire.MemoryState.Contested => MemoryRecordStates.Contested,
                Wire.MemoryState.Forgotten => MemoryRecordStates.Forgotten,
                Wire.MemoryState.Superseded => MemoryRecordStates.Superseded,
                _ => throw new InvalidDataException("The memory state is invalid."),
            },
            value.SupersededByMemoryId,
            Parse(value.CreatedAtUtc),
            Parse(value.UpdatedAtUtc),
            value.LastAccessedAtUtc is { } accessed ? Parse(accessed) : null,
            checked((int)value.AccessCount),
            value.Provenance switch
            {
                Wire.MemoryProvenance.Unknown => AgentMemoryProvenance.Unknown,
                Wire.MemoryProvenance.User => AgentMemoryProvenance.User,
                Wire.MemoryProvenance.Assistant => AgentMemoryProvenance.Assistant,
                Wire.MemoryProvenance.Tool => AgentMemoryProvenance.Tool,
                _ => throw new InvalidDataException("The memory provenance is invalid."),
            })
        {
            MemoryRevision = value.MemoryRevision,
            IsManual = value.IsManual,
        };

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

    private static Wire.MemoryQueryKind ToWire(MemoryQueryKind value)
        => value switch
        {
            MemoryQueryKind.Sessions => Wire.MemoryQueryKind.Sessions,
            MemoryQueryKind.SessionCheckpoint => Wire.MemoryQueryKind.SessionCheckpoint,
            MemoryQueryKind.WorkingSummary => Wire.MemoryQueryKind.WorkingSummary,
            MemoryQueryKind.Memories => Wire.MemoryQueryKind.Memories,
            MemoryQueryKind.Details => Wire.MemoryQueryKind.Details,
            MemoryQueryKind.SemanticState => Wire.MemoryQueryKind.SemanticState,
            MemoryQueryKind.SemanticIndexStatus => Wire.MemoryQueryKind.SemanticIndexStatus,
            MemoryQueryKind.WorkerStatus => Wire.MemoryQueryKind.WorkerStatus,
            MemoryQueryKind.Metrics => Wire.MemoryQueryKind.Metrics,
            _ => throw new InvalidDataException("The memory query kind is invalid."),
        };

    private static MemoryQueryKind FromWire(Wire.MemoryQueryKind value)
        => value switch
        {
            Wire.MemoryQueryKind.Sessions => MemoryQueryKind.Sessions,
            Wire.MemoryQueryKind.SessionCheckpoint => MemoryQueryKind.SessionCheckpoint,
            Wire.MemoryQueryKind.WorkingSummary => MemoryQueryKind.WorkingSummary,
            Wire.MemoryQueryKind.Memories => MemoryQueryKind.Memories,
            Wire.MemoryQueryKind.Details => MemoryQueryKind.Details,
            Wire.MemoryQueryKind.SemanticState => MemoryQueryKind.SemanticState,
            Wire.MemoryQueryKind.SemanticIndexStatus => MemoryQueryKind.SemanticIndexStatus,
            Wire.MemoryQueryKind.WorkerStatus => MemoryQueryKind.WorkerStatus,
            Wire.MemoryQueryKind.Metrics => MemoryQueryKind.Metrics,
            _ => throw new InvalidDataException("The memory query kind is invalid."),
        };

    private static Wire.MemoryCommandKind ToWire(MemoryCommandKind value)
        => value switch
        {
            MemoryCommandKind.Update => Wire.MemoryCommandKind.Update,
            MemoryCommandKind.Pin => Wire.MemoryCommandKind.Pin,
            MemoryCommandKind.Contest => Wire.MemoryCommandKind.Contest,
            MemoryCommandKind.Forget => Wire.MemoryCommandKind.Forget,
            MemoryCommandKind.Supersede => Wire.MemoryCommandKind.Supersede,
            MemoryCommandKind.Correct => Wire.MemoryCommandKind.Correct,
            MemoryCommandKind.Reindex => Wire.MemoryCommandKind.Reindex,
            _ => throw new InvalidDataException("The memory command kind is invalid."),
        };

    private static MemoryCommandKind FromWire(Wire.MemoryCommandKind value)
        => value switch
        {
            Wire.MemoryCommandKind.Update => MemoryCommandKind.Update,
            Wire.MemoryCommandKind.Pin => MemoryCommandKind.Pin,
            Wire.MemoryCommandKind.Contest => MemoryCommandKind.Contest,
            Wire.MemoryCommandKind.Forget => MemoryCommandKind.Forget,
            Wire.MemoryCommandKind.Supersede => MemoryCommandKind.Supersede,
            Wire.MemoryCommandKind.Correct => MemoryCommandKind.Correct,
            Wire.MemoryCommandKind.Reindex => MemoryCommandKind.Reindex,
            _ => throw new InvalidDataException("The memory command kind is invalid."),
        };

    private static SunderRpcOffsetDateTime Format(DateTimeOffset value)
        => new(value.ToUniversalTime().ToString(
            "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",
            CultureInfo.InvariantCulture));

    private static DateTimeOffset Parse(SunderRpcOffsetDateTime value)
        => value.TryGetDateTimeOffset(out var parsed)
            ? parsed.ToUniversalTime()
            : throw new InvalidDataException("The memory timestamp exceeds .NET precision.");
}
