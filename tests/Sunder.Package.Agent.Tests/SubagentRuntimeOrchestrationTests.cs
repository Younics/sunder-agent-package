using System.Text.Json;
using Microsoft.Extensions.AI;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Services;
using Sunder.Package.Agent.Storage;
using Sunder.Package.Agent.Subagents.Models;
using Sunder.Package.Agent.Subagents.Services;
using Sunder.Sdk.Stacks;
using Sunder.Sdk.Logging;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class SubagentRuntimeOrchestrationTests
{
    [Fact]
    public void RequestParser_RejectsDuplicateTaskIdsWithinBatch()
    {
        var parser = new SubagentRequestParser();
        const string argumentsJson = """
            {
              "tasks": [
                { "prompt": "First", "subagent_type": "reviewer", "task_id": "shared" },
                { "prompt": "Second", "subagent_type": "researcher", "task_id": "SHARED" }
              ]
            }
            """;

        var parsed = parser.TryParseBatch(argumentsJson, out _, out var error);

        Assert.False(parsed);
        Assert.Contains("duplicate task_id", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BatchRenderer_PreservesMixedTerminalAndWaitingStates()
    {
        using var scope = RegressionTestPackageScope.Create();
        var service = new SubagentService(new SubagentStore(scope.Context));
        var adapter = new SubagentPermissionStatusAdapter(new RegressionTestExtensionCatalog());
        var renderer = new SubagentBatchResultRenderer(service, new SubagentRequestParser(), adapter);
        var completed = CreateTaskResult(SubagentTaskResultState.Completed, "completed");
        var failed = CreateTaskResult(SubagentTaskResultState.Failed, "failed");
        var waiting = CreateTaskResult(SubagentTaskResultState.Waiting, "waiting");

        var result = renderer.BuildBatchResult([completed, failed, waiting]);

        Assert.False(result.IsError);
        Assert.Equal(AgentToolResultErrorCodes.ChildWaitingForApproval, result.ErrorCode);
        Assert.Contains("status=\"completed\"", result.Content, StringComparison.Ordinal);
        Assert.Contains("status=\"failed\"", result.Content, StringComparison.Ordinal);
        Assert.Contains("status=\"waiting\"", result.Content, StringComparison.Ordinal);
        using var payload = JsonDocument.Parse(result.StructuredPayloadJson!);
        Assert.Equal(
            new[] { "completed", "failed", "waiting" },
            payload.RootElement.GetProperty("tasks").EnumerateArray()
                .Select(task => task.GetProperty("state").GetString()!)
                .ToArray());
    }

    [Fact]
    public async Task OrchestratedLoop_UsesExplicitHostInnerLoop()
    {
        var runtime = new RecordingInnerLoopRuntime();
        var loop = new OrchestratedAgentBehaviorLoop();

        var result = await loop.RunAsync(null!, runtime);

        Assert.Equal(1, runtime.InvocationCount);
        Assert.Equal(AgentBehaviorLoopCompletionKind.Completed, result.CompletionKind);
    }

    [Fact]
    public void ChildSessionReuse_RequiresMatchingSubagentProfile()
    {
        using var scope = RegressionTestPackageScope.Create();
        var catalog = new RegressionTestExtensionCatalog();
        var store = new AgentLocalStore(scope.Context);
        var sessionService = new AgentSessionService(store, catalog);
        var workspaceService = new AgentWorkspaceService(store, catalog, sessionService);
        var toolService = new AgentToolService(
            sessionService,
            workspaceService,
            new AgentExecutionTargetService(catalog),
            catalog);
        var profileService = new AgentProfileService(store, toolService, catalog, catalog.BehaviorLoops);
        var childSessions = new AgentChildRunSessionService(sessionService, profileService);
        var parent = sessionService.CreateSession("Parent", workspaceId: "workspace");

        var first = childSessions.PrepareChildSession(CreateChildRequest(parent.SessionId, CreateProfile("subagent-reviewer")));
        var second = childSessions.PrepareChildSession(CreateChildRequest(parent.SessionId, CreateProfile("subagent-researcher")));

        Assert.NotEqual(first.ChildSession.SessionId, second.ChildSession.SessionId);
        Assert.Equal(2, sessionService.ListSessions().Count(session =>
            session.ParentSessionId == parent.SessionId
            && string.Equals(session.TaskId, "shared-task", StringComparison.OrdinalIgnoreCase)));

        static AgentChildRunRequest CreateChildRequest(Guid parentSessionId, AgentProfileRecord profile)
            => new(
                parentSessionId,
                Guid.NewGuid(),
                1,
                Guid.NewGuid().ToString("N"),
                "workspace",
                "shared-task",
                profile,
                "Do the work.",
                profile.DisplayName);

        static AgentProfileRecord CreateProfile(string profileId)
        {
            var now = DateTimeOffset.UtcNow;
            return new AgentProfileRecord(
                profileId,
                profileId,
                null,
                null,
                "provider",
                "model",
                null,
                null,
                now,
                now,
                [],
                [],
                "default",
                IsInternal: true);
        }
    }

    [Fact]
    public async Task ChildRuntimeProfileIdentity_IsQualifiedByConcurrentParentSnapshots()
    {
        var now = DateTimeOffset.UtcNow;
        var subagent = CreateSubagent("reviewer");
        AgentProfileRecord Parent(string id, string model) => new(
            id,
            id,
            null,
            null,
            "provider",
            model,
            null,
            null,
            now,
            now,
            [new AgentProfileModelBindingRecord(
                id,
                AgentModelCapabilityKinds.Chat,
                "provider",
                model,
                null,
                now)],
            [],
            AgentBehaviorLoopIds.Default);

        var profiles = await Task.WhenAll(
            Task.Run(() => SubagentChildRunCoordinator.BuildChildProfile(
                Parent("parent-a", "model-a"),
                subagent)),
            Task.Run(() => SubagentChildRunCoordinator.BuildChildProfile(
                Parent("parent-b", "model-b"),
                subagent)));
        var repeated = SubagentChildRunCoordinator.BuildChildProfile(
            Parent("parent-a", "model-a"),
            subagent);

        Assert.NotEqual(profiles[0].ProfileId, profiles[1].ProfileId);
        Assert.Equal(profiles[0].ProfileId, repeated.ProfileId);
        Assert.StartsWith($"subagent-{subagent.SubagentId}-", profiles[0].ProfileId, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{not-json")]
    [InlineData("{\"version\":99,\"subagents\":[],\"future\":true}")]
    public void Store_RefusesToOverwriteCorruptOrFutureDocuments(string existingContent)
    {
        using var scope = RegressionTestPackageScope.Create();
        var filePath = scope.Context.Storage.RoleLocalWorkspace.GetLocalPath("subagents/subagents.json");
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        File.WriteAllText(filePath, existingContent);
        var store = new SubagentStore(scope.Context);

        Assert.ThrowsAny<Exception>(() => store.List());
        Assert.ThrowsAny<Exception>(() => store.Save(CreateSubagent("new-agent")));
        Assert.Equal(existingContent, File.ReadAllText(filePath));
    }

    [Fact]
    public void Store_PreservesUnknownFieldsWhenUpdatingCurrentDocument()
    {
        using var scope = RegressionTestPackageScope.Create();
        var filePath = scope.Context.Storage.RoleLocalWorkspace.GetLocalPath("subagents/subagents.json");
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        var existing = CreateSubagent("agent-1");
        var serialized = JsonSerializer.Serialize(existing, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        File.WriteAllText(filePath, $$"""
            {
              "version": 1,
              "futureRoot": { "enabled": true },
              "subagents": [
                {{serialized[..^1]}}, "futureRecord": 42 }
              ]
            }
            """);
        var store = new SubagentStore(scope.Context);

        store.Save(existing with { DisplayName = "Updated" });

        using var document = JsonDocument.Parse(File.ReadAllText(filePath));
        Assert.True(document.RootElement.GetProperty("futureRoot").GetProperty("enabled").GetBoolean());
        var stored = Assert.Single(document.RootElement.GetProperty("subagents").EnumerateArray());
        Assert.Equal(42, stored.GetProperty("futureRecord").GetInt32());
        Assert.Equal("Updated", stored.GetProperty("displayName").GetString());
    }

    [Fact]
    public async Task Store_ConcurrentInstancesDoNotLoseSaves()
    {
        using var scope = RegressionTestPackageScope.Create();
        const int saveCount = 24;
        var stores = Enumerable.Range(0, saveCount)
            .Select(_ => new SubagentStore(scope.Context))
            .ToArray();

        await Task.WhenAll(stores.Select((store, index) => Task.Run(() =>
            store.Save(CreateSubagent($"agent-{index:00}")))));

        var records = new SubagentStore(scope.Context).List();
        Assert.Equal(saveCount, records.Count);
        Assert.Equal(saveCount, records.Select(record => record.SubagentId).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public async Task Store_WaitsForExclusivePersistenceLease()
    {
        using var scope = RegressionTestPackageScope.Create();
        var store = new SubagentStore(scope.Context);
        var lockPath = scope.Context.Storage.RoleLocalWorkspace.GetLocalPath("subagents/subagents.json.lock");
        using var externalLease = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        var save = Task.Run(() => store.Save(CreateSubagent("blocked")));
        await Task.Delay(100);
        Assert.False(save.IsCompleted);

        externalLease.Dispose();
        await save.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("blocked", Assert.Single(store.List()).SubagentId);
    }

    [Fact]
    public async Task StackImport_ReportsStoreFailureWithoutPartialImport()
    {
        using var scope = RegressionTestPackageScope.Create();
        var filePath = scope.Context.Storage.RoleLocalWorkspace.GetLocalPath("subagents/subagents.json");
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        const string corruptContent = "{broken";
        File.WriteAllText(filePath, corruptContent);
        var service = new SubagentService(new SubagentStore(scope.Context));
        var contributor = new SubagentStackContributor(service, scope.Context, new RegressionTestExtensionCatalog());
        const string fragmentId = "subagent.reviewer";
        const string subagentId = "reviewer";
        var fragment = new StackFragmentImport(
            fragmentId,
            "sunder.package.agent.subagents",
            contributor.ContributorId,
            "sunder.package.agent.subagents/subagent",
            1,
            "Reviewer",
            """
            {
              "subagentId": "reviewer",
              "displayName": "Reviewer",
              "description": "Reviews changes.",
              "instructions": "Report defects.",
              "selectableCapabilityAssignments": []
            }
            """);

        var result = await contributor.ImportAsync(new StackImportRequest(
            [fragment],
            new Dictionary<string, string>(),
            new Dictionary<string, string>(),
            [$"subagent:{fragmentId}:{subagentId}"]));

        Assert.Equal(StackImportOutcome.Failed, result.Outcome);
        Assert.Empty(result.ImportedItems);
        Assert.Contains(result.Errors, error => error.Contains("atomic store update", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(corruptContent, File.ReadAllText(filePath));
    }

    private static SubagentTaskResult CreateTaskResult(SubagentTaskResultState state, string label)
    {
        var sessionId = Guid.NewGuid();
        var payload = JsonSerializer.Serialize(new
        {
            childSessionId = sessionId,
            childSessionTitle = label,
            subagentId = label,
            subagentName = label,
            state = state.ToString().ToLowerInvariant(),
        });
        return new SubagentTaskResult(
            state,
            new AgentToolResult(
                SubagentConstants.DelegateTasksToolId,
                label,
                Content: label,
                StructuredPayloadJson: payload,
                BackendId: sessionId.ToString("N")));
    }

    private static SubagentRecord CreateSubagent(string id)
    {
        var now = DateTimeOffset.UtcNow;
        return new SubagentRecord(
            id,
            id,
            "Description",
            "Instructions",
            null,
            null,
            [],
            now,
            now);
    }

    private sealed class RecordingInnerLoopRuntime : IAgentBehaviorLoopRuntime, IAgentInnerBehaviorLoopRuntime
    {
        public int InvocationCount { get; private set; }

        public ValueTask<AgentBehaviorLoopResult> RunDefaultLoopAsync(
            AgentBehaviorLoopContext context,
            CancellationToken cancellationToken = default)
        {
            InvocationCount++;
            var checkpoint = new AgentRunCheckpointRecord(
                Guid.NewGuid(),
                Guid.NewGuid(),
                1,
                AgentRunStatus.Completed,
                "Completed.",
                DateTimeOffset.UtcNow);
            return ValueTask.FromResult(new AgentBehaviorLoopResult(checkpoint, AgentBehaviorLoopCompletionKind.Completed));
        }

        public bool IsCurrentRun() => true;

        public IReadOnlyList<AgentTurnRecord> ListTurns() => [];

        public IReadOnlyList<AgentTurnRecord> ListRecentTurns(int limit) => [];

        public ValueTask<AgentBehaviorInstructionContext> BuildInstructionContextAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<IReadOnlyList<AgentRuntimeTool>> ListReadyToolsAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<IChatClient> CreateChatClientAsync(
            AgentChatClientContext context,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public AgentRunCheckpointRecord SaveCheckpoint(AgentRunStatus status, string? summary)
            => throw new NotSupportedException();

        public void LogEvent(
            PackageLogLevel level,
            string eventName,
            string message,
            long? elapsedMilliseconds = null,
            IReadOnlyDictionary<string, object?>? attributes = null,
            Exception? exception = null)
        {
        }

        public AgentTurnRecord UpsertAssistantTurn(AgentTurnRecord? assistantTurn, string content)
            => throw new NotSupportedException();

        public ValueTask PublishLifecycleEventAsync(
            AgentLifecycleEventKind kind,
            AgentRunStatus status,
            AgentTurnRecord? triggerTurn = null,
            AgentRunCheckpointRecord? checkpoint = null,
            bool isInterrupted = false,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<AgentToolCallOutcome> InvokeToolAsync(
            AgentToolCallRequest toolCall,
            AgentTurnRecord? assistantTurn,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<IReadOnlyList<AgentToolCallOutcome>> InvokeToolsAsync(
            IReadOnlyList<AgentToolCallRequest> toolCalls,
            AgentTurnRecord? assistantTurn,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
