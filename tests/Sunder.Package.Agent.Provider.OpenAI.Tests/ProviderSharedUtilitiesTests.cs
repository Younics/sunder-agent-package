using System.Text.Json;
using Microsoft.Extensions.AI;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Provider.Shared;
using Sunder.Package.Agent.Provider.TestSupport;
using Sunder.Sdk.Configuration;
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
        var results = ProviderEmbeddingBatch.CreateResultBuffer(["first", " ", "third"], out var inputs);
        var first = Embedding(1);
        var third = Embedding(3);

        ProviderEmbeddingBatch.ApplyIndexedResults(results, inputs,
        [
            new ProviderIndexedEmbedding(1, third),
            new ProviderIndexedEmbedding(0, first),
        ]);

        Assert.Same(first, results[0]);
        Assert.Null(results[1]);
        Assert.Same(third, results[2]);
    }

    [Fact]
    public void EmbeddingCorrelation_RejectsSparseIndices()
    {
        var results = ProviderEmbeddingBatch.CreateResultBuffer(["first", "second"], out var inputs);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ProviderEmbeddingBatch.ApplyIndexedResults(results, inputs,
            [
                new ProviderIndexedEmbedding(1, Embedding(2)),
            ]));

        Assert.Contains("count/index mismatch", exception.Message, StringComparison.Ordinal);
        Assert.Contains("0", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EmbeddingCorrelation_RejectsDuplicateIndices()
    {
        var results = ProviderEmbeddingBatch.CreateResultBuffer(["first", "second"], out var inputs);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ProviderEmbeddingBatch.ApplyIndexedResults(results, inputs,
            [
                new ProviderIndexedEmbedding(0, Embedding(1)),
                new ProviderIndexedEmbedding(0, Embedding(2)),
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
                new("reasoning", "Reasoning"),
                new("REASONING", "Reasoning again"),
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
        var invalidLimits = new AgentModelDescriptor("vendor/invalid", "Invalid", 0, 10);
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
    public async Task UtilityModelSelection_NormalizesKnownValuesAndFallsBackForUnknownValues()
    {
        var selection = new ProviderUtilityModelSelection(
            "utility.modelId",
            "vendor/default",
            [
                new PackageConfigurationOption("vendor/default", "Default"),
                new PackageConfigurationOption("vendor/other", "Other"),
            ],
            value => string.Equals(value?.Trim(), "vendor/legacy", StringComparison.OrdinalIgnoreCase)
                ? "vendor/other"
                : value);
        var normalizedContext = new ProviderTestPackageContext(
            "vendor.package",
            new Dictionary<string, string> { ["utility.modelId"] = " vendor/legacy " });
        var unknownContext = new ProviderTestPackageContext(
            "vendor.package",
            new Dictionary<string, string> { ["utility.modelId"] = "vendor/unknown" });

        Assert.Equal("vendor/other", await selection.ResolveAsync(normalizedContext.Settings));
        Assert.Equal("vendor/default", await selection.ResolveAsync(unknownContext.Settings));
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
            : new OperationCanceledException("canceled");

        Assert.Equal(expected, ProviderStreamFailureClassifier.Classify(
            exception,
            cancellation.Token,
            inspectInnerExceptions: nested,
            includeTimeouts: nested).ToString());
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

    private static AgentEmbeddingGenerationResult Embedding(float value)
        => new("vendor/embedding", [value]);

    private static AgentModelDescriptor Model(string id, string name, DateOnly? releaseDate)
        => new AgentModelDescriptor(id, name, 100, 10) { ReleaseDate = releaseDate };

    private static async IAsyncEnumerable<ChatResponseUpdate> AsAsync(IEnumerable<ChatResponseUpdate> updates)
    {
        await Task.Yield();
        foreach (var update in updates)
        {
            yield return update;
        }
    }
}
