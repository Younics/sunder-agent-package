namespace Sunder.Package.Agent.Provider.TestSupport;

public enum ProviderFailureClassification
{
    Unknown,
    ContextWindowExceeded,
}

public sealed record ProviderFailureConformanceAdapter(
    string ProviderName,
    Func<Exception, ProviderFailureClassification> Classify);

public sealed record ProviderConformanceAdapter(
    string ProviderName,
    Func<Task> FailureClassification,
    Func<Task> PrematureEof,
    Func<Task> CallerCancellation,
    Func<Task> ProviderTimeout,
    Func<Task> Readiness,
    Func<Task> ToolResultOrder,
    Func<Task> TelemetryAndRedaction);

public sealed record ProviderConformanceObservation(
    string ProviderName,
    ProviderFailureClassification ContextWindowClassification,
    ProviderFailureClassification AmbiguousClassification,
    string PrematureEofErrorCode,
    string ProviderTimeoutErrorCode,
    bool CallerCancellationPropagated,
    IReadOnlyList<string> ReadinessTransitions,
    IReadOnlyList<string> ToolResultOrder,
    IReadOnlyList<string> TelemetryEvents,
    string TelemetryText,
    string SecretCanary,
    string RequestCanary);

public sealed class ProviderConformanceException(string message) : Exception(message);

public static class ProviderConformanceHarness
{
    private static readonly string[] ExpectedToolResultOrder = ["text", "tool", "text"];

    public static async Task VerifyAsync(ProviderConformanceAdapter adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentException.ThrowIfNullOrWhiteSpace(adapter.ProviderName);
        var operations = new (string Name, Func<Task> Execute)[]
        {
            ("failure classification", adapter.FailureClassification),
            ("premature EOF", adapter.PrematureEof),
            ("caller cancellation", adapter.CallerCancellation),
            ("provider timeout", adapter.ProviderTimeout),
            ("readiness", adapter.Readiness),
            ("tool result order", adapter.ToolResultOrder),
            ("telemetry and redaction", adapter.TelemetryAndRedaction),
        };
        foreach (var operation in operations)
        {
            ArgumentNullException.ThrowIfNull(operation.Execute);
            try
            {
                await operation.Execute().ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not ProviderConformanceException)
            {
                throw new ProviderConformanceException(
                    $"Provider '{adapter.ProviderName}' failed its real {operation.Name} conformance path: "
                    + exception.Message);
            }
        }
    }

    public static void VerifyFailureClassification(ProviderFailureConformanceAdapter adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentException.ThrowIfNullOrWhiteSpace(adapter.ProviderName);
        if (!ProviderFailureInputs.ExplicitContextWindowFailures.TryGetValue(
                adapter.ProviderName,
                out var explicitFailure))
        {
            throw new ProviderConformanceException(
                $"Provider '{adapter.ProviderName}' has no common context-window failure input.");
        }

        Require(
            adapter.ProviderName,
            adapter.Classify(new InvalidOperationException(explicitFailure))
                == ProviderFailureClassification.ContextWindowExceeded,
            "explicit context-window overflow was not normalized");
        Require(
            adapter.ProviderName,
            adapter.Classify(new InvalidOperationException(
                "outer provider failure",
                new InvalidOperationException(explicitFailure)))
                == ProviderFailureClassification.ContextWindowExceeded,
            "nested context-window overflow was not normalized");

        foreach (var ambiguousFailure in ProviderFailureInputs.AmbiguousFailures)
        {
            Require(
                adapter.ProviderName,
                adapter.Classify(new InvalidOperationException(ambiguousFailure))
                    == ProviderFailureClassification.Unknown,
                $"ambiguous failure was over-classified: {ambiguousFailure}");
        }
    }

    public static void VerifyPrematureEof(string providerName, string? errorCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
        Require(providerName, ExpectedPrematureEofCodes.TryGetValue(providerName, out var expected),
            "no exact premature EOF code is declared");
        Require(providerName, string.Equals(errorCode, expected, StringComparison.Ordinal),
            $"premature EOF code '{errorCode}' does not equal '{expected}'");
    }

