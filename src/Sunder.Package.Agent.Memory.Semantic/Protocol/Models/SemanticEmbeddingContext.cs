namespace Sunder.Package.Agent.Memory.Semantic.Services;

public sealed record SemanticEmbeddingContext(
    string StatusLabel,
    string StatusText,
    bool IsReady,
    string? ProviderId = null,
    string? ModelId = null,
    string? ProviderDisplayName = null,
    string? ConfigurationFingerprint = null)
{
    public static SemanticEmbeddingContext Ready(
        string providerId,
        string modelId,
        string providerDisplayName,
        string configurationFingerprint = "")
        => new(
            "Ready",
            $"Semantic retrieval is active via {providerDisplayName} / {modelId}.",
            true,
            providerId,
            modelId,
            providerDisplayName,
            configurationFingerprint);

    public static SemanticEmbeddingContext Disabled(string statusText) => new("Disabled", statusText, false);

    public static SemanticEmbeddingContext Unavailable(string statusText) => new("Unavailable", statusText, false);
}
