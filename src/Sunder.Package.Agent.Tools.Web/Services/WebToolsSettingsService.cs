using Sunder.Agent.Execution.Common;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Settings;
using Sunder.Sdk.Worker;

namespace Sunder.Package.Agent.Tools.Web.Services;

public sealed class WebToolsSettingsService(IPackageContext packageContext) : ISunderWorkerSettingsSchemaProvider
{
    private readonly IPackageContext _packageContext = packageContext;

    public PackageSettingsSchema SettingsSchema => WebToolsConfiguration.Schema;

    public async Task<int> GetDefaultMaxResultsAsync(CancellationToken cancellationToken = default)
        => BoundedValue.ParsePositiveInt32Clamped(
            await _packageContext.Settings.GetValueAsync("search.maxResults.default", cancellationToken),
            fallback: 5,
            maximum: 10);

    public Task<string?> GetExaApiKeyAsync(CancellationToken cancellationToken = default)
        => _packageContext.Secrets.GetSecretAsync("search.exa.apiKey", cancellationToken);

    public Task<string?> GetParallelApiKeyAsync(CancellationToken cancellationToken = default)
        => _packageContext.Secrets.GetSecretAsync("search.parallel.apiKey", cancellationToken);

    public async Task<string> GetSearchProviderAsync(CancellationToken cancellationToken = default)
    {
        var value = (await _packageContext.Settings.GetValueAsync("search.provider", cancellationToken))?.Trim().ToLowerInvariant();
        return value is "exa" or "parallel" ? value : "auto";
    }
}
