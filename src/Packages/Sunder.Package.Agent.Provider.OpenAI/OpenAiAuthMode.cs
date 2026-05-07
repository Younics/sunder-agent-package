using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Provider.OpenAI;

internal static class OpenAiAuthMode
{
    public const string ApiKey = "api-key";
    public const string CodexConnected = "codex-connected";

    public static string GetSelected(IPackageConfiguration configuration)
        => string.Equals(configuration.GetValue("auth.mode"), ApiKey, StringComparison.OrdinalIgnoreCase)
            ? ApiKey
            : CodexConnected;
}
