using System.Text;
using System.Text.Json;
using System.Net;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAI.Responses;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Provider.OpenAI.Auth;
using Sunder.Package.Agent.Provider.OpenAI.Transport;
using Sunder.Sdk.Abstractions;
using Xunit;

namespace Sunder.Package.Agent.Provider.OpenAI.Tests;

#pragma warning disable OPENAI001, SCME0001

public sealed class OpenAiAgentProviderTests
{
    [Fact]
    public async Task CreateChatClientAsync_ApiKeyMode_UsesResponsesOptionsWrapperEvenWhenCodexSessionExists()
    {
        var packageContext = new TestPackageContext(
            new Dictionary<string, string>
            {
                ["auth.mode"] = OpenAiAuthMode.ApiKey,
            },
            new Dictionary<string, string>
            {
                ["auth.apiKey"] = "sk-test",
                ["auth.codex.session"] = JsonSerializer.Serialize(new OpenAiCodexSession(
                    "access-token",
                    "refresh-token",
                    DateTimeOffset.UtcNow.AddHours(1),
                    "account-id")),
            });
        var provider = CreateProvider(packageContext);

        var client = await provider.CreateChatClientAsync(new AgentChatClientContext("openai", "openai/gpt-5.5"));

        Assert.Contains("OpenAiModelOptionsChatClient", client.GetType().FullName, StringComparison.Ordinal);
        Assert.NotNull(client.GetService(typeof(Microsoft.Extensions.AI.ChatClientMetadata)));
    }

    [Fact]
    public async Task GetAvailableModelsAsync_ExposesGpt56ModesWithoutSyntheticModelRows()
    {
        var provider = CreateProvider(new TestPackageContext(
            new Dictionary<string, string> { ["auth.mode"] = OpenAiAuthMode.ApiKey },
            new Dictionary<string, string> { ["auth.apiKey"] = "sk-test" }));

        var models = await provider.GetAvailableModelsAsync();

        var sol = models.Single(model => model.ModelId == "openai/gpt-5.6-sol");
        Assert.Contains(sol.SpeedOptions ?? [], option => option.SpeedOptionId == "fast");
        Assert.Contains(sol.ModeOptions ?? [], option => option.ModeOptionId == "pro" && !option.DisablesReasoning);
        Assert.Equal(new DateOnly(2026, 7, 9), sol.ReleaseDate);
        Assert.All(models.Where(model => model.ModelId != "openai/codex-mini-latest"), model => Assert.NotNull(model.ReleaseDate));
        Assert.Null(models.Single(model => model.ModelId == "openai/codex-mini-latest").ReleaseDate);
        Assert.Contains(models, model => model.ModelId == "openai/gpt-5.6");
        Assert.DoesNotContain(models, model => model.ModelId == "openai/gpt-5.6-sol-fast");
        Assert.DoesNotContain(models, model => model.ModelId == "openai/gpt-5.6-sol-pro");
    }

    [Fact]
    public async Task GetAvailableModelsAsync_CodexMode_UsesCodexContextLimitsAndSolRecommendation()
    {
        var provider = CreateProvider(new TestPackageContext(
            new Dictionary<string, string> { ["auth.mode"] = OpenAiAuthMode.CodexConnected },
            new Dictionary<string, string>()));

        var models = await provider.GetAvailableModelsAsync();

        var sol = models.Single(model => model.ModelId == "openai/gpt-5.6-sol");
        var luna = models.Single(model => model.ModelId == "openai/gpt-5.6-luna");
        Assert.True(sol.IsRecommended);
        Assert.False(luna.IsRecommended);
        Assert.Equal(500000, sol.ContextWindow);
        Assert.Equal(128000, sol.MaxOutputTokens);
        Assert.Equal(500000, luna.ContextWindow);
        Assert.Equal(128000, luna.MaxOutputTokens);
        Assert.DoesNotContain(models, model => model.ModelId == "openai/gpt-5.6");
    }

