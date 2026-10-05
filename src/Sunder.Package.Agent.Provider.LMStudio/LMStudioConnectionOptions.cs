using System.Security.Cryptography;
using System.Text;

namespace Sunder.Package.Agent.Provider.LMStudio;

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal sealed record LMStudioConnectionOptions(
    Uri BaseUri,
    string NormalizedBaseUrl,
    string? ApiKey,
    TimeSpan Timeout)
{
    public LMStudioConnectionCacheKey CacheKey { get; } = new(
        NormalizedBaseUrl,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ApiKey ?? string.Empty))));

    public static bool TryCreate(
        string? baseUrl,
        string? apiKey,
        TimeSpan timeout,
        out LMStudioConnectionOptions options,
        out string validationError)
    {
        if (!TryNormalizeBaseUrl(baseUrl, out var baseUri, out var normalizedBaseUrl, out validationError))
        {
            options = null!;
            return false;
        }

        var normalizedApiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
        if (normalizedApiKey is not null
            && baseUri.Scheme == Uri.UriSchemeHttp
            && !baseUri.IsLoopback)
        {
            options = null!;
            validationError = "An authenticated LM Studio endpoint must use HTTPS unless it is loopback-only.";
            return false;
        }

        if (timeout <= TimeSpan.Zero)
        {
            options = null!;
            validationError = "The LM Studio timeout must be greater than zero.";
            return false;
        }

        options = new LMStudioConnectionOptions(
            baseUri,
            normalizedBaseUrl,
            normalizedApiKey,
            timeout);
        return true;
    }

    public static bool TryNormalizeBaseUrl(
        string? value,
        out Uri baseUri,
        out string normalizedBaseUrl,
        out string validationError)
    {
        var candidate = string.IsNullOrWhiteSpace(value)
            ? LMStudioProviderConfiguration.DefaultBaseUrl
            : value.Trim();
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var parsedUri)
            || (parsedUri.Scheme != Uri.UriSchemeHttp && parsedUri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrWhiteSpace(parsedUri.Host))
        {
            baseUri = null!;
            normalizedBaseUrl = string.Empty;
            validationError = "Enter an absolute HTTP or HTTPS LM Studio base URL.";
            return false;
        }

        if (!string.IsNullOrEmpty(parsedUri.Query) || !string.IsNullOrEmpty(parsedUri.Fragment))
        {
            baseUri = null!;
            normalizedBaseUrl = string.Empty;
            validationError = "The LM Studio base URL cannot contain a query string or fragment.";
            return false;
        }

        if (!string.IsNullOrEmpty(parsedUri.UserInfo))
        {
            baseUri = null!;
            normalizedBaseUrl = string.Empty;
            validationError = "The LM Studio base URL cannot contain user information.";
            return false;
        }

        normalizedBaseUrl = parsedUri.AbsoluteUri.TrimEnd('/');
        baseUri = new Uri(normalizedBaseUrl + '/', UriKind.Absolute);
        validationError = string.Empty;
        return true;
    }
}

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal sealed record LMStudioConnectionCacheKey(string BaseUrl, string CredentialFingerprint);
