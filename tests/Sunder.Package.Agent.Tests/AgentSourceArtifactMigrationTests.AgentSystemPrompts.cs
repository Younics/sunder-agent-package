using System.Reflection;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using AgentServices = Sunder.Generated.Packages.Sunder_Package_Agent_Services;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentSourceArtifactMigrationTests
{
    private static async Task VerifyAgentSystemPrompts(Assembly owner, Assembly providers)
    {
        _ = providers; // Imported artifact providers are covered by the controlled generated-provider fixture.
        var composerType = owner.GetType("Sunder.Package.Agent.Services.AgentSystemPromptComposer", true)!;
        var serviceType = owner.GetType(typeof(AgentServices.ISystemPromptContributorService).FullName!, true)!;
        var catalogType = typeof(FakeServiceCatalog<>).MakeGenericType(serviceType);
        await using var catalog = (IAsyncDisposable)AgentRoleBoundary.Create(catalogType,
            CurrentPublicRpcDescriptors.Get(RegressionTestBindings.SystemPromptContributors))!;
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

        AgentSystemPromptBlock[] blocks =
        [
            new("duplicate", "Ignored optional", "Optional duplicate", Priority: 999, SourceId: "fixture.prompt"),
            new("DUPLICATE", "Required fixture", "Required package policy", Priority: 1, Required: true, SourceId: "FIXTURE.PROMPT"),
            new("bounded", "Bounded fixture", "abcdefghijk", Priority: 3, MaxChars: 5, SourceId: "fixture.prompt",
                Metadata: new Dictionary<string, string> { ["diagnostic"] = "metadata-not-rendered" }),
            new("blank", " ", "Blank-title omission", SourceId: "fixture.prompt"),
        ];
        var peer = new AgentSystemPromptPeer("Prompt fixture") { Blocks = blocks };
        var registration = Add("example.agent.prompt", peer);
        Assert.Null(owner.GetType("Sunder.Package.Agent.Runtime.Rpc.GeneratedSystemPromptClient"));
        var result = await peer.ContributeAsync(request);
        Assert.Equal(blocks.Length, result.Count); Assert.Equal(5, result[2].MaxChars);
        Assert.Equal("metadata-not-rendered", result[2].Metadata!["diagnostic"]);
        var received = peer.Request!;
        Assert.Equal(request.RunId, received.RunId); Assert.Equal(request.RunRevision, received.RunRevision);
        Assert.Equal(request.RunStartedAtUtc, received.RunStartedAtUtc);
        Assert.Equal("untrusted-profile", received.Profile!.Instructions);
        Assert.Equal("untrusted-user-message", received.UserMessage);
        Assert.Equal("untrusted-transcript", received.Turns[0].Items[0].TextContent);
        Assert.Equal(AgentToolConcurrencyMode.ParallelSafe, received.AvailableTools[0].ConcurrencyMode);
        Assert.Equal(12345, received.RunCapabilities.ContextWindowTokens);
        Assert.Null(received.ExecutionBinding);

        var rendered = (await compose(request, "Static base instructions", default))!;
        Assert.Contains("Required package policy", rendered, StringComparison.Ordinal);
        Assert.Contains("abcde\n\n[truncated]", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("Optional duplicate", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("Blank-title omission", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("metadata-not-rendered", rendered, StringComparison.Ordinal);
        Assert.True(rendered.IndexOf("Required fixture", StringComparison.Ordinal) < rendered.IndexOf("Bounded fixture", StringComparison.Ordinal));
        foreach (var text in new[] { "untrusted-profile", "untrusted-user-message", "untrusted-transcript", "untrusted-workspace" })
            Assert.DoesNotContain(text, rendered, StringComparison.Ordinal);
        peer.Failure = new IOException("private-prompt-failure");
        Assert.Equal(baseline, await compose(request, "Static base instructions", default));
        await Assert.ThrowsAsync<IOException>(() => peer.ContributeAsync(request).AsTask());
        peer.Failure = null;
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel(); var before = peer.Calls;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => compose(request, "Static base instructions", cancelled.Token).AsTask());
            Assert.Equal(before, peer.Calls);
        }
        peer.Gate = new();
        using (var cancelled = new CancellationTokenSource())
        {
            var active = compose(request, "Static base instructions", cancelled.Token).AsTask();
            await peer.Gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => active);
            await peer.Gate.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Retire(registration);
        Assert.Equal(baseline, await compose(request, "Static base instructions", default));
        var replacement = new AgentSystemPromptPeer("Replacement prompt") { Blocks = blocks };
        Add("example.agent.prompt", replacement);
        Assert.Contains("Required package policy", (await compose(request, "Static base instructions", default))!, StringComparison.Ordinal);
        Assert.Equal("Replacement prompt", (await replacement.DescribeAsync()).DisplayName);

        object Add(string packageId, AgentSystemPromptPeer value)
        {
            return AgentRoleBoundary.Call(catalog, "Add", AgentRoleBoundary.Convert(value, serviceType), packageId, "fixture.system.prompt")!;
        }

        void Retire(object provider) => AgentRoleBoundary.Call(catalog, "Retire", provider);
    }

    private sealed class AgentSystemPromptPeer(string displayName) : AgentServices.ISystemPromptContributorService
    {
        internal AgentSystemPromptBlock[] Blocks { get; set; } = [];
        internal AgentSystemPromptRequest? Request { get; private set; }
        internal Exception? Failure { get; set; }
        internal FilesCallGate? Gate { get; set; }
        internal int Calls { get; private set; }

        public ValueTask<AgentServiceIdentity> DescribeAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new AgentServiceIdentity("fixture.prompt", displayName));
        }

        public async ValueTask<IReadOnlyList<AgentSystemPromptBlock>> ContributeAsync(AgentSystemPromptRequest request, CancellationToken cancellationToken = default)
        {
            Calls++; Request = request;
            if (Gate is { } gate)
            {
                gate.Started.TrySetResult();
                try { await gate.Release.Task.WaitAsync(cancellationToken); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { gate.Cancelled.TrySetResult(); throw; }
            }
            if (Failure is not null) throw Failure;
            return Blocks;
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
