namespace Sunder.Package.Agent.Provider.Shared;

internal static class ProviderContextWindowFailureClassifier
{
    public static bool IsOpenAi(Exception exception)
        => Matches(exception, IsOpenAi);

    public static bool IsOpenAi(string? value)
        => IsExplicitContextOverflow(value)
           || !IsExcludedFailure(value)
           && (Contains(value, "input is too long for this model")
               || Contains(value, "input is too long for the requested model"));

    public static bool IsAnthropic(Exception exception)
        => Matches(exception, IsAnthropic);

    public static bool IsAnthropic(string? value)
        => IsExplicitContextOverflow(value)
           || !IsExcludedFailure(value)
           && ContainsAll(value, "prompt is too long", "token");

    public static bool IsGemini(Exception exception)
        => Matches(exception, IsGemini);

    public static bool IsGemini(string? value)
        => IsExplicitContextOverflow(value)
           || !IsExcludedFailure(value)
           && (ContainsAll(value, "input token count", "exceed", "maximum number of tokens allowed")
               || ContainsAll(value, "number of input tokens", "exceed", "maximum input token"));

    public static bool IsLmStudio(Exception exception)
        => Matches(exception, IsLmStudio);

    public static bool IsLmStudio(string? value)
        => IsExplicitContextOverflow(value)
           || !IsExcludedFailure(value)
           && (Contains(value, "input is too long for this model")
               || Contains(value, "input is too long for the requested model"));

    private static bool IsExplicitContextOverflow(string? value)
    {
        if (Contains(value, "context_length_exceeded"))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(value) || IsExcludedFailure(value))
        {
            return false;
        }

        var namesContextBoundary = Contains(value, "context window")
                                   || Contains(value, "context length")
                                   || Contains(value, "context limit")
                                   || Contains(value, "context size");
        if (!namesContextBoundary)
        {
            return false;
        }

        return Contains(value, "exceed")
               || Contains(value, "too long")
               || Contains(value, "too many token")
               || Contains(value, "greater than")
               || Contains(value, "out of room")
               || Contains(value, "overflow")
               || Contains(value, "is full")
               || Contains(value, "limit reached")
               || ContainsAll(value, "maximum context length", "resulted in")
               || ContainsAll(value, "maximum context length", "requested")
               || ContainsAll(value, "maximum context length", "provided");
    }

    private static bool IsExcludedFailure(string? value)
        => Contains(value, "max_output_tokens")
           || Contains(value, "output length")
           || Contains(value, "output token")
           || Contains(value, "insufficient_quota")
           || Contains(value, "quota")
           || Contains(value, "request_too_large")
           || Contains(value, "request too large")
           || Contains(value, "payload too large");

    private static bool Matches(Exception exception, Func<string?, bool> classifier)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (classifier(current.Message))
            {
                return true;
            }
        }

        return false;
    }

    private static bool Contains(string? value, string signal)
        => value?.Contains(signal, StringComparison.OrdinalIgnoreCase) == true;

    private static bool ContainsAll(string? value, params string[] signals)
        => !string.IsNullOrWhiteSpace(value)
           && signals.All(signal => value.Contains(signal, StringComparison.OrdinalIgnoreCase));
}
