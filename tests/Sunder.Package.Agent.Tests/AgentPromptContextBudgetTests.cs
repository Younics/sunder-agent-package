using System.Text.Json;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class AgentPromptContextBudgetTests
{
    // The existing generated-provider case covers negative MaxChars before dispatch.
    // Cover the other budget and ensure the fix does not impose an unrelated upper cap.
    [Theory]
    [InlineData(-1, 2000, false)]
    [InlineData(0, 0, true)]
    [InlineData(int.MaxValue, int.MaxValue, true)]
    public void PromptContextPlan_ValidatesEntryBudgetAndPreservesZeroAndUncappedBudgets(
        int maxEntryCount,
        int maxChars,
        bool expectedValid)
    {
        var descriptor = CurrentPublicRpcDescriptors.Get("sunder.agent.prompt.context.contributor");
        var plan = JsonSerializer.SerializeToElement(new
        {
            intent = "lookup",
            queryText = string.Empty,
            reason = (string?)null,
            preferredCategories = (string[]?)null,
            maxEntryCount,
            maxChars,
        });

        Assert.Equal(expectedValid, descriptor.IsValid("#/$defs/AgentPromptContextPlan", plan, out _));
    }
}
