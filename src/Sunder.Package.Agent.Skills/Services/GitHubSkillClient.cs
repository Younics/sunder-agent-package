using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Sunder.Package.Agent.Skills.Services;

public interface IGitHubSkillClient
{
    Task<string?> TryGetDefaultBranchAsync(string owner, string repo, CancellationToken cancellationToken = default);

    Task<GitHubSkillFolder?> TryGetFolderAsync(GitHubSkillFolderRequest request, CancellationToken cancellationToken = default);

    Task<GitHubSkillFolder?> TryGetSkillFolderAsync(GitHubSkillFolderRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GitHubSkillFile>> ListFilesAsync(GitHubSkillFolder folder, CancellationToken cancellationToken = default);

    Task<byte[]> ReadFileAsync(GitHubSkillFolder folder, GitHubSkillFile file, CancellationToken cancellationToken = default);
}

public sealed record GitHubSkillFolderRequest(string Owner, string Repo, string Ref, string FolderPath);

public sealed record GitHubSkillFolder(string Owner, string Repo, string Ref, string FolderPath, string CommitSha, string? TreeSha);

public sealed record GitHubSkillFile(string RelativePath, string RepositoryPath, long Size);

public sealed partial class GitHubSkillClient : IGitHubSkillClient, IDisposable
{
    internal const int MaximumJsonResponseBytes = 4 * 1024 * 1024;
    internal const int MaximumRawFileBytes = 10 * 1024 * 1024;
    internal const int MaximumPaginationPages = 4;
    internal const int MaximumRedirects = 5;
    private const int MaximumErrorBytes = 8 * 1024;
    private const int MaximumTreeEntries = 4_096;
    private const int MaximumDirectoryEntries = 4_096;
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonDocumentOptions JsonOptions = new() { MaxDepth = 32 };
    private readonly HttpClient _httpClient;
    private readonly IReadOnlyDictionary<string, string[]> _apiCredentialHeaders;
    private readonly TimeSpan _timeout;
    private readonly bool _disposeHttpClient;

    public GitHubSkillClient()
        : this(CreateHttpClient(), DefaultTimeout, disposeHttpClient: true)
    {
    }

    public GitHubSkillClient(HttpClient httpClient)
        : this(httpClient, DefaultTimeout, disposeHttpClient: false)
    {
    }

    internal GitHubSkillClient(HttpClient httpClient, TimeSpan timeout)
        : this(httpClient, timeout, disposeHttpClient: false)
    {
    }

    private GitHubSkillClient(HttpClient httpClient, TimeSpan timeout, bool disposeHttpClient)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        _httpClient = httpClient;
        _httpClient.BaseAddress ??= new Uri("https://api.github.com/");
        if (!string.Equals(_httpClient.BaseAddress.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(_httpClient.BaseAddress.UserInfo))
        {
            throw new ArgumentException("GitHub API base address must use HTTPS without embedded credentials.", nameof(httpClient));
        }
        _apiCredentialHeaders = CaptureApiCredentialHeaders(_httpClient.DefaultRequestHeaders);
        _timeout = timeout;
        _disposeHttpClient = disposeHttpClient;
    }

    public async Task<string?> TryGetDefaultBranchAsync(
        string owner,
        string repo,
        CancellationToken cancellationToken = default)
    {
        var response = await GetAsync(
            BuildRepositoryPath(owner, repo),
            MaximumJsonResponseBytes,
            missingAsNull: true,
            JsonAccept,
            cancellationToken).ConfigureAwait(false);
        if (response is null)
        {
            return null;
        }

        using var document = ParseJson(response.Bytes, "GitHub repository response");
        return ReadRequiredString(document.RootElement, "default_branch", "GitHub repository response");
    }

    public async Task<GitHubSkillFolder?> TryGetFolderAsync(
        GitHubSkillFolderRequest request,
        CancellationToken cancellationToken = default)
    {
        var folderPath = NormalizeGitHubPath(request.FolderPath);
        var commit = await TryResolveCommitAsync(
            request.Owner,
            request.Repo,
            request.Ref,
            cancellationToken).ConfigureAwait(false);
        if (commit is null)
        {
            return null;
        }

        var treeSha = string.IsNullOrEmpty(folderPath)
            ? commit.TreeSha
            : await TryResolveFolderTreeShaAsync(
                request.Owner,
                request.Repo,
                folderPath,
                commit.CommitSha,
                cancellationToken).ConfigureAwait(false);
        if (treeSha is null)
        {
            return null;
        }

        return new GitHubSkillFolder(
            request.Owner,
            request.Repo,
            request.Ref,
            folderPath,
            commit.CommitSha,
            treeSha);
    }

