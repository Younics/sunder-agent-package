using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Tools.Web.Services;

public sealed class WebToolsSettingsService(IPackageContext packageContext)
{
    private readonly IPackageContext _packageContext = packageContext;

    public int GetDefaultMaxResults()
        => int.TryParse(_packageContext.Configuration.GetValue("search.maxResults.default"), out var value) && value > 0
            ? Math.Min(value, 10)
            : 5;

    public string? GetExaApiKey()
        => _packageContext.Secrets.GetSecret("search.exa.apiKey");
}
