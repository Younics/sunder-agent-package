using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Sunder.Package.Agent.Tools.Web.Services;

public sealed class WebFetchService
{
    private const int DefaultTimeoutSeconds = 30;
    private const int MaxTimeoutSeconds = 120;
    private const int MaxResponseBytes = 5 * 1024 * 1024;
    private const int MaxContentLength = 20000;

    public async Task<WebFetchResult> FetchAsync(string url, string format, int? timeoutSeconds, CancellationToken cancellationToken)
    {
        using var httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds ?? DefaultTimeoutSeconds, 1, MaxTimeoutSeconds))
        };

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", "Sunder/1.0");

        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var memoryStream = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            if (memoryStream.Length + read > MaxResponseBytes)
            {
                memoryStream.Write(buffer, 0, MaxResponseBytes - (int)memoryStream.Length);
                break;
            }

            memoryStream.Write(buffer, 0, read);
        }

        var contentType = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
        var rawContent = System.Text.Encoding.UTF8.GetString(memoryStream.ToArray());
        var title = ExtractTitle(rawContent);
        var renderedContent = format.ToLowerInvariant() switch
        {
            "html" => rawContent,
            "markdown" when contentType.Contains("html", StringComparison.OrdinalIgnoreCase) => ToMarkdown(rawContent),
            "markdown" => $"```text\n{rawContent}\n```",
            _ when contentType.Contains("html", StringComparison.OrdinalIgnoreCase) => ExtractPlainText(rawContent),
            _ => rawContent,
        };

        var wasTruncated = renderedContent.Length > MaxContentLength || memoryStream.Length >= MaxResponseBytes;
        if (renderedContent.Length > MaxContentLength)
        {
            renderedContent = renderedContent[..MaxContentLength];
        }

        var payload = JsonSerializer.Serialize(new
        {
            url,
            finalUrl = response.RequestMessage?.RequestUri?.ToString() ?? url,
            title,
            contentType,
            format,
            wasTruncated,
            content = renderedContent,
        });

        return new WebFetchResult(
            Summary: string.IsNullOrWhiteSpace(title)
                ? $"Fetched {response.RequestMessage?.RequestUri ?? response.RequestMessage?.RequestUri}"
                : $"Fetched '{title}'",
            Content: renderedContent,
            StructuredPayloadJson: payload,
            Title: title,
            FinalUrl: response.RequestMessage?.RequestUri?.ToString() ?? url,
            ContentType: contentType,
            WasTruncated: wasTruncated);
    }

    private static string? ExtractTitle(string html)
    {
        var match = Regex.Match(html, "<title>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return match.Success ? WebUtility.HtmlDecode(match.Groups[1].Value.Trim()) : null;
    }

    private static string ExtractPlainText(string html)
    {
        var withoutScripts = Regex.Replace(html, "<(script|style)[^>]*?>.*?</\\1>", string.Empty, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        var withBreaks = Regex.Replace(withoutScripts, "<(br|/p|/div|/li|/h1|/h2|/h3|/h4|/h5|/h6)>", "\n", RegexOptions.IgnoreCase);
        var withoutTags = Regex.Replace(withBreaks, "<[^>]+>", " ");
        var decoded = WebUtility.HtmlDecode(withoutTags);
        var normalizedLines = decoded
            .Split('\n')
            .Select(line => Regex.Replace(line, "\\s+", " ").Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line));
        return string.Join("\n\n", normalizedLines);
    }

    private static string ToMarkdown(string html)
    {
        var title = ExtractTitle(html);
        var body = ExtractPlainText(html);
        if (string.IsNullOrWhiteSpace(title))
        {
            return body;
        }

        return $"# {title}\n\n{body}";
    }
}

public sealed record WebFetchResult(
    string Summary,
    string Content,
    string StructuredPayloadJson,
    string? Title,
    string FinalUrl,
    string ContentType,
    bool WasTruncated);
