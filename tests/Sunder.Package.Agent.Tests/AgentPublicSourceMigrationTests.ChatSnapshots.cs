using System.Text.Json;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentPublicSourceMigrationTests
{
    // Assertion-only value snapshots. Content is read through its real SDK
    // authority; these objects are never sent as RPC requests or responses.
    private static async Task<JsonElement> ChatEventSnapshotAsync(AgentChatStreamEvent value,
        CancellationToken token = default, bool received = false)
    {
        if (value is AgentChatErrorEvent error)
            return RunJson(new { kind = "error", error.ErrorCode, error.ErrorMessage, error.ErrorContent, error.ErrorFailureKind });
        var update = Assert.IsType<AgentChatUpdateEvent>(value);
        var contents = update.ContentItems is null ? null : await ChatContentSnapshotsAsync(update.ContentItems, token, received);
        return RunJson(new
        {
            kind = "update", update.Role, update.ResponseId, update.MessageId, update.ConversationId,
            update.ModelId, update.FinishReason, update.CreatedAt, ContentItems = contents, update.ContinuationHandle,
        });
    }

    private static async Task<JsonElement> ChatMessagesSnapshotAsync(IReadOnlyList<AgentChatMessage> messages,
        CancellationToken token = default)
    {
        var values = new List<object>();
        foreach (var message in messages)
            values.Add(new { message.Role, message.MessageId, message.AuthorName,
                Contents = await ChatContentSnapshotsAsync(message.Contents, token, received: false) });
        return RunJson(values);
    }

    private static async Task<IReadOnlyList<Dictionary<string, object?>>> ChatContentSnapshotsAsync(
        IReadOnlyList<AgentChatContent> contents, CancellationToken token, bool received)
    {
        var values = new List<Dictionary<string, object?>>();
        foreach (var item in contents)
        {
            object? content = null;
            if (item.Content is { } resource)
            {
                await using var owned = resource;
                if (received)
                {
                    Assert.NotNull(resource.ExpiresAtUtc);
                    Assert.True(resource.ExpiresAtUtc > DateTimeOffset.UtcNow);
                    // SDK registration defaults to a single-use transferred
                    // lease; local FromBytes repeatability is not transferred.
                    Assert.False(resource.IsRepeatable);
                }
                else Assert.True(resource.IsRepeatable);
                await using var stream = await resource.OpenReadAsync(token);
                using var bytes = new MemoryStream();
                await stream.CopyToAsync(bytes, token);
                Assert.Equal(resource.Length, bytes.Length);
                content = new { resource.MediaType, resource.FileName, resource.Length, resource.Sha256,
                    Bytes = bytes.ToArray() };
            }
            var snapshot = new Dictionary<string, object?>
            {
                ["kind"] = item.Kind, ["text"] = item.Text, ["protectedData"] = item.ProtectedData,
                ["callId"] = item.CallId, ["name"] = item.Name, ["informationalOnly"] = item.InformationalOnly,
                ["mediaType"] = item.MediaType, ["contentUri"] = item.ContentUri, ["content"] = content,
                ["usage"] = item.Usage, ["errorCode"] = item.ErrorCode, ["errorMessage"] = item.ErrorMessage,
            };
            if (item.Json.TryGetValue(out var json)) snapshot["json"] = json;
            values.Add(snapshot);
        }
        return values;
    }
}
