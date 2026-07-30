using System.Text.Json;
using Google.GenAI;
using Google.GenAI.Types;
using Microsoft.Extensions.AI;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Provider.Shared;
using Sunder.Package.Agent.Provider.TestSupport;
using Sunder.Package.Agent.Provider.Gemini;
using Sunder.Sdk.Logging;
using Xunit;
using AIChatMessage = Microsoft.Extensions.AI.ChatMessage;
using AIChatRole = Microsoft.Extensions.AI.ChatRole;

namespace Sunder.Package.Agent.Provider.Gemini.Tests;

public sealed class GeminiChatClientWireTests
{
    private const string StreamResponse =
        "data: {\"responseId\":\"response-1\",\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"ok\"}]},\"finishReason\":\"STOP\"}]}\n\n";

    [Fact]
    public async Task StreamingRequest_PreservesWireRolesMixedContentAndToolResultNames()
    {
        await using var server = new GeminiWireServer(StreamResponse, "text/event-stream");
        using var client = CreateClient(server, modelId: "gemini/gemini-3-flash-preview");
        AIChatMessage[] messages =
        [
            new(AIChatRole.System, "System message"),
            new(AIChatRole.User,
            [
                new TextContent("before"),
                new DataContent(new byte[] { 1 }, "image/png"),
                new TextContent("after"),
                new DataContent(new byte[] { 2 }, "application/pdf"),
                new DataContent(new byte[] { 3 }, "audio/mpeg"),
                new DataContent(new byte[] { 4 }, "video/mp4"),
            ]),
            new(AIChatRole.Assistant,
            [
                new TextContent("calling"),
                new FunctionCallContent("call-1", "read_file", new Dictionary<string, object?>
                {
                    ["path"] = "README.md",
                }),
            ]),
            new(AIChatRole.Tool,
            [
                new TextContent("result follows"),
                new FunctionResultContent("call-1", "file contents"),
            ]),
        ];

        var updates = await ReadUpdatesAsync(
            client,
            messages,
            new ChatOptions { Instructions = "Option instruction" });
        var request = await server.Request;
        using var document = JsonDocument.Parse(request.Body);
        var root = document.RootElement;

        Assert.Equal("ok", Assert.Single(updates).Text);
        Assert.Contains(":streamGenerateContent", request.Path, StringComparison.Ordinal);
        Assert.Equal(
            ["Option instruction", "System message"],
            root.GetProperty("systemInstruction").GetProperty("parts")
                .EnumerateArray().Select(part => part.GetProperty("text").GetString()));
        var contents = root.GetProperty("contents").EnumerateArray().ToArray();
        Assert.Equal(["user", "model", "user"], contents.Select(content => content.GetProperty("role").GetString()));

        var userParts = contents[0].GetProperty("parts").EnumerateArray().ToArray();
        Assert.Equal("before", userParts[0].GetProperty("text").GetString());
        Assert.Equal("image/png", userParts[1].GetProperty("inlineData").GetProperty("mimeType").GetString());
        Assert.Equal("after", userParts[2].GetProperty("text").GetString());
        Assert.Equal("application/pdf", userParts[3].GetProperty("inlineData").GetProperty("mimeType").GetString());
        Assert.Equal("audio/mpeg", userParts[4].GetProperty("inlineData").GetProperty("mimeType").GetString());
        Assert.Equal("video/mp4", userParts[5].GetProperty("inlineData").GetProperty("mimeType").GetString());

        var functionCall = contents[1].GetProperty("parts")[1].GetProperty("functionCall");
        Assert.Equal("call-1", functionCall.GetProperty("id").GetString());
        Assert.Equal("read_file", functionCall.GetProperty("name").GetString());
        Assert.Equal("README.md", functionCall.GetProperty("args").GetProperty("path").GetString());
        var functionResponse = contents[2].GetProperty("parts")[1].GetProperty("functionResponse");
        Assert.Equal("call-1", functionResponse.GetProperty("id").GetString());
        Assert.Equal("read_file", functionResponse.GetProperty("name").GetString());
        Assert.Equal("file contents", functionResponse.GetProperty("response").GetProperty("output").GetString());
    }

