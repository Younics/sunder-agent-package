using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Provider.OpenAI;

internal static class OpenAiAuthMode
{
    public const string ConfigurationKey = "auth.mode";
    public const string ApiKey = "api-key";
    public const string CodexConnected = "codex-connected";

    public static async Task<string> GetSelectedAsync(
        IPackageSettings settings,
        CancellationToken cancellationToken = default)
        => string.Equals(
            await settings.GetValueAsync(ConfigurationKey, cancellationToken),
            ApiKey,
            StringComparison.OrdinalIgnoreCase)
            ? ApiKey
            : CodexConnected;
}
