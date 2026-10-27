using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Provider.Shared;

// One holder per provider request/enumeration. SDK exceptions do not always
// expose response headers; retain only the bounded retry minimum, never bodies
// or credentials. This handler observes responses and never retries a request.
internal sealed class ProviderRetryMetadata
{
    private long _retryAfterMilliseconds = -1;
    internal long? RetryAfterMilliseconds => Volatile.Read(ref _retryAfterMilliseconds) is var value && value >= 0 ? value : null;

    internal void Observe(HttpResponseMessage response)
        => Volatile.Write(ref _retryAfterMilliseconds, AgentProviderFailureClassifier.RetryAfter(
            response.Headers.TryGetValues("retry-after-ms", out var ms) ? ms.FirstOrDefault() : null,
            response.Headers.TryGetValues("retry-after", out var after) ? after.FirstOrDefault() : null,
            DateTimeOffset.UtcNow) ?? -1);
}

internal sealed class ProviderRetryMetadataHandler(ProviderRetryMetadata metadata) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        metadata.Observe(response);
        return response;
    }
}
