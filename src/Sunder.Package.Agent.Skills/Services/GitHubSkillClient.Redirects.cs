using System.Net;
using System.Net.Http.Headers;

namespace Sunder.Package.Agent.Skills.Services;

public sealed partial class GitHubSkillClient
{
    private static readonly string[] CredentialHeaderNames =
        ["Authorization", "Proxy-Authorization", "Cookie"];
    private static readonly HashSet<string> DownloadHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "raw.githubusercontent.com",
        "objects.githubusercontent.com",
        "github-releases.githubusercontent.com",
        "release-assets.githubusercontent.com",
        "codeload.github.com",
    };

    private HttpRequestMessage CreateRequest(Uri uri, string accept)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("Sunder-Agent-Skills/1.0");
        request.Headers.Accept.ParseAdd(accept);
        if (IsApiAuthority(uri))
        {
            request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
            foreach (var (name, values) in _apiCredentialHeaders)
            {
                request.Headers.TryAddWithoutValidation(name, values);
            }
        }
        return request;
    }

    private Uri ResolveRedirectUri(Uri currentUri, Uri? location, string accept)
    {
        if (location is null)
        {
            throw new InvalidDataException("GitHub redirect response omitted its Location header.");
        }
        var target = location.IsAbsoluteUri ? location : new Uri(currentUri, location);
        return ValidateRequestUri(target, accept, isRedirect: true);
    }

    private Uri ValidateRequestUri(Uri uri, string accept, bool isRedirect)
    {
        if (!uri.IsAbsoluteUri
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new InvalidDataException("GitHub request attempted to use an unsafe URI.");
        }
        if (IsApiAuthority(uri)
            || string.Equals(accept, RawAccept, StringComparison.Ordinal)
            && uri.IsDefaultPort
            && DownloadHosts.Contains(uri.Host))
        {
            return uri;
        }
        throw new InvalidDataException(isRedirect
            ? "GitHub redirect attempted to leave the allowed API or download authorities."
            : "GitHub request attempted to leave the configured API authority.");
    }

    private bool IsApiAuthority(Uri uri)
        => string.Equals(uri.Scheme, _httpClient.BaseAddress!.Scheme, StringComparison.OrdinalIgnoreCase)
           && string.Equals(uri.Authority, _httpClient.BaseAddress.Authority, StringComparison.OrdinalIgnoreCase);

    private static bool IsRedirect(HttpStatusCode statusCode)
        => statusCode is HttpStatusCode.MovedPermanently
            or HttpStatusCode.Found
            or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;

    private static IReadOnlyDictionary<string, string[]> CaptureApiCredentialHeaders(
        HttpRequestHeaders defaultHeaders)
    {
        var captured = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in CredentialHeaderNames)
        {
            if (defaultHeaders.TryGetValues(name, out var values))
            {
                captured[name] = values.ToArray();
                defaultHeaders.Remove(name);
            }
        }
        return captured;
    }
}
