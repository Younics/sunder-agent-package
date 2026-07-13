using Sunder.Agent.Execution.Common;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Tools.Web.Services;

public sealed class WebToolsSettingsService(IPackageContext packageContext)
{
    private readonly IPackageContext _packageContext = packageContext;

    public async Task<int> GetDefaultMaxResultsAsync(CancellationToken cancellationToken = default)
        => BoundedValue.ParsePositiveInt32Clamped(
            await _packageContext.Settings.GetValueAsync("search.maxResults.default", cancellationToken),
            fallback: 5,
            maximum: 10);

    public Task<string?> GetExaApiKeyAsync(CancellationToken cancellationToken = default)
        => _packageContext.Secrets.GetSecretAsync("search.exa.apiKey", cancellationToken);
}
