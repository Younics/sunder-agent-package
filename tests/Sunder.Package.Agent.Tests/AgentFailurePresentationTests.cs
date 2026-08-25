using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.Services;
using Sunder.Package.Agent.Services.BehaviorLoops;
using Sunder.Package.Agent.Storage;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Logging;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class AgentFailurePresentationTests
{
    private const string SensitiveDiagnostic =
        "SECRET_token=abc123 at /Users/alice/.ssh/id_rsa\0\u001b[31m";

    public static IEnumerable<object[]> StreamQueueExceptionChains()
    {
        yield return [StreamQueueFailure("direct queue diagnostic")];
        yield return
        [
            new InvalidOperationException(
                "outer wrapper",
                StreamQueueFailure("inner queue diagnostic")),
        ];
        yield return
        [
            new InvalidOperationException(
                "aggregate wrapper",
                new AggregateException(
                    new IOException("unrelated failure"),
                    new InvalidOperationException(
                        "nested wrapper",
                        StreamQueueFailure("aggregate queue diagnostic")))),
        ];
    }

    [Theory]
    [MemberData(nameof(StreamQueueExceptionChains))]
    public void Policy_ClassifiesTypedStreamQueueLimitAcrossExceptionChains(Exception exception)
    {
        var presentation = AgentFailurePresentationPolicy.Create(exception);

        Assert.Equal(AgentRunStatus.Interrupted, presentation.Status);
        Assert.Equal(AgentLifecycleEventKind.RunInterrupted, presentation.LifecycleEventKind);
        Assert.True(presentation.IsInterrupted);
        Assert.Equal(
            AgentFailurePresentationPolicy.StreamInterruptedMarkdown,
            presentation.Markdown);
        Assert.Equal(
            AgentFailurePresentationPolicy.StreamInterruptedSummary,
            presentation.CheckpointSummary);
        Assert.True(presentation.Markdown.Length < 512);
        Assert.True(presentation.CheckpointSummary.Length < 128);
        Assert.False(AgentProviderResilience.IsTransient(exception));
    }

    [Fact]
    public void Policy_DoesNotClassifyByMessageText()
    {
        var exception = new SunderRpcException(new SunderRpcError(
            SunderRpcErrorKind.ResourceExhausted,
            "rpc.worker.some-other-limit",
            $"{AgentChatProviderRpc.StreamQueueLimitErrorCode}: response stream queue limit"));

        var presentation = AgentFailurePresentationPolicy.Create(exception);

        Assert.Equal(AgentRunStatus.Failed, presentation.Status);
        Assert.Equal(AgentLifecycleEventKind.RunFailed, presentation.LifecycleEventKind);
        Assert.Equal(AgentFailurePresentationPolicy.GenericFailureMarkdown, presentation.Markdown);
    }

    [Fact]
    public async Task ProviderResilience_DoesNotRetryTypedUnsafeStreamQueueFailure()
    {
        var exception = StreamQueueFailure("connection timed out while queue was overloaded");
        var attemptCount = 0;
        var retryNotificationCount = 0;
        var pipeline = AgentProviderResilience.CreatePipeline(
            _ => retryNotificationCount++,
            canRetry: () => true);

        var observed = await Assert.ThrowsAsync<SunderRpcException>(async () =>
            await pipeline.ExecuteAsync(
                _ =>
                {
                    attemptCount++;
                    return ValueTask.FromException(exception);
                },
                CancellationToken.None));

        Assert.Same(exception, observed);
        Assert.Equal(1, attemptCount);
        Assert.Equal(0, retryNotificationCount);
    }

    [Fact]
    public void ProviderResilience_DoesNotRetryAnyNestedUnsafeRpcFailure()
    {
        var rpcFailure = new SunderRpcException(new SunderRpcError(
            SunderRpcErrorKind.Unavailable,
            "rpc.worker.other-unsafe-failure",
            "connection timed out",
            SunderRpcDispatchState.PossiblyDispatched,
            SunderRpcRetrySafety.Unsafe));
        var providerFailure = new AgentChatProviderException(
            "connection timed out",
            "The provider request failed.",
            innerException: rpcFailure);

        Assert.False(AgentProviderResilience.IsTransient(providerFailure));
    }

    [Fact]
    public async Task LoopTerminalHandler_StreamQueueLimitReplacesPartialDraftAndInterruptsRun()
    {
        var host = new RecordingBehaviorLoopRuntime();
        var partialTurn = host.CreatePartialAssistantTurn("partial assistant draft");
        var state = new AgentAssistantTurnState { Turn = partialTurn };
        var exception = StreamQueueFailure(SensitiveDiagnostic);

        var result = await new AgentLoopTerminalHandler().HandleFailureAsync(
            host,
            CreateContext(host.SessionId),
            state,
            exception,
            elapsedMilliseconds: 42);

        Assert.Equal(AgentBehaviorLoopCompletionKind.Interrupted, result.CompletionKind);
        Assert.Equal(AgentRunStatus.Interrupted, result.Checkpoint.Status);
        Assert.Equal(
            AgentFailurePresentationPolicy.StreamInterruptedSummary,
            result.Checkpoint.Summary);
        Assert.Equal(AgentFailurePresentationPolicy.StreamInterruptedMarkdown, host.PersistedContent);
        Assert.Equal(partialTurn.TurnId, state.Turn?.TurnId);
        Assert.Equal(1, host.CompleteCount);
        Assert.Equal(AgentLifecycleEventKind.RunInterrupted, host.LifecycleKind);
        Assert.Equal(AgentRunStatus.Interrupted, host.LifecycleStatus);
        Assert.True(host.LifecycleIsInterrupted);
        Assert.Equal(
            AgentFailurePresentationPolicy.StreamInterruptedMarkdown,
            RenderText(host.LifecycleTriggerTurn!));
        Assert.DoesNotContain(SensitiveDiagnostic, host.PersistedContent, StringComparison.Ordinal);
        Assert.DoesNotContain(SensitiveDiagnostic, result.Checkpoint.Summary, StringComparison.Ordinal);

        var log = Assert.Single(host.Logs);
        Assert.Equal(PackageLogLevel.Warning, log.Level);
        Assert.Equal("behavior.loop.interrupted", log.EventName);
        Assert.Equal(AgentFailurePresentationPolicy.StreamInterruptedSummary, log.Message);
        Assert.Same(exception, log.Exception);
    }

    [Fact]
    public async Task LoopTerminalHandler_GenericFailureSanitizesPresentationAndFailsRun()
    {
        var host = new RecordingBehaviorLoopRuntime();
        var state = new AgentAssistantTurnState
        {
            Turn = host.CreatePartialAssistantTurn("partial assistant draft"),
        };
        var exception = new InvalidOperationException(SensitiveDiagnostic);

        var result = await new AgentLoopTerminalHandler().HandleFailureAsync(
            host,
            CreateContext(host.SessionId),
            state,
            exception,
            elapsedMilliseconds: 19);

        Assert.Equal(AgentBehaviorLoopCompletionKind.Failed, result.CompletionKind);
        Assert.Equal(AgentRunStatus.Failed, result.Checkpoint.Status);
        Assert.Equal(AgentFailurePresentationPolicy.GenericFailureMarkdown, host.PersistedContent);
        Assert.Equal(AgentFailurePresentationPolicy.GenericFailureSummary, result.Checkpoint.Summary);
        Assert.Equal(1, host.CompleteCount);
        Assert.Equal(AgentLifecycleEventKind.RunFailed, host.LifecycleKind);
        Assert.Equal(AgentRunStatus.Failed, host.LifecycleStatus);
        Assert.False(host.LifecycleIsInterrupted);
        AssertPresentationHasNoDiagnostics(
            host.PersistedContent,
            result.Checkpoint.Summary!,
            RenderText(host.LifecycleTriggerTurn!));

        var log = Assert.Single(host.Logs);
        Assert.Equal(PackageLogLevel.Error, log.Level);
        Assert.Equal("behavior.loop.failed", log.EventName);
        Assert.Equal(AgentFailurePresentationPolicy.GenericFailureSummary, log.Message);
        Assert.Same(exception, log.Exception);
    }

    [Fact]
    public async Task LoopTerminalHandler_ProviderFailurePreservesDeclaredSafeContent()
    {
        const string providerContent =
            "### Provider request failed\n\nThe provider-declared safe explanation.";
        var host = new RecordingBehaviorLoopRuntime();
        var exception = new AgentChatProviderException(
            SensitiveDiagnostic,
            providerContent,
            "provider.safe-code");

        var result = await new AgentLoopTerminalHandler().HandleProviderFailureAsync(
            host,
            CreateContext(host.SessionId),
            new AgentAssistantTurnState(),
            exception,
            elapsedMilliseconds: 7);

        Assert.Equal(AgentRunStatus.Failed, result.Checkpoint.Status);
        Assert.Equal(providerContent, host.PersistedContent);
        Assert.Equal("provider.safe-code", result.Checkpoint.Summary);
    }

    [Fact]
    public async Task RunExecutionFailure_StreamQueueLimitPersistsSanitizedInterruption()
    {
        using var fixture = RunExecutionFixture.Create();
        var partialTurn = fixture.AppendPartialAssistantTurn("partial assistant draft");
        var exception = StreamQueueFailure(SensitiveDiagnostic);

        var checkpoint = await fixture.Service.HandleFailureAsync(fixture.Started, exception);

        AssertExecutionPresentation(
            fixture,
            partialTurn,
            checkpoint,
            exception,
            AgentRunStatus.Interrupted,
            AgentLifecycleEventKind.RunInterrupted,
            AgentFailurePresentationPolicy.StreamInterruptedMarkdown,
            AgentFailurePresentationPolicy.StreamInterruptedSummary,
            "run.interrupted",
            PackageLogLevel.Warning);
    }

    [Fact]
    public async Task RunExecutionFailure_GenericExceptionNeverPersistsDiagnostics()
    {
        using var fixture = RunExecutionFixture.Create();
        var partialTurn = fixture.AppendPartialAssistantTurn("partial assistant draft");
        var exception = new InvalidOperationException(SensitiveDiagnostic);

        var checkpoint = await fixture.Service.HandleFailureAsync(fixture.Started, exception);

        AssertExecutionPresentation(
            fixture,
            partialTurn,
            checkpoint,
            exception,
            AgentRunStatus.Failed,
            AgentLifecycleEventKind.RunFailed,
            AgentFailurePresentationPolicy.GenericFailureMarkdown,
            AgentFailurePresentationPolicy.GenericFailureSummary,
            "run.failed",
            PackageLogLevel.Error);
    }

    private static void AssertExecutionPresentation(
        RunExecutionFixture fixture,
        AgentTurnRecord partialTurn,
        AgentRunCheckpointRecord checkpoint,
        Exception exception,
        AgentRunStatus expectedStatus,
        AgentLifecycleEventKind expectedLifecycleKind,
        string expectedMarkdown,
        string expectedSummary,
        string expectedLogEvent,
        PackageLogLevel expectedLogLevel)
    {
        Assert.Equal(expectedStatus, checkpoint.Status);
        Assert.Equal(expectedSummary, checkpoint.Summary);

        var assistantTurn = Assert.Single(
            fixture.SessionService.ListTurns(fixture.Started.Plan.RunKey.SessionId),
            turn => turn.Role == AgentMessageRole.Assistant);
        Assert.Equal(partialTurn.TurnId, assistantTurn.TurnId);
        Assert.Equal(partialTurn.ContentRevision + 2, assistantTurn.ContentRevision);
        Assert.False(assistantTurn.IsStreaming);
        Assert.Equal(expectedMarkdown, RenderText(assistantTurn));

        var lifecycle = Assert.Single(
            fixture.Store.ListLifecycleOutboxEvents(),
            item => item.Kind == expectedLifecycleKind);
        var payload = lifecycle.ToEnvelope().Payload;
        Assert.Equal(expectedStatus, payload.Run?.Status);
        Assert.Equal(expectedSummary, payload.Checkpoint?.Summary);
        Assert.Equal(expectedMarkdown, RenderText(payload.TriggerTurn!));
        AssertPresentationHasNoDiagnostics(
            RenderText(assistantTurn),
            checkpoint.Summary!,
            lifecycle.PayloadJson);

        var log = Assert.Single(
            fixture.EventLogger.Entries,
            item => string.Equals(item.EventName, expectedLogEvent, StringComparison.Ordinal));
        Assert.Equal(expectedLogLevel, log.Level);
        Assert.Equal(expectedSummary, log.Message);
        Assert.Same(exception, log.Exception);
    }

    private static void AssertPresentationHasNoDiagnostics(params string[] values)
    {
        foreach (var value in values)
        {
            Assert.DoesNotContain("SECRET_token", value, StringComparison.Ordinal);
            Assert.DoesNotContain("/Users/alice/.ssh/id_rsa", value, StringComparison.Ordinal);
            Assert.DoesNotContain('\0', value);
            Assert.DoesNotContain("\u001b[31m", value, StringComparison.Ordinal);
        }
    }

    private static SunderRpcException StreamQueueFailure(string diagnostic)
        => new(new SunderRpcError(
            SunderRpcErrorKind.ResourceExhausted,
            AgentChatProviderRpc.StreamQueueLimitErrorCode,
            diagnostic,
            SunderRpcDispatchState.PossiblyDispatched,
            SunderRpcRetrySafety.Unsafe));

    private static AgentBehaviorLoopContext CreateContext(Guid sessionId)
    {
        var now = DateTimeOffset.UtcNow;
        return new AgentBehaviorLoopContext(
            new AgentSessionRecord(
                sessionId,
                "Session",
                AgentSessionState.Active,
                now,
                now),
            new AgentProfileRecord(
                "profile",
                "Profile",
                null,
                null,
                "provider",
                "model",
                null,
                null,
                now,
                now),
            "provider",
            "model",
            new AgentProviderRunCapabilities(false, false, false, "Failure presentation test."),
            Workspace: null,
            ExecutionBinding: null,
            Guid.NewGuid(),
            RunRevision: 1,
            new AgentRunCheckpointRecord(
                Guid.NewGuid(),
                sessionId,
                1,
                AgentRunStatus.Running,
                "Running.",
                now),
            now,
            "Test failure presentation.",
            Guid.NewGuid());
    }

    private static AgentTurnRecord CreateTextTurn(
        Guid turnId,
        Guid sessionId,
        string content,
        long contentRevision,
        bool isStreaming)
    {
        var now = DateTimeOffset.UtcNow;
        return new AgentTurnRecord(
            turnId,
            sessionId,
            AgentMessageRole.Assistant,
            AgentTurnKind.Message,
            [
                new AgentTurnItemRecord(
                    Guid.NewGuid(),
                    turnId,
                    0,
                    AgentTurnItemKind.Text,
                    content,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    false,
                    false,
                    null,
                    null),
            ],
            now,
            now)
        {
            ContentRevision = contentRevision,
            IsStreaming = isStreaming,
            RunId = Guid.NewGuid(),
            RunRevision = 1,
        };
    }

    private static string RenderText(AgentTurnRecord turn)
        => string.Concat(turn.Items
            .Where(item => item.Kind == AgentTurnItemKind.Text)
            .OrderBy(item => item.SequenceNumber)
            .Select(item => item.TextContent ?? string.Empty));

    private sealed class RecordingBehaviorLoopRuntime : IAgentBehaviorLoopRuntime
    {
        public Guid SessionId { get; } = Guid.NewGuid();
        public string PersistedContent { get; private set; } = string.Empty;
        public int CompleteCount { get; private set; }
        public AgentLifecycleEventKind? LifecycleKind { get; private set; }
        public AgentRunStatus? LifecycleStatus { get; private set; }
        public bool LifecycleIsInterrupted { get; private set; }
        public AgentTurnRecord? LifecycleTriggerTurn { get; private set; }
        public List<RecordedLogEntry> Logs { get; } = [];

        public AgentTurnRecord CreatePartialAssistantTurn(string content)
        {
            PersistedContent = content;
            return CreateTextTurn(Guid.NewGuid(), SessionId, content, 1, isStreaming: true);
        }

        public bool IsCurrentRun() => true;

        public IReadOnlyList<AgentTurnRecord> ListTurns() => [];

        public IReadOnlyList<AgentTurnRecord> ListRecentTurns(int limit) => [];

        public ValueTask<AgentBehaviorInstructionContext> BuildInstructionContextAsync(
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<IReadOnlyList<AgentRuntimeTool>> ListReadyToolsAsync(
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<IChatClient> CreateChatClientAsync(
            AgentChatClientContext context,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public AgentRunCheckpointRecord SaveCheckpoint(AgentRunStatus status, string? summary)
            => new(
                Guid.NewGuid(),
                SessionId,
                1,
                status,
                summary,
                DateTimeOffset.UtcNow);

        public void LogEvent(
            PackageLogLevel level,
            string eventName,
            string message,
            long? elapsedMilliseconds = null,
            IReadOnlyDictionary<string, object?>? attributes = null,
            Exception? exception = null)
            => Logs.Add(new RecordedLogEntry(level, eventName, message, exception));

        public AgentTurnRecord UpsertAssistantTurn(
            AgentTurnRecord? assistantTurn,
            string content)
        {
            PersistedContent = content;
            return CreateTextTurn(
                assistantTurn?.TurnId ?? Guid.NewGuid(),
                SessionId,
                content,
                (assistantTurn?.ContentRevision ?? 0) + 1,
                isStreaming: true);
        }

        public AgentTurnRecord CompleteAssistantTurn(AgentTurnRecord assistantTurn)
        {
            CompleteCount++;
            return assistantTurn with
            {
                ContentRevision = assistantTurn.ContentRevision + 1,
                IsStreaming = false,
            };
        }

        public ValueTask PublishLifecycleEventAsync(
            AgentLifecycleEventKind kind,
            AgentRunStatus status,
            AgentTurnRecord? triggerTurn = null,
            AgentRunCheckpointRecord? checkpoint = null,
            bool isInterrupted = false,
            CancellationToken cancellationToken = default)
        {
            LifecycleKind = kind;
            LifecycleStatus = status;
            LifecycleIsInterrupted = isInterrupted;
            LifecycleTriggerTurn = triggerTurn;
            return ValueTask.CompletedTask;
        }

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

    private sealed class RunExecutionFixture : IDisposable
    {
        private readonly RegressionTestPackageScope _scope;
        private readonly RegressionTestExtensionCatalog _catalog;

        private RunExecutionFixture(
            RegressionTestPackageScope scope,
            RegressionTestExtensionCatalog catalog,
            AgentLocalStore store,
            AgentSessionService sessionService,
            RecordingEventLogger eventLogger,
            AgentRunExecutionService service,
            AgentRunStarted started)
        {
            _scope = scope;
            _catalog = catalog;
            Store = store;
            SessionService = sessionService;
            EventLogger = eventLogger;
            Service = service;
            Started = started;
        }

        public AgentLocalStore Store { get; }
        public AgentSessionService SessionService { get; }
        public RecordingEventLogger EventLogger { get; }
        public AgentRunExecutionService Service { get; }
        public AgentRunStarted Started { get; }

        public static RunExecutionFixture Create()
        {
            var scope = RegressionTestPackageScope.Create();
            var catalog = new RegressionTestExtensionCatalog();
            var eventLogger = new RecordingEventLogger();
            var packageContext = new RecordingPackageContext(scope.Context, eventLogger);
            var store = new AgentLocalStore(packageContext);
            var sessionService = new AgentSessionService(store, catalog);
            var workspaceService = new AgentWorkspaceService(store, catalog, sessionService);
            var workspace = workspaceService.CreateWorkspace("Failure presentation workspace");
            var now = DateTimeOffset.UtcNow;
            var binding = new AgentProfileModelBindingRecord(
                "profile",
                AgentModelCapabilityKinds.Chat,
                "provider",
                "model",
                null,
                now);
            var profile = new AgentProfileRecord(
                "profile",
                "Profile",
                null,
                null,
                "provider",
                "model",
                null,
                null,
                now,
                now,
                [binding],
                []);
            store.SaveProfile(profile);
            var session = sessionService.CreateSession(
                "Failure presentation session",
                profileId: profile.ProfileId,
                workspaceId: workspace.WorkspaceId);
            var admission = sessionService.AdmitUserTurn(new AgentUserTurnAdmissionRequest(
                Guid.NewGuid(),
                session.SessionId,
                profile.ProfileId,
                workspace.WorkspaceId,
                "Test user message.",
                AgentRunAdmissionKind.Normal,
                RollbackAnchorTurnId: null,
                new string('a', 64),
                []));
            var lease = new AgentDurableRunLease(admission.Run);
            var cancellation = new CancellationTokenSource();
            var runHandle = new AgentActiveRunHandle(
                admission.Run.Key.RunId,
                admission.Run.Key.RunRevision,
                admission.Run.StartedAtUtc,
                profile.ProfileId,
                admission.Run.UserMessage,
                cancellation)
            {
                DurableLease = lease,
            };
            var activeRunRegistry = new AgentActiveRunRegistry();
            Assert.True(activeRunRegistry.Activate(session.SessionId, runHandle).IsAccepted);
            var running = Assert.IsType<AgentRunTransitionResult>(
                sessionService.TryBeginAdmittedRunExecution(lease, "Running."));
            var plan = new AgentRunPlan(
                admission.Run.Key,
                runHandle,
                admission.Run.StartedAtUtc,
                session,
                profile,
                workspace,
                new AgentRunProviderSelection(binding),
                binding,
                new AgentProviderRunCapabilities(false, false, false, "Test capabilities."),
                ModelVariant: null,
                ModelSpeedOption: null,
                ModelModeOption: null,
                [],
                admission.Run.UserMessage,
                RollbackAnchorTurnId: null,
                ShouldGenerateSessionTitle: false);
            var started = new AgentRunStarted(
                plan,
                admission.UserTurn!,
                running.Checkpoint,
                cancellation.Token,
                InterruptedCheckpoint: null);
            var runEventLogger = new AgentRunEventLogger(packageContext);
            var memoryCoordinator = new AgentMemoryCoordinator(sessionService, catalog);
            var service = new AgentRunExecutionService(
                sessionService,
                workspaceService,
                memoryCoordinator,
                activeRunRegistry,
                runEventLogger,
                behaviorLoopHostFactory: null!,
                behaviorLoopResolver: null!);
            return new RunExecutionFixture(
                scope,
                catalog,
                store,
                sessionService,
                eventLogger,
                service,
                started);
        }

        public AgentTurnRecord AppendPartialAssistantTurn(string content)
            => SessionService.AppendTextTurn(
                Started.Plan.RunHandle.DurableLease!,
                AgentMessageRole.Assistant,
                content);

        public void Dispose()
        {
            Started.Plan.RunHandle.CancellationTokenSource.Dispose();
            Started.Plan.Dispose();
            _catalog.Dispose();
            _scope.Dispose();
        }
    }

    private sealed class RecordingPackageContext(
        IPackageContext inner,
        RecordingEventLogger eventLogger) : IPackageContext
    {
        public string PackageId => inner.PackageId;
        public string Version => inner.Version;
        public string ContentRootPath => inner.ContentRootPath;
        public IPackageStorageContext Storage => inner.Storage;
        public IPackageSettings Settings => inner.Settings;
        public IPackageSecrets Secrets => inner.Secrets;
        public IPackageLogging Logging { get; } = new RecordingPackageLogging(eventLogger);
    }

    private sealed class RecordingPackageLogging(RecordingEventLogger eventLogger) : IPackageLogging
    {
        public ILoggerFactory LoggerFactory { get; } = NullLoggerFactory.Instance;
        public IPackageEventLogger Events { get; } = eventLogger;
    }

    private sealed class RecordingEventLogger : IPackageEventLogger
    {
        public ConcurrentQueue<RecordedLogEntry> Entries { get; } = new();

        public ValueTask WriteAsync(
            PackageLogLevel level,
            string eventName,
            string message,
            IReadOnlyDictionary<string, object?>? attributes = null,
            Exception? exception = null,
            CancellationToken cancellationToken = default)
        {
            Entries.Enqueue(new RecordedLogEntry(level, eventName, message, exception));
            return ValueTask.CompletedTask;
        }
    }

    private sealed record RecordedLogEntry(
        PackageLogLevel Level,
        string EventName,
        string Message,
        Exception? Exception);
}
