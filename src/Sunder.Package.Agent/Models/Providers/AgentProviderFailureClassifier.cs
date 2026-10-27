using System.Globalization;
using System.Net.Http;
using Sunder.Package.Agent.Provider.Shared;

namespace Sunder.Package.Agent.Contracts.Models;

// Shared by the owner and provider adapters. Typed status/codes take precedence
// over human-readable fallback text; never inspect tool output for retry advice.
internal static class AgentProviderFailureClassifier
{
    public static AgentChatProviderFailureKind Classify(string? diagnostic, int? status = null)
    {
        var text = ProviderErrorDiagnosticExtractor.Extract(diagnostic) ?? string.Empty;
        bool Has(params string[] values) => values.Any(value => text.Contains(value, StringComparison.OrdinalIgnoreCase));
        if (status == 402 || Has("insufficient_quota", "quota exceeded", "quota_exceeded", "billing_error", "insufficient balance"))
            return AgentChatProviderFailureKind.QuotaExceeded;
        if (status is 401 or 403 || Has("missing-api-key", "auth-required", "authentication_error", "invalid_api_key"))
            return AgentChatProviderFailureKind.Authentication;
        if (Has("content_policy", "content-filter", "safety policy", "SAFETY", "RECITATION"))
            return AgentChatProviderFailureKind.ContentPolicy;
        if (status == 429 || Has("rate_limit", "rate limit", "too many requests"))
            return AgentChatProviderFailureKind.RateLimited;
        if (status is >= 400 and < 500 and not (408 or 409 or 425))
            return AgentChatProviderFailureKind.InvalidRequest;
        if (status is 503 or 529 || Has("overloaded", "overload_error", "at capacity", "server is busy"))
            return AgentChatProviderFailureKind.Overloaded;
        if (Has("timeout", "timed out")) return AgentChatProviderFailureKind.Timeout;
        if (status is 408 or 409 or 425 or >= 500 || Has("server_error", "server error", "internal_error", "internal error",
                "temporarily unavailable", "service unavailable", "connection", "socket", "ResponseEnded", "premature", "incomplete-stream", "incomplete-response"))
            return AgentChatProviderFailureKind.Transient;
        if (Has("invalid_request", "invalid request", "unsupported", "malformed", "client-init", "multiple-tool-calls"))
            return AgentChatProviderFailureKind.InvalidRequest;
        return AgentChatProviderFailureKind.Unknown;
    }

    public static AgentChatProviderException Normalize(AgentChatProviderException error, long? retryAfterMilliseconds = null)
    {
        var status = error.StatusCode;
        for (var inner = error.InnerException; inner is not null; inner = inner.InnerException)
            if (inner is HttpRequestException { StatusCode: { } code }) { status ??= (int)code; break; }
        var kind = ClassifyProvider(error, status);
        var diagnostic = error;
        if (!IsTerminal(kind))
        {
            // The exception tree cannot cross a provider/run-control stream.
            // Project positive terminal evidence before that boundary discards
            // wrappers, rather than accidentally restoring an overload retry.
            var pending = new Stack<Exception>();
            pending.Push(error);
            while (pending.TryPop(out var current))
            {
                if (current is AgentChatProviderException provider)
                {
                    var candidate = ClassifyProvider(provider, provider.StatusCode);
                    if (IsTerminal(candidate))
                    {
                        diagnostic = provider;
                        kind = candidate;
                        status = provider.StatusCode;
                        break;
                    }
                }
                if (current is HttpRequestException { StatusCode: { } rejection }
                    && Classify(null, (int)rejection) is var httpKind && IsTerminal(httpKind))
                {
                    kind = httpKind;
                    status = (int)rejection;
                    break;
                }
                if (current is AggregateException aggregate)
                    for (var index = aggregate.InnerExceptions.Count - 1; index >= 0; index--)
                        pending.Push(aggregate.InnerExceptions[index]);
                else if (current.InnerException is { } nested) pending.Push(nested);
            }
        }
        return new(diagnostic.Message, diagnostic.Content, diagnostic.ErrorCode, error.InnerException)
        {
            FailureKind = kind,
            StatusCode = status,
            RetryAfterMilliseconds = (error.RetryAfterMilliseconds ?? retryAfterMilliseconds) is { } delay
                ? Math.Clamp(delay, 0, 900_000) : null,
        };
    }

    private static AgentChatProviderFailureKind ClassifyProvider(AgentChatProviderException error, int? status)
    {
        if (IsTerminal(error.FailureKind)) return error.FailureKind;
        var fromStatus = Classify(null, status);
        if (IsTerminal(fromStatus)) return fromStatus;
        return error.FailureKind == AgentChatProviderFailureKind.Unknown
            ? Classify($"{error.ErrorCode}: {ProviderErrorDiagnosticExtractor.Extract(error.Message)}\n{ProviderErrorDiagnosticExtractor.Extract(error.Content)}", status)
            : error.FailureKind;
    }

    private static bool IsTerminal(AgentChatProviderFailureKind kind)
        => kind is AgentChatProviderFailureKind.Authentication or AgentChatProviderFailureKind.QuotaExceeded
            or AgentChatProviderFailureKind.InvalidRequest or AgentChatProviderFailureKind.ContentPolicy
            or AgentChatProviderFailureKind.ContextWindowExceeded;

    public static long? RetryAfter(string? milliseconds, string? retryAfter, DateTimeOffset now)
    {
        static long Clamp(double value) => (long)Math.Clamp(Math.Ceiling(value), 0, 900_000);
        if (double.TryParse(milliseconds, NumberStyles.Float, CultureInfo.InvariantCulture, out var ms) && double.IsFinite(ms))
            return Clamp(ms);
        if (double.TryParse(retryAfter, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && double.IsFinite(seconds))
            return Clamp(seconds * 1000);
        return DateTimeOffset.TryParse(retryAfter, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)
            ? Clamp((date - now).TotalMilliseconds) : null;
    }
}
