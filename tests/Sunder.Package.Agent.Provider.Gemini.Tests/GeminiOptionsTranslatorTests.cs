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
}
