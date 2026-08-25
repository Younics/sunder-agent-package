using Sunder.Package.Agent.Provider.TestSupport;
using Xunit;

namespace Sunder.Package.Agent.Provider.OpenAI.Tests;

public sealed class ProviderConformanceHarnessMutationTests
{
    [Fact]
    public void CommonValidator_RejectsEveryControlledContractMutation()
    {
        var valid = new ProviderConformanceObservation(
            "OpenAI",
            ProviderFailureClassification.ContextWindowExceeded,
            ProviderFailureClassification.Unknown,
            "openai-response-failed",
            "codex-timeout",
            CallerCancellationPropagated: true,
            ["NeedsConfiguration", "Ready"],
            ["text", "tool", "text"],
            [
                "provider.request.start",
                "provider.stream.start",
                "provider.stream.first_event",
                "provider.stream.completed",
            ],
            "redacted telemetry",
            ProviderFailureInputs.SecretCanary,
            ProviderFailureInputs.RequestCanary);
        Assert.Empty(ProviderConformanceHarness.Validate(valid));

        ProviderConformanceObservation[] mutations =
        [
            valid with { ContextWindowClassification = ProviderFailureClassification.Unknown },
            valid with { AmbiguousClassification = ProviderFailureClassification.ContextWindowExceeded },
            valid with { PrematureEofErrorCode = "openai-incomplete-ish" },
            valid with { ProviderTimeoutErrorCode = "openai-timeout-ish" },
            valid with { CallerCancellationPropagated = false },
            valid with { ReadinessTransitions = ["Ready", "NeedsConfiguration"] },
            valid with { ToolResultOrder = ["tool", "text", "text"] },
            valid with { TelemetryEvents = ["provider.request.start"] },
            valid with { TelemetryText = ProviderFailureInputs.SecretCanary },
            valid with { TelemetryText = ProviderFailureInputs.RequestCanary },
        ];

        Assert.All(mutations, mutation => Assert.NotEmpty(ProviderConformanceHarness.Validate(mutation)));
    }
}