    [Fact]
    public async Task CodexConnectedTransport_Gpt56Luna_UsesStableResponsesLiteSessionContract()
    {
        var packageContext = new TestPackageContext(
            new Dictionary<string, string> { ["auth.mode"] = OpenAiAuthMode.CodexConnected },
            new Dictionary<string, string>());
        using var auth = new CodexConnectedAuthStrategy(packageContext);
        var handler = new CapturingHttpMessageHandler(_ => CreateCompletedResponse());
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://chatgpt.com/backend-api/") };
        var transport = new CodexConnectedTransport(auth, client);
        var continuationStore = new CodexResponseContinuationStore();
        var firstSessionId = Guid.NewGuid().ToString("N");
        var secondSessionId = Guid.NewGuid().ToString("N");

        await SendAsync(firstSessionId);
        await SendAsync(firstSessionId);
        await SendAsync(secondSessionId);

        Assert.Equal(3, handler.Requests.Count);
        var firstProviderSessionId = AssertResponsesLiteRequest(handler.Requests[0]);
        var repeatedProviderSessionId = AssertResponsesLiteRequest(handler.Requests[1]);
        var secondProviderSessionId = AssertResponsesLiteRequest(handler.Requests[2]);
        Assert.Equal(firstProviderSessionId, repeatedProviderSessionId);
        Assert.NotEqual(firstProviderSessionId, secondProviderSessionId);

        async Task SendAsync(string conversationId)
            => await DrainAsync(transport.StreamResponseAsync(
                new OpenAiCodexSession("access-token", "refresh-token", DateTimeOffset.UtcNow.AddHours(1), "account-id"),
                new AgentChatClientContext("openai", "openai/gpt-5.6-luna"),
                [new ChatMessage(ChatRole.User, "Hello")],
                new ChatOptions { ConversationId = conversationId },
                continuationStore,
                "response-id",
                "message-id"));

        static string AssertResponsesLiteRequest(CapturedHttpRequest request)
        {
            Assert.Equal("https://chatgpt.com/backend-api/codex/responses", request.Uri.ToString());
            Assert.Equal("sunder", request.Headers["originator"]);
            Assert.Equal("0.144.0", request.Headers["version"]);
            Assert.StartsWith("sunder/", request.Headers["User-Agent"], StringComparison.Ordinal);
            Assert.Equal("true", request.Headers["x-openai-internal-codex-responses-lite"]);
            Assert.DoesNotContain("session_id", request.Headers.Keys, StringComparer.OrdinalIgnoreCase);

            var providerSessionId = request.Headers["session-id"];
            Assert.True(Guid.TryParse(providerSessionId, out var parsedSessionId));
            Assert.Equal(7, parsedSessionId.Version);
            Assert.Equal(providerSessionId, request.Headers["thread-id"]);
            Assert.Equal(providerSessionId, request.Headers["x-client-request-id"]);
            Assert.Equal(providerSessionId, request.Headers["x-session-affinity"]);

            using var body = JsonDocument.Parse(request.Body);
            Assert.Equal("gpt-5.6-luna", body.RootElement.GetProperty("model").GetString());
            Assert.Equal(providerSessionId, body.RootElement.GetProperty("prompt_cache_key").GetString());
            Assert.False(body.RootElement.TryGetProperty("previous_response_id", out _));
            return providerSessionId;
        }
    }

    [Fact]
    public void CodexResponseContinuationStore_DeleteSessionData_ClearsResponsesLiteSessionId()
    {
        var store = new CodexResponseContinuationStore();
        var sessionId = Guid.NewGuid();
        var conversationId = sessionId.ToString("N");

        var first = store.GetOrCreateResponsesLiteSessionId(conversationId);
        Assert.Equal(first, store.GetOrCreateResponsesLiteSessionId(conversationId));

        store.DeleteSessionData(sessionId);

        Assert.NotEqual(first, store.GetOrCreateResponsesLiteSessionId(conversationId));
    }

    [Fact]
    public async Task CodexConnectedTransport_Gpt56Luna_RequiresConversationId()
    {
        var packageContext = new TestPackageContext(
            new Dictionary<string, string> { ["auth.mode"] = OpenAiAuthMode.CodexConnected },
            new Dictionary<string, string>());
        using var auth = new CodexConnectedAuthStrategy(packageContext);
        var handler = new CapturingHttpMessageHandler(_ => CreateCompletedResponse());
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://chatgpt.com/backend-api/") };
        var transport = new CodexConnectedTransport(auth, client);

        var exception = await Assert.ThrowsAsync<AgentChatProviderException>(() => DrainAsync(transport.StreamResponseAsync(
            new OpenAiCodexSession("access-token", "refresh-token", DateTimeOffset.UtcNow.AddHours(1), "account-id"),
            new AgentChatClientContext("openai", "openai/gpt-5.6-luna"),
            [new ChatMessage(ChatRole.User, "Hello")],
            new ChatOptions(),
            new CodexResponseContinuationStore(),
            "response-id",
            "message-id")));

        Assert.Equal("openai-responses-lite-session-required", exception.ErrorCode);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task CodexConnectedTransport_ModelNotFound_IsClassifiedWithoutToolFailureLabel()
    {
        var packageContext = new TestPackageContext(
            new Dictionary<string, string> { ["auth.mode"] = OpenAiAuthMode.CodexConnected },
            new Dictionary<string, string>());
        using var auth = new CodexConnectedAuthStrategy(packageContext);
        var handler = new CapturingHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("""{"error":{"message":"Model not found gpt-5.6-luna","type":"invalid_request_error","param":"model","code":null}}"""),
        });
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://chatgpt.com/backend-api/") };
        var transport = new CodexConnectedTransport(auth, client);

