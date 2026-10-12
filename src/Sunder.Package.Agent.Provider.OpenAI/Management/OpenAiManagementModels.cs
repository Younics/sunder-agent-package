namespace Sunder.Package.Agent.Provider.OpenAI;

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal sealed record OpenAiAuthOperationResult(
    bool IsConnected,
    bool HasCachedSession,
    DateTimeOffset? ExpiresAtUtc,
    string? ErrorMessage = null);
