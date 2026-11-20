using Sunder.Package.Agent.Contracts.Models;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class AgentChatModelSettingsJsonTests
{
    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"reasoningVariantId\":42}")]
    [InlineData("{\"futureSetting\":true}")]
    [InlineData("{malformed")]
    public void TryParse_DistinguishesMalformedSettingsFromDefault(string json)
    {
        Assert.False(AgentChatModelSettingsJson.TryParse(json, out _));
        Assert.True(AgentChatModelSettingsJson.TryParse(null, out _));
        Assert.True(AgentChatModelSettingsJson.TryParse("{}", out _));
    }

    [Fact]
    public void SerializeAndParse_PreservesSpeedReasoningAndMode()
    {
        var settings = new AgentChatModelSettings("high", "fast", "pro");

        var json = AgentChatModelSettingsJson.Serialize(settings);
        var parsed = AgentChatModelSettingsJson.Parse(json);

        Assert.Equal("high", parsed.ReasoningVariantId);
        Assert.Equal("fast", parsed.SpeedOptionId);
        Assert.Equal("pro", parsed.ModeOptionId);
    }
}
