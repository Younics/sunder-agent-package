using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Sunder.Package.Agent.Provider.LMStudio.Tests;

internal sealed record LMStudioObservedRequest(
    HttpMethod Method,
    Uri Uri,
    AuthenticationHeaderValue? Authorization,
    string? Body);

internal sealed class LMStudioTestHttpHandler(
    Func<LMStudioObservedRequest, int, CancellationToken, Task<HttpResponseMessage>> responseFactory)
    : HttpMessageHandler
{
    private readonly Func<LMStudioObservedRequest, int, CancellationToken, Task<HttpResponseMessage>> _responseFactory = responseFactory;
    private readonly List<LMStudioObservedRequest> _requests = [];

    public IReadOnlyList<LMStudioObservedRequest> Requests => _requests;

    public bool IsDisposed { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var observed = new LMStudioObservedRequest(
            request.Method,
            request.RequestUri!,
            request.Headers.Authorization,
            request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken));
        _requests.Add(observed);
        return await _responseFactory(observed, _requests.Count, cancellationToken);
    }

    protected override void Dispose(bool disposing)
    {
        IsDisposed = true;
        base.Dispose(disposing);
    }

    public static Task<HttpResponseMessage> Json(
        string json,
        HttpStatusCode statusCode = HttpStatusCode.OK)
        => Task.FromResult(new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        });
}
