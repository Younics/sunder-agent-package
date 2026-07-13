using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Memory.Semantic.Services;

public sealed class MemorySemanticSettingsService(IPackageContext packageContext)
{
    private const int DefaultEmbeddingBatchSize = 16;
    private const int DefaultMaxCanonicalTextChars = 1200;

    private readonly IPackageContext _packageContext = packageContext;
    internal int CachedMaxCanonicalTextChars { get; private set; } = DefaultMaxCanonicalTextChars;

    public async Task<bool> IsSemanticRetrievalEnabledAsync(CancellationToken cancellationToken = default)
        => !bool.TryParse(await _packageContext.Settings.GetValueAsync("semantic.enabled", cancellationToken), out var enabled) || enabled;

    public async Task<int> GetEmbeddingBatchSizeAsync(CancellationToken cancellationToken = default)
        => ParsePositiveInt(await _packageContext.Settings.GetValueAsync("semantic.batchSize", cancellationToken), DefaultEmbeddingBatchSize);

    public async Task<int> GetMaxCanonicalTextCharsAsync(CancellationToken cancellationToken = default)
    {
        CachedMaxCanonicalTextChars = ParsePositiveInt(
            await _packageContext.Settings.GetValueAsync("semantic.maxCanonicalTextChars", cancellationToken),
            DefaultMaxCanonicalTextChars);
        return CachedMaxCanonicalTextChars;
    }

    public async Task<SemanticReindexMode> GetReindexModeAsync(CancellationToken cancellationToken = default)
        => string.Equals(await _packageContext.Settings.GetValueAsync("semantic.reindex.mode", cancellationToken), "never", StringComparison.OrdinalIgnoreCase)
            ? SemanticReindexMode.Never
            : SemanticReindexMode.Lazy;

    private static int ParsePositiveInt(string? value, int fallback)
        => int.TryParse(value, out var parsed) && parsed > 0 ? parsed : fallback;
}

public enum SemanticReindexMode
{
    Lazy = 0,
    Never = 1,
}
