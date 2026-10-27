using System.Net;
using System.Text;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Provider.OpenAI.Transport;
using Xunit;

namespace Sunder.Package.Agent.Provider.OpenAI.Tests;

public sealed class CodexOutputLimitSafetyTests
{
    [Theory]
    [InlineData("content_filter")]
    [InlineData("unknown")]
    public async Task OnlyTheStructuredOutputTokenReasonCanContinue(string reason)
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($"data: {{\"type\":\"response.incomplete\",\"response\":{{\"id\":\"resp-1\",\"status\":\"incomplete\",\"incomplete_details\":{{\"reason\":\"{reason}\"}}}}}}\n\n",
                Encoding.UTF8, "text/event-stream"),
        };
        var exception = await Assert.ThrowsAsync<AgentChatProviderException>(async () =>
        {
            await foreach (var _ in CodexResponsesStreamParser.ParseAsync(response,
                new AgentChatClientContext("openai", "openai/gpt-5.5"), null, "resp-1", "msg-1", false, CancellationToken.None)) { }
        });
        Assert.Equal("openai-stream-incomplete", exception.ErrorCode);
    }
}
