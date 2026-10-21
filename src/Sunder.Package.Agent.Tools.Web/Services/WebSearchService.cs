using System.Security.Cryptography;
using System.Text;
using Sunder.Generated.Packages.Younics_Ai_Agent_Models;
using Sunder.Package.Agent.Tools.Web.Backends;

namespace Sunder.Package.Agent.Tools.Web.Services;

public sealed class WebSearchService
{
    private readonly ExaWebSearchBackend _exa;
    private readonly ParallelWebSearchBackend _parallel;
    private readonly WebToolsSettingsService _settings;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly Dictionary<string, Cooldown> _cooldowns = new(StringComparer.Ordinal);

    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    public WebSearchService(ExaWebSearchBackend exa, ParallelWebSearchBackend parallel, WebToolsSettingsService settings)
        : this(exa, parallel, settings, TimeProvider.System) { }

    internal WebSearchService(ExaWebSearchBackend exa, ParallelWebSearchBackend parallel,
        WebToolsSettingsService settings, TimeProvider time)
    {
        _exa = exa;
        _parallel = parallel;
        _settings = settings;
        _time = time;
    }

    public async ValueTask<AgentToolReadiness> GetReadinessAsync(CancellationToken cancellationToken = default)
    {
        var provider = await _settings.GetSearchProviderAsync(cancellationToken).ConfigureAwait(false);
        var keyedExa = !string.IsNullOrWhiteSpace(await _settings.GetExaApiKeyAsync(cancellationToken).ConfigureAwait(false));
        return new AgentToolReadiness
        {
            ToolId = "web_search", Status = AgentToolReadinessStatus.Ready,
            Message = provider == "auto"
                ? $"Web search is ready via {(keyedExa ? "Exa" : "Parallel")} with automatic rate-limit failover. Free search is subject to provider quotas."
                : $"Web search is ready via {(provider == "exa" ? "Exa" : "Parallel")}. Free search is subject to provider quotas.",
        };
    }

    public async Task<WebSearchBackendResult> SearchAsync(
        string query, int maxResults, Guid? sessionId, CancellationToken cancellationToken = default)
    {
        var selection = await _settings.GetSearchProviderAsync(cancellationToken).ConfigureAwait(false);
        var exaKey = await _settings.GetExaApiKeyAsync(cancellationToken).ConfigureAwait(false);
        var parallelKey = await _settings.GetParallelApiKeyAsync(cancellationToken).ConfigureAwait(false);
        string[] providers = selection != "auto" ? [selection]
            : !string.IsNullOrWhiteSpace(exaKey) ? ["exa", "parallel"] : ["parallel", "exa"];
        DateTimeOffset? earliestRetry = null;
        foreach (var provider in providers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = provider == "exa" ? exaKey : parallelKey;
            var identity = string.IsNullOrWhiteSpace(key) ? string.Empty
                : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
            var now = _time.GetUtcNow();
            DateTimeOffset? retryAt;
            lock (_gate)
            {
                retryAt = _cooldowns.TryGetValue(provider, out var cooldown)
                    && cooldown.CredentialIdentity == identity && cooldown.Until > now
                    ? cooldown.Until : null;
            }
            if (retryAt is null)
            {
                try
                {
                    var result = provider == "exa"
                        ? await _exa.SearchAsync(query, maxResults, exaKey, cancellationToken).ConfigureAwait(false)
                        : await _parallel.SearchAsync(query, maxResults, sessionId, parallelKey, cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    return result;
                }
                catch (WebSearchRateLimitException exception)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    retryAt = exception.GetRetryAt(_time.GetUtcNow());
                    lock (_gate)
                    {
                        // Concurrent sessions must not shorten an already observed cooldown.
                        if (_cooldowns.TryGetValue(provider, out var current)
                            && current.CredentialIdentity == identity && current.Until > retryAt)
                        {
                            retryAt = current.Until;
                        }
                        _cooldowns[provider] = new Cooldown(identity, retryAt.Value);
                    }
                }
            }
            if (earliestRetry is null || retryAt < earliestRetry) earliestRetry = retryAt;
        }
        throw new WebSearchRateLimitException(selection, retryAt: earliestRetry);
    }

    private sealed record Cooldown(string CredentialIdentity, DateTimeOffset Until);
}