    public static void VerifyProviderTimeout(string providerName, string? errorCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
        Require(providerName, ExpectedProviderTimeoutCodes.TryGetValue(providerName, out var expected),
            "no exact provider timeout code is declared");
        Require(providerName, string.Equals(errorCode, expected, StringComparison.Ordinal),
            $"provider timeout code '{errorCode}' does not equal '{expected}'");
    }

    public static void VerifyCallerCancellation(
        string providerName,
        Exception exception,
        CancellationToken callerCancellation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
        ArgumentNullException.ThrowIfNull(exception);
        Require(providerName, callerCancellation.IsCancellationRequested, "caller token was not canceled");
        Require(
            providerName,
            exception is OperationCanceledException canceled
            && canceled.CancellationToken == callerCancellation,
            $"caller cancellation was mapped to {exception.GetType().Name}");
    }

    public static void VerifyReadinessTransitions(
        string providerName,
        IEnumerable<string> readinessTransitions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
        ArgumentNullException.ThrowIfNull(readinessTransitions);
        var observed = readinessTransitions.ToArray();
        Require(
            providerName,
            HasExpectedReadinessTransition(observed),
            "readiness did not transition from NeedsConfiguration to Ready: "
            + string.Join(",", observed));
    }

    public static void VerifyToolResultOrder(string providerName, IEnumerable<string> resultOrder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
        ArgumentNullException.ThrowIfNull(resultOrder);
        var observed = resultOrder.ToArray();
        Require(
            providerName,
            observed.SequenceEqual(ExpectedToolResultOrder, StringComparer.Ordinal),
            "tool/text result order changed: " + string.Join(",", observed));
    }

