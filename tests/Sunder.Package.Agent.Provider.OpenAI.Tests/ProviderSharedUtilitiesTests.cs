using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Sunder.Generated.Packages.Sunder_Package_Agent_Models;
using Sunder.Package.Agent.Provider.Shared;
using Sunder.Package.Agent.Provider.TestSupport;
using Sunder.Sdk.Settings;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;
using Xunit;

namespace Sunder.Package.Agent.Provider.OpenAI.Tests;

public sealed class ProviderSharedUtilitiesTests
{
    [Theory]
    [InlineData("openai/gpt-5.5", "openai", "gpt-5.5")]
    [InlineData("OPENAI/gpt-5.5", "openai", "gpt-5.5")]
    [InlineData("gpt-5.5", "openai", "gpt-5.5")]
    [InlineData("", "openai", "")]
    public void ModelPrefix_IsRemovedOnlyForTheSelectedProvider(string modelId, string providerId, string expected)
        => Assert.Equal(expected, ProviderModelId.RemovePrefix(modelId, providerId));

    [Fact]
    public void JsonObjectArguments_AcceptObjectsAndRejectMalformedOrNonObjectValues()
    {
        Assert.True(ProviderJson.TryParseObjectArguments(
            "{\"path\":\"README.md\",\"count\":2}",
            out var parsed));

        Assert.Equal("README.md", Assert.IsType<JsonElement>(parsed["path"]).GetString());
        Assert.Equal(2, Assert.IsType<JsonElement>(parsed["count"]).GetInt32());
        Assert.False(ProviderJson.TryParseObjectArguments("[1,2]", out _));
        Assert.False(ProviderJson.TryParseObjectArguments("not-json", out _));
        Assert.False(ProviderJson.TryParseObjectArguments("{\"path\":\"old\",\"PATH\":\"new\"}", out _));
    }

    [Fact]
    public void ToolResultRendering_PreservesTextAndJson()
    {
        using var document = JsonDocument.Parse("{\"ok\":true}");

        Assert.Equal("plain text", ProviderToolResult.RenderText("plain text"));
        Assert.Equal("{\"ok\":true}", ProviderToolResult.RenderText(document.RootElement));
        Assert.Equal(string.Empty, ProviderToolResult.RenderText(null));
    }

    [Fact]
    public void EmbeddingCorrelation_UsesVendorIndicesRatherThanResponseOrder()
    {
        var results = ProviderEmbeddingBatch.CreateResultBuffer<AgentEmbeddingGenerationResult>(["first", " ", "third"], out var inputs);
        var first = Embedding(1);
        var third = Embedding(3);

        ProviderEmbeddingBatch.ApplyIndexedResults(results, inputs,
        [
            new ProviderIndexedEmbedding<AgentEmbeddingGenerationResult>(1, third),
            new ProviderIndexedEmbedding<AgentEmbeddingGenerationResult>(0, first),
        ]);

        Assert.Same(first, results[0]);
        Assert.Null(results[1]);
        Assert.Same(third, results[2]);
    }

    [Fact]
    public void EmbeddingCorrelation_RejectsSparseIndices()
    {
        var results = ProviderEmbeddingBatch.CreateResultBuffer<AgentEmbeddingGenerationResult>(["first", "second"], out var inputs);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ProviderEmbeddingBatch.ApplyIndexedResults(results, inputs,
            [
                new ProviderIndexedEmbedding<AgentEmbeddingGenerationResult>(1, Embedding(2)),
            ]));

