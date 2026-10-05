using System.Text.Json;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentSourceArtifactMigrationTests
{
    private static async Task VerifyLMStudioCatalog(IPackageContext context, LMStudioArtifactHttp handler, LMStudioArtifactClock clock,
        Func<string, string, object, CancellationToken, Task<JsonElement>> rpc)
    {
        var before = handler.ModelRequests;
        clock.Advance(); await rpc("chat", "list-models", new { }, default);
        Assert.Equal(before + 1, handler.ModelRequests);
        foreach (var mode in new[] { "empty", "failure", "invalid" })
        {
            handler.CatalogMode = mode; clock.Advance();
            foreach (var kind in new[] { "chat", "embedding" })
            {
                Assert.Equal(mode == "empty" ? "needs-configuration" : "failed", (await rpc(kind, "get-readiness", new { }, default)).GetProperty("status").GetString());
                Assert.Empty((await rpc(kind, "list-models", new { }, default)).EnumerateArray());
            }
            if (mode == "empty") Assert.Equal(JsonValueKind.Null, (await rpc("chat", "resolve-utility-model", new { }, default)).ValueKind);
        }
        handler.CatalogMode = "normal"; clock.Advance();
        var shared = handler.BlockNext("models"); before = handler.ModelRequests;
        using (var cancelled = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        {
            var first = rpc("chat", "list-models", new { }, cancelled.Token);
            await shared.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var second = rpc("embedding", "get-readiness", new { }, default);
            Assert.False(second.IsCompleted); cancelled.Cancel();
            try
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
                Assert.False(shared.Cancelled.Task.IsCompleted);
            }
            finally { shared.Release.TrySetResult(); }
            Assert.Equal("ready", (await second).GetProperty("status").GetString());
            Assert.Equal(before + 1, handler.ModelRequests);
        }
        clock.Advance(); var abandoned = handler.BlockNext("models");
        using (var cancelled = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        {
            var pending = rpc("chat", "list-models", new { }, cancelled.Token);
            await abandoned.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            await abandoned.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.NotEmpty((await rpc("embedding", "list-models", new { }, default)).EnumerateArray());
        await context.Settings.SetValueAsync("connection.baseUrl", "http://127.0.0.1:43123/old/v1");
        var old = handler.BlockNext("models", "{\"data\":[{\"id\":\"stale-model\",\"type\":\"llm\"}]}");
        var stale = rpc("chat", "list-models", new { }, default);
        await old.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await context.Settings.SetValueAsync("connection.baseUrl", "http://127.0.0.1:43123/current/v1");
        var current = await rpc("chat", "list-models", new { }, default);
        old.Release.TrySetResult();
        Assert.Equal("lmstudio/stale-model", Assert.Single((await stale).EnumerateArray()).GetProperty("modelId").GetString());
        before = handler.ModelRequests;
        Assert.Equal(current.GetRawText(), (await rpc("chat", "list-models", new { }, default)).GetRawText());
        Assert.Equal(before, handler.ModelRequests);
    }

    private static async Task VerifyLMStudioEmbeddings(LMStudioArtifactHttp handler,
        Func<string, string, object, CancellationToken, Task<JsonElement>> rpc)
    {
        var before = handler.Requests.Count;
        Assert.Equal(JsonValueKind.Null, (await rpc("embedding", "generate", new { modelId = "lmstudio/embedding-model", text = " " }, default)).ValueKind);
        Assert.Empty((await rpc("embedding", "generate-batch", new { modelId = "lmstudio/embedding-model", texts = Array.Empty<string>() }, default)).EnumerateArray());
        Assert.Equal(before, handler.Requests.Count);
        var request = new { modelId = "lmstudio/embedding-model", texts = new[] { "first", " ", "second" } };
        var result = await rpc("embedding", "generate-batch", request, default);
        Assert.Equal(3, result.GetArrayLength()); Assert.Equal(JsonValueKind.Null, result[1].ValueKind);
        Assert.Equal(0.25, result[0].GetProperty("values")[0].GetDouble()); Assert.Equal(1.25, result[2].GetProperty("values")[0].GetDouble());
        Assert.Equal(3, result[0].GetProperty("dimensions").GetInt32());
        Assert.Equal("lmstudio/embedding-model", result[2].GetProperty("modelId").GetString());
        var body = handler.Requests.Last().Body!.Value;
        Assert.Equal("embedding-model", body.GetProperty("model").GetString());
        Assert.Equal(new[] { "first", "second" }, body.GetProperty("input").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal(3, (await rpc("embedding", "generate", new { modelId = "lmstudio/embedding-model", text = "single" }, default)).GetProperty("dimensions").GetInt32());
        foreach (var mode in new[] { "duplicate", "missing", "out-of-range" })
        {
            handler.EmbeddingMode = mode;
            var error = await Assert.ThrowsAsync<SunderRpcException>(() => rpc("embedding", "generate-batch", request, default));
            Assert.Equal(SunderRpcErrorKind.ProviderFaulted, error.Error.Kind);
            Assert.DoesNotContain("artifact-lmstudio-key", error.Message, StringComparison.Ordinal);
        }
        handler.EmbeddingMode = "normal";
        var blocked = handler.BlockNext("embeddings");
        using var cancelled = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pending = rpc("embedding", "generate-batch", request, cancelled.Token);
        await blocked.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await blocked.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
