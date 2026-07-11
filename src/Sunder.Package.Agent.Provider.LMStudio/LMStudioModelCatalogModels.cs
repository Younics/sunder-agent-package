using System.Net;

namespace Sunder.Package.Agent.Provider.LMStudio;

[Flags]
internal enum LMStudioModelCapability
{
    Unknown = 0,
    Chat = 1,
    Embedding = 2,
}

internal enum LMStudioCatalogFailureKind
{
    Configuration,
    Http,
    InvalidResponse,
    Network,
    Timeout,
}

internal sealed record LMStudioModelInfo(
    string Id,
    string DisplayName,
    LMStudioModelCapability Capability,
    int? ContextWindow = null,
    int? MaxOutputTokens = null,
    int? Dimensions = null)
{
    public bool IsChatCandidate => Capability is LMStudioModelCapability.Unknown
        || Capability.HasFlag(LMStudioModelCapability.Chat);

    public bool IsEmbeddingCandidate => Capability.HasFlag(LMStudioModelCapability.Embedding);
}

internal sealed record LMStudioCatalogFailure(
    LMStudioCatalogFailureKind Kind,
    string Message,
    HttpStatusCode? StatusCode = null,
    Exception? Exception = null);

internal sealed record LMStudioModelCatalogResult(
    IReadOnlyList<LMStudioModelInfo> Models,
    LMStudioCatalogFailure? Failure)
{
    public bool IsSuccess => Failure is null;

    public static LMStudioModelCatalogResult Success(IReadOnlyList<LMStudioModelInfo> models) => new(models, null);

    public static LMStudioModelCatalogResult Failed(LMStudioCatalogFailure failure) => new([], failure);
}
