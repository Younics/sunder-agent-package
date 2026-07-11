using System.Net;
using Sunder.Package.Agent.Provider.OpenAI.Auth;

namespace Sunder.Package.Agent.Provider.OpenAI.Transport;

internal sealed class CodexAttemptPolicy(OpenAiCodexSession session)
{
    private bool _authenticationRefreshAttempted;
    private bool _continuationFallbackAttempted;

    public OpenAiCodexSession Session { get; private set; } = session;

    public bool DisableContinuation => _continuationFallbackAttempted;

    public CodexAttemptAction Decide(
        CodexResponsesRequest request,
        HttpStatusCode statusCode,
        string responseContent)
    {
        if (!_continuationFallbackAttempted
            && request.HasPreviousResponseId
            && IsContinuationFailure(statusCode, responseContent))
        {
            _continuationFallbackAttempted = true;
            return CodexAttemptAction.RetryWithoutContinuation;
        }

        if (IsAuthenticationFailure(statusCode, responseContent))
        {
            if (_authenticationRefreshAttempted)
            {
                return CodexAttemptAction.AuthenticationRequired;
            }

            _authenticationRefreshAttempted = true;
            return CodexAttemptAction.RefreshAuthentication;
        }

        return CodexAttemptAction.ThrowHttpFailure;
    }

    public void ApplyRefreshedSession(OpenAiCodexSession session) => Session = session;

    public string BuildFailureTitle(bool toolAware)
    {
        var requestKind = toolAware ? "Codex-connected tool request" : "Codex-connected request";
        if (_continuationFallbackAttempted && _authenticationRefreshAttempted)
        {
            return $"{requestKind} failed after auth refresh and continuation fallback";
        }

        if (_continuationFallbackAttempted)
        {
            return $"{requestKind} failed after continuation fallback";
        }

        return _authenticationRefreshAttempted
            ? $"{requestKind} failed after auth refresh"
            : $"{requestKind} failed";
    }

    private static bool IsContinuationFailure(HttpStatusCode statusCode, string responseContent)
        => statusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound or HttpStatusCode.Conflict
           && responseContent.Contains("previous_response", StringComparison.OrdinalIgnoreCase);

    private static bool IsAuthenticationFailure(HttpStatusCode statusCode, string responseContent)
        => statusCode == HttpStatusCode.Unauthorized
           || statusCode == HttpStatusCode.Forbidden
           && (responseContent.Contains("token", StringComparison.OrdinalIgnoreCase)
               || responseContent.Contains("auth", StringComparison.OrdinalIgnoreCase)
               || responseContent.Contains("expired", StringComparison.OrdinalIgnoreCase)
               || responseContent.Contains("unauthorized", StringComparison.OrdinalIgnoreCase));
}

internal enum CodexAttemptAction
{
    RetryWithoutContinuation,
    RefreshAuthentication,
    AuthenticationRequired,
    ThrowHttpFailure,
}