    public static void VerifyTelemetryAndRedaction(
        string providerName,
        IEnumerable<string> eventNames,
        string telemetryText,
        string secretCanary,
        string requestCanary)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
        ArgumentNullException.ThrowIfNull(eventNames);
        ArgumentNullException.ThrowIfNull(telemetryText);
        ArgumentException.ThrowIfNullOrWhiteSpace(secretCanary);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestCanary);
        var events = eventNames.ToHashSet(StringComparer.Ordinal);
        foreach (var requiredEvent in RequiredTelemetryEvents)
        {
            Require(providerName, events.Contains(requiredEvent), $"telemetry event '{requiredEvent}' is missing");
        }
        Require(
            providerName,
            !telemetryText.Contains(secretCanary, StringComparison.Ordinal),
            "telemetry exposed the secret canary");
        Require(
            providerName,
            !telemetryText.Contains(requestCanary, StringComparison.Ordinal),
            "telemetry exposed the request canary");
    }

    public static void Verify(ProviderConformanceObservation observation)
    {
        var violations = Validate(observation);
        if (violations.Count != 0)
        {
            throw new ProviderConformanceException(
                $"Provider '{observation.ProviderName}' violated common conformance: "
                + string.Join("; ", violations));
        }
    }

    public static IReadOnlyList<string> Validate(ProviderConformanceObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        var violations = new List<string>();
        if (string.IsNullOrWhiteSpace(observation.ProviderName))
        {
            violations.Add("provider name is empty");
        }
        if (observation.ContextWindowClassification != ProviderFailureClassification.ContextWindowExceeded)
        {
            violations.Add("explicit context-window overflow was not normalized");
        }
        if (observation.AmbiguousClassification != ProviderFailureClassification.Unknown)
        {
            violations.Add("ambiguous failure was over-classified");
        }
        if (!ExpectedPrematureEofCodes.TryGetValue(observation.ProviderName, out var expectedEof)
            || !string.Equals(observation.PrematureEofErrorCode, expectedEof, StringComparison.Ordinal))
        {
            violations.Add($"premature EOF code '{observation.PrematureEofErrorCode}' is not terminal");
        }
        if (!ExpectedProviderTimeoutCodes.TryGetValue(observation.ProviderName, out var expectedTimeout)
            || !string.Equals(observation.ProviderTimeoutErrorCode, expectedTimeout, StringComparison.Ordinal))
        {
            violations.Add($"provider timeout code '{observation.ProviderTimeoutErrorCode}' is not distinct");
        }
        if (!observation.CallerCancellationPropagated)
        {
            violations.Add("caller cancellation was mapped to a provider failure");
        }
        if (!HasExpectedReadinessTransition(observation.ReadinessTransitions))
        {
            violations.Add(
                "readiness did not transition from NeedsConfiguration to Ready: "
                + string.Join(",", observation.ReadinessTransitions));
        }
        if (!observation.ToolResultOrder.SequenceEqual(ExpectedToolResultOrder, StringComparer.Ordinal))
        {
            violations.Add("tool/text result order changed: " + string.Join(",", observation.ToolResultOrder));
        }
        foreach (var requiredEvent in RequiredTelemetryEvents)
        {
            if (!observation.TelemetryEvents.Contains(requiredEvent, StringComparer.Ordinal))
            {
                violations.Add($"telemetry event '{requiredEvent}' is missing");
            }
        }
        if (string.IsNullOrWhiteSpace(observation.SecretCanary))
        {
            violations.Add("secret-redaction canary is empty");
        }
        else if (observation.TelemetryText.Contains(observation.SecretCanary, StringComparison.Ordinal))
        {
            violations.Add("telemetry exposed the secret canary");
        }
        if (string.IsNullOrWhiteSpace(observation.RequestCanary))
        {
            violations.Add("request-redaction canary is empty");
        }
        else if (observation.TelemetryText.Contains(observation.RequestCanary, StringComparison.Ordinal))
        {
            violations.Add("telemetry exposed the request canary");
        }
        return violations;
    }

    private static bool HasExpectedReadinessTransition(IReadOnlyList<string> transitions)
        => transitions.Count >= 2
           && string.Equals(transitions[0], "NeedsConfiguration", StringComparison.Ordinal)
           && string.Equals(transitions[^1], "Ready", StringComparison.Ordinal);

    private static readonly string[] RequiredTelemetryEvents =
    [
        "provider.request.start",
        "provider.stream.start",
        "provider.stream.first_event",
        "provider.stream.completed",
    ];

    private static readonly IReadOnlyDictionary<string, string> ExpectedPrematureEofCodes =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Anthropic"] = "anthropic-incomplete-response",
            ["Gemini"] = "gemini-incomplete-response",
            ["LM Studio"] = "lmstudio-incomplete-response",
            ["OpenAI"] = "openai-response-failed",
        };

    private static readonly IReadOnlyDictionary<string, string> ExpectedProviderTimeoutCodes =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Anthropic"] = "anthropic-timeout",
            ["Gemini"] = "gemini-timeout",
            ["LM Studio"] = "lmstudio-timeout",
            ["OpenAI"] = "codex-timeout",
        };

    private static void Require(string providerName, bool condition, string violation)
    {
        if (!condition)
        {
            throw new ProviderConformanceException(
                $"Provider '{providerName}' violated common conformance: {violation}.");
        }
    }
}

public static class ProviderFailureInputs
{
    public const string SecretCanary = "provider-conformance-secret-canary";
    public const string RequestCanary = "provider-conformance-request-canary";

    public static IReadOnlyDictionary<string, string> ExplicitContextWindowFailures { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Anthropic"] = "prompt is too long: 200001 tokens > 200000 maximum",
            ["Gemini"] = "The input token count (1048577) exceeds the maximum number of tokens allowed (1048576).",
            ["LM Studio"] = "context_length_exceeded: maximum context length is 4096 tokens",
            ["OpenAI"] = "context_length_exceeded: input exceeds the model context window",
        };

    public static IReadOnlyList<string> AmbiguousFailures { get; } =
    [
        "HTTP 400: bad request",
        "max_output_tokens exceeds the supported value",
        "The output length limit was reached",
        "Insufficient quota",
        "Request payload is too large",
    ];
}
