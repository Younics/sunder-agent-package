using Microsoft.Extensions.AI;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Provider.Gemini;
using Xunit;
using AIChatMessage = Microsoft.Extensions.AI.ChatMessage;
using AIChatRole = Microsoft.Extensions.AI.ChatRole;

namespace Sunder.Package.Agent.Provider.Gemini.Tests;

public sealed class GeminiContentTranslatorTests
{
    [Fact]
    public void Translate_PreservesOrderedMixedContentAndFunctionCorrelation()
    {
        AIChatMessage[] messages =
        [
            new(AIChatRole.System, "Follow the policy."),
            new(AIChatRole.User,
            [
                new TextContent("before"),
                new DataContent(new byte[] { 1, 2, 3 }, "image/png") { Name = "image.png" },
                new TextContent("after"),
            ]),
            new(AIChatRole.Assistant,
            [
                new TextContent("checking"),
                new FunctionCallContent("call-1", "read_file", new Dictionary<string, object?>
                {
                    ["path"] = "README.md",
                }),
                new FunctionCallContent("call-2", "search_files", new Dictionary<string, object?>
                {
                    ["pattern"] = "*.cs",
                }),
            ]),
            new(AIChatRole.Tool,
            [
                new FunctionResultContent("call-2", "search results"),
                new TextContent("between"),
                new FunctionResultContent("call-1", "file contents"),
            ]),
        ];

        var translation = new GeminiContentTranslator().Translate(messages);

        Assert.Equal("Follow the policy.", Assert.Single(translation.SystemInstruction!.Parts!).Text);
        Assert.Equal(["user", "model", "user"], translation.Contents.Select(content => content.Role));
        Assert.Collection(
            translation.Contents[0].Parts!,
            part => Assert.Equal("before", part.Text),
            part =>
            {
                Assert.Equal("image/png", part.InlineData?.MimeType);
                Assert.Equal(new byte[] { 1, 2, 3 }, part.InlineData?.Data);
            },
            part => Assert.Equal("after", part.Text));
        Assert.Collection(
            translation.Contents[2].Parts!,
            part =>
            {
                Assert.Equal("call-2", part.FunctionResponse?.Id);
                Assert.Equal("search_files", part.FunctionResponse?.Name);
            },
            part => Assert.Equal("between", part.Text),
            part =>
            {
                Assert.Equal("call-1", part.FunctionResponse?.Id);
                Assert.Equal("read_file", part.FunctionResponse?.Name);
            });
    }

    [Fact]
    public void Translate_MapsImagePdfAudioAndVideoAttachments()
    {
        var message = new AIChatMessage(AIChatRole.User,
        [
            new DataContent(new byte[] { 1 }, "image/jpeg"),
            new DataContent(new byte[] { 2 }, "application/pdf"),
            new DataContent(new byte[] { 3 }, "audio/mpeg"),
            new DataContent(new byte[] { 4 }, "video/mp4"),
        ]);

        var parts = Assert.Single(new GeminiContentTranslator().Translate([message]).Contents).Parts!;

        Assert.Equal(
            ["image/jpeg", "application/pdf", "audio/mpeg", "video/mp4"],
            parts.Select(part => part.InlineData?.MimeType));
        Assert.Equal([1, 2, 3, 4], parts.Select(part => part.InlineData!.Data![0]));
    }

    [Fact]
    public void Translate_ReassociatesSavedThoughtSignatureWithFunctionCall()
    {
        var message = new AIChatMessage(AIChatRole.Assistant,
        [
            new FunctionCallContent("call-1", "read_file", null),
            new TextReasoningContent(string.Empty) { ProtectedData = Convert.ToBase64String([5, 6]) },
        ]);

        var part = Assert.Single(
            Assert.Single(new GeminiContentTranslator().Translate([message]).Contents).Parts!);

        Assert.Equal(new byte[] { 5, 6 }, part.ThoughtSignature);
    }

    [Fact]
    public void Translate_OrphanedFunctionResultIsRejected()
    {
        var message = new AIChatMessage(AIChatRole.Tool,
            [new FunctionResultContent("missing-call", "result")]);

        var exception = Assert.Throws<InvalidOperationException>(
            () => new GeminiContentTranslator().Translate([message]));

        Assert.Contains("missing-call", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Translate_UnsupportedContentIsExplicitlyRejected()
    {
        var message = new AIChatMessage(AIChatRole.User, [new AIContent()]);

        var exception = Assert.Throws<AgentChatProviderException>(
            () => new GeminiContentTranslator().Translate([message]));

        Assert.Equal("gemini-unsupported-content", exception.ErrorCode);
    }

    [Fact]
    public void Translate_UnsupportedRoleIsExplicitlyRejected()
    {
        var message = new AIChatMessage(new AIChatRole("developer"), "instruction");

        var exception = Assert.Throws<AgentChatProviderException>(
            () => new GeminiContentTranslator().Translate([message]));

        Assert.Equal("gemini-unsupported-role", exception.ErrorCode);
    }
}
