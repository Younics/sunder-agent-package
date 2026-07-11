using System.Text.Json;

namespace Sunder.Package.Agent.Provider.LMStudio;

internal sealed class LMStudioModelCatalogService
{
    internal static readonly TimeSpan DefaultCacheTtl = TimeSpan.FromSeconds(10);

    private readonly object _cacheLock = new();
    private readonly LMStudioConnection _connection;
    private readonly TimeSpan _cacheTtl;
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<LMStudioConnectionCacheKey, InflightEntry> _inflight = [];
    private CacheEntry? _cache;

    public LMStudioModelCatalogService(
        LMStudioConnection connection,
        TimeSpan? cacheTtl = null,
        TimeProvider? timeProvider = null)
    {
        _connection = connection;
        _cacheTtl = cacheTtl ?? DefaultCacheTtl;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async ValueTask<LMStudioModelCatalogResult> GetCatalogAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_connection.TryGetOptions(out var options, out var validationError))
        {
            return LMStudioModelCatalogResult.Failed(new LMStudioCatalogFailure(
                LMStudioCatalogFailureKind.Configuration,
                validationError));
        }

        InflightEntry inflight;
        var now = _timeProvider.GetUtcNow();
        lock (_cacheLock)
        {
            if (_cache is { } cache
                && cache.Key == options.CacheKey
                && now < cache.ExpiresAt)
            {
                return cache.Result;
            }

            if (_inflight.TryGetValue(options.CacheKey, out var existing))
            {
                inflight = existing;
                inflight.WaiterCount++;
            }
            else
            {
                inflight = new InflightEntry(options.CacheKey);
                _inflight.Add(options.CacheKey, inflight);
                inflight.Task = FetchAndCacheAsync(options, inflight);
            }
        }