    public async Task<GitHubSkillFolder?> TryGetSkillFolderAsync(
        GitHubSkillFolderRequest request,
        CancellationToken cancellationToken = default)
    {
        var folder = await TryGetFolderAsync(request, cancellationToken).ConfigureAwait(false);
        if (folder is null)
        {
            return null;
        }

        var skillMarkdownPath = CombineGitHubPath(folder.FolderPath, "SKILL.md");
        var response = await GetAsync(
            BuildContentPath(folder.Owner, folder.Repo, skillMarkdownPath, folder.CommitSha),
            MaximumJsonResponseBytes,
            missingAsNull: true,
            JsonAccept,
            cancellationToken).ConfigureAwait(false);
        if (response is null)
        {
            return null;
        }

        using var document = ParseJson(response.Bytes, "GitHub skill manifest response");
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !string.Equals(ReadRequiredString(root, "type", "GitHub skill manifest response"), "file", StringComparison.Ordinal)
            || !string.Equals(ReadRequiredString(root, "path", "GitHub skill manifest response"), skillMarkdownPath, StringComparison.Ordinal))
        {
            return null;
        }

        return folder;
    }

    public async Task<IReadOnlyList<GitHubSkillFile>> ListFilesAsync(
        GitHubSkillFolder folder,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(folder.TreeSha))
        {
            throw new InvalidDataException("GitHub skill folder is missing its fixed tree identity.");
        }

        var files = new List<GitHubSkillFile>();
        var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Uri? next = ResolveUri(
            $"repos/{Escape(folder.Owner)}/{Escape(folder.Repo)}/git/trees/{Escape(folder.TreeSha)}?recursive=1");
        var page = 0;
        var entryCount = 0;
        while (next is not null)
        {
            if (++page > MaximumPaginationPages)
            {
                throw new InvalidDataException(
                    $"GitHub tree pagination exceeds the {MaximumPaginationPages}-page limit.");
            }

            var response = await GetAsync(
                next,
                MaximumJsonResponseBytes,
                missingAsNull: false,
                JsonAccept,
                cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("GitHub tree response was unexpectedly missing.");
            using var document = ParseJson(response.Bytes, "GitHub tree response");
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("truncated", out var truncatedElement)
                || truncatedElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                || !root.TryGetProperty("tree", out var treeElement)
                || treeElement.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException("GitHub tree response has an invalid shape.");
            }
            if (truncatedElement.GetBoolean())
            {
                throw new InvalidDataException("GitHub skill folder tree was truncated and cannot be imported safely.");
            }

            foreach (var item in treeElement.EnumerateArray())
            {
                if (++entryCount > MaximumTreeEntries)
                {
                    throw new InvalidDataException(
                        $"GitHub skill tree exceeds the {MaximumTreeEntries}-entry transport limit.");
                }
                ParseTreeEntry(folder, item, paths, files);
            }

            next = ValidateNextLink(response.NextPage);
            if (page == MaximumPaginationPages && next is not null)
            {
                throw new InvalidDataException(
                    $"GitHub tree pagination exceeds the {MaximumPaginationPages}-page limit.");
            }
        }

