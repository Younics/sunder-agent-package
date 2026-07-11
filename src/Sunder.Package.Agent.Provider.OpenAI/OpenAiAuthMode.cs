using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Provider.OpenAI;

internal static class OpenAiAuthMode
{
    public const string ConfigurationKey = "auth.mode";
    public const string ApiKey = "api-key";
    public const string CodexConnected = "codex-connected";

    public static async Task<string> GetSelectedAsync(
        IPackageConfiguration configuration,
        CancellationToken cancellationToken = default)
        => string.Equals(
            await configuration.GetValueAsync(ConfigurationKey, cancellationToken),
            ApiKey,
            StringComparison.OrdinalIgnoreCase)
            ? ApiKey
            : CodexConnected;
}