    [Fact]
    public async Task StreamingRequest_MapsMaxOutputTokensAndReasoningOnWire()
    {
        await using var server = new GeminiWireServer(StreamResponse, "text/event-stream");
        using var client = CreateClient(server, modelId: "gemini/gemini-3-flash-preview");

        await ReadUpdatesAsync(
            client,
            [new AIChatMessage(AIChatRole.User, "answer")],
            new ChatOptions
            {
                MaxOutputTokens = 321,
                Reasoning = new ReasoningOptions
                {
                    Effort = ReasoningEffort.Medium,
                    Output = ReasoningOutput.Summary,
                },
            });
        using var document = JsonDocument.Parse((await server.Request).Body);
        var generationConfig = document.RootElement.GetProperty("generationConfig");

        Assert.Equal(321, generationConfig.GetProperty("maxOutputTokens").GetInt32());
        Assert.Equal("MEDIUM", generationConfig.GetProperty("thinkingConfig").GetProperty("thinkingLevel").GetString());
        Assert.True(generationConfig.GetProperty("thinkingConfig").GetProperty("includeThoughts").GetBoolean());
    }

    [Fact]
    public async Task ToolCompletion_MapsReasoningAndFunctionCallResponse()
    {
        const string response = """
            {"responseId":"response-tool","candidates":[{"content":{"role":"model","parts":[
              {"thought":true,"text":"Checking.","thoughtSignature":"AQI="},
              {"functionCall":{"id":"call-9","name":"read_file","args":{"path":"README.md"}}}
            ]},"finishReason":"STOP"}]}
            """;
        await using var server = new GeminiWireServer(response);
        using var client = CreateClient(server);
        using var schema = JsonDocument.Parse("""{"type":"object","properties":{"path":{"type":"string"}}}""");
        var tool = AIFunctionFactory.CreateDeclaration("read_file", "Reads a file", schema.RootElement);

        var updates = await ReadUpdatesAsync(
            client,
            [new AIChatMessage(AIChatRole.User, "Read the file")],
            new ChatOptions { Tools = [tool], AllowMultipleToolCalls = true });
        var request = await server.Request;

        Assert.Contains(":generateContent", request.Path, StringComparison.Ordinal);
        Assert.DoesNotContain(":streamGenerateContent", request.Path, StringComparison.Ordinal);
        var contents = Assert.Single(updates).Contents;
        var reasoning = Assert.IsType<TextReasoningContent>(contents[0]);
        Assert.Equal("Checking.", reasoning.Text);
        Assert.Equal("AQI=", reasoning.ProtectedData);
        var functionCall = Assert.IsType<FunctionCallContent>(contents[1]);
        Assert.Equal("call-9", functionCall.CallId);
        Assert.Equal("read_file", functionCall.Name);
        Assert.Equal("README.md", Assert.IsType<JsonElement>(functionCall.Arguments!["path"]).GetString());
    }

    [Fact]
    public async Task StreamingRequest_PropagatesCancellationAndRecordsTelemetry()
    {
        await using var server = new GeminiWireServer(hangAfterRequest: true);
        var logger = new RecordingEventLogger();
        using var client = CreateClient(server, logger);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ReadUpdatesAsync(
                client,
                [new AIChatMessage(AIChatRole.User, "wait")],
                cancellationToken: cancellation.Token));

