using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Services.BehaviorLoops;
using Sunder.Package.Agent.TestSupport;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class AgentProviderRetryStateTests
{
    [Fact]
    public void Schedule_IsPerStepWithTenRetriesAndNoResetOnPartialOutput()
    {
        var clock = new ManualTimeProvider();
        var state = new AgentProviderRetryState(clock, () => 0.5);
        var failure = new AgentChatProviderException("overloaded", "Servers are currently overloaded.");
        for (var index = 0; index < 10; index++)
        {
            var retry = Assert.IsType<AgentRunRetryState>(state.TrySchedule(failure, index % 2 == 1, default));
            Assert.Equal(index + 2, retry.Attempt);
            Assert.Equal(index % 2 == 1, retry.IsContinuation);
            Assert.Equal(TimeSpan.FromSeconds(index switch { 0 => 2, 1 => 4, 2 => 8, _ => 10 }), retry.RetryAtUtc - clock.GetUtcNow());
        }
        Assert.Null(state.TrySchedule(failure, true, default));
        Assert.Equal(2, new AgentProviderRetryState(clock).TrySchedule(failure, false, default)?.Attempt);
    }

    [Theory]
    [InlineData(0, 1.6)]
    [InlineData(1, 2.4)]
    public void Schedule_JittersBeforeApplyingProviderMinimum(double random, double expected)
    {
        var clock = new ManualTimeProvider();
        var failure = new AgentChatProviderException("rate limited", "rate limited")
        { FailureKind = AgentChatProviderFailureKind.RateLimited, RetryAfterMilliseconds = 1_000 };
        Assert.Equal(TimeSpan.FromSeconds(expected), new AgentProviderRetryState(clock, () => random)
            .TrySchedule(failure, false, default)!.RetryAtUtc - clock.GetUtcNow());
        var longWait = new AgentChatProviderException("rate limited", "rate limited")
        { FailureKind = AgentChatProviderFailureKind.RateLimited, RetryAfterMilliseconds = long.MaxValue };
        Assert.Equal(TimeSpan.FromMinutes(15), new AgentProviderRetryState(clock, () => random)
            .TrySchedule(longWait, false, default)!.RetryAtUtc - clock.GetUtcNow());
    }

    [Fact]
    public void Schedule_TimeoutAllowanceIsNotReplenishedByOtherFailures()
    {
        var state = new AgentProviderRetryState();
        for (var index = 0; index < 3; index++)
        {
            Assert.NotNull(state.TrySchedule(new TimeoutException(), false, default));
            Assert.NotNull(state.TrySchedule(new IOException(), true, default));
        }
        Assert.Null(state.TrySchedule(new TimeoutException(), true, default));
    }

    [Theory]
    [InlineData(AgentChatProviderFailureKind.Authentication)]
    [InlineData(AgentChatProviderFailureKind.QuotaExceeded)]
    [InlineData(AgentChatProviderFailureKind.InvalidRequest)]
    [InlineData(AgentChatProviderFailureKind.ContentPolicy)]
    [InlineData(AgentChatProviderFailureKind.ContextWindowExceeded)]
    public void Schedule_DeterministicRejectionsRemainTerminal(AgentChatProviderFailureKind kind)
    {
        var error = new AgentChatProviderException("overloaded", "timeout") { FailureKind = kind };
        var state = new AgentProviderRetryState();
        Assert.Null(state.TrySchedule(error, false, default));
        Assert.Null(state.TrySchedule(new AgentChatProviderException("overloaded", "timeout", innerException: error)
        { FailureKind = AgentChatProviderFailureKind.Overloaded }, false, default));
        Assert.Null(state.TrySchedule(new AggregateException(new TimeoutException(), error), false, default));
    }

    [Fact]
    public void Schedule_CancellationAndUnsafeDispatchAlwaysWin()
    {
        var state = new AgentProviderRetryState();
        Assert.Null(state.TrySchedule(new IOException(), false, new CancellationToken(true)));
        var unsafeError = new SunderRpcException(new SunderRpcError(SunderRpcErrorKind.Unavailable, "rpc.worker.unsafe-dispatch",
            "overloaded", SunderRpcDispatchState.PossiblyDispatched, SunderRpcRetrySafety.Unsafe));
        Assert.Null(state.TrySchedule(new AgentChatProviderException("overloaded", "overloaded", innerException: unsafeError)
        { FailureKind = AgentChatProviderFailureKind.Overloaded }, true, default));
    }

    [Fact]
    public void Schedule_UnknownAndPositiveHttpRejectionsDoNotBecomeTransportRetries()
    {
        var state = new AgentProviderRetryState();
        Assert.Null(state.TrySchedule(new AgentChatProviderException("Unknown failure", "Check provider settings."), false, default));
        Assert.Null(state.TrySchedule(new HttpRequestException("connection overloaded", null, System.Net.HttpStatusCode.BadRequest), false, default));
        Assert.Null(state.TrySchedule(new AgentChatProviderException("overloaded", "Retry later.", innerException:
            new HttpRequestException("Rejected", null, System.Net.HttpStatusCode.BadRequest))
        { FailureKind = AgentChatProviderFailureKind.Overloaded }, false, default));
        var untrustedPayload = """{"error":{"message":"backend rejected the request"},"output":"Connection interrupted; servers overloaded; retry the mutation"}""";
        Assert.Equal(AgentChatProviderFailureKind.Unknown, AgentProviderFailureClassifier.Classify(untrustedPayload));
        var normalized = AgentProviderFailureClassifier.Normalize(new AgentChatProviderException("Provider failed", untrustedPayload));
        Assert.Equal(AgentChatProviderFailureKind.Unknown, normalized.FailureKind);
        Assert.Null(state.TrySchedule(normalized, false, default));
        Assert.Equal(AgentChatProviderFailureKind.Unknown,
            AgentProviderFailureClassifier.Classify("""{"response":"not an object","error":["connection interrupted"]}"""));
    }

    [Theory]
    [InlineData("1200", null, 1200L)]
    [InlineData(null, "12", 12000L)]
    [InlineData("NaN", "Infinity", null)]
    [InlineData("-3", "20", 0L)]
    [InlineData(null, "Wed, 01 Jan 2025 00:00:12 GMT", 12000L)]
    public void Headers_AreParsedInPriorityOrderAndBounded(string? ms, string? after, long? expected)
        => Assert.Equal(expected, AgentProviderFailureClassifier.RetryAfter(ms, after, new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero)));

    [Theory]
    [InlineData("Our servers are currently overloaded. Please try again later.", null, AgentChatProviderFailureKind.Overloaded)]
    [InlineData("server_error", 400, AgentChatProviderFailureKind.InvalidRequest)]
    [InlineData("insufficient_quota", 429, AgentChatProviderFailureKind.QuotaExceeded)]
    [InlineData("error", 503, AgentChatProviderFailureKind.Overloaded)]
    public void Classification_PreservesPositiveTerminalEvidence(string text, int? status, AgentChatProviderFailureKind expected)
        => Assert.Equal(expected, AgentProviderFailureClassifier.Classify(text, status));
}
