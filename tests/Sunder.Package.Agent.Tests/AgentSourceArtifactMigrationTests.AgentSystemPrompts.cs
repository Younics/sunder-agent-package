using System.Reflection;
using System.Text.Json;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Worker;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentSourceArtifactMigrationTests
{
    private static async Task VerifyAgentSystemPrompts(Assembly owner, Assembly providers)
    {
        using var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
        var rpc = new RegressionTestRpcClient(state, "sunder.package.agent");
        using var catalog = AgentRpcCatalog.CreateDormant(rpc);
        var composerType = owner.GetType("Sunder.Package.Agent.Services.AgentSystemPromptComposer", true)!;
        var composer = AgentRoleBoundary.Create(composerType, catalog);
        Func<AgentSystemPromptRequest, string?, CancellationToken, ValueTask<string?>> compose = (request, text, token) =>
            AgentRoleBoundary.As<ValueTask<string?>>(AgentRoleBoundary.Call(composer, "ComposeAsync", request, text, token)!);
        var request = SystemPromptRequest();
        var baseline = await compose(request, "Static base instructions", default);
        Assert.StartsWith("Static base instructions", baseline, StringComparison.Ordinal);
        Assert.Contains("Current UTC Date And Time", baseline!, StringComparison.Ordinal);
        Assert.Contains("Context Trust Boundary", baseline!, StringComparison.Ordinal);
        Assert.Contains("Tool Runtime Context", baseline!, StringComparison.Ordinal);
        Assert.Contains("Tool Concurrency", baseline!, StringComparison.Ordinal);
        Assert.Empty((await rpc.DiscoverAsync(AgentRpcContractIds.SystemPromptContributor)).Providers);

        var calls = 0;
        JsonElement received = default;
        AgentSystemPromptBlock[] blocks =
        [
            new("duplicate", "Ignored optional", "Optional duplicate", Priority: 999, SourceId: "fixture.prompt"),
            new("DUPLICATE", "Required fixture", "Required package policy", Priority: 1, Required: true, SourceId: "FIXTURE.PROMPT"),
            new("bounded", "Bounded fixture", "abcdefghijk", Priority: 3, MaxChars: 5, SourceId: "fixture.prompt",
                Metadata: new Dictionary<string, string> { ["diagnostic"] = "metadata-not-rendered" }),
            new("blank", " ", "Blank-title omission", SourceId: "fixture.prompt"),
        ];
        Exception? failure = null;
        FilesCallGate? gate = null;
        Func<JsonElement, CancellationToken, ValueTask<JsonElement>> callback = async (value, token) =>
        {
            calls++; received = value.Clone();
            if (gate is { } active)
            {
                active.Started.TrySetResult();
                try { await active.Release.Task.WaitAsync(token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { active.Cancelled.TrySetResult(); throw; }
            }
            if (failure is not null) throw failure;
            return JsonSerializer.SerializeToElement(blocks, MemoryProviderJson);
        };
        var registration = Add("example.agent.prompt", "Prompt fixture", callback);
        var snapshot = Assert.Single((await rpc.DiscoverAsync(AgentRpcContractIds.SystemPromptContributor)).Providers);
        var observed = new FilesRpcObserver(rpc, "contribute");
        var clientType = owner.GetType("Sunder.Package.Agent.Runtime.Rpc.GeneratedSystemPromptClient", true)!;
        IAgentSystemPromptContributor Create(SunderRpcProviderSnapshot selected) => AgentRoleBoundary.As<IAgentSystemPromptContributor>(Activator.CreateInstance(clientType, observed, selected)!);
        var client = Create(snapshot); await using var lifetime = (IAsyncDisposable)client;
        Assert.Equal("fixture.prompt", client.ContributorId); Assert.Equal("Prompt fixture", client.DisplayName);
        var result = await client.ContributeAsync(request);
        Assert.Equal(blocks.Length, result.Count); Assert.Equal(5, result[2].MaxChars);
        Assert.Equal("metadata-not-rendered", result[2].Metadata!["diagnostic"]);
        Assert.Equal(request.RunId, received.GetProperty("runId").GetGuid());
        Assert.Equal(request.RunRevision, received.GetProperty("runRevision").GetInt64());
        Assert.EndsWith("Z", received.GetProperty("runStartedAtUtc").GetString(), StringComparison.Ordinal);
        Assert.Equal(request.RunStartedAtUtc, received.GetProperty("runStartedAtUtc").GetDateTimeOffset());
        Assert.Equal("untrusted-profile", received.GetProperty("profile").GetProperty("instructions").GetString());
        Assert.Equal("untrusted-user-message", received.GetProperty("userMessage").GetString());
        Assert.Equal("untrusted-transcript", received.GetProperty("turns")[0].GetProperty("items")[0].GetProperty("textContent").GetString());
        Assert.Equal("parallel-safe", received.GetProperty("availableTools")[0].GetProperty("concurrencyMode").GetString());
        Assert.Equal(12345, received.GetProperty("runCapabilities").GetProperty("contextWindowTokens").GetInt64());
        Assert.False(received.TryGetProperty("executionBinding", out _));

        var rendered = (await compose(request, "Static base instructions", default))!;
        Assert.Contains("Required package policy", rendered, StringComparison.Ordinal);
        Assert.Contains("abcde\n\n[truncated]", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("Optional duplicate", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("Blank-title omission", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("metadata-not-rendered", rendered, StringComparison.Ordinal);
        Assert.True(rendered.IndexOf("Required fixture", StringComparison.Ordinal) < rendered.IndexOf("Bounded fixture", StringComparison.Ordinal));
        foreach (var text in new[] { "untrusted-profile", "untrusted-user-message", "untrusted-transcript", "untrusted-workspace" })
            Assert.DoesNotContain(text, rendered, StringComparison.Ordinal);
        failure = new IOException("private-prompt-failure");
        Assert.Equal(baseline, await compose(request, "Static base instructions", default));
        Assert.Equal(SunderRpcErrorKind.ProviderFaulted, (await Assert.ThrowsAsync<SunderRpcException>(() => client.ContributeAsync(request).AsTask())).Error.Kind);
        failure = null;

        foreach (var change in new Func<SunderRpcProviderSnapshot, SunderRpcProviderSnapshot>[]
        {
            value => value with { PackageId = "other.package" }, value => value with { PackageVersion = "2.0.1" },
            value => value with { ProviderId = "other.provider" }, value => value with { ContractId = "other.contract" },
            value => value with { ContractVersion = "1.0.1" }, value => value with { DescriptorSha256 = new string('a', 64) },
            value => value with { AbiSha256 = new string('b', 64) }, value => value with { ActivationId = Guid.NewGuid() },
            value => value with { ActivationEpoch = value.ActivationEpoch + 1 }, value => value with { SessionGeneration = value.SessionGeneration + 1 },
            value => value with { Endpoint = new("other-endpoint") },
        })
        {
            observed.Transform = change; var before = calls;
            Assert.Equal(SunderRpcErrorKind.StaleEndpoint, (await Assert.ThrowsAsync<SunderRpcException>(() => client.ContributeAsync(request).AsTask())).Error.Kind);
            Assert.Equal(before, calls);
        }
        observed.Transform = null; observed.InvalidateAfterInvoke = true;
        Assert.Equal(SunderRpcErrorKind.StaleEndpoint, (await Assert.ThrowsAsync<SunderRpcException>(() => client.ContributeAsync(request).AsTask())).Error.Kind);
        observed.Transform = null; observed.InvalidateAfterInvoke = false;
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel(); var before = calls;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ContributeAsync(request, cancelled.Token).AsTask());
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => compose(request, "Static base instructions", cancelled.Token).AsTask());
            Assert.Equal(before, calls);
        }
        gate = new();
        using (var cancelled = new CancellationTokenSource())
        {
            var active = compose(request, "Static base instructions", cancelled.Token).AsTask();
            await gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => active);
            await gate.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        gate = new();
        var retiring = compose(request, "Static base instructions", default).AsTask();
        await gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await state.RetireAsync(registration.Handler).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(baseline, await retiring);
        gate = null;
        Add("example.agent.prompt", "Replacement prompt", callback);
        Assert.Equal(SunderRpcErrorKind.StaleEndpoint, (await Assert.ThrowsAsync<SunderRpcException>(() => client.ContributeAsync(request).AsTask())).Error.Kind);
        Assert.Contains("Required package policy", (await compose(request, "Static base instructions", default))!, StringComparison.Ordinal);
        var replacement = Create(Assert.Single((await rpc.DiscoverAsync(AgentRpcContractIds.SystemPromptContributor)).Providers));
        await using var replacementLifetime = (IAsyncDisposable)replacement;
        Assert.Equal("Replacement prompt", replacement.DisplayName);
        gate = new();
        var stoppingCall = replacement.ContributeAsync(request).AsTask();
        await gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stop = replacementLifetime.DisposeAsync().AsTask();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stoppingCall);
        await stop.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(SunderRpcErrorKind.StaleEndpoint, Assert.Throws<SunderRpcException>(() => replacement.DisplayName).Error.Kind);
        Assert.Equal(0, observed.UnscopedLookups); Assert.All(observed.Scopes, scope => Assert.True(scope.Disposed));

        SunderWorkerProviderRegistration Add(string packageId, string name, Func<JsonElement, CancellationToken, ValueTask<JsonElement>> respond)
        {
            var type = providers.GetType("PromptProvider", true)!;
            var provider = Activator.CreateInstance(type, name, respond)!;
            var value = (SunderWorkerProviderRegistration)type.GetMethod("CreateSunderRpcProviderRegistration")!.Invoke(provider, null)!;
            state.Add(packageId, RegressionTestRpcDescriptors.Get(AgentRpcContractIds.SystemPromptContributor), value.Handler,
                value.Handler, Noop.Instance, value.ProviderId, "2.0.0");
            return value;
        }
    }

    private static AgentSystemPromptRequest SystemPromptRequest()
    {
        var now = new DateTimeOffset(2026, 9, 28, 12, 34, 56, TimeSpan.FromHours(2));
        var session = Guid.NewGuid(); var turn = Guid.NewGuid();
        return new(new(session, "untrusted-title", AgentSessionState.Active, now, now, ProfileId: "profile.prompt"),
            new("profile.prompt", "Profile", null, "untrusted-profile", "provider.prompt", "model.prompt", null, null, now, now),
            "provider.prompt", "model.prompt", new(true, true, true, "Fixture capabilities", true, true, true, true, 12345, 678),
            new("workspace.prompt", "untrusted-workspace", null, now, now), null,
            [new("tool.prompt", "Fixture tool", "Tool description", RuntimeInstructions: "Static tool instructions") { ConcurrencyMode = AgentToolConcurrencyMode.ParallelSafe }],
            [new(turn, session, AgentMessageRole.User, AgentTurnKind.Message,
                [new(Guid.NewGuid(), turn, 0, AgentTurnItemKind.Text, "untrusted-transcript", null, null, null, null, null, null, false, false, null, null)], now, now)],
            Guid.NewGuid(), 7, now, "untrusted-user-message");
    }
}