        Assert.Contains(logger.Events, entry => entry.EventName == "provider.stream.canceled");
    }

    [Fact]
    public async Task StreamingRequest_MapsHttpErrorsAndRecordsTelemetry()
    {
        await using var server = new GeminiWireServer(
            """{"error":{"code":400,"message":"bad request","status":"INVALID_ARGUMENT"}}""",
            statusCode: 400);
        var logger = new RecordingEventLogger();
        using var client = CreateClient(server, logger);

        var exception = await Assert.ThrowsAsync<AgentChatProviderException>(
            () => ReadUpdatesAsync(client, [new AIChatMessage(AIChatRole.User, "fail")]));

        Assert.Equal("gemini-http-error", exception.ErrorCode);
        Assert.Equal(AgentChatProviderFailureKind.Unknown, exception.FailureKind);
        Assert.Contains(logger.Events, entry => entry.EventName == "provider.stream.failed");
    }

    [Fact]
    public async Task StreamingRequest_ContextWindowFailureIsNormalized()
    {
        await using var server = new GeminiWireServer(
            """{"error":{"code":400,"message":"The input token count (1048577) exceeds the maximum number of tokens allowed (1048576).","status":"INVALID_ARGUMENT"}}""",
            statusCode: 400);
        using var client = CreateClient(server);

        var exception = await Assert.ThrowsAsync<AgentChatProviderException>(
            () => ReadUpdatesAsync(client, [new AIChatMessage(AIChatRole.User, "fail")]));

        Assert.Equal(AgentChatProviderFailureKind.ContextWindowExceeded, exception.FailureKind);
    }

    [Theory]
    [InlineData("Gemini returned HTTP 400: bad request")]
    [InlineData("max_output_tokens exceeds the supported value")]
    [InlineData("The output length limit was reached")]
    [InlineData("Quota exceeded for GenerateContent requests")]
    [InlineData("Request payload is too large")]
    public void ExceptionMapper_DoesNotClassifyAmbiguousFailures(string message)
    {
        var exception = GeminiExceptionMapper.Request(new ClientError(message, 400, "INVALID_ARGUMENT"));

        Assert.Equal(AgentChatProviderFailureKind.Unknown, exception.FailureKind);
    }

    [Fact]
    public async Task StreamingRequest_PrematureEofIsFailure()
    {
        const string response =
            "data: {\"responseId\":\"response-1\",\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"partial\"}]}}]}\n\n";
        await using var server = new GeminiWireServer(response, "text/event-stream");
        using var client = CreateClient(server);

        var exception = await Assert.ThrowsAsync<AgentChatProviderException>(() => ReadUpdatesAsync(
            client,
            [new AIChatMessage(AIChatRole.User, "continue")]));

        Assert.Equal("gemini-incomplete-response", exception.ErrorCode);
    }

    [Theory]
    [InlineData("MAX_TOKENS")]
    [InlineData("SAFETY")]
    [InlineData("MALFORMED_FUNCTION_CALL")]
    public async Task StreamingRequest_NonSuccessFinishReasonsAreFailures(string finishReason)
    {
        var response = $"data: {{\"candidates\":[{{\"finishReason\":\"{finishReason}\"}}]}}\n\n";
        await using var server = new GeminiWireServer(response, "text/event-stream");
        using var client = CreateClient(server);

        var exception = await Assert.ThrowsAsync<AgentChatProviderException>(() => ReadUpdatesAsync(
            client,
            [new AIChatMessage(AIChatRole.User, "continue")]));

        Assert.Equal("gemini-incomplete-response", exception.ErrorCode);
    }

    [Fact]
    public async Task StreamingRequest_ProviderTimeoutIsNotReportedAsCallerCancellation()
    {
        await using var server = new GeminiWireServer(hangAfterRequest: true);
        using var client = new GeminiChatClient(
            new AgentChatClientContext("gemini", "gemini/gemini-test"),
            new ProviderCredentialAccessor(
                new ProviderTestSecrets(new Dictionary<string, string>
                {
                    [GeminiProviderConfiguration.ApiKeySecretKey] = "fixed-key",
                }),
                GeminiProviderConfiguration.ApiKeySecretKey),
            apiKey => new Client(
                apiKey: apiKey,
                httpOptions: new HttpOptions { BaseUrl = server.BaseUrl, Timeout = 50 }));

        var exception = await Assert.ThrowsAsync<AgentChatProviderException>(() => ReadUpdatesAsync(
            client,
            [new AIChatMessage(AIChatRole.User, "wait")]));

        Assert.Equal("gemini-timeout", exception.ErrorCode);
    }

    private static GeminiChatClient CreateClient(
        GeminiWireServer server,
        IPackageEventLogger? eventLogger = null,
        string modelId = "gemini/gemini-test")
        => new(
            new AgentChatClientContext("gemini", modelId, eventLogger),
            new ProviderCredentialAccessor(
                new ProviderTestSecrets(new Dictionary<string, string>
                {
                    [GeminiProviderConfiguration.ApiKeySecretKey] = "fixed-key",
                }),
                GeminiProviderConfiguration.ApiKeySecretKey),
            apiKey => new Client(
                apiKey: apiKey,
                httpOptions: new HttpOptions { BaseUrl = server.BaseUrl }));

    private static async Task<IReadOnlyList<ChatResponseUpdate>> ReadUpdatesAsync(
        IChatClient client,
        IEnumerable<AIChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync(messages, options, cancellationToken))
        {
            updates.Add(update);
        }

        return updates;
    }

    private sealed class RecordingEventLogger : IPackageEventLogger
    {
        public List<(PackageLogLevel Level, string EventName)> Events { get; } = [];

        public ValueTask WriteAsync(
            PackageLogLevel level,
            string eventName,
            string message,
            IReadOnlyDictionary<string, object?>? attributes = null,
            Exception? exception = null,
            CancellationToken cancellationToken = default)
        {
            Events.Add((level, eventName));
            return ValueTask.CompletedTask;
        }
    }
}
