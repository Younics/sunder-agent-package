using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Services.BehaviorLoops;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class AgentRunProgressGuardTests
{
    [Fact]
    public void CanonicalArgumentsGetCorrectionBeforeInterruptionAndSuccessResetsTheStreak()
    {
        var guard = new AgentRunProgressGuard();
        var failure = new AgentToolCallOutcome(AgentToolCallOutcomeKind.Executed,
            Result: new AgentToolResult("read", "Missing", Content: "Not found", IsError: true));
        for (var index = 1; index <= 16; index++)
        {
            var call = new AgentToolCallRequest($"call-{index}", "read",
                index % 2 == 0 ? "{\"a\":1,\"b\":2}" : "{ \"b\": 2, \"a\": 1 }");
            var detected = guard.RecordToolOutcome(call, failure, out var advice);
            Assert.Equal(index is 8 or 16, detected);
            if (detected) Assert.Equal(index == 8, advice!.IsCorrection);
        }
        var request = new AgentToolCallRequest("success", "read", "{}");
        Assert.False(guard.RecordToolOutcome(request, failure with { Result = failure.Result! with { IsError = false } }, out _));
        Assert.False(guard.RecordToolOutcome(request, failure, out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChangesBeyondTheOldFingerprintPrefixOrInStructuredResultsCountAsProgress(bool structured)
    {
        var guard = new AgentRunProgressGuard();
        for (var index = 0; index < 40; index++)
        {
            var result = new AgentToolResult("read", "Retryable error", Content: new string('x', 5_000) + (structured ? "" : index),
                StructuredPayloadJson: structured ? $"{{\"revision\":{index}}}" : null, IsError: true);
            Assert.False(guard.RecordToolOutcome(new($"call-{index}", "read", "{}"),
                new(AgentToolCallOutcomeKind.Executed, Result: result), out _));
        }
    }
}