        Assert.Contains("count/index mismatch", exception.Message, StringComparison.Ordinal);
        Assert.Contains("0", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EmbeddingCorrelation_RejectsDuplicateIndices()
    {
        var results = ProviderEmbeddingBatch.CreateResultBuffer<AgentEmbeddingGenerationResult>(["first", "second"], out var inputs);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ProviderEmbeddingBatch.ApplyIndexedResults(results, inputs,
            [
                new ProviderIndexedEmbedding<AgentEmbeddingGenerationResult>(0, Embedding(1)),
                new ProviderIndexedEmbedding<AgentEmbeddingGenerationResult>(0, Embedding(2)),
            ]));

        Assert.Contains("duplicate index 0", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResponseAggregation_PreservesMeaiResponseAndMessageMetadata()
    {
        var createdAt = DateTimeOffset.UtcNow;
        ChatResponseUpdate[] updates =
        [
            new(ChatRole.Assistant, "hel")
            {
                AuthorName = "assistant",
                CreatedAt = createdAt,
                MessageId = "message-1",
                ModelId = "vendor/model-actual",
                ResponseId = "response-1",
                AdditionalProperties = new AdditionalPropertiesDictionary
                {
                    ["message-metadata"] = "message-value",
                },
            },
            new(ChatRole.Assistant, "lo")
            {
                MessageId = "message-1",
                ModelId = "vendor/model-actual",
                ResponseId = "response-1",
            },
            new()
            {
                FinishReason = ChatFinishReason.Stop,
                ModelId = "vendor/model-actual",
                ResponseId = "response-1",
                Contents =
                [
                    new UsageContent(new UsageDetails
                    {
                        InputTokenCount = 2,
                        OutputTokenCount = 3,
                        TotalTokenCount = 5,
                    }),
                ],
                AdditionalProperties = new AdditionalPropertiesDictionary
                {
                    ["response-metadata"] = "response-value",
                },
            },
        ];

        var response = await ProviderChatResponseAggregator.AggregateAsync(
            AsAsync(updates),
            "vendor/fallback",
            CancellationToken.None);

        Assert.Equal("response-1", response.ResponseId);
        Assert.Equal("vendor/model-actual", response.ModelId);
        Assert.Equal(ChatFinishReason.Stop, response.FinishReason);
        Assert.Equal(2, response.Usage?.InputTokenCount);
        Assert.Equal(3, response.Usage?.OutputTokenCount);
        Assert.Equal(5, response.Usage?.TotalTokenCount);
        Assert.Equal("response-value", response.AdditionalProperties?["response-metadata"]);
        var message = Assert.Single(response.Messages);
        Assert.Equal("message-1", message.MessageId);
        Assert.Equal("assistant", message.AuthorName);
        Assert.Equal(createdAt, message.CreatedAt);
        Assert.Equal("hello", message.Text);
        Assert.Equal("message-value", message.AdditionalProperties?["message-metadata"]);
    }

    [Fact]
    public void ModelCatalog_OrdersDeterministicallyAndRequiresExplicitUtilityEligibility()
    {
        var snapshot = ProviderModelCatalog.ValidateAndOrder(
        [
            Model("vendor/z", "Same", new DateOnly(2025, 1, 1)),
            Model("vendor/old", "Old", new DateOnly(2024, 1, 1)),
            Model("vendor/a", "Same", new DateOnly(2025, 1, 1)),
            Model("vendor/undated", "Undated", null),
        ],
        "vendor/a",
        static model => model.ModelId != "vendor/z");

        Assert.Equal(
            ["vendor/a", "vendor/z", "vendor/old", "vendor/undated"],
            snapshot.Models.Select(model => model.ModelId));
        Assert.Equal(
            ["vendor/a", "vendor/old", "vendor/undated"],
            snapshot.UtilityModelOptions.Select(option => option.Value));
    }

    [Fact]
    public void ModelCatalog_RejectsDuplicateModelAndOptionIds()
    {
        Assert.Throws<InvalidOperationException>(() => ProviderModelCatalog.ValidateAndOrder(
        [
            Model("vendor/model", "One", null),
            Model("VENDOR/MODEL", "Two", null),
        ],
        "vendor/model",
        static _ => true));

        var modelWithDuplicateOptions = Model("vendor/model", "Model", null) with
        {
            Variants =
            [
                new() { VariantId = "reasoning", DisplayName = "Reasoning", Description = null, ReasoningEffort = null },
                new() { VariantId = "REASONING", DisplayName = "Reasoning again", Description = null, ReasoningEffort = null },
            ],
        };
        Assert.Throws<InvalidOperationException>(() => ProviderModelCatalog.ValidateAndOrder(
            [modelWithDuplicateOptions],
            "vendor/model",
            static _ => true));
    }

    [Fact]
    public void ModelCatalog_RejectsInvalidLimitsAndMissingOrIneligibleDefaults()
    {
        var invalidLimits = Model("vendor/invalid", "Invalid", null) with { ContextWindow = 0 };
        Assert.Throws<InvalidOperationException>(() => ProviderModelCatalog.ValidateAndOrder(
            [invalidLimits],
            "vendor/invalid",
            static _ => true));

        var model = Model("vendor/model", "Model", null);
        Assert.Throws<InvalidOperationException>(() => ProviderModelCatalog.ValidateAndOrder(
            [model],
            "vendor/missing",
            static _ => true));
        Assert.Throws<InvalidOperationException>(() => ProviderModelCatalog.ValidateAndOrder(
            [model],
            "vendor/model",
            static _ => false));
    }

    [Fact]
    public async Task OpenAiUtilityModelSelection_NormalizesLegacyValuesAndFallsBackForUnknownValues()
    {
        var selection = OpenAiProviderConfiguration.UtilityModelSelection;
        var normalizedContext = new ProviderTestPackageContext(
            "sunder.package.agent.provider.openai",
            new Dictionary<string, string> { [OpenAiProviderConfiguration.UtilityModelKey] = " openai/gpt-5.5-fast " });
        var unknownContext = new ProviderTestPackageContext(
            "sunder.package.agent.provider.openai",
            new Dictionary<string, string> { [OpenAiProviderConfiguration.UtilityModelKey] = "openai/unknown" });

        Assert.Equal("openai/gpt-5.5", await selection.ResolveAsync(normalizedContext.Settings));
        Assert.Equal(OpenAiProviderConfiguration.DefaultUtilityModelId, await selection.ResolveAsync(unknownContext.Settings));
    }

    [Fact]
    public void UsageContent_WireCountsPreserveMissingTotalWhileVendorSnapshotsCanInferIt()
    {
        var snapshot = new ProviderUsageSnapshot(2, 3, CachedInputTokenCount: 1);
        var wire = ProviderResponseUpdates.CreateUsageContent(snapshot, inferTotalTokenCount: false).Details;
        Assert.Equal(2, wire.InputTokenCount);
        Assert.Equal(3, wire.OutputTokenCount);
        Assert.Equal(1, wire.CachedInputTokenCount);
        Assert.Null(wire.TotalTokenCount);
        Assert.Null(wire.ReasoningTokenCount);
        Assert.Equal(5, ProviderResponseUpdates.CreateUsageContent(snapshot).Details.TotalTokenCount);
        Assert.Equal(9, ProviderResponseUpdates.CreateUsageContent(snapshot with { TotalTokenCount = 9 }, inferTotalTokenCount: false).Details.TotalTokenCount);
    }

    [Fact]
    public void UsageAccumulator_MergesPartialSnapshotsWithoutDoubleCounting()
    {
        var usage = new ProviderUsageAccumulator();
        usage.SetLatest(new ProviderUsageSnapshot(InputTokenCount: 12, OutputTokenCount: 0, CachedInputTokenCount: 4));
        usage.SetLatest(new ProviderUsageSnapshot(InputTokenCount: null, OutputTokenCount: 7, ReasoningTokenCount: 3));

        var content = usage.CreateContent();
        Assert.NotNull(content);
        var details = content!.Details;

        Assert.Equal(12, details.InputTokenCount);
        Assert.Equal(7, details.OutputTokenCount);
        Assert.Equal(19, details.TotalTokenCount);
        Assert.Equal(4, details.CachedInputTokenCount);
        Assert.Equal(3, details.ReasoningTokenCount);
    }

    [Theory]
    [InlineData(false, false, "ProviderCancellation")]
    [InlineData(true, false, "CallerCancellation")]
    [InlineData(false, true, "ProviderCancellation")]
    public void StreamFailureClassification_DistinguishesCallerProviderAndNestedCancellation(
        bool callerCanceled,
        bool nested,
        string expected)
    {
        using var cancellation = new CancellationTokenSource();
        if (callerCanceled)
        {
            cancellation.Cancel();
        }

        Exception exception = nested
            ? new InvalidOperationException("wrapper", new TimeoutException("provider timeout"))
            : new OperationCanceledException("canceled", innerException: null, cancellation.Token);

        Assert.Equal(expected, ProviderStreamFailureClassifier.Classify(
            exception,
            cancellation.Token,
            inspectInnerExceptions: nested,
            includeTimeouts: nested).ToString());
        if (callerCanceled)
        {
            ProviderConformanceHarness.VerifyCallerCancellation(
                "OpenAI",
                exception,
                cancellation.Token);
        }
    }

    [Fact]
    public void ResponseUpdateFactory_AssignsIdentityAndDescribesSemanticContent()
    {
        var update = ProviderResponseUpdates.Create(
            "vendor/model",
            "response-1",
            "message-1",
            new FunctionCallContent("call-1", "read", new Dictionary<string, object?>()));

        Assert.Equal("response-1", update.ResponseId);
        Assert.Equal("message-1", update.MessageId);
        Assert.Equal("vendor/model", update.ModelId);
        Assert.Equal("ToolCallRequested", ProviderResponseUpdates.Describe(update));
    }

    [Fact]
    public async Task ChatMessages_ReadSingleUseAliasesOnceAndKeepRemoteUrisRemote()
    {
        await using var content = SunderContent.FromStream(new MemoryStream([1, 2, 3]), "image/png", "image.png", length: 3);
        var binary = ChatContent("content-reference") with { Content = content };
        var remote = ChatContent("content-reference") with
        {
            ContentUri = new SunderRpcUri("https://example.test/image.png"),
            MediaType = "image/png",
        };

        var messages = await ProviderChatValues.MessagesAsync(
            [new() { Role = "user", MessageId = "message", AuthorName = null, Contents = [binary, binary, remote] }],
            CancellationToken.None);

        var message = Assert.Single(messages);
        Assert.Equal(3, message.Contents.Count);
        Assert.Equal(new byte[] { 1, 2, 3 }, Assert.IsType<DataContent>(message.Contents[0]).Data.ToArray());
        Assert.Equal(new byte[] { 1, 2, 3 }, Assert.IsType<DataContent>(message.Contents[1]).Data.ToArray());
        Assert.Equal("image.png", Assert.IsType<DataContent>(message.Contents[1]).Name);
        Assert.Equal(new Uri("https://example.test/image.png"), Assert.IsType<UriContent>(message.Contents[2]).Uri);
    }

    [Theory]
    [InlineData(null, "content.bin")]
    [InlineData("", "content.bin")]
    [InlineData(" \t", "content.bin")]
    [InlineData("reply.png", "reply.png")]
    public async Task ChatUpdate_UsesBinaryContentAndPreservesExplicitJsonNull(string? name, string expectedFileName)
    {
        var update = await ProviderChatValues.UpdateAsync(new ChatResponseUpdate
        {
            Contents = [new DataContent(new byte[] { 4, 5, 6 }, "image/png") { Name = name }, new FunctionResultContent("call", null)],
        });
        try
        {
            var contents = Assert.IsAssignableFrom<IReadOnlyList<AgentChatContent>>(update.ContentItems);
            var binary = Assert.IsType<SunderContent>(contents[0].Content);
            Assert.Null(contents[0].ContentUri);
            Assert.Equal(expectedFileName, binary.FileName);
            Assert.False(contents[0].Json.TryGetValue(out _));
            Assert.True(contents[1].Json.TryGetValue(out var value));
            Assert.Equal(JsonValueKind.Null, value.ValueKind);
            await using var stream = await binary.OpenReadAsync();
            var bytes = new byte[3];
            await stream.ReadExactlyAsync(bytes);
            Assert.Equal(new byte[] { 4, 5, 6 }, bytes);
        }
        finally
        {
            await ProviderChatValues.DisposeContentAsync(update);
        }
    }

    [Fact]
    public async Task ChatStream_EarlyExitCancelsBackpressuredProducerAndDisposesBinaryContent()
    {
        var client = new BinaryStreamingClient();
        var request = new AgentChatStreamRequest
        {
            ProviderId = "vendor", ModelId = "vendor/model", Messages = [],
            Options = new()
            {
                Instructions = null, ConversationId = null, ModelId = null, Temperature = null,
                MaxOutputTokens = null, TopP = null, TopK = null, FrequencyPenalty = null,
                PresencePenalty = null, Seed = null, AllowMultipleToolCalls = null,
                ReasoningEffort = null, Tools = [], AdditionalProperties = null, ContinuationHandle = null,
            },
        };
        var enumerator = ProviderChatStream.ReadAsync((_, _) => ValueTask.FromResult<IChatClient>(client), request, CancellationToken.None)
            .GetAsyncEnumerator();
        SunderContent content;
        try
        {
            Assert.True(await enumerator.MoveNextAsync());
            var update = Assert.IsType<AgentChatUpdateEvent>(enumerator.Current);
            content = Assert.IsType<SunderContent>(Assert.Single(update.ContentItems!).Content);
            Assert.InRange(client.Produced, 2, 99);
            await using var stream = await content.OpenReadAsync();
            Assert.Equal(1, stream.ReadByte());
        }
        finally
        {
            await enumerator.DisposeAsync();
        }

        Assert.Equal(1, client.DisposeCount);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => content.OpenReadAsync().AsTask());
    }

    private static AgentChatContent ChatContent(string kind) => new()
    {
        Kind = kind, Text = null, ProtectedData = null, CallId = null, Name = null,
        Json = default, InformationalOnly = false, MediaType = null, ContentUri = null,
        Content = null, Usage = null, ErrorCode = null, ErrorMessage = null,
    };

    private sealed class BinaryStreamingClient : IChatClient
    {
        public int Produced { get; private set; }
        public int DisposeCount { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            for (var index = 0; index < 100; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Produced++;
                yield return new() { Contents = [new DataContent(new byte[] { 1, 2, 3 }, "image/png")] };
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() => DisposeCount++;
    }

    private static AgentEmbeddingGenerationResult Embedding(float value)
        => new() { ModelId = "vendor/embedding", Values = [value] };

    private static AgentModelDescriptor Model(string id, string name, DateOnly? releaseDate)
        => new()
        {
            ModelId = id, DisplayName = name, ContextWindow = 100, MaxOutputTokens = 10,
            IsRecommended = false, Variants = null, SpeedOptions = null, ModeOptions = null, ReleaseDate = releaseDate,
        };

    private static async IAsyncEnumerable<ChatResponseUpdate> AsAsync(IEnumerable<ChatResponseUpdate> updates)
    {
        await Task.Yield();
        foreach (var update in updates)
        {
            yield return update;
        }
    }
}
