using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Sunder.Package.Agent.Skills.Services;
using Xunit;

namespace Sunder.Package.Agent.Skills.Tests;

public sealed class GitHubSkillClientTests
{
    private const string CommitSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string RootTreeSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string SkillTreeSha = "cccccccccccccccccccccccccccccccccccccccc";

    [Fact]
    public async Task FixedCommitResolutionPinsTreeAndRawReads()
    {
        var requests = new List<Uri>();
        using var httpClient = CreateClient((request, _) =>
        {
            requests.Add(request.RequestUri!);
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/commits/main", StringComparison.Ordinal))
            {
                return JsonObject(new
                {
                    sha = CommitSha,
                    commit = new { tree = new { sha = RootTreeSha } },
                });
            }
            if (path.EndsWith("/contents", StringComparison.Ordinal))
            {
                return JsonObject(new[]
                {
                    new { name = "skill", path = "skill", type = "dir", sha = SkillTreeSha },
                });
            }
            if (path.EndsWith("/contents/skill/SKILL.md", StringComparison.Ordinal)
                && request.Headers.Accept.Any(value => value.MediaType == "application/vnd.github+json"))
            {
                return JsonObject(new
                {
                    name = "SKILL.md",
                    path = "skill/SKILL.md",
                    type = "file",
                    sha = "dddddddddddddddddddddddddddddddddddddddd",
                });
            }
            if (path.Contains("/git/trees/", StringComparison.Ordinal))
            {
                return Json("{" +
                    "\"truncated\":false,\"tree\":[" +
                    "{\"path\":\"SKILL.md\",\"mode\":\"100644\",\"type\":\"blob\",\"size\":23}," +
                    "{\"path\":\"resource.txt\",\"mode\":\"100644\",\"type\":\"blob\",\"size\":8}]}");
            }
            if (path.EndsWith("/contents/skill/SKILL.md", StringComparison.Ordinal))
            {
                return Bytes("---\nname: pinned\n---\n"u8.ToArray());
            }
            if (path.EndsWith("/contents/skill/resource.txt", StringComparison.Ordinal))
            {
                return Bytes("resource"u8.ToArray());
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        using var client = new GitHubSkillClient(httpClient);

        var folder = Assert.IsType<GitHubSkillFolder>(await client.TryGetSkillFolderAsync(
            new GitHubSkillFolderRequest("owner", "repo", "main", "skill")));
        var files = await client.ListFilesAsync(folder);
        var resource = Assert.Single(files, file => file.RelativePath == "resource.txt");
        var bytes = await client.ReadFileAsync(folder, resource);

        Assert.Equal(CommitSha, folder.CommitSha);
        Assert.Equal("resource", Encoding.UTF8.GetString(bytes));
        Assert.All(
            requests.Where(uri => uri.AbsolutePath.Contains("/contents", StringComparison.Ordinal)),
            uri => Assert.Equal("?ref=" + CommitSha, uri.Query));
    }

    [Fact]
    public async Task ChunkedOversizedJsonStopsAtTransportBoundAndDisposesStream()
    {
        var stream = new TrackingMemoryStream(new byte[GitHubSkillClient.MaximumJsonResponseBytes + 1]);
        using var httpClient = CreateClient((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new UnlengthContent(stream),
        });
        using var client = new GitHubSkillClient(httpClient);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            client.TryGetDefaultBranchAsync("owner", "repo"));

        Assert.True(stream.WasDisposed);
        Assert.InRange(stream.FinalPosition, 1, GitHubSkillClient.MaximumJsonResponseBytes + 1L);
    }

    [Fact]
    public async Task TreePaginationStopsAtConfiguredPageBound()
    {
        var requestCount = 0;
        using var httpClient = CreateClient((_, _) =>
        {
            var page = Interlocked.Increment(ref requestCount);
            var response = JsonObject(new
            {
                truncated = false,
                tree = new[]
                {
                    new { path = $"file-{page}.txt", mode = "100644", type = "blob", size = 1 },
                },
            });
            response.Headers.TryAddWithoutValidation(
                "Link",
                $"<https://api.github.test/repos/owner/repo/git/trees/tree?page={page + 1}>; rel=\"next\"");
            return response;
        });
        using var client = new GitHubSkillClient(httpClient);
        var folder = new GitHubSkillFolder(
            "owner",
            "repo",
            "main",
            "skill",
            CommitSha,
            SkillTreeSha);

        await Assert.ThrowsAsync<InvalidDataException>(() => client.ListFilesAsync(folder));

        Assert.Equal(GitHubSkillClient.MaximumPaginationPages, requestCount);
    }

    [Fact]
    public async Task TruncatedTreeFailsClosed()
    {
        using var httpClient = CreateClient((_, _) => JsonObject(new
        {
            truncated = true,
            tree = Array.Empty<object>(),
        }));
        using var client = new GitHubSkillClient(httpClient);
        var folder = new GitHubSkillFolder("owner", "repo", "main", "", CommitSha, RootTreeSha);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => client.ListFilesAsync(folder));

        Assert.Contains("truncated", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RawReadStopsOneBytePastFixedTreeSize()
    {
        var stream = new TrackingMemoryStream(new byte[100]);
        using var httpClient = CreateClient((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new UnlengthContent(stream),
        });
        using var client = new GitHubSkillClient(httpClient);
        var folder = new GitHubSkillFolder("owner", "repo", "main", "skill", CommitSha, SkillTreeSha);
        var file = new GitHubSkillFile("resource.txt", "skill/resource.txt", 1);

        await Assert.ThrowsAsync<InvalidDataException>(() => client.ReadFileAsync(folder, file));

        Assert.True(stream.WasDisposed);
        Assert.Equal(2, stream.FinalPosition);
    }

    [Fact]
    public async Task RateLimitResponseHasStableCredentialFreeFailure()
    {
        using var httpClient = CreateClient((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("API rate limit exceeded for secret-token"),
            };
            response.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", "0");
            return response;
        });
        using var client = new GitHubSkillClient(httpClient);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.TryGetDefaultBranchAsync("owner", "repo"));

        Assert.Contains("rate limit", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret-token", exception.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("120000", "blob", "symbolic link")]
    [InlineData("160000", "commit", "submodule")]
    public async Task TreeRejectsLinksAndSubmodules(string mode, string type, string expected)
    {
        using var httpClient = CreateClient((_, _) => JsonObject(new
        {
            truncated = false,
            tree = new[] { new { path = "unsafe", mode, type, size = 1 } },
        }));
        using var client = new GitHubSkillClient(httpClient);
        var folder = new GitHubSkillFolder("owner", "repo", "main", "", CommitSha, RootTreeSha);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => client.ListFilesAsync(folder));

        Assert.Contains(expected, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CallerCancellationReachesInFlightRequest()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var httpClient = CreateClient(async (_, cancellationToken) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return Json("{}");
        });
        using var client = new GitHubSkillClient(httpClient);
        using var cancellation = new CancellationTokenSource();

        var request = client.TryGetDefaultBranchAsync("owner", "repo", cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
    }

    [Fact]
    public async Task RequestTimeoutIsExplicit()
    {
        using var httpClient = CreateClient(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return Json("{}");
        });
        using var client = new GitHubSkillClient(httpClient, TimeSpan.FromMilliseconds(25));

        await Assert.ThrowsAsync<TimeoutException>(() =>
            client.TryGetDefaultBranchAsync("owner", "repo"));
    }

    [Fact]
    public async Task RenamedRepositoryRedirectStaysOnApiAuthorityAndRetainsCredentials()
    {
        var requests = new List<(Uri Uri, string? Authorization)>();
        using var httpClient = CreateClient((request, _) =>
        {
            requests.Add((request.RequestUri!, request.Headers.Authorization?.ToString()));
            if (requests.Count == 1)
            {
                return Redirect(HttpStatusCode.MovedPermanently, "/repositories/42");
            }
            return JsonObject(new { default_branch = "main" });
        });
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "secret-token");
        using var client = new GitHubSkillClient(httpClient);

        var branch = await client.TryGetDefaultBranchAsync("old-owner", "old-repo");

        Assert.Equal("main", branch);
        Assert.Equal(2, requests.Count);
        Assert.All(requests, request => Assert.Equal("Bearer secret-token", request.Authorization));
        Assert.Equal("/repositories/42", requests[1].Uri.AbsolutePath);
    }

    [Fact]
    public async Task RawDownloadRedirectUsesAllowlistAndStripsApiCredentials()
    {
        var requests = new List<(Uri Uri, string? Authorization, bool HasApiVersion)>();
        using var httpClient = CreateClient((request, _) =>
        {
            requests.Add((
                request.RequestUri!,
                request.Headers.Authorization?.ToString(),
                request.Headers.Contains("X-GitHub-Api-Version")));
            return requests.Count == 1
                ? Redirect(HttpStatusCode.Found, "https://raw.githubusercontent.com/owner/repo/" + CommitSha + "/skill/resource.txt")
                : Bytes("resource"u8.ToArray());
        });
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "secret-token");
        using var client = new GitHubSkillClient(httpClient);
        var folder = new GitHubSkillFolder("owner", "repo", "main", "skill", CommitSha, SkillTreeSha);

        var bytes = await client.ReadFileAsync(
            folder,
            new GitHubSkillFile("resource.txt", "skill/resource.txt", 8));

        Assert.Equal("resource", Encoding.UTF8.GetString(bytes));
        Assert.Equal("Bearer secret-token", requests[0].Authorization);
        Assert.True(requests[0].HasApiVersion);
        Assert.Null(requests[1].Authorization);
        Assert.False(requests[1].HasApiVersion);
        Assert.Equal("raw.githubusercontent.com", requests[1].Uri.Host);
    }

