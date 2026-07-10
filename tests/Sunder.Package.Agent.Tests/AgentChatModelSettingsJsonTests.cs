using Sunder.Package.Agent.Contracts.Models;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class AgentChatModelSettingsJsonTests
{
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
