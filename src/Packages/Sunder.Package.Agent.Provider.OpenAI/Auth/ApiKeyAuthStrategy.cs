using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Provider.OpenAI.Auth;

public sealed class ApiKeyAuthStrategy(IPackageContext packageContext)
{
    public string ModeId { get; } = "api-key";

    public string? GetApiKey() => packageContext.Secrets.GetSecret("auth.apiKey");
}
