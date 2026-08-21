using Google.GenAI.Types;
using Microsoft.Extensions.AI;
using System.Text.Json;
using Sunder.Package.Agent.Provider.Gemini;
using Xunit;

namespace Sunder.Package.Agent.Provider.Gemini.Tests;

public sealed class GeminiOptionsTranslatorTests
{
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
