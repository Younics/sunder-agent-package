using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Memory.Semantic.Services;

public sealed class MemorySemanticSettingsService(IPackageContext packageContext)
{
    private const int DefaultEmbeddingBatchSize = 16;
    private const int DefaultMaxCanonicalTextChars = 1200;

    private readonly IPackageContext _packageContext = packageContext;

    public bool IsSemanticRetrievalEnabled()
        => !bool.TryParse(_packageContext.Configuration.GetValue("semantic.enabled"), out var enabled) || enabled;

    public int GetEmbeddingBatchSize()
        => ParsePositiveInt(_packageContext.Configuration.GetValue("semantic.batchSize"), DefaultEmbeddingBatchSize);

    public int GetMaxCanonicalTextChars()
        => ParsePositiveInt(_packageContext.Configuration.GetValue("semantic.maxCanonicalTextChars"), DefaultMaxCanonicalTextChars);

    public SemanticReindexMode GetReindexMode()
        => string.Equals(_packageContext.Configuration.GetValue("semantic.reindex.mode"), "never", StringComparison.OrdinalIgnoreCase)
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