    [Fact]
    public async Task RawDownloadRejectsOpenRedirectAndBoundsRedirectCount()
    {
        using var openRedirectClient = CreateClient((_, _) =>
            Redirect(HttpStatusCode.Found, "https://attacker.example/payload"));
        using var openRedirect = new GitHubSkillClient(openRedirectClient);
        var folder = new GitHubSkillFolder("owner", "repo", "main", "skill", CommitSha, SkillTreeSha);
        var file = new GitHubSkillFile("resource.txt", "skill/resource.txt", 8);

        var authorityFailure = await Assert.ThrowsAsync<InvalidDataException>(() =>
            openRedirect.ReadFileAsync(folder, file));
        Assert.Contains("allowed", authorityFailure.Message, StringComparison.OrdinalIgnoreCase);

        var requestCount = 0;
        using var loopingClient = CreateClient((request, _) =>
        {
            var next = Interlocked.Increment(ref requestCount);
            return Redirect(HttpStatusCode.TemporaryRedirect, $"/redirect/{next}");
        });
        using var looping = new GitHubSkillClient(loopingClient);

        var redirectFailure = await Assert.ThrowsAsync<InvalidDataException>(() =>
            looping.ReadFileAsync(folder, file));
        Assert.Contains("redirect limit", redirectFailure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(GitHubSkillClient.MaximumRedirects + 1, requestCount);
    }

    private static HttpClient CreateClient(
        Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> response)
        => CreateClient((request, cancellationToken) =>
            Task.FromResult(response(request, cancellationToken)));

    private static HttpClient CreateClient(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response)
        => new(new StubHandler(response))
        {
            BaseAddress = new Uri("https://api.github.test/"),
            Timeout = Timeout.InfiniteTimeSpan,
        };

    private static HttpResponseMessage Json(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    private static HttpResponseMessage JsonObject(object value)
        => Json(JsonSerializer.Serialize(value));

    private static HttpResponseMessage Bytes(byte[] bytes)
        => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };

    private static HttpResponseMessage Redirect(HttpStatusCode statusCode, string location)
        => new(statusCode)
        {
            Headers = { Location = new Uri(location, UriKind.RelativeOrAbsolute) },
        };

    private sealed class StubHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => response(request, cancellationToken);
    }

    private sealed class UnlengthContent(Stream stream) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream target, TransportContext? context)
            => stream.CopyToAsync(target);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override Task<Stream> CreateContentReadStreamAsync()
            => Task.FromResult(stream);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                stream.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    private sealed class TrackingMemoryStream(byte[] bytes) : MemoryStream(bytes)
    {
        public bool WasDisposed { get; private set; }
        public long FinalPosition { get; private set; }

        protected override void Dispose(bool disposing)
        {
            if (!WasDisposed)
            {
                FinalPosition = Position;
                WasDisposed = true;
            }
            base.Dispose(disposing);
        }
    }
}
