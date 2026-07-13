using System.Text.Json;
using Google.GenAI.Types;
using Microsoft.Extensions.AI;
using Sunder.Package.Agent.Provider.Gemini;
using Xunit;

namespace Sunder.Package.Agent.Provider.Gemini.Tests;

public sealed class GeminiResponseTranslatorTests
{
    [Fact]
    public void Translate_PreservesReasoningTextSignatureAndContentOrder()
    {
        var response = new GenerateContentResponse
        {
            Candidates =
            [
                new Candidate
                {
                    Content = new Content
                    {
                        Parts =
                        [
                            new Part { Thought = true, Text = "Checking.", ThoughtSignature = [1, 2] },
                            new Part { Text = "Done." },
                        ],
                    },
                },
            ],
        };

        var translation = new GeminiResponseTranslator().Translate(response, allowMultipleToolCalls: true);

        var reasoning = Assert.IsType<TextReasoningContent>(translation.Contents[0]);
        Assert.Equal("Checking.", reasoning.Text);
        Assert.Equal(Convert.ToBase64String([1, 2]), reasoning.ProtectedData);
        Assert.Equal("Done.", Assert.IsType<TextContent>(translation.Contents[1]).Text);
    }

    [Fact]
    public void Translate_FunctionCallPreservesIdNameAndArguments()
    {
        var response = new GenerateContentResponse
        {
            Candidates =
            [
                new Candidate
                {
                    Content = new Content
                    {
                        Parts =
                        [
                            new Part
                            {
                                FunctionCall = new FunctionCall
                                {
                                    Id = "call-1",
                                    Name = "read_file",
                                    Args = new Dictionary<string, object> { ["path"] = "README.md" },
                                },
                                ThoughtSignature = [3, 4],
                            },
                        ],
                    },
                },
            ],
        };

        var contents = new GeminiResponseTranslator().Translate(response, true).Contents;
        var functionCall = Assert.IsType<FunctionCallContent>(contents[0]);

        Assert.Equal("call-1", functionCall.CallId);
        Assert.Equal("read_file", functionCall.Name);
        Assert.Equal("README.md", Assert.IsType<JsonElement>(functionCall.Arguments!["path"]).GetString());
        Assert.Equal(
            Convert.ToBase64String([3, 4]),
            Assert.IsType<TextReasoningContent>(contents[1]).ProtectedData);
    }

    [Fact]
    public void ParseArgumentsJson_MalformedJsonIsRejected()
    {
        var exception = Assert.Throws<Sunder.Package.Agent.Contracts.Models.AgentChatProviderException>(
            () => GeminiResponseTranslator.ParseArgumentsJson("{not-json"));

        Assert.Equal("gemini-malformed-tool-call", exception.ErrorCode);
    }

    [Fact]
    public void Translate_MultipleFunctionCallsHonorsToolCallPolicy()
    {
        var response = new GenerateContentResponse
        {
            Candidates =
            [
                new Candidate
                {
                    Content = new Content
                    {
                        Parts =
                        [
                            new Part { FunctionCall = new FunctionCall { Id = "call-1", Name = "first" } },
                            new Part { FunctionCall = new FunctionCall { Id = "call-2", Name = "second" } },
                        ],
                    },
                },
            ],
        };

        var exception = Assert.Throws<Sunder.Package.Agent.Contracts.Models.AgentChatProviderException>(
            () => new GeminiResponseTranslator().Translate(response, allowMultipleToolCalls: false));
        var allowed = new GeminiResponseTranslator().Translate(response, allowMultipleToolCalls: true);

        Assert.Equal("gemini-multiple-tool-calls", exception.ErrorCode);
        Assert.Equal(2, allowed.Contents.OfType<FunctionCallContent>().Count());
    }

    [Fact]
    public void Translate_PreservesUsageAccounting()
    {
        var response = new GenerateContentResponse
        {
            UsageMetadata = new GenerateContentResponseUsageMetadata
            {
                PromptTokenCount = 12,
                CandidatesTokenCount = 7,
                TotalTokenCount = 19,
                CachedContentTokenCount = 4,
                ThoughtsTokenCount = 3,
            },
        };

        var usage = new GeminiResponseTranslator().Translate(response, true).Usage;

        Assert.Equal(12, usage.InputTokenCount);
        Assert.Equal(7, usage.OutputTokenCount);
        Assert.Equal(19, usage.TotalTokenCount);
        Assert.Equal(4, usage.CachedInputTokenCount);
        Assert.Equal(3, usage.ReasoningTokenCount);
    }
}
