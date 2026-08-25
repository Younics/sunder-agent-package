using System.Collections.Concurrent;

namespace Sunder.Package.Agent.TestSupport;

public sealed record RecordedHttpRequest(
    HttpMethod Method,
    Uri? Uri,
    IReadOnlyDictionary<string, string> Headers,
    byte[] Body);

public sealed class ControlledHttpMessageHandler(
    Func<RecordedHttpRequest, int, CancellationToken, Task<HttpResponseMessage>> responseFactory)
    : HttpMessageHandler
{
    private readonly ConcurrentQueue<RecordedHttpRequest> _requests = new();
    private readonly AsyncCheckpoint _received = new();

    public IReadOnlyList<RecordedHttpRequest> Requests => _requests.ToArray();

    public Task WaitForRequestAsync(int requestCount = 1, CancellationToken cancellationToken = default)
        => _received.WaitForCountAsync(requestCount, cancellationToken);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? []
            : await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        var contentHeaders = request.Content is null
            ? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>()
            : request.Content.Headers;
        var headers = request.Headers
            .Concat(contentHeaders)
            .ToDictionary(
                static header => header.Key,
                static header => string.Join(",", header.Value),
                StringComparer.OrdinalIgnoreCase);
        var observed = new RecordedHttpRequest(request.Method, request.RequestUri, headers, body);
        _requests.Enqueue(observed);
        _received.Signal();
        return await responseFactory(observed, _requests.Count, cancellationToken).ConfigureAwait(false);
    }
}
