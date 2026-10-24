using Google.GenAI.Types;
using Microsoft.Extensions.AI;
using System.Text.Json;
using Sunder.Package.Agent.Provider.Gemini;
using Sunder.Package.Agent.Contracts.Models;
using Xunit;

namespace Sunder.Package.Agent.Provider.Gemini.Tests;

public sealed class GeminiOptionsTranslatorTests
{
    [Theory]
    [InlineData("gemini/gemini-4-argon", "low", "LOW")]
    [InlineData("gemini/gemini-3.8-flash", "medium", "MEDIUM")]
    [InlineData("gemini/gemini-3.6-flash", "minimal", "MINIMAL")]
    public void CurrentModels_MapOnlyExactNativeThinkingAndProcessing(string model, string effort, string wireEffort)
    {
        var options = new ChatOptions { AdditionalProperties = new()
        {
            [AgentChatModelOptionKeys.ReasoningVariantId] = effort,
            [AgentChatModelOptionKeys.SpeedOptionId] = "fast",
        } };
        var fast = new GeminiOptionsTranslator().Translate(options, true, null, model);
        options.AdditionalProperties[AgentChatModelOptionKeys.SpeedOptionId] = "normal";
        var normal = new GeminiOptionsTranslator().Translate(options, true, null, model);

        Assert.Equal(wireEffort, fast.ThinkingConfig?.ThinkingLevel?.ToString());
        Assert.Equal(fast.ThinkingConfig?.ThinkingLevel, normal.ThinkingConfig?.ThinkingLevel);
        Assert.Equal(ServiceTier.Priority, fast.ServiceTier);
        Assert.Equal(ServiceTier.Standard, normal.ServiceTier);
        var defaults = new GeminiOptionsTranslator().Translate(null, false, null, model);
        Assert.Null(defaults.ThinkingConfig);
        Assert.Null(defaults.ServiceTier);
    }

    [Theory]
    [InlineData("gemini/gemini-4-argon", "max", "normal")]
    [InlineData("gemini/gemini-3.8-flash", "xhigh", "normal")]
    [InlineData("gemini/gemini-4-argon", "minimal", "normal")]
    [InlineData("gemini/gemini-3.5-flash-lite", "low", "fast")]
    public void CurrentModels_RejectUnsupportedEffortAndPriority(string model, string effort, string speed)
        => Assert.Throws<AgentChatProviderException>(() => new GeminiOptionsTranslator().Translate(
            new() { AdditionalProperties = new()
            {
                [AgentChatModelOptionKeys.ReasoningVariantId] = effort,
                [AgentChatModelOptionKeys.SpeedOptionId] = speed,
            } }, false, null, model));

    [Theory]
    [InlineData(true, 32768)]
    [InlineData(false, 65536)]
    public void Argon_OutputLimitMatchesTheActualTransport(bool tools, int limit)
    {
        var config = new GeminiOptionsTranslator().Translate(new() { MaxOutputTokens = 262144 }, tools, null, "gemini-4-argon");
        Assert.Equal(limit, config.MaxOutputTokens);
        Assert.Equal(32768, GeminiModelCatalog.Find("gemini-4-argon")!.MaxOutputTokens);
    }

    [Fact]
    public void Translate_MapsMaxOutputTokens()
    {
        var config = new GeminiOptionsTranslator().Translate(
            new ChatOptions { MaxOutputTokens = 4096 },
            includeTools: false,
            systemInstruction: null,
            modelId: "gemini/gemini-3-flash-preview");

        Assert.Equal(4096, config.MaxOutputTokens);
    }

    [Fact]
    public void Translate_MapsReasoningEffortAndRequestedThoughts()
    {
        var config = new GeminiOptionsTranslator().Translate(
            new ChatOptions
            {
                Reasoning = new ReasoningOptions
                {
                    Effort = ReasoningEffort.Medium,
                    Output = ReasoningOutput.Summary,
                },
            },
            includeTools: false,
            systemInstruction: null,
            modelId: "gemini/gemini-3-flash-preview");

        Assert.NotNull(config.ThinkingConfig);
        Assert.Equal(ThinkingLevel.Medium, config.ThinkingConfig.ThinkingLevel);
        Assert.True(config.ThinkingConfig.IncludeThoughts);
    }

    [Fact]
    public void Translate_PrependsOptionInstructionsToSystemMessages()
    {
        var config = new GeminiOptionsTranslator().Translate(
            new ChatOptions { Instructions = "Option instruction" },
            includeTools: false,
            new Content { Parts = [new Part { Text = "System message" }] },
            modelId: "gemini/gemini-3-flash-preview");

        Assert.Equal(
            ["Option instruction", "System message"],
            config.SystemInstruction!.Parts!.Select(part => part.Text));
    }

