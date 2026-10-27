using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Shared.PackageViews;
using Xunit;

namespace Sunder.Package.Agent.Presentation.State.Tests;

public sealed class RunActivityStateTests
{
    [Fact]
    public void Status_StaysAtLatestAndRetainsReasoningAcrossToolsAndRetry()
    {
        var active = true;
        var following = true;
        using var state = new AgentRunActivityState(() => active, () => following);
        state.TrackStatus(1, 1, "Inspecting the request.", true, null, null);
        Assert.True(state.ShouldShow);
        state.TrackStatus(1, 2, "Running tools", false, "Inspecting the request.", null);
        Assert.True(state.ShouldShow);
        Assert.Equal("Inspecting the request.", state.ReasoningText);
        var deadline = new AgentRunRetryState(2, DateTimeOffset.UtcNow.AddSeconds(12), "Servers overloaded", true);
        state.TrackStatus(1, 3, deadline.Reason, false, null, deadline);
        Assert.Same(deadline, state.Retry);
        state.TrackStatus(1, 2, "old status", false, "old reasoning", null);
        Assert.Same(deadline, state.Retry);
        Assert.Equal("Inspecting the request.", state.ReasoningText);
        following = false;
        Assert.False(state.ShouldShow);
        state.TrackStatus(1, 4, "Responding", false, null, null);
        following = true;
        Assert.True(state.ShouldShow);
        Assert.Equal("Responding", state.Text);
        Assert.Null(state.Retry);
        active = false;
        state.NotifyRunStateChanged();
        Assert.False(state.ShouldShow);
        state.Reset();
        Assert.Empty(state.ReasoningText);
    }

    [Theory]
    [InlineData(12.1, "13s")]
    [InlineData(0.1, "1s")]
    [InlineData(0, "now")]
    [InlineData(-4, "now")]
    public void Countdown_IsDerivedFromDeadlineRatherThanTickCount(double remaining, string expected)
    {
        var now = DateTimeOffset.UtcNow;
        var retry = new AgentRunRetryState(4, now.AddSeconds(remaining), "Servers overloaded", false);
        var text = RunActivityText.FormatRetry(retry, now);
        Assert.Contains(expected, text);
        Assert.Contains("attempt 4", text);
        Assert.DoesNotContain("-4", text);
    }
}