        return files.OrderBy(file => file.RelativePath, StringComparer.Ordinal).ToArray();
    }

    public async Task<byte[]> ReadFileAsync(
        GitHubSkillFolder folder,
        GitHubSkillFile file,
        CancellationToken cancellationToken = default)
    {
        var expectedRepositoryPath = CombineGitHubPath(folder.FolderPath, file.RelativePath);
        if (!string.Equals(
                NormalizeGitHubPath(file.RepositoryPath),
                expectedRepositoryPath,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("GitHub skill file does not belong to the resolved folder.");
        }
        if (file.Size is < 0 or > MaximumRawFileBytes)
        {
            throw new InvalidDataException("GitHub skill file has an invalid declared size.");
        }

        var response = await GetAsync(
            BuildContentPath(folder.Owner, folder.Repo, expectedRepositoryPath, folder.CommitSha),
            checked((int)file.Size),
            missingAsNull: false,
            RawAccept,
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("GitHub skill file was unexpectedly missing.");
        if (response.Bytes.LongLength != file.Size)
        {
            throw new InvalidDataException(
                $"GitHub skill file '{file.RelativePath}' changed size or did not match its fixed tree metadata.");
        }
        return response.Bytes;
    }

    public void Dispose()
    {
        if (_disposeHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private async Task<ResolvedCommit?> TryResolveCommitAsync(
        string owner,
        string repo,
        string reference,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            throw new InvalidDataException("GitHub reference is empty.");
        }

        var response = await GetAsync(
            $"{BuildRepositoryPath(owner, repo)}/commits/{Escape(reference)}",
            MaximumJsonResponseBytes,
            missingAsNull: true,
            JsonAccept,
            cancellationToken).ConfigureAwait(false);
        if (response is null)
        {
            return null;
        }

        using var document = ParseJson(response.Bytes, "GitHub commit response");
        var root = document.RootElement;
        var commitSha = ReadRequiredSha(root, "sha", "GitHub commit response");
        if (!root.TryGetProperty("commit", out var commitElement)
            || commitElement.ValueKind != JsonValueKind.Object
            || !commitElement.TryGetProperty("tree", out var treeElement)
            || treeElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("GitHub commit response is missing its tree identity.");
        }
        return new ResolvedCommit(
            commitSha,
            ReadRequiredSha(treeElement, "sha", "GitHub commit tree response"));
    }

    private async Task<string?> TryResolveFolderTreeShaAsync(
        string owner,
        string repo,
        string folderPath,
        string commitSha,
        CancellationToken cancellationToken)
    {
        var lastSlashIndex = folderPath.LastIndexOf('/');
        var parentPath = lastSlashIndex < 0 ? string.Empty : folderPath[..lastSlashIndex];
        var folderName = lastSlashIndex < 0 ? folderPath : folderPath[(lastSlashIndex + 1)..];
        var entries = await ReadDirectoryPagesAsync(
            BuildContentPath(owner, repo, parentPath, commitSha),
            cancellationToken).ConfigureAwait(false);
        var entry = entries.SingleOrDefault(candidate =>
            string.Equals(candidate.Name, folderName, StringComparison.Ordinal)
            && string.Equals(candidate.Path, folderPath, StringComparison.Ordinal));
        return entry is { Type: "dir" } ? entry.Sha : null;
    }

    private async Task<IReadOnlyList<ContentEntry>> ReadDirectoryPagesAsync(
        string initialPath,
        CancellationToken cancellationToken)
    {
        var entries = new List<ContentEntry>();
        Uri? next = ResolveUri(initialPath);
        var page = 0;
        while (next is not null)
        {
            if (++page > MaximumPaginationPages)
            {
                throw new InvalidDataException(
                    $"GitHub directory pagination exceeds the {MaximumPaginationPages}-page limit.");
            }
            var response = await GetAsync(
                next,
                MaximumJsonResponseBytes,
                missingAsNull: true,
                JsonAccept,
                cancellationToken).ConfigureAwait(false);
            if (response is null)
            {
                return [];
            }

            using var document = ParseJson(response.Bytes, "GitHub directory response");
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException("GitHub directory response is not an array.");
            }
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (entries.Count == MaximumDirectoryEntries)
                {
                    throw new InvalidDataException(
                        $"GitHub directory response exceeds the {MaximumDirectoryEntries}-entry limit.");
                }
                entries.Add(new ContentEntry(
                    ReadRequiredString(item, "name", "GitHub directory entry"),
                    NormalizeGitHubPath(ReadRequiredString(item, "path", "GitHub directory entry")),
                    ReadRequiredString(item, "type", "GitHub directory entry"),
                    ReadRequiredSha(item, "sha", "GitHub directory entry")));
            }

            next = ValidateNextLink(response.NextPage);
            if (page == MaximumPaginationPages && next is not null)
            {
                throw new InvalidDataException(
                    $"GitHub directory pagination exceeds the {MaximumPaginationPages}-page limit.");
            }
        }

        return entries;
    }

    private async Task<BoundedResponse?> GetAsync(
        string path,
        int maximumBytes,
        bool missingAsNull,
        string accept,
        CancellationToken cancellationToken)
        => await GetAsync(
            ResolveUri(path),
            maximumBytes,
            missingAsNull,
            accept,
            cancellationToken).ConfigureAwait(false);

    private async Task<BoundedResponse?> GetAsync(
        Uri uri,
        int maximumBytes,
        bool missingAsNull,
        string accept,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(_timeout);
        var requestCancellationToken = timeoutSource.Token;

        try
        {
            var currentUri = ValidateRequestUri(uri, accept, isRedirect: false);
            var redirectCount = 0;
            while (true)
            {
                using var request = CreateRequest(currentUri, accept);
                using var response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    requestCancellationToken).ConfigureAwait(false);
                if (IsRedirect(response.StatusCode))
                {
                    if (redirectCount == MaximumRedirects)
                    {
                        throw new InvalidDataException(
                            $"GitHub request exceeds the {MaximumRedirects}-redirect limit.");
                    }
                    currentUri = ResolveRedirectUri(currentUri, response.Headers.Location, accept);
                    redirectCount++;
                    continue;
                }
                if (missingAsNull
                    && response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.UnprocessableEntity)
                {
                    return null;
                }
                if (!response.IsSuccessStatusCode)
                {
                    var error = await ReadBoundedBytesAsync(
                        response.Content,
                        MaximumErrorBytes,
                        throwOnExcess: false,
                        requestCancellationToken).ConfigureAwait(false);
                    if (IsRateLimit(response, error))
                    {
                        throw new InvalidOperationException(
                            "GitHub rate limit reached; try again after the limit resets.");
                    }
                    throw new HttpRequestException(
                        $"GitHub request failed with HTTP {(int)response.StatusCode} ({response.StatusCode}).",
                        inner: null,
                        response.StatusCode);
                }

                var bytes = await ReadBoundedBytesAsync(
                    response.Content,
                    maximumBytes,
                    throwOnExcess: true,
                    requestCancellationToken).ConfigureAwait(false);
                return new BoundedResponse(bytes, ReadNextPage(response.Headers));
            }
        }
        catch (OperationCanceledException) when (
            timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"GitHub did not respond within {_timeout.TotalSeconds:0.###} seconds.");
        }
    }

    private static async Task<byte[]> ReadBoundedBytesAsync(
        HttpContent content,
        int maximumBytes,
        bool throwOnExcess,
        CancellationToken cancellationToken)
    {
        if (throwOnExcess
            && content.Headers.ContentLength is { } declaredLength
            && declaredLength > maximumBytes)
        {
            throw new InvalidDataException($"GitHub response exceeds the {maximumBytes}-byte limit.");
        }

        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var memory = new MemoryStream(Math.Min(maximumBytes, 64 * 1024));
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var remaining = maximumBytes - checked((int)memory.Length);
            if (!throwOnExcess && remaining == 0)
            {
                break;
            }
            var readLimit = throwOnExcess ? remaining + 1 : remaining;
            var read = await stream.ReadAsync(
                buffer.AsMemory(0, Math.Min(buffer.Length, readLimit)),
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }
            if (read > remaining)
            {
                if (throwOnExcess)
                {
                    throw new InvalidDataException($"GitHub response exceeds the {maximumBytes}-byte limit.");
                }
                memory.Write(buffer, 0, Math.Max(0, remaining));
                break;
            }
            memory.Write(buffer, 0, read);
        }
        return memory.ToArray();
    }

    private static bool IsRateLimit(HttpResponseMessage response, byte[] errorBytes)
    {
        if (response.StatusCode == HttpStatusCode.TooManyRequests
            || response.Headers.RetryAfter is not null)
        {
            return true;
        }
        if (response.StatusCode != HttpStatusCode.Forbidden)
        {
            return false;
        }
        if (response.Headers.TryGetValues("X-RateLimit-Remaining", out var values)
            && values.Any(value => string.Equals(value, "0", StringComparison.Ordinal)))
        {
            return true;
        }

        try
        {
            return StrictUtf8.GetString(errorBytes).Contains("rate limit", StringComparison.OrdinalIgnoreCase);
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private void ParseTreeEntry(
        GitHubSkillFolder folder,
        JsonElement item,
        IDictionary<string, string> paths,
        ICollection<GitHubSkillFile> files)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("GitHub tree contains a non-object entry.");
        }

        var relativePath = NormalizeGitHubPath(
            ReadRequiredString(item, "path", "GitHub tree entry"));
        if (!paths.TryAdd(relativePath, relativePath))
        {
            throw new InvalidDataException(
                $"GitHub tree contains duplicate or case-colliding path '{relativePath}' and '{paths[relativePath]}'.");
        }
        var type = ReadRequiredString(item, "type", "GitHub tree entry");
        var mode = ReadRequiredString(item, "mode", "GitHub tree entry");
        if (mode == "120000")
        {
            throw new InvalidDataException($"GitHub skill tree contains symbolic link '{relativePath}'.");
        }
        if (type == "commit" || mode == "160000")
        {
            throw new InvalidDataException($"GitHub skill tree contains submodule '{relativePath}'.");
        }
        if (type == "tree" && mode == "040000")
        {
            return;
        }
        if (type != "blob" || mode is not ("100644" or "100755"))
        {
            throw new InvalidDataException($"GitHub skill tree contains unsupported entry '{relativePath}'.");
        }
        if (!item.TryGetProperty("size", out var sizeElement)
            || !sizeElement.TryGetInt64(out var size)
            || size < 0)
        {
            throw new InvalidDataException($"GitHub skill tree entry '{relativePath}' has an invalid size.");
        }

        files.Add(new GitHubSkillFile(
            relativePath,
            CombineGitHubPath(folder.FolderPath, relativePath),
            size));
    }

    private Uri? ValidateNextLink(Uri? next)
    {
        if (next is null)
        {
            return null;
        }
        var baseAddress = _httpClient.BaseAddress!;
        if (!string.Equals(next.Scheme, baseAddress.Scheme, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(next.Authority, baseAddress.Authority, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("GitHub pagination attempted to leave the configured API authority.");
        }
        return next;
    }

    private Uri ResolveUri(string path)
        => new(_httpClient.BaseAddress!, path);

    private static Uri? ReadNextPage(HttpResponseHeaders headers)
    {
        if (!headers.TryGetValues("Link", out var values))
        {
            return null;
        }
        foreach (var part in string.Join(',', values).Split(','))
        {
            if (!part.Contains("rel=\"next\"", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var start = part.IndexOf('<');
            var end = part.IndexOf('>');
            if (start < 0 || end <= start + 1
                || !Uri.TryCreate(part[(start + 1)..end], UriKind.Absolute, out var next))
            {
                throw new InvalidDataException("GitHub response contains an invalid pagination link.");
            }
            return next;
        }
        return null;
    }

    private static JsonDocument ParseJson(byte[] bytes, string description)
    {
        try
        {
            return JsonDocument.Parse(bytes, JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"{description} contains malformed JSON.", exception);
        }
    }

    private static string ReadRequiredString(JsonElement element, string propertyName, string description)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(propertyName, out var property)
           && property.ValueKind == JsonValueKind.String
           && !string.IsNullOrWhiteSpace(property.GetString())
            ? property.GetString()!
            : throw new InvalidDataException($"{description} is missing string property '{propertyName}'.");

    private static string ReadRequiredSha(JsonElement element, string propertyName, string description)
    {
        var value = ReadRequiredString(element, propertyName, description);
        if (value.Length != 40 || value.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidDataException($"{description} contains an invalid Git object identity.");
        }
        return value;
    }

    private static string BuildRepositoryPath(string owner, string repo)
    {
        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(repo))
        {
            throw new InvalidDataException("GitHub repository owner and name are required.");
        }
        return $"repos/{Escape(owner)}/{Escape(repo)}";
    }

    private static string BuildContentPath(string owner, string repo, string path, string reference)
    {
        var normalizedPath = NormalizeGitHubPath(path);
        var suffix = normalizedPath.Length == 0 ? "contents" : "contents/" + EscapePath(normalizedPath);
        return $"{BuildRepositoryPath(owner, repo)}/{suffix}?ref={Escape(reference)}";
    }

    private static string NormalizeGitHubPath(string path)
    {
        var normalized = path.Trim().Trim('/').Replace('\\', '/');
        if (normalized.Length == 0)
        {
            return string.Empty;
        }
        var segments = normalized.Split('/');
        if (segments.Any(segment => segment is "" or "." or ".."))
        {
            throw new InvalidDataException($"GitHub path is invalid: '{path}'.");
        }
        return string.Join('/', segments);
    }

    private static string CombineGitHubPath(string left, string right)
    {
        left = NormalizeGitHubPath(left);
        right = NormalizeGitHubPath(right);
        return left.Length == 0 ? right : right.Length == 0 ? left : left + "/" + right;
    }

    private static string EscapePath(string path)
        => string.Join('/', path.Split('/').Select(Escape));

    private static string Escape(string value)
        => Uri.EscapeDataString(value.Trim());

    private static HttpClient CreateHttpClient()
        => new(new SocketsHttpHandler { AllowAutoRedirect = false })
        {
            BaseAddress = new Uri("https://api.github.com/"),
            Timeout = Timeout.InfiniteTimeSpan,
        };

    private const string JsonAccept = "application/vnd.github+json";
    private const string RawAccept = "application/vnd.github.raw+json";

    private sealed record BoundedResponse(byte[] Bytes, Uri? NextPage);
    private sealed record ResolvedCommit(string CommitSha, string TreeSha);
    private sealed record ContentEntry(string Name, string Path, string Type, string Sha);
}
