using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Services;
using Sunder.Package.Agent.Services.BehaviorLoops;
using Sunder.Package.Agent.Storage;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class AgentToolImageTests
{
    private static byte[] Png => Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGO4I2fzHwAFRgI258Nk7wAAAABJRU5ErkJggg==");
    private static AgentToolResult Image(byte[]? bytes = null, string? digest = null)
    {
        bytes ??= Png;
        return new("maker_screenshot", "Package screenshot", "Image", StructuredPayloadJson: JsonSerializer.Serialize(new
        { type = "sunder.tool-image.v1", mediaType = "image/png", sha256 = digest ?? Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes }));
    }

    [Fact]
    public async Task AdoptedImageIsSessionCallOwnedDurableAndRetainedUntilSessionCleanup()
    {
        using var scope = RegressionTestPackageScope.Create();
        var attachments = new AgentAttachmentService(scope.Context);
        var tools = new AgentToolService(null!, null!, null!, null!, null!, attachments);
        var store = new AgentLocalStore(scope.Context);
        var sessions = new AgentSessionService(store);
        var workspace = new AgentWorkspaceService(store).CreateWorkspace("Tool images");
        var session = sessions.CreateSession("Tool images", workspaceId: workspace.WorkspaceId);
        var context = new AgentToolExecutionContext(session.SessionId, RunId: Guid.NewGuid(), RunRevision: 1, ToolCallId: "capture-1");
        var adopted = await tools.AdoptToolImageAsync(Image(), context, default);
        var metadata = Assert.IsType<AgentAttachmentMetadata>(AgentToolService.ReadStoredToolImage(adopted.StructuredPayloadJson));
        Assert.StartsWith(session.SessionId.ToString("N") + "/" + AgentToolService.ToolImageOwner(context.RunId!.Value, context.ToolCallId!).ToString("N") + "/", metadata.StorageRelativePath);
        Assert.DoesNotContain(Convert.ToBase64String(Png), adopted.StructuredPayloadJson);
        sessions.AppendToolResultTurn(session.SessionId, "capture-1", adopted.ToolId, "{}", adopted.Content, adopted.Summary,
            adopted.StructuredPayloadJson, null, false, false, null, null);
        // Ordinary tool JSON is not host attachment metadata, even if its fields
        // resemble a legacy attachment. Only adopted image envelopes can retain it.
        sessions.AppendToolResultTurn(session.SessionId, "foreign-path", "ordinary.tool", "{}", "Original tool output", null,
            "{\"StorageRelativePath\":\"unowned/package-view.png\"}", null, false, false, null, null);
        var reopened = new AgentAttachmentService(scope.Context);
        Assert.Equal(Png, await reopened.ReadAttachmentBytesAsync(metadata));
        Assert.Equal(metadata.StorageRelativePath, Assert.Single(new AgentLocalStore(scope.Context).ListReferencedAttachmentPaths()));
        Assert.Equal(0, reopened.CleanupOrphans(store.ListReferencedAttachmentPaths(), DateTimeOffset.MaxValue));
        reopened.DeleteSessionData(session.SessionId);
        await Assert.ThrowsAsync<FileNotFoundException>(() => reopened.ReadAttachmentBytesAsync(metadata));
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("dimensions")]
    [InlineData("checksum")]
    [InlineData("truncated")]
    [InlineData("forged-storage")]
    public async Task InvalidImagesCannotCreateHostAttachmentAuthority(string failure)
    {
        using var scope = RegressionTestPackageScope.Create();
        var attachments = new AgentAttachmentService(scope.Context);
        var tools = new AgentToolService(null!, null!, null!, null!, null!, attachments);
        var bytes = Png;
        if (failure == "dimensions") BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16, 4), 2049);
        if (failure == "checksum") bytes[48] ^= 1;
        if (failure == "truncated") bytes = bytes[..33];
        var result = failure == "forged-storage" ? Image() with { StructuredPayloadJson = "{\"type\":\"sunder.stored-tool-image.v1\",\"attachment\":{}}" }
            : Image(bytes, failure == "hash" ? new('0', 64) : null);
        await Assert.ThrowsAsync<InvalidDataException>(() => tools.AdoptToolImageAsync(result, new(Guid.NewGuid(), RunId: Guid.NewGuid(), ToolCallId: "capture"), default));
        Assert.Equal(0, attachments.CleanupOrphans(new HashSet<string>(), DateTimeOffset.MaxValue));
    }

    [Fact]
    public async Task PromptIncludesOnlyNewestCurrentRunImageAndKeepsBytesInTypedContent()
    {
        using var scope = RegressionTestPackageScope.Create();
        var attachments = new AgentAttachmentService(scope.Context);
        var tools = new AgentToolService(null!, null!, null!, null!, null!, attachments);
        var store = new AgentLocalStore(scope.Context);
        var sessions = new AgentSessionService(store);
        var workspace = new AgentWorkspaceService(store).CreateWorkspace("Image prompt");
        var session = sessions.CreateSession("Image prompt", workspaceId: workspace.WorkspaceId);
        var runId = Guid.NewGuid();
        var user = sessions.AppendUserTurn(session.SessionId, AgentMessageRole.User, "Inspect this view", []);
        List<AgentTurnRecord> turns = [user];
        for (var index = 0; index < 2; index++)
        {
            var call = "capture-" + index;
            var result = await tools.AdoptToolImageAsync(Image(), new(session.SessionId, RunId: runId, RunRevision: 1, ToolCallId: call), default);
            turns.Add(sessions.AppendToolCallTurn(session.SessionId, AgentMessageRole.Assistant, call, result.ToolId, "{}") with { RunId = runId, RunRevision = 1 });
            turns.Add(sessions.AppendToolResultTurn(session.SessionId, call, result.ToolId, "{}", result.Content, result.Summary,
                result.StructuredPayloadJson, null, false, false, null, null) with { RunId = runId, RunRevision = 1 });
        }
        var pipeline = new AgentPromptPreparationPipeline(new AgentSystemPromptComposer(null!), attachments);
        var capabilities = new AgentProviderRunCapabilities(true, true, true, "fixture", SupportsImageInput: true);
        var messages = await pipeline.BuildPromptMessagesAsync(turns, user.TurnId, false, false, capabilities, default, runId, 1);
        var imageResult = Assert.Single(messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>(), result => result.Result is IEnumerable<AIContent>);
        Assert.Equal("capture-1", imageResult.CallId);
        var content = AgentChatMapper.FromContent(imageResult);
        Assert.Equal("tool-result", content.Kind);
        Assert.NotNull(content.Content);
        Assert.DoesNotContain(Convert.ToBase64String(Png), JsonSerializer.Serialize(content.Json));
        await using (content.Content!)
        {
            await using var stream = await content.Content!.OpenReadAsync();
            using var bytes = new MemoryStream(); await stream.CopyToAsync(bytes);
            Assert.Equal(Png, bytes.ToArray());
        }
        var imageTurn = turns.Last(turn => turn.Kind == AgentTurnKind.ToolResult);
        var imageItem = Assert.Single(imageTurn.Items);
        var metadata = AgentToolService.ReadStoredToolImage(imageItem.StructuredPayloadJson)!;
        var foreignItem = imageItem with { StructuredPayloadJson = JsonSerializer.Serialize(new { type = AgentToolService.StoredToolImageType,
            attachment = metadata with { StorageRelativePath = Guid.NewGuid().ToString("N") + "/foreign/image.png" } }) };
        var foreignTurns = turns.Select(turn => turn.TurnId == imageTurn.TurnId ? turn with { Items = [foreignItem] } : turn).ToArray();
        var foreignMessages = await pipeline.BuildPromptMessagesAsync(foreignTurns, user.TurnId, false, false, capabilities, default, runId, 1);
        Assert.DoesNotContain(foreignMessages.SelectMany(message => message.Contents).OfType<FunctionResultContent>(), result => result.Result is IEnumerable<AIContent>);
        await File.AppendAllTextAsync(attachments.GetAttachmentContentPath(metadata), "corrupt");
        var corruptMessages = await pipeline.BuildPromptMessagesAsync(turns, user.TurnId, false, false, capabilities, default, runId, 1);
        var corruptResult = corruptMessages.SelectMany(message => message.Contents).OfType<FunctionResultContent>().Single(result => result.CallId == "capture-1");
        Assert.Contains("Image unavailable", Assert.IsType<string>(corruptResult.Result));
        var nextUser = sessions.AppendUserTurn(session.SessionId, AgentMessageRole.User, "Next run", []);
        turns.Add(nextUser);
        var nextMessages = await pipeline.BuildPromptMessagesAsync(turns, nextUser.TurnId, false, false, capabilities, default, Guid.NewGuid(), 2);
        Assert.DoesNotContain(nextMessages.SelectMany(message => message.Contents).OfType<FunctionResultContent>(), result => result.Result is IEnumerable<AIContent>);
        var unsupported = await pipeline.BuildPromptMessagesAsync(turns, user.TurnId, false, false, capabilities with { SupportsImageInput = false }, default, runId, 1);
        Assert.DoesNotContain(unsupported.SelectMany(message => message.Contents).OfType<FunctionResultContent>(), result => result.Result is IEnumerable<AIContent>);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScreenshotQuotaDoesNotDeletePreviouslyStoredImages(bool byteQuota)
    {
        using var scope = RegressionTestPackageScope.Create();
        var attachments = new AgentAttachmentService(scope.Context);
        var session = Guid.NewGuid();
        AgentAttachmentMetadata? first = null;
        for (var index = 0; index < (byteQuota ? 1 : 64); index++)
        {
            var stored = await attachments.StoreToolImageAsync(session, Guid.NewGuid(), Png, default);
            first ??= stored.Metadata;
        }
        string? filledPath = null;
        if (byteQuota)
        {
            filledPath = attachments.GetAttachmentContentPath(first!);
            using var file = File.OpenWrite(filledPath);
            file.SetLength(32 * 1024 * 1024);
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => attachments.StoreToolImageAsync(session, Guid.NewGuid(), Png, default));
        if (byteQuota) Assert.Equal(32 * 1024 * 1024, new FileInfo(filledPath!).Length);
        else Assert.Equal(Png, await attachments.ReadAttachmentBytesAsync(first!));
    }
}
