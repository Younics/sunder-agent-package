extern alias WebWorker;

using WebWorker::Sunder.Package.Agent.Tools.Web;
using WebWorker::Sunder.Package.Agent.Tools.Web.Services;
using WebModels = WebWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Models;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class WebFetchContractTests
{
    [Fact]
    public void InvalidRequestPresentation_UsesSharedMarkdownShape()
    {
        var detail = new WebFetchTool(new WebFetchService()).ResolveToolPresentation(new WebModels.AgentToolPresentationRequest
        {
            ToolId = "web_fetch", ArgumentsJson = "{", IsError = false,
        })!.DetailMarkdown!;
        Assert.StartsWith("**Request**", detail, StringComparison.Ordinal);
        Assert.Contains("```json\n{\n```", detail.ReplaceLineEndings("\n"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(121)]
    public async Task WebFetch_RejectsOutOfRangeTimeoutBeforeNetworkCall(int timeoutSeconds)
    {
        var result = await new WebFetchTool(new WebFetchService()).ExecuteAsync(
            new WebModels.AgentToolExecutionContext
            {
                SessionId = null, ProfileId = null, Workspace = null, ExecutionBinding = null,
                AllowOutsideConfiguredScope = false, RunId = null, RunRevision = null, UserTurnId = null,
                ToolCallId = null, TranscriptEpoch = 0, ApprovedResourceReferences = [],
                ApprovedResourceClaims = [], ApprovedResourceCapabilities = [],
            },
            new WebModels.AgentToolRequest
            {
                ToolId = "web_fetch",
                ArgumentsJson = $$"""{"url":"https://example.com","timeoutSeconds":{{timeoutSeconds}}}""",
            });
        Assert.True(result.IsError);
        Assert.Equal("web-fetch-args", result.ErrorCode);
    }
}
