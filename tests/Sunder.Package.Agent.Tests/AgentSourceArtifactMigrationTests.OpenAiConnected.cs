using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Authentication;
using Sunder.Sdk.Worker;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentSourceArtifactMigrationTests
{
    private static async Task VerifyOpenAiConnected(Assembly runtime, Assembly agentOwner, SunderWorkerOptions options, IPackageContext context, OpenAiArtifactHttp backend,
        Func<string, string, object, CancellationToken, Task<JsonElement>> rpc,
        Func<string, object[], Task<JsonElement>> manage,
        Func<ChatMessage[]?, ChatOptions?, CancellationToken, Task<List<ChatResponseUpdate>>> read)
    {
        await context.Settings.SetValueAsync("auth.mode", "codex-connected");
        Assert.Equal("openai-codex-auth-required", (await AssertRoleChatFailure(agentOwner, () => read(null, null, default))).ErrorCode);
        var models = await rpc("chat", "list-models", new { }, default);
        Assert.DoesNotContain(models.EnumerateArray(), model => model.GetProperty("modelId").GetString() == "openai/gpt-5.6");
        Assert.Contains(models.EnumerateArray(), model => model.GetProperty("modelId").GetString() == "openai/gpt-5.6-sol");
        var expiry = DateTimeOffset.UtcNow.AddHours(1);
        await SaveSession(expiry);
        var status = await manage("GetAuthStatusAsync", [CancellationToken.None]);
        Assert.True(status.GetProperty("isConnected").GetBoolean()); Assert.True(status.GetProperty("hasCachedSession").GetBoolean());
        Assert.Equal(expiry, status.GetProperty("expiresAtUtc").GetDateTimeOffset());
        Assert.DoesNotContain("artifact-access", status.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("artifact-refresh", status.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(PackageAuthStatusKind.Connected, (await options.AuthHandler!.GetStatusAsync()).Status);
        Assert.Equal("ready", (await rpc("embedding", "get-readiness", new { }, default)).GetProperty("status").GetString());
        var session = Guid.NewGuid(); var retainedSession = Guid.NewGuid();
        var conversation = session.ToString("N") + ":main";
        var chatOptions = new ChatOptions { ConversationId = conversation, ModelId = "openai/gpt-5.4", Instructions = "Connected instruction" };
        ChatMessage[] first = [new(ChatRole.User, "First")];
        var updates = await read(first, chatOptions, default);
        Assert.Equal("Connected text", string.Concat(updates.SelectMany(value => value.Contents).OfType<TextContent>().Select(value => value.Text)));
        var initial = backend.Requests.Last();
        Assert.Equal("Bearer artifact-access", initial.Authorization); Assert.Equal("artifact-account", initial.Headers["ChatGPT-Account-Id"]);
        Assert.Equal(conversation, initial.Headers["session-id"]);
        var responseId = backend.LastCodexResponseId;
        ChatMessage[] second = [.. first, new(ChatRole.Assistant, "Connected text"), new(ChatRole.User, "Second")];
        await read(second, chatOptions, default);
        Assert.Equal(responseId, backend.Requests.Last().Body.GetProperty("previous_response_id").GetString());
        Assert.Single(backend.Requests.Last().Body.GetProperty("input").EnumerateArray());
        var retained = new ChatOptions { ConversationId = retainedSession.ToString("N") + ":main", ModelId = "openai/gpt-5.4", Instructions = "Connected instruction" };
        await read(first, retained, default); var retainedId = backend.LastCodexResponseId;
        Assert.Equal("sunder.package.agent.provider.openai:codex-response-continuation", (await rpc("session-cleaner", "describe", new { }, default)).GetProperty("id").GetString());
        await rpc("session-cleaner", "delete-session", new { sessionId = session }, default);
        await read(second, chatOptions, default); Assert.False(backend.Requests.Last().Body.TryGetProperty("previous_response_id", out _));
        await read(second, retained, default); Assert.Equal(retainedId, backend.Requests.Last().Body.GetProperty("previous_response_id").GetString());
        var lite = new ChatOptions { ConversationId = conversation, ModelId = "openai/gpt-5.6-sol" };
        await read(first, lite, default); var liteSession = backend.Requests.Last().Headers["session-id"];
        Assert.Equal("true", backend.Requests.Last().Headers["x-openai-internal-codex-responses-lite"]);
        Assert.Equal(7, Guid.Parse(liteSession).Version);
        await read(first, lite, default); Assert.Equal(liteSession, backend.Requests.Last().Headers["session-id"]);
        await rpc("session-cleaner", "delete-session", new { sessionId = session }, default);
        await read(first, lite, default); Assert.NotEqual(liteSession, backend.Requests.Last().Headers["session-id"]);
        Assert.False(backend.Requests.Last().Body.TryGetProperty("previous_response_id", out _));

        var tool = AIFunctionFactory.CreateDeclaration("read_file", "Read", JsonSerializer.SerializeToElement(new
        { type = "object", properties = new { path = new { type = "string" } }, required = new[] { "path" }, additionalProperties = false }));
        backend.CodexMode = "tools";
        var toolOptions = new ChatOptions { ModelId = "openai/gpt-5.4", ConversationId = conversation, Tools = [tool], AllowMultipleToolCalls = true };
        updates = await read([new(ChatRole.User, [new TextContent("Inspect"), new DataContent(new byte[] { 4, 5, 6 }, "application/pdf") { Name = "file.pdf" }])], toolOptions, default);
        var call = Assert.Single(updates.SelectMany(value => value.Contents).OfType<FunctionCallContent>());
        Assert.Equal("call.connected", call.CallId); Assert.Equal("read_file", call.Name);
        Assert.Equal("README.md", Assert.IsType<JsonElement>(call.Arguments!["path"]).GetString());
        Assert.Contains("BAUG", backend.Requests.Last().Body.GetRawText(), StringComparison.Ordinal);
        Assert.True(backend.Requests.Last().Body.GetProperty("parallel_tool_calls").GetBoolean());
        backend.CodexMode = "context-limit";
        var error = await AssertRoleChatFailure(agentOwner, () => read(first, chatOptions, default));
        Assert.Equal(AgentChatProviderFailureKind.ContextWindowExceeded, error.FailureKind);
        Assert.DoesNotContain("artifact-access", error.ToString(), StringComparison.Ordinal);
        backend.CodexMode = "normal";
        var blockedChat = backend.BlockNext("codex");
        using (var cancelled = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        {
            var pending = read(first, chatOptions, cancelled.Token);
            await blockedChat.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            await blockedChat.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        await SaveSession(DateTimeOffset.UtcNow.AddMinutes(-5));
        status = await manage("GetAuthStatusAsync", [CancellationToken.None]);
        Assert.True(status.GetProperty("isConnected").GetBoolean()); Assert.True(backend.AuthRequests > 0);
        await read(first, chatOptions, default);
        Assert.Equal("Bearer refreshed-access", backend.Requests.Last().Authorization);
        await SaveSession(DateTimeOffset.UtcNow.AddMinutes(-5));
        var blockedAuth = backend.BlockNext("auth");
        using (var cancelled = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        {
            var pending = manage("GetAuthStatusAsync", [cancelled.Token]);
            await blockedAuth.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            await blockedAuth.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.False((await manage("DisconnectAuthAsync", [CancellationToken.None])).GetProperty("isConnected").GetBoolean());
        Assert.Null(await context.Secrets.GetSecretAsync("auth.codex.session"));
        Assert.Equal("ready", (await rpc("embedding", "get-readiness", new { }, default)).GetProperty("status").GetString());
        Assert.Equal(PackageAuthStatusKind.NotConnected, (await options.AuthHandler.GetStatusAsync()).Status);
        Assert.Equal("openai-codex-auth-required", (await AssertRoleChatFailure(agentOwner, () => read(first, chatOptions, default))).ErrorCode);

        async Task SaveSession(DateTimeOffset expires)
        {
            var storeType = runtime.GetType("Sunder.Package.Agent.Provider.OpenAI.Auth.CodexSessionStore", true)!;
            var store = Activator.CreateInstance(storeType, context.Secrets)!;
            var value = Activator.CreateInstance(runtime.GetType("Sunder.Package.Agent.Provider.OpenAI.Auth.OpenAiCodexSession", true)!,
                "artifact-access", "artifact-refresh", expires, "artifact-account")!;
            await ((Task)storeType.GetMethod("SaveAsync")!.Invoke(store, [value, CancellationToken.None])!).WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
}