        try
        {
            return await inflight.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ReleaseWaiter(inflight);
        }
    }

    private async Task<LMStudioModelCatalogResult> FetchAndCacheAsync(
        LMStudioConnectionOptions options,
        InflightEntry inflight)
    {
        try
        {
            var result = await FetchAsync(options, inflight.Cancellation.Token).ConfigureAwait(false);
            lock (_cacheLock)
            {
                if (OwnsInflightEntry(inflight)
                    && !inflight.Cancellation.IsCancellationRequested
                    && _connection.TryGetOptions(out var currentOptions, out _)
                    && currentOptions.CacheKey == options.CacheKey)
                {
                    _cache = new CacheEntry(
                        options.CacheKey,
                        _timeProvider.GetUtcNow() + _cacheTtl,
                        result);
                }
            }

            return result;
        }
        finally
        {
            CompleteInflight(inflight);
        }
    }

    private void ReleaseWaiter(InflightEntry inflight)
    {
        var cancel = false;
        lock (_cacheLock)
        {
            inflight.WaiterCount--;
            if (inflight.WaiterCount == 0 && !inflight.IsCompleted && OwnsInflightEntry(inflight))
            {
                _inflight.Remove(inflight.Key);
                cancel = true;
            }
        }

        if (cancel)
        {
            SafeCancel(inflight.Cancellation);
        }
    }

    private void CompleteInflight(InflightEntry inflight)
    {
        lock (_cacheLock)
        {
            inflight.IsCompleted = true;
            if (OwnsInflightEntry(inflight))
            {
                _inflight.Remove(inflight.Key);
            }
        }

        inflight.Cancellation.Dispose();
    }

    private bool OwnsInflightEntry(InflightEntry inflight)
        => _inflight.TryGetValue(inflight.Key, out var current) && ReferenceEquals(current, inflight);

    private static void SafeCancel(CancellationTokenSource cancellation)
    {
        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task<LMStudioModelCatalogResult> FetchAsync(
        LMStudioConnectionOptions options,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = _connection.CreateRequest(HttpMethod.Get, "models", options);
            using var response = await _connection.SendAsync(request, options, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return LMStudioModelCatalogResult.Failed(new LMStudioCatalogFailure(
                    LMStudioCatalogFailureKind.Http,
                    $"LM Studio returned {(int)response.StatusCode} {response.ReasonPhrase} while loading models.",
                    response.StatusCode));
            }

            await using var content = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(content, cancellationToken: cancellationToken).ConfigureAwait(false);
            return ParseCatalog(document.RootElement);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (TimeoutException ex)
        {
            return LMStudioModelCatalogResult.Failed(new LMStudioCatalogFailure(
                LMStudioCatalogFailureKind.Timeout,
                ex.Message,
                Exception: ex));
        }
        catch (JsonException ex)
        {
            return LMStudioModelCatalogResult.Failed(new LMStudioCatalogFailure(
                LMStudioCatalogFailureKind.InvalidResponse,
                "LM Studio returned an invalid model catalog.",
                Exception: ex));
        }
        catch (Exception ex)
        {
            return LMStudioModelCatalogResult.Failed(new LMStudioCatalogFailure(
                LMStudioCatalogFailureKind.Network,
                $"LM Studio is not reachable: {ex.Message}",
                Exception: ex));
        }
    }

    private static LMStudioModelCatalogResult ParseCatalog(JsonElement root)
    {
        if (!TryGetArray(root, "data", out var modelsElement)
            && !TryGetArray(root, "models", out modelsElement))
        {
            return LMStudioModelCatalogResult.Failed(new LMStudioCatalogFailure(
                LMStudioCatalogFailureKind.InvalidResponse,
                "LM Studio returned a model catalog without a data array."));
        }

        var models = new Dictionary<string, LMStudioModelInfo>(StringComparer.OrdinalIgnoreCase);
        var itemCount = 0;
        foreach (var item in modelsElement.EnumerateArray())
        {
            itemCount++;
            if (item.ValueKind != JsonValueKind.Object || GetString(item, "id") is not { Length: > 0 } id)
            {
                continue;
            }

            var model = new LMStudioModelInfo(
                id,
                GetString(item, "display_name", "name") ?? id,
                Classify(item, id),
                GetPositiveInt(item, "max_context_length", "context_length", "loaded_context_length"),
                GetPositiveInt(item, "max_output_tokens", "max_completion_tokens"),
                GetPositiveInt(item, "dimensions", "embedding_dimension", "embedding_dimensions"));
            if (models.TryGetValue(id, out var existing))
            {
                model = model with
                {
                    Capability = existing.Capability | model.Capability,
                    ContextWindow = model.ContextWindow ?? existing.ContextWindow,
                    MaxOutputTokens = model.MaxOutputTokens ?? existing.MaxOutputTokens,
                    Dimensions = model.Dimensions ?? existing.Dimensions,
                };
            }

            models[id] = model;
        }

        if (itemCount > 0 && models.Count == 0)
        {
            return LMStudioModelCatalogResult.Failed(new LMStudioCatalogFailure(
                LMStudioCatalogFailureKind.InvalidResponse,
                "LM Studio returned model entries without valid IDs."));
        }

        return LMStudioModelCatalogResult.Success(models.Values
            .OrderBy(model => model.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(model => model.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray());
    }

    private static LMStudioModelCapability Classify(JsonElement item, string id)
    {
        var capability = LMStudioModelCapability.Unknown;
        foreach (var fieldName in new[] { "type", "model_type", "kind" })
        {
            if (GetString(item, fieldName) is { } value)
            {
                capability |= ClassifyToken(value);
            }
        }

        if (TryGetProperty(item, "capabilities", out var capabilities))
        {
            capability |= ClassifyCapabilities(capabilities);
        }

        if (TryGetProperty(item, "metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object)
        {
            foreach (var fieldName in new[] { "type", "model_type", "kind" })
            {
                if (GetString(metadata, fieldName) is { } value)
                {
                    capability |= ClassifyToken(value);
                }
            }

            if (TryGetProperty(metadata, "capabilities", out capabilities))
            {
                capability |= ClassifyCapabilities(capabilities);
            }
        }

        if (capability != LMStudioModelCapability.Unknown)
        {
            return capability;
        }

        return GetPositiveInt(item, "dimensions", "embedding_dimension", "embedding_dimensions") is not null
               || IsLikelyEmbeddingModelId(id)
            ? LMStudioModelCapability.Embedding
            : LMStudioModelCapability.Unknown;
    }

    private static LMStudioModelCapability ClassifyCapabilities(JsonElement capabilities)
    {
        var result = LMStudioModelCapability.Unknown;
        if (capabilities.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in capabilities.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    result |= ClassifyToken(item.GetString()!);
                }
            }
        }
        else if (capabilities.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in capabilities.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.True)
                {
                    result |= ClassifyToken(property.Name);
                }
            }
        }

        return result;
    }

    private static LMStudioModelCapability ClassifyToken(string value)
    {
        var normalized = value.Trim().Replace('_', '-').ToLowerInvariant();
        if (normalized.Contains("embed", StringComparison.Ordinal))
        {
            return LMStudioModelCapability.Embedding;
        }

        return normalized is "llm" or "vlm" or "chat" or "completion" or "completions"
            or "text-generation" or "generate"
            ? LMStudioModelCapability.Chat
            : LMStudioModelCapability.Unknown;
    }

    private static bool IsLikelyEmbeddingModelId(string id)
    {
        var normalized = id.Trim().Replace('_', '-').ToLowerInvariant();
        return normalized.Contains("embed", StringComparison.Ordinal)
               || normalized.Contains("minilm", StringComparison.Ordinal)
               || HasModelFamily(normalized, "bge")
               || HasModelFamily(normalized, "e5")
               || HasModelFamily(normalized, "gte")
               || HasModelFamily(normalized, "instructor");
    }

    private static bool HasModelFamily(string modelId, string family)
    {
        var index = 0;
        while ((index = modelId.IndexOf(family, index, StringComparison.Ordinal)) >= 0)
        {
            var startsAtBoundary = index == 0 || modelId[index - 1] is '/' or '-' or '.';
            var end = index + family.Length;
            var endsAtBoundary = end == modelId.Length || modelId[end] is '/' or '-' or '.';
            if (startsAtBoundary && endsAtBoundary)
            {
                return true;
            }

            index = end;
        }

        return false;
    }

    private static string? GetString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (TryGetProperty(element, name, out var value)
                && value.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(value.GetString()))
            {
                return value.GetString()!.Trim();
            }
        }

        return null;
    }

    private static int? GetPositiveInt(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (TryGetProperty(element, name, out var value)
                && value.TryGetInt32(out var parsed)
                && parsed > 0)
            {
                return parsed;
            }
        }

        return null;
    }

    private static bool TryGetArray(JsonElement element, string name, out JsonElement value)
        => TryGetProperty(element, name, out value) && value.ValueKind == JsonValueKind.Array;

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private sealed record CacheEntry(
        LMStudioConnectionCacheKey Key,
        DateTimeOffset ExpiresAt,
        LMStudioModelCatalogResult Result);

    private sealed class InflightEntry(LMStudioConnectionCacheKey key)
    {
        public LMStudioConnectionCacheKey Key { get; } = key;

        public CancellationTokenSource Cancellation { get; } = new();

        public Task<LMStudioModelCatalogResult> Task { get; set; } = null!;

        public int WaiterCount { get; set; } = 1;

        public bool IsCompleted { get; set; }
    }
}
