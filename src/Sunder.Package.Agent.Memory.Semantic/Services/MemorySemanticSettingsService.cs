using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Memory.Semantic.Services;

public sealed class MemorySemanticSettingsService(IPackageContext packageContext)
{
    private const int DefaultEmbeddingBatchSize = 16;
    private const int DefaultMaxCanonicalTextChars = 1200;
    private const int MaxEmbeddingBatchSize = 128;
    private const int MaxCanonicalTextChars = 8_000;

    private readonly IPackageContext _packageContext = packageContext;
    internal int CachedMaxCanonicalTextChars { get; private set; } = DefaultMaxCanonicalTextChars;

    public async Task<bool> IsSemanticRetrievalEnabledAsync(CancellationToken cancellationToken = default)
        => !bool.TryParse(await _packageContext.Settings.GetValueAsync("semantic.enabled", cancellationToken), out var enabled) || enabled;

    public async Task<int> GetEmbeddingBatchSizeAsync(CancellationToken cancellationToken = default)
        => ParseBoundedInt(
            await _packageContext.Settings.GetValueAsync("semantic.batchSize", cancellationToken),
            DefaultEmbeddingBatchSize,
            minimum: 1,
            maximum: MaxEmbeddingBatchSize);

    public async Task<int> GetMaxCanonicalTextCharsAsync(CancellationToken cancellationToken = default)
    {
        CachedMaxCanonicalTextChars = ParseBoundedInt(
            await _packageContext.Settings.GetValueAsync("semantic.maxCanonicalTextChars", cancellationToken),
            DefaultMaxCanonicalTextChars,
            minimum: 128,
            maximum: MaxCanonicalTextChars);
        return CachedMaxCanonicalTextChars;
    }

    public async Task<SemanticReindexMode> GetReindexModeAsync(CancellationToken cancellationToken = default)
        => string.Equals(await _packageContext.Settings.GetValueAsync("semantic.reindex.mode", cancellationToken), "never", StringComparison.OrdinalIgnoreCase)
            ? SemanticReindexMode.Never
            : SemanticReindexMode.Lazy;

    private static int ParseBoundedInt(string? value, int fallback, int minimum, int maximum)
        => int.TryParse(value, out var parsed)
            ? Math.Clamp(parsed, minimum, maximum)
            : fallback;
}

public enum SemanticReindexMode
{
    Lazy = 0,
    Never = 1,
}