    [Theory]
    [InlineData("gemini/gemini-2.5-pro", ReasoningEffort.High, 32768)]
    [InlineData("gemini/gemini-2.5-flash", ReasoningEffort.High, 24576)]
    [InlineData("gemini/gemini-2.5-flash-lite", ReasoningEffort.Low, 1024)]
    public void Translate_UsesValidGemini25ThinkingBudgets(
        string modelId,
        ReasoningEffort effort,
        int expectedBudget)
    {
        var config = new GeminiOptionsTranslator().Translate(
            new ChatOptions { Reasoning = new ReasoningOptions { Effort = effort } },
            includeTools: false,
            systemInstruction: null,
            modelId);

        Assert.Equal(expectedBudget, config.ThinkingConfig!.ThinkingBudget);
        Assert.Null(config.ThinkingConfig.ThinkingLevel);
    }

    [Theory]
    [InlineData("auto", "AUTO", null)]
    [InlineData("any", "ANY", null)]
    [InlineData("specific", "ANY", "read_file")]
    public void Translate_MapsToolModes(string modeName, string expectedMode, string? expectedAllowedName)
    {
        using var schema = JsonDocument.Parse("""{"type":"object"}""");
        var tool = AIFunctionFactory.CreateDeclaration("read_file", "Read", schema.RootElement);
        ChatToolMode mode = modeName switch
        {
            "auto" => ChatToolMode.Auto,
            "any" => ChatToolMode.RequireAny,
            _ => ChatToolMode.RequireSpecific("read_file"),
        };

        var config = new GeminiOptionsTranslator().Translate(
            new ChatOptions { ToolMode = mode, Tools = [tool] },
            includeTools: true,
            systemInstruction: null,
            modelId: "gemini/gemini-3-flash-preview");

        Assert.Equal(expectedMode, config.ToolConfig!.FunctionCallingConfig!.Mode!.ToString());
        Assert.Equal(
            expectedAllowedName is null ? [] : [expectedAllowedName],
            config.ToolConfig.FunctionCallingConfig.AllowedFunctionNames ?? []);
    }

    [Fact]
    public void Translate_PreservesMcpEnvelopeRefsCombinatorsAndNullableTimeout()
    {
        using var schema = JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "__sunder_mcp_call": {
                  "type": "object",
                  "properties": {
                    "__sunder_mcp_metadata_version": { "type": "string", "const": "sunder.mcp.call/1" },
                    "arguments": {
                      "$id": "urn:sunder:mcp:arguments:1",
                      "$ref": "#/$defs/args",
                      "$defs": {
                        "args": {
                          "oneOf": [
                            { "type": "object", "properties": { "prompt": { "type": "string" } }, "additionalProperties": false },
                            { "allOf": [
                              { "type": "object", "properties": { "path": { "type": "string" } } },
                              { "type": "object", "properties": { "depth": { "type": "integer" } } }
                            ], "unevaluatedProperties": false }
                          ]
                        }
                      }
                    },
                    "__sunder_expected_timeout_seconds": { "type": ["integer", "null"], "minimum": 1, "maximum": 1800 }
                  },
                  "required": ["__sunder_mcp_metadata_version", "arguments"],
                  "additionalProperties": false
                }
              },
              "required": ["__sunder_mcp_call"],
              "additionalProperties": false
            }
            """);
        var tool = AIFunctionFactory.CreateDeclaration("mcp_tool", "MCP tool", schema.RootElement);

        var config = new GeminiOptionsTranslator().Translate(
            new ChatOptions { Tools = [tool] },
            includeTools: true,
            systemInstruction: null,
            modelId: "gemini/gemini-3-flash-preview");

        var translated = Assert.IsType<JsonElement>(
            Assert.Single(Assert.Single(config.Tools!).FunctionDeclarations!).ParametersJsonSchema);
        var envelope = translated.GetProperty("properties").GetProperty("__sunder_mcp_call");
        Assert.Equal(
            ["__sunder_mcp_metadata_version", "arguments"],
            envelope.GetProperty("required").EnumerateArray().Select(item => item.GetString()));
        var arguments = envelope.GetProperty("properties").GetProperty("arguments");
        Assert.Equal("#/$defs/args", arguments.GetProperty("$ref").GetString());
        Assert.Equal(2, arguments.GetProperty("$defs").GetProperty("args").GetProperty("oneOf").GetArrayLength());
        Assert.Equal(
            ["integer", "null"],
            envelope.GetProperty("properties")
                .GetProperty("__sunder_expected_timeout_seconds")
                .GetProperty("type")
                .EnumerateArray()
                .Select(item => item.GetString()));
    }
}