        var exception = await Assert.ThrowsAsync<AgentChatProviderException>(() => DrainAsync(transport.StreamResponseAsync(
            new OpenAiCodexSession("access-token", "refresh-token", DateTimeOffset.UtcNow.AddHours(1), "account-id"),
            new AgentChatClientContext("openai", "openai/gpt-5.6-luna"),
            [new ChatMessage(ChatRole.User, "Hello")],
            new ChatOptions { ConversationId = "session-123" },
            new CodexResponseContinuationStore(),
            "response-id",
            "message-id")));

        Assert.Equal("openai-model-unavailable", exception.ErrorCode);
        Assert.Contains("### OpenAI model unavailable", exception.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("tool request failed", exception.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OpenAiModelOptionsChatClient_MapsFastAndProToRawResponsesOptions()
    {
        var inner = new CapturingChatClient();
        var client = new OpenAiModelOptionsChatClient(inner);
        var options = new ChatOptions
        {
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                [AgentChatModelOptionKeys.SpeedOptionId] = "fast",
                [AgentChatModelOptionKeys.ModeOptionId] = "pro",
            },
            Reasoning = new ReasoningOptions
            {
                Effort = ReasoningEffort.ExtraHigh,
                Output = ReasoningOutput.Summary,
            },
            RawRepresentationFactory = _ => new CreateResponseOptions { MaxOutputTokenCount = 123 },
        };

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "Say hi.")], options);

        var rawOptions = Assert.IsType<CreateResponseOptions>(inner.CapturedOptions?.RawRepresentationFactory?.Invoke(inner));
        Assert.Equal("priority", rawOptions.ServiceTier?.ToString());
        Assert.Equal(123, rawOptions.MaxOutputTokenCount);
        Assert.NotNull(rawOptions.ReasoningOptions);
        Assert.Equal("xhigh", rawOptions.ReasoningOptions.ReasoningEffortLevel?.ToString());
        Assert.Equal("concise", rawOptions.ReasoningOptions.ReasoningSummaryVerbosity?.ToString());
        Assert.True(rawOptions.ReasoningOptions.Patch.TryGetValue("$.mode"u8, out string? mode));
        Assert.Equal("pro", mode);
    }

    [Fact]
    public void UtilityModels_AreOrderedNewestFirstWithUndatedModelsLast()
    {
        var modelIds = OpenAiProviderConfiguration.UtilityModelOptions.Select(option => option.Value).ToArray();

        Assert.Equal("openai/gpt-5.6", modelIds[0]);
        Assert.Equal("openai/codex-mini-latest", modelIds[^1]);
        Assert.True(Array.IndexOf(modelIds, "openai/gpt-5.4-mini") < Array.IndexOf(modelIds, "openai/gpt-5.4"));
    }

    [Fact]
    public async Task CreateChatClientAsync_CodexMode_UsesCodexClientEvenWhenApiKeyExists()
    {
        var packageContext = new TestPackageContext(
            new Dictionary<string, string>
            {
                ["auth.mode"] = OpenAiAuthMode.CodexConnected,
            },
            new Dictionary<string, string>
            {
                ["auth.apiKey"] = "sk-test",
                ["auth.codex.session"] = JsonSerializer.Serialize(new OpenAiCodexSession(
                    "access-token",
                    "refresh-token",
                    DateTimeOffset.UtcNow.AddHours(1),
                    "account-id")),
            });
        var provider = CreateProvider(packageContext);

        var client = await provider.CreateChatClientAsync(new AgentChatClientContext("openai", "openai/gpt-5.5"));

        Assert.IsType<OpenAiCodexChatClient>(client);
    }

    [Fact]
    public void ExtractChatGptAccountId_ReadsTopLevelClaim()
    {
        var jwt = CreateJwt(new Dictionary<string, object?>
        {
            ["chatgpt_account_id"] = "account-top-level",
        });

        Assert.Equal("account-top-level", CodexConnectedAuthStrategy.ExtractChatGptAccountId(jwt));
    }

    [Fact]
    public void ExtractChatGptAccountId_ReadsOpenAiAuthClaim()
    {
        var jwt = CreateJwt(new Dictionary<string, object?>
        {
            ["https://api.openai.com/auth"] = new Dictionary<string, object?>
            {
                ["chatgpt_account_id"] = "account-auth-claim",
            },
        });

        Assert.Equal("account-auth-claim", CodexConnectedAuthStrategy.ExtractChatGptAccountId(jwt));
    }

    [Fact]
    public void ExtractChatGptAccountId_FallsBackToFirstOrganizationId()
    {
        var jwt = CreateJwt(new Dictionary<string, object?>
        {
            ["organizations"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["id"] = "organization-id",
                },
            },
        });

        Assert.Equal("organization-id", CodexConnectedAuthStrategy.ExtractChatGptAccountId(jwt));
    }

    private static OpenAiAgentProvider CreateProvider(TestPackageContext packageContext)
    {
        var codexConnectedAuthStrategy = new CodexConnectedAuthStrategy(packageContext);
        var codexConnectedTransport = new CodexConnectedTransport(
            codexConnectedAuthStrategy,
            new HttpClient { BaseAddress = new Uri("https://chatgpt.com/backend-api/") });
        return new OpenAiAgentProvider(
            new ApiKeyAuthStrategy(packageContext),
            codexConnectedAuthStrategy,
            codexConnectedTransport,
            new CodexResponseContinuationStore(),
            packageContext);
    }

    private static async Task DrainAsync(IAsyncEnumerable<ChatResponseUpdate> updates)
    {
        await foreach (var _ in updates)
        {
        }
    }

    private static HttpResponseMessage CreateCompletedResponse()
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                data: {"type":"response.completed","response":{"id":"response-id","status":"completed"}}

                """),
        };

    private static string CreateJwt(IReadOnlyDictionary<string, object?> claims)
        => Base64UrlEncode("{}") + "." + Base64UrlEncode(JsonSerializer.Serialize(claims)) + ".signature";

    private static string Base64UrlEncode(string value)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private sealed class TestPackageContext(
        IReadOnlyDictionary<string, string> configurationValues,
        IReadOnlyDictionary<string, string> secretValues) : IPackageContext
    {
        public string PackageId { get; } = "sunder.package.agent.provider.openai";

        public string Version { get; } = "1.0.0";

        public string ContentRootPath { get; } = AppContext.BaseDirectory;

        public IPackageStorageContext Storage { get; } = new TestPackageStorageContext();

        public IPackageSettings Settings { get; } = new TestPackageSettings(configurationValues);

        public IPackageSecrets Secrets { get; } = new TestPackageSecrets(secretValues);


        public Sunder.Sdk.Logging.IPackageLogging Logging { get; } = Sunder.Sdk.Logging.NullPackageLogging.Instance;
    }

    private sealed class CapturingChatClient : IChatClient
    {
        public ChatOptions? CapturedOptions { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            CapturedOptions = options;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class CapturingHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        : HttpMessageHandler
    {
        public List<CapturedHttpRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var headers = request.Headers.ToDictionary(
                header => header.Key,
                header => string.Join(",", header.Value),
                StringComparer.OrdinalIgnoreCase);
            Requests.Add(new CapturedHttpRequest(
                request.RequestUri!,
                headers,
                request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken)));
            return responseFactory(request);
        }
    }

    private sealed record CapturedHttpRequest(
        Uri Uri,
        IReadOnlyDictionary<string, string> Headers,
        string Body);

    private sealed class TestPackageSettings(IReadOnlyDictionary<string, string> values) : IPackageSettings
    {
        public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default)
            => Task.FromResult(values.TryGetValue(key, out var value) ? value : null);
        public Task<string?> GetStoredValueAsync(string key, CancellationToken cancellationToken = default)
            => GetValueAsync(key, cancellationToken);
        public Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task DeleteValueAsync(string key, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class TestPackageSecrets(IReadOnlyDictionary<string, string> values) : IPackageSecrets
    {
        private readonly Dictionary<string, string> _values = new(values, StringComparer.OrdinalIgnoreCase);

        public Task<string?> GetSecretAsync(string key, CancellationToken cancellationToken = default)
            => Task.FromResult(_values.TryGetValue(key, out var value) ? value : null);

        public Task SetSecretAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            _values[key] = value;
            return Task.CompletedTask;
        }

        public Task DeleteSecretAsync(string key, CancellationToken cancellationToken = default)
        {
            _values.Remove(key);
            return Task.CompletedTask;
        }
    }

    private sealed class TestPackageStorageContext : IPackageStorageContext
    {
        public IPackageFileStore Files => throw new NotSupportedException();

        public IPackageKeyValueStore State => throw new NotSupportedException();

        public IPackageRoleLocalWorkspace RoleLocalWorkspace => throw new NotSupportedException();
    }
}
