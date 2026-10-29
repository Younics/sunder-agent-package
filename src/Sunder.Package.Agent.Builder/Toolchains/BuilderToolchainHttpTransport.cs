using System.Net;
using System.Text.RegularExpressions;

namespace Sunder.Package.Agent.Builder;

/// <summary>A dedicated, credential-free client. Never share a Registry/package-authenticated HttpClient here.</summary>
public sealed class BuilderToolchainHttpTransport : IBuilderToolchainTransport, IDisposable
{
    private readonly HttpClient client = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        Credentials = null,
        PreAuthenticate = false,
        UseProxy = false,
        AutomaticDecompression = DecompressionMethods.None,
        ConnectTimeout = TimeSpan.FromSeconds(20),
        MaxResponseHeadersLength = 32,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    }) { Timeout = Timeout.InfiniteTimeSpan };

    public static void ValidateUri(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || uri.Port != 443 ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            uri.OriginalString.Contains('%', StringComparison.Ordinal) || uri.OriginalString.Contains('\\', StringComparison.Ordinal))
            throw new InvalidDataException("Toolchain downloads require a credential-free official HTTPS URL.");
        var path = uri.AbsolutePath;
        var allowed = uri.Host switch
        {
            "builds.dotnet.microsoft.com" or "dotnetcli.blob.core.windows.net" =>
                Match(path, @"^/dotnet/release-metadata/[1-9][0-9]\.0/releases\.json$") ||
                Match(path, @"^/dotnet/Sdk/[1-9][0-9]\.0\.[0-9]+/dotnet-sdk-[1-9][0-9]\.0\.[0-9]+-(win|osx|linux|linux-musl)-(x64|arm64)\.(zip|tar\.gz)$"),
            "nodejs.org" => path == "/dist/index.json" ||
                Match(path, @"^/dist/v[1-9][0-9]\.[0-9]+\.[0-9]+/(SHASUMS256\.txt|node-v[1-9][0-9]\.[0-9]+\.[0-9]+-(win|darwin|linux)-(x64|arm64)(-musl)?\.(zip|tar\.gz))$"),
            "registry.npmjs.org" => Match(path, @"^/npm/([0-9]+\.[0-9]+\.[0-9]+|-/npm-[0-9]+\.[0-9]+\.[0-9]+\.tgz)$"),
            _ => false,
        };
        if (!allowed) throw new InvalidDataException("The toolchain download origin or path is not allowlisted.");
    }

    public async Task CopyToAsync(Uri uri, Stream destination, long maximumBytes, IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        if (maximumBytes is <= 0 or > BuilderToolchainPolicy.MaximumArchiveBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        ValidateUri(uri);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(maximumBytes <= BuilderToolchainPolicy.MaximumMetadataBytes ? TimeSpan.FromSeconds(45) : TimeSpan.FromMinutes(15));
        var token = deadline.Token;
        var originalPath = uri.AbsolutePath;
        try
        {
            for (var redirects = 0; redirects <= 5; redirects++)
            {
                ValidateUri(uri);
                // Redirects may move a Microsoft artifact between the reviewed Microsoft origins,
                // but may not change the artifact/metadata identity or cross vendors.
                if (uri.AbsolutePath != originalPath) throw new InvalidDataException("The download redirect changed the requested artifact path.");
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.UserAgent.ParseAdd("Sunder-Builder-Toolchains/1.0");
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
                {
                    var location = response.Headers.Location ?? throw new InvalidDataException("The official server returned a redirect without a location.");
                    uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                    continue;
                }
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentEncoding.Count != 0)
                    throw new InvalidDataException("Encoded toolchain download responses are not supported.");
                var expected = response.Content.Headers.ContentLength;
                if (expected > maximumBytes) throw new InvalidDataException("The toolchain response exceeds its size limit.");
                await using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                var buffer = new byte[64 * 1024];
                long total = 0, lastReported = 0;
                while (true)
                {
                    // Each read also has a stall timeout; an active connection cannot hang an operation indefinitely.
                    using var readDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                    readDeadline.CancelAfter(TimeSpan.FromSeconds(45));
                    var count = await source.ReadAsync(buffer, readDeadline.Token).ConfigureAwait(false);
                    if (count == 0) break;
                    total = checked(total + count);
                    if (total > maximumBytes) throw new InvalidDataException("The toolchain response exceeds its size limit.");
                    await destination.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                    if (total - lastReported >= 8 * 1024 * 1024)
                    {
                        progress?.Report($"Downloaded {total / (1024 * 1024)} MiB{(expected.HasValue ? $" of {expected / (1024 * 1024)} MiB" : "")}.");
                        lastReported = total;
                    }
                }
                if (expected is not null && total != expected) throw new EndOfStreamException("The toolchain download was incomplete. Retry the operation.");
                return;
            }
            throw new InvalidDataException("The toolchain server exceeded the redirect limit.");
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The official toolchain download timed out. Retry when the connection is available.", ex);
        }
    }

    private static bool Match(string value, string pattern) => Regex.IsMatch(value, pattern,
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public void Dispose() => client.Dispose();
}
