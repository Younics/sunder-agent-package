using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Tools.Web.Services;

public sealed class WebToolsSettingsService(IPackageContext packageContext)
{
    private readonly IPackageContext _packageContext = packageContext;

    public async Task<int> GetDefaultMaxResultsAsync(CancellationToken cancellationToken = default)
        => int.TryParse(await _packageContext.Configuration.GetValueAsync("search.maxResults.default", cancellationToken), out var value) && value > 0
            ? Math.Min(value, 10)
            : 5;

    public Task<string?> GetExaApiKeyAsync(CancellationToken cancellationToken = default)
        => _packageContext.Secrets.GetSecretAsync("search.exa.apiKey", cancellationToken);
}
