using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Sunder.Package.Agent.Tests;

extern alias MemoryWorker;
using MemoryWorker::Sunder.Package.Agent.Contracts;
using MemoryWorker::Sunder.Package.Agent.Contracts.Contracts;
using MemoryWorker::Sunder.Package.Agent.Contracts.Models;
using MemoryWorker::Sunder.Package.Agent.Memory.Semantic;
using MemoryWorker::Sunder.Package.Agent.Memory.Semantic.Services;
using AgentEmbeddingGenerationResult = MemoryWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentEmbeddingGenerationResult;
using AgentEmbeddingModelDescriptor = MemoryWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentEmbeddingModelDescriptor;
using AgentEmbeddingProviderDescriptor = MemoryWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentEmbeddingProviderDescriptor;
using AgentEmbeddingProviderReadiness = MemoryWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentEmbeddingProviderReadiness;
using AgentLifecycleEventKind = MemoryWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentLifecycleEventKind;
using AgentMessageRole = MemoryWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentMessageRole;
using AgentProfileModelBindingRecord = MemoryWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentProfileModelBindingRecord;
using AgentProfileRecord = MemoryWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentProfileRecord;
using AgentRunCheckpointRecord = MemoryWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentRunCheckpointRecord;
using AgentSessionContextCheckpointRecord = MemoryWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentSessionContextCheckpointRecord;
using AgentSessionRecord = MemoryWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentSessionRecord;
using AgentSessionState = MemoryWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentSessionState;
using AgentTurnRecord = MemoryWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentTurnRecord;
using AgentWorkingSummaryRecord = MemoryWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentWorkingSummaryRecord;
using AgentWorkspaceRecord = MemoryWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentWorkspaceRecord;
using MemoryModels = MemoryWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Models;
using IEmbeddingProviderService = MemoryWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Services.CYounics_Ai_Agent_Embedding_Provider.IEmbeddingProviderService;
using IRuntimeCatalogService = MemoryWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Services.CYounics_Ai_Agent_Runtime_Catalog.IRuntimeCatalogService;

public sealed class MemoryEvaluationTests
{
    private readonly ITestOutputHelper _output;

    public MemoryEvaluationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task LongSessionPromotion_SelectivelyCapturesHighValueFacts()
    {
        await using var harness = new MemoryEvaluationHarness();

        for (var index = 0; index < 12; index++)
        {
            await harness.AddUserTurnAsync($"Quick progress check {index}.");
        }

        await harness.AddUserTurnAsync("My name is Micha.");
        await harness.AddUserTurnAsync("I prefer concise final summaries.");
        await harness.AddUserTurnAsync("Always use apply_patch for file edits.");
        await harness.AddUserTurnAsync("The project uses Blazor Server on .NET 10.");
        await harness.AddUserTurnAsync("The working directory is /workspace/app.");

        var memories = harness.ListActiveMemories();
        var report = BuildPromotionReport("long-session-promotion", memories,
            ["participant-fact", "preference", "standing-instruction", "project-fact", "environment-fact"]);
        WriteReport(report);

        Assert.Contains(memories, memory => memory.Category == "participant-fact");
        Assert.Contains(memories, memory => memory.Category == "preference");
        Assert.Contains(memories, memory => memory.Category == "standing-instruction");
        Assert.Contains(memories, memory => memory.Category == "project-fact");
        Assert.Contains(memories, memory => memory.Category == "environment-fact");
        Assert.True(report.IsBounded, $"Expected bounded promotion set, got {report.MemoryCount} memories.");
    }

    [Fact]
    public async Task RecallEvaluation_ReturnsRelevantEntriesWithinBudget()
    {
        await using var harness = new MemoryEvaluationHarness(enableEmbeddings: true);

        await harness.AddUserTurnAsync("I prefer concise final summaries.");
        await harness.AddUserTurnAsync("Always use apply_patch for file edits.");
        await harness.AddUserTurnAsync("The project uses Blazor Server on .NET 10.");
        await harness.AddUserTurnAsync("The working directory is /workspace/app.");
        await harness.ReindexAsync();

        var preferenceEntries = await harness.RecallAsync(
            "What response style do I prefer?",
            AgentMemoryRecallIntent.Preference,
            ["preference", "standing-instruction"],
            maxEntryCount: 4,
            maxChars: 1200);
        var projectEntries = await harness.RecallAsync(
            "What framework does this project use?",
            AgentMemoryRecallIntent.ProjectFact,
            ["project-fact"],
            maxEntryCount: 4,
            maxChars: 1200);

        var preferenceReport = BuildRecallReport("preference-recall", preferenceEntries, maxEntryCount: 4, maxChars: 1200, entry => entry.Category is "preference" or "standing-instruction");
        var projectReport = BuildRecallReport("project-recall", projectEntries, maxEntryCount: 4, maxChars: 1200, entry => entry.Category == "project-fact");
        WriteReport(preferenceReport);
        WriteReport(projectReport);

        Assert.NotEmpty(preferenceEntries);
        Assert.True(preferenceReport.IsBounded);
        Assert.Contains(preferenceEntries[0].Category, new[] { "preference", "standing-instruction" });
        Assert.Contains(preferenceEntries[0].MatchReasons!, reason => reason.Kind is "full-text-match" or "semantic-similarity");

        Assert.NotEmpty(projectEntries);
        Assert.True(projectReport.IsBounded);
        Assert.Equal("project-fact", projectEntries[0].Category);
        Assert.Contains(projectEntries[0].MatchReasons!, reason => reason.Kind is "full-text-match" or "semantic-similarity");

        Assert.True(preferenceReport.RelevantRatio >= 0.66d);
        Assert.True(projectReport.RelevantRatio >= 0.66d);
    }

    [Fact]
    public async Task ContinuityEvaluation_ReturnsProjectAndInstructionContext()
    {
        await using var harness = new MemoryEvaluationHarness(enableEmbeddings: true);

        await harness.AddUserTurnAsync("Always use apply_patch for file edits.");
        await harness.AddUserTurnAsync("I prefer concise final summaries.");
        await harness.AddUserTurnAsync("The project uses Blazor Server on .NET 10.");
        await harness.AddUserTurnAsync("The working directory is /workspace/app.");
        await harness.ReindexAsync();

        var entries = await harness.RecallAsync(
            "Continue from earlier. What stack are we on and how should the final update read?",
            AgentMemoryRecallIntent.Continuity,
            ["standing-instruction", "project-fact", "preference", "remembered-fact"],
            maxEntryCount: 5,
            maxChars: 1500);

        var report = BuildRecallReport("continuity-recall", entries, maxEntryCount: 5, maxChars: 1500, entry => entry.Category is "project-fact" or "preference" or "standing-instruction");
        WriteReport(report);

        Assert.True(report.IsBounded);
        Assert.Contains(entries, entry => entry.Category == "project-fact");
        Assert.Contains(entries, entry => entry.Category is "preference" or "standing-instruction");
        Assert.True(report.RelevantRatio >= 0.66d);
    }

    [Fact]
    public async Task DiversityEvaluation_ContinuityRecall_CoversMultipleCategories()
    {
        await using var harness = new MemoryEvaluationHarness(enableEmbeddings: true);

        await harness.AddUserTurnAsync("Always use apply_patch for file edits.");
        await harness.AddUserTurnAsync("I prefer concise final summaries.");
        await harness.AddUserTurnAsync("The project uses Blazor Server on .NET 10.");
        await harness.AddUserTurnAsync("The project also uses SignalR for real-time updates.");
        await harness.AddUserTurnAsync("The working directory is /workspace/app.");
        await harness.ReindexAsync();

        var entries = await harness.RecallAsync(
            "Continue from earlier and remind me of the stack, environment, and output style.",
            AgentMemoryRecallIntent.Continuity,
            ["standing-instruction", "project-fact", "preference", "environment-fact"],
            maxEntryCount: 5,
            maxChars: 1500);

        var report = BuildRecallReport("continuity-diversity", entries, maxEntryCount: 5, maxChars: 1500, entry => entry.Category is "project-fact" or "preference" or "standing-instruction" or "environment-fact");
        WriteReport(report);

        var distinctCategories = entries.Select(entry => entry.Category).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        Assert.True(distinctCategories >= 3, $"Expected at least 3 distinct categories, got {distinctCategories}.");
    }

    [Fact]
    public async Task ContestedEvaluation_RanksUserProvidedMemoryAboveContestedMemory()
    {
        await using var harness = new MemoryEvaluationHarness();

        var activeMemory = harness.Store.UpsertMemory(new MemoryUpsertRequest(
            harness.SessionId,
            Category: "preference",
            Content: "Prefer concise summaries.",
            NormalizedContent: "prefer concise summaries.",
            EvidenceText: "Active preference.",
            SourceTurnId: Guid.NewGuid(),
            IsPinned: true,
            Importance: 0.8f,
            Confidence: 0.85f,
            Provenance: AgentMemoryProvenance.User));
        var contestedMemory = harness.Store.UpsertMemory(new MemoryUpsertRequest(
            harness.SessionId,
            Category: "preference",
            Content: "Prefer concise summaries for every response.",
            NormalizedContent: "prefer concise summaries for every response.",
            EvidenceText: "Contested preference.",
            SourceTurnId: Guid.NewGuid(),
            IsPinned: true,
            Importance: 0.8f,
            Confidence: 0.85f,
            Provenance: AgentMemoryProvenance.User));
        harness.Store.SetContested(contestedMemory.MemoryId);

        var entries = await harness.RecallAsync(
            "What summary style should I use?",
            AgentMemoryRecallIntent.Preference,
            ["preference"],
            maxEntryCount: 4,
            maxChars: 1200);

        var report = BuildRecallReport("contested-vs-user-provided", entries, maxEntryCount: 4, maxChars: 1200, entry => entry.TrustState == AgentMemoryTrustState.UserProvided || entry.TrustState == AgentMemoryTrustState.Contested);
        WriteReport(report);

        Assert.NotEmpty(entries);
        Assert.Equal(AgentMemoryTrustState.UserProvided, entries[0].TrustState);
        Assert.Equal(activeMemory.MemoryId.ToString("N"), entries[0].MemoryId);
        Assert.Contains(entries, entry => entry.TrustState == AgentMemoryTrustState.Contested);
    }

    [Fact]
    public async Task CorrectionEvaluation_PrefersCorrectedMemoryOverSupersededOriginal()
    {
        await using var harness = new MemoryEvaluationHarness();

        var source = harness.Store.UpsertMemory(new MemoryUpsertRequest(
            harness.SessionId,
            Category: "project-fact",
            Content: "The project uses old framework wording.",
            NormalizedContent: "the project uses old framework wording.",
            EvidenceText: "Original project evidence.",
            SourceTurnId: Guid.NewGuid(),
            IsPinned: false,
            Importance: 0.8f,
            Confidence: 0.85f));
        var correction = harness.Store.CreateCorrectedMemory(source.MemoryId, "project-fact", "The project uses updated framework wording.", "Corrected during evaluation.");

        var entries = await harness.RecallAsync(
            "What framework wording is current for this project?",
            AgentMemoryRecallIntent.ProjectFact,
            ["project-fact"],
            maxEntryCount: 4,
            maxChars: 1200);

        var report = BuildRecallReport("corrected-vs-superseded", entries, maxEntryCount: 4, maxChars: 1200, entry => entry.Category == "project-fact");
        WriteReport(report);

        Assert.NotEmpty(entries);
        Assert.Equal(correction.CorrectedMemory.MemoryId.ToString("N"), entries[0].MemoryId);
        Assert.DoesNotContain(entries, entry => entry.MemoryId == source.MemoryId.ToString("N"));
    }

    [Fact]
    public async Task NoisySessionEvaluation_DoesNotPromoteAssistantOrToolClaims()
    {
        await using var harness = new MemoryEvaluationHarness(enableEmbeddings: true);

        for (var index = 0; index < 10; index++)
        {
            await harness.AddUserTurnAsync($"Remember this random note {index} about unrelated planning history.");
        }

        await harness.AddToolResultAsync("Inspect workspace", "Working directory is /workspace/app.");
        await harness.AddAssistantTurnAsync("The project uses Blazor Server on .NET 10.");
        await harness.ReindexAsync();

        var entries = await harness.RecallAsync(
            "What is the workspace folder?",
            AgentMemoryRecallIntent.EnvironmentFact,
            ["environment-fact", "project-fact", "remembered-fact"],
            maxEntryCount: 4,
            maxChars: 1200);

        var report = BuildRecallReport("noisy-untrusted-claim-recall", entries, maxEntryCount: 4, maxChars: 1200, entry => entry.Category is "environment-fact" or "project-fact");
        WriteReport(report);

        Assert.Empty(entries);
    }

    private PromotionEvaluationReport BuildPromotionReport(
        string scenario,
        IReadOnlyList<StoredMemoryRecord> memories,
        IReadOnlyList<string> expectedCategories)
    {
        var categories = memories.Select(memory => memory.Category).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(category => category, StringComparer.OrdinalIgnoreCase).ToArray();
        var missingCategories = expectedCategories.Where(category => !categories.Contains(category, StringComparer.OrdinalIgnoreCase)).ToArray();
        return new PromotionEvaluationReport(
            scenario,
            memories.Count,
            categories,
            missingCategories,
            IsBounded: memories.Count <= 8);
    }

    private RecallEvaluationReport BuildRecallReport(
        string scenario,
        IReadOnlyList<AgentMemoryRecallEntry> entries,
        int maxEntryCount,
        int maxChars,
        Func<AgentMemoryRecallEntry, bool> relevancePredicate)
    {
        var totalChars = entries.Sum(entry => (entry.Content?.Length ?? 0) + (entry.EvidenceText?.Length ?? 0));
        var relevantCount = entries.Count(relevancePredicate);
        return new RecallEvaluationReport(
            scenario,
            EntryCount: entries.Count,
            TotalChars: totalChars,
            RelevantCount: relevantCount,
            RelevantRatio: entries.Count == 0 ? 0d : relevantCount / (double)entries.Count,
            TopCategory: entries.FirstOrDefault()?.Category,
            TopTrustState: entries.FirstOrDefault()?.TrustState,
            MatchReasons: entries.FirstOrDefault()?.MatchReasons?.Select(reason => reason.Kind).ToArray() ?? [],
            IsBounded: entries.Count <= maxEntryCount && totalChars <= maxChars);
    }

    private void WriteReport(PromotionEvaluationReport report)
    {
        _output.WriteLine($"Promotion[{report.Scenario}] count={report.MemoryCount} bounded={report.IsBounded} categories=[{string.Join(", ", report.Categories)}] missing=[{string.Join(", ", report.MissingCategories)}]");
    }

    private void WriteReport(RecallEvaluationReport report)
    {
        _output.WriteLine($"Recall[{report.Scenario}] entries={report.EntryCount} chars={report.TotalChars} relevant={report.RelevantCount} ratio={report.RelevantRatio:0.00} top={report.TopCategory}/{report.TopTrustState} bounded={report.IsBounded} reasons=[{string.Join(", ", report.MatchReasons)}]");
    }

    private sealed record PromotionEvaluationReport(
        string Scenario,
        int MemoryCount,
        IReadOnlyList<string> Categories,
        IReadOnlyList<string> MissingCategories,
        bool IsBounded);

    private sealed record RecallEvaluationReport(
        string Scenario,
        int EntryCount,
        int TotalChars,
        int RelevantCount,
        double RelevantRatio,
        string? TopCategory,
        AgentMemoryTrustState? TopTrustState,
        IReadOnlyList<string> MatchReasons,
        bool IsBounded);

    private static Sunder.Sdk.Rpc.SunderRpcOffsetDateTime RpcTimestamp(DateTimeOffset value)
        => new(value.ToString("O", System.Globalization.CultureInfo.InvariantCulture));

    private sealed class MemoryEvaluationHarness : IAsyncDisposable
    {
        private readonly FakeServiceCatalog<IRuntimeCatalogService> _runtimeServices;
        private readonly FakeServiceCatalog<IEmbeddingProviderService> _embeddingServices;
        private readonly EvaluationRuntimeCatalog _runtimeCatalog;
        private readonly EvaluationPackageContext _packageContext;
        private readonly SemanticMemoryIndexingBackgroundService _indexingBackgroundService;
        private readonly MemorySemanticFeature _feature;
        private readonly SemanticMemoryRecallService _recall;
        private readonly List<AgentTurnRecord> _turns = [];
        private long _nextRevision = 1;

        public MemoryEvaluationHarness(bool enableEmbeddings = false)
        {
            SessionId = Guid.NewGuid();
            ProfileId = "profile-eval";
            _runtimeCatalog = new EvaluationRuntimeCatalog(CreateProfile(enableEmbeddings), SessionId, ProfileId);
            _runtimeServices = new FakeServiceCatalog<IRuntimeCatalogService>(
                CurrentPublicRpcDescriptors.Get(RegressionTestBindings.RuntimeCatalog));
            _embeddingServices = new FakeServiceCatalog<IEmbeddingProviderService>(
                CurrentPublicRpcDescriptors.Get(RegressionTestBindings.EmbeddingProviders));
            _packageContext = new EvaluationPackageContext(
                Path.Combine(Path.GetTempPath(), "sunder-memory-eval", Guid.NewGuid().ToString("N")));
            Store = new MemoryLocalStore(_packageContext);
            Settings = new MemorySemanticSettingsService(_packageContext);

            _runtimeServices.Add(_runtimeCatalog);
            if (enableEmbeddings)
            {
                _embeddingServices.Add(new EvaluationEmbeddingProvider("test-embeddings"));
            }

            var metrics = new SemanticMemoryMetricsService();
            var resolver = new SemanticModelRuntimeResolver(
                _runtimeServices,
                _embeddingServices,
                Settings);
            RetrievalBackend = new SemanticMemoryRetrievalBackend(Store, resolver, Settings);
            _indexingBackgroundService = new SemanticMemoryIndexingBackgroundService(Store, resolver, Settings, RetrievalBackend, metrics);
            _recall = new SemanticMemoryRecallService(Store, RetrievalBackend, metrics);
            _feature = new MemorySemanticFeature(
                Store,
                _recall,
                new SemanticMemoryPromotionService(Store, _indexingBackgroundService, metrics));
        }

        public Guid SessionId { get; }

        public string ProfileId { get; }

        public MemoryLocalStore Store { get; }

        public SemanticMemoryRetrievalBackend RetrievalBackend { get; }

        public MemorySemanticSettingsService Settings { get; }

        public async Task AddUserTurnAsync(string text)
        {
            var turn = CreateTextTurn(AgentMessageRole.User, text);
            _turns.Add(turn);
            await _feature.HandleAsync(CreateLifecycleEvent(AgentLifecycleEventKind.UserTurnAdded, text, turn));
        }

        public async Task AddAssistantTurnAsync(string text)
        {
            var turn = CreateTextTurn(AgentMessageRole.Assistant, text);
            _turns.Add(turn);
            await _feature.HandleAsync(CreateLifecycleEvent(AgentLifecycleEventKind.AssistantTurnCompleted, text, turn));
        }

        public async Task AddToolResultAsync(string summary, string content)
        {
            var turnId = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            var turn = new AgentTurnRecord
            {
                TurnSequence = _turns.Count + 1,
                ContentRevision = 1, CreatedAtUtc = RpcTimestamp(now), IsStreaming = false,
                Items = [new MemoryModels.AgentTurnItemRecord
                {
                    ItemId = Guid.NewGuid(), TurnId = turnId, SequenceNumber = 0,
                    Kind = MemoryModels.AgentTurnItemKind.ToolResult, TextContent = content,
                    ToolId = "inspect_workspace", CallId = Guid.NewGuid().ToString("N"), ResultSummary = summary,
                    IsError = false, IsToolHeaderProjection = false, ToolDetailRevision = 0, WasTruncated = false,
                }],
                Kind = MemoryModels.AgentTurnKind.ToolResult, Role = AgentMessageRole.Tool,
                SessionId = SessionId, TurnId = turnId, UpdatedAtUtc = RpcTimestamp(now),
            };
            _turns.Add(turn);
            await _feature.HandleAsync(CreateLifecycleEvent(AgentLifecycleEventKind.ToolResultRecorded, summary, turn));
        }

        public IReadOnlyList<StoredMemoryRecord> ListActiveMemories()
            => Store.ListActiveMemories(SessionId);

        public async Task ReindexAsync()
            => await RetrievalBackend.ReindexSessionAsync(SessionId, ProfileId, Store.ListMemories(SessionId, includeInactive: false), CancellationToken.None);

        public async Task<IReadOnlyList<AgentMemoryRecallEntry>> RecallAsync(
            string query,
            AgentMemoryRecallIntent intent,
            IReadOnlyList<string> preferredCategories,
            int maxEntryCount,
            int maxChars)
        {
            var turn = CreateTextTurn(AgentMessageRole.User, query);
            var sessionContext = SessionContext();
            var runContext = RunContext();
            var turnContext = new MemoryModels.AgentTurnContextRecord { Session = sessionContext, Run = runContext, UserMessage = query };
            var recall = await _recall.RecallAsync(new MemoryModels.AgentPromptContextRequest
            {
                AvailableTools = [],
                ContextPlan = new MemoryModels.AgentPromptContextPlan
                {
                    Intent = intent.ToString(), QueryText = query, PreferredCategories = preferredCategories,
                    MaxEntryCount = maxEntryCount, MaxChars = maxChars,
                },
                RecentLiveBufferTurns = BuildRecentLiveBuffer(), Run = runContext, Session = sessionContext,
                TranscriptEpoch = _nextRevision, Turn = turnContext, Turns = _turns,
            }, new AgentMemoryRecallPlan(intent, query, PreferredCategories: preferredCategories, MaxEntryCount: maxEntryCount, MaxChars: maxChars));
            return recall?.Entries ?? [];
        }

        public async ValueTask DisposeAsync()
        {
            await _indexingBackgroundService.DisposeAsync();
            await _runtimeServices.DisposeAsync();
            await _embeddingServices.DisposeAsync();
        }

        private MemoryModels.AgentProfileRecord CreateProfile(bool enableEmbeddings)
            => new()
            {
                ProfileId = ProfileId,
                DisplayName = "Eval Profile",
                IsInternal = false,
                EmbeddingProviderId = enableEmbeddings ? "test-embeddings" : null,
                EmbeddingModelId = enableEmbeddings ? "semantic-v1" : null,
                CreatedAtUtc = RpcTimestamp(DateTimeOffset.UtcNow),
                UpdatedAtUtc = RpcTimestamp(DateTimeOffset.UtcNow),
            };

        private AgentTurnRecord CreateTextTurn(AgentMessageRole role, string text)
        {
            var turnId = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            return new AgentTurnRecord
            {
                TurnSequence = _turns.Count + 1,
                ContentRevision = 1, CreatedAtUtc = RpcTimestamp(now), IsStreaming = false,
                Items = [new MemoryModels.AgentTurnItemRecord
                {
                    ItemId = Guid.NewGuid(), TurnId = turnId, SequenceNumber = 0,
                    Kind = MemoryModels.AgentTurnItemKind.Text, TextContent = text,
                    IsError = false, IsToolHeaderProjection = false, ToolDetailRevision = 0, WasTruncated = false,
                }],
                Kind = MemoryModels.AgentTurnKind.Message, Role = role, SessionId = SessionId, TurnId = turnId,
                UpdatedAtUtc = RpcTimestamp(now),
            };
        }

        private MemoryModels.AgentDurableLifecycleEventEnvelope CreateLifecycleEvent(AgentLifecycleEventKind kind, string userMessage, AgentTurnRecord triggerTurn)
        {
            var payload = new MemoryModels.AgentDurableLifecycleEventPayload
            {
                ContentErased = false, DeletedChildSessionCount = 0, DeletedChildSessionIds = [], DeletedSessionCount = 0,
                DeletedSessionIds = [], DeletedTurnCount = 0, DeletedTurnIds = [], DeletionChunkReceipts = [],
                RecentLiveBufferTurns = BuildRecentLiveBuffer(), Turns = _turns.ToArray(), Session = SessionContext(), Run = RunContext(),
                SessionId = SessionId, RootSessionId = SessionId, WorkspaceId = "workspace", UserMessage = userMessage, TriggerTurn = triggerTurn,
            };
            var payloadJson = JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            return new MemoryModels.AgentDurableLifecycleEventEnvelope
            {
                EventId = Guid.NewGuid().ToString("N"), Kind = kind, OccurredAtUtc = RpcTimestamp(DateTimeOffset.UtcNow),
                OrderingKey = "workspace:workspace:root", Payload = payload,
                PayloadHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payloadJson))).ToLowerInvariant(),
                PayloadJson = payloadJson, Sequence = _nextRevision++, SourceKey = "evaluation-memory",
            };
        }

        private IReadOnlyList<AgentTurnRecord> BuildRecentLiveBuffer()
            => _turns.Count <= 8 ? _turns.ToArray() : _turns.TakeLast(8).ToArray();

        private MemoryModels.AgentSessionContextRecord SessionContext()
            => new() { SessionId = SessionId, ProfileId = ProfileId, ProfileDisplayName = "Eval Profile", SessionTitle = "Eval Session", SessionState = AgentSessionState.Active };

        private MemoryModels.AgentRunContextRecord RunContext()
            => new() { RunId = Guid.NewGuid(), Revision = _nextRevision++, Status = MemoryModels.AgentRunStatus.Running, IsInterrupted = false, StartedAtUtc = RpcTimestamp(DateTimeOffset.UtcNow) };
    }

    private sealed class EvaluationRuntimeCatalog(AgentProfileRecord profile, Guid sessionId, string profileId) : IRuntimeCatalogService
    {
        private readonly AgentSessionRecord _session = new()
        {
            SessionId = sessionId, Title = "Eval Session", State = AgentSessionState.Active,
            CreatedAtUtc = RpcTimestamp(DateTimeOffset.UtcNow), UpdatedAtUtc = RpcTimestamp(DateTimeOffset.UtcNow),
        };
        private readonly AgentWorkspaceRecord _workspace = new()
        {
            WorkspaceId = "workspace", DisplayName = "Workspace", Documents = [], Paths = [],
            CreatedAtUtc = RpcTimestamp(DateTimeOffset.UtcNow), UpdatedAtUtc = RpcTimestamp(DateTimeOffset.UtcNow),
        };

        public event Action<string>? ProfileChanged
        {
            add { }
            remove { }
        }

        public event Action<Guid>? SessionChanged
        {
            add { }
            remove { }
        }

        public event Action<Guid, AgentTurnRecord>? TurnChanged
        {
            add { }
            remove { }
        }

        public event Action? ResetRequired
        {
            add { }
            remove { }
        }

        public IReadOnlyList<AgentSessionRecord> ListSessions() => [_session];

        public IReadOnlyList<AgentSessionRecord> ListSessionsForProfile(string requestedProfileId)
            => string.Equals(requestedProfileId, profileId, StringComparison.OrdinalIgnoreCase) ? [_session] : [];

        public IReadOnlyList<AgentSessionRecord> ListSessionsForWorkspace(string workspaceId)
            => string.Equals(workspaceId, _workspace.WorkspaceId, StringComparison.OrdinalIgnoreCase) ? [_session] : [];

        public AgentSessionRecord? GetSession(Guid requestedSessionId)
            => requestedSessionId == _session.SessionId ? _session : null;

        public ValueTask<IReadOnlyList<AgentWorkspaceRecord>> ListWorkspacesAsync(
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<AgentWorkspaceRecord>>([_workspace]);

        public ValueTask<AgentWorkspaceRecord?> GetWorkspaceAsync(
            string workspaceId,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<AgentWorkspaceRecord?>(
                string.Equals(workspaceId, _workspace.WorkspaceId, StringComparison.OrdinalIgnoreCase)
                    ? _workspace
                    : null);

        public AgentProfileRecord? GetSessionProfile(Guid requestedSessionId)
            => requestedSessionId == _session.SessionId ? profile : null;

        public AgentWorkingSummaryRecord? GetWorkingSummary(Guid sessionId) => null;

        public AgentSessionContextCheckpointRecord? GetLatestSessionContextCheckpoint(Guid sessionId) => null;

        public AgentRunCheckpointRecord? GetLatestCheckpoint(Guid sessionId) => null;

        public IReadOnlyList<AgentTurnRecord> ListRecentTurns(Guid sessionId, int limit) => [];

        public IReadOnlyList<AgentTurnRecord> ListTurnsBefore(Guid sessionId, DateTimeOffset beforeCreatedAtUtc, Guid beforeTurnId, int limit) => [];

        public IReadOnlyList<AgentTurnRecord> ListTurnsAfter(Guid sessionId, DateTimeOffset afterCreatedAtUtc, Guid afterTurnId, int limit) => [];

        public IReadOnlyList<AgentProfileRecord> ListProfiles() => [profile];

        public AgentProfileRecord? GetProfile(string profileId)
            => string.Equals(profile.ProfileId, profileId, StringComparison.OrdinalIgnoreCase) ? profile : null;

        public AgentProfileModelBindingRecord? GetSessionModelBinding(Guid sessionId, string capabilityKind) => null;

        public AgentProfileModelBindingRecord? GetModelBinding(string profileId, string capabilityKind) => null;

        ValueTask<AgentRunCheckpointRecord?> IRuntimeCatalogService.GetLatestCheckpointAsync(Guid request, CancellationToken token)
            => ValueTask.FromResult<AgentRunCheckpointRecord?>(null);
        ValueTask<MemoryModels.AgentRunActivityUpdate?> IRuntimeCatalogService.GetRunActivityAsync(Guid request, CancellationToken token)
            => ValueTask.FromResult<MemoryModels.AgentRunActivityUpdate?>(null);
        ValueTask<AgentProfileModelBindingRecord?> IRuntimeCatalogService.GetModelBindingAsync(
            MemoryModels.CYounics_Ai_Agent_Runtime_Catalog_RuntimeCatalogGetModelBindingRequest request, CancellationToken token)
            => ValueTask.FromResult(GetModelBinding(request.ProfileId, request.CapabilityKind));
        ValueTask<AgentProfileRecord?> IRuntimeCatalogService.GetProfileAsync(string request, CancellationToken token)
            => ValueTask.FromResult(GetProfile(request));
        ValueTask<AgentSessionRecord?> IRuntimeCatalogService.GetSessionAsync(Guid request, CancellationToken token)
            => ValueTask.FromResult(GetSession(request));
        ValueTask<AgentSessionContextCheckpointRecord?> IRuntimeCatalogService.GetSessionContextCheckpointAsync(Guid request, CancellationToken token)
            => ValueTask.FromResult(GetLatestSessionContextCheckpoint(request));
        ValueTask<AgentProfileModelBindingRecord?> IRuntimeCatalogService.GetSessionModelBindingAsync(
            MemoryModels.CYounics_Ai_Agent_Runtime_Catalog_RuntimeCatalogGetSessionModelBindingRequest request, CancellationToken token)
            => ValueTask.FromResult(GetSessionModelBinding(request.SessionId, request.CapabilityKind));
        ValueTask<AgentProfileRecord?> IRuntimeCatalogService.GetSessionProfileAsync(Guid request, CancellationToken token)
            => ValueTask.FromResult(GetSessionProfile(request));
        ValueTask<AgentWorkingSummaryRecord?> IRuntimeCatalogService.GetWorkingSummaryAsync(Guid request, CancellationToken token)
            => ValueTask.FromResult(GetWorkingSummary(request));
        ValueTask<AgentWorkspaceRecord?> IRuntimeCatalogService.GetWorkspaceAsync(string request, CancellationToken token)
            => GetWorkspaceAsync(request, token);
        ValueTask<IReadOnlyList<AgentProfileRecord>> IRuntimeCatalogService.ListProfilesAsync(CancellationToken token)
            => ValueTask.FromResult(ListProfiles());
        ValueTask<IReadOnlyList<AgentSessionRecord>> IRuntimeCatalogService.ListSessionsAsync(CancellationToken token)
            => ValueTask.FromResult(ListSessions());
        ValueTask<IReadOnlyList<AgentSessionRecord>> IRuntimeCatalogService.ListSessionsForProfileAsync(string request, CancellationToken token)
            => ValueTask.FromResult(ListSessionsForProfile(request));
        ValueTask<IReadOnlyList<AgentSessionRecord>> IRuntimeCatalogService.ListSessionsForWorkspaceAsync(string request, CancellationToken token)
            => ValueTask.FromResult(ListSessionsForWorkspace(request));
        async IAsyncEnumerable<MemoryModels.AgentRuntimeCatalogEvent> IRuntimeCatalogService.WatchAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
        {
            await Task.CompletedTask;
            yield break;
        }
        ValueTask<MemoryModels.AgentTranscriptToolDetailRecord?> IRuntimeCatalogService.GetTranscriptToolDetailAsync(
            MemoryModels.AgentTranscriptToolDetailRequest request, CancellationToken token) => ValueTask.FromResult<MemoryModels.AgentTranscriptToolDetailRecord?>(null);
        ValueTask<MemoryModels.AgentCompactTranscriptPage> IRuntimeCatalogService.ReadCompactTranscriptAsync(
            MemoryModels.AgentCompactTranscriptRequest request, CancellationToken token)
            => ValueTask.FromException<MemoryModels.AgentCompactTranscriptPage>(new NotSupportedException("Memory evaluation does not read Compact transcripts."));
        ValueTask<MemoryModels.AgentTranscriptActivityMembersPage> IRuntimeCatalogService.ReadActivityMembersAsync(
            MemoryModels.AgentTranscriptActivityMembersRequest request, CancellationToken token)
            => ValueTask.FromException<MemoryModels.AgentTranscriptActivityMembersPage>(new NotSupportedException("Memory evaluation does not expand transcript groups."));
        ValueTask<IReadOnlyList<AgentTurnRecord>> IRuntimeCatalogService.ListRecentTranscriptHeadersAsync(
            MemoryModels.CYounics_Ai_Agent_Runtime_Catalog_RuntimeCatalogListRecentTranscriptHeadersRequest request, CancellationToken token)
            => ValueTask.FromResult<IReadOnlyList<AgentTurnRecord>>([]);
        ValueTask<IReadOnlyList<AgentTurnRecord>> IRuntimeCatalogService.ListRecentTurnsAsync(
            MemoryModels.CYounics_Ai_Agent_Runtime_Catalog_RuntimeCatalogListRecentTurnsRequest request, CancellationToken token)
            => ValueTask.FromResult(ListRecentTurns(request.SessionId, checked((int)request.Limit)));
        ValueTask<IReadOnlyList<AgentTurnRecord>> IRuntimeCatalogService.ListTranscriptHeadersAfterAsync(
            MemoryModels.CYounics_Ai_Agent_Runtime_Catalog_RuntimeCatalogListTranscriptHeadersAfterRequest request, CancellationToken token)
            => ValueTask.FromResult<IReadOnlyList<AgentTurnRecord>>([]);
        ValueTask<IReadOnlyList<AgentTurnRecord>> IRuntimeCatalogService.ListTranscriptHeadersBeforeAsync(
            MemoryModels.CYounics_Ai_Agent_Runtime_Catalog_RuntimeCatalogListTranscriptHeadersBeforeRequest request, CancellationToken token)
            => ValueTask.FromResult<IReadOnlyList<AgentTurnRecord>>([]);
        ValueTask<IReadOnlyList<AgentTurnRecord>> IRuntimeCatalogService.ListTurnsAfterAsync(
            MemoryModels.CYounics_Ai_Agent_Runtime_Catalog_RuntimeCatalogListTurnsAfterRequest request, CancellationToken token)
            => ValueTask.FromResult<IReadOnlyList<AgentTurnRecord>>([]);
        ValueTask<IReadOnlyList<AgentTurnRecord>> IRuntimeCatalogService.ListTurnsBeforeAsync(
            MemoryModels.CYounics_Ai_Agent_Runtime_Catalog_RuntimeCatalogListTurnsBeforeRequest request, CancellationToken token)
            => ValueTask.FromResult<IReadOnlyList<AgentTurnRecord>>([]);
        ValueTask<IReadOnlyList<AgentWorkspaceRecord>> IRuntimeCatalogService.ListWorkspacesAsync(CancellationToken token)
            => ListWorkspacesAsync(token);
    }

    private sealed class EvaluationEmbeddingProvider(string providerId) : IEmbeddingProviderService
    {
        private AgentEmbeddingProviderDescriptor Descriptor { get; } = new()
        {
            ProviderId = providerId,
            DisplayName = "Evaluation Embeddings",
            SupportedAuthModes = [],
        };

        public ValueTask<AgentEmbeddingProviderDescriptor> DescribeAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Descriptor);
        }

        public ValueTask<IReadOnlyList<AgentEmbeddingModelDescriptor>> ListModelsAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<AgentEmbeddingModelDescriptor>>([new()
            {
                ModelId = "semantic-v1", DisplayName = "Semantic V1", Dimensions = 3, IsRecommended = true,
            }]);

        public ValueTask<AgentEmbeddingProviderReadiness> GetReadinessAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentEmbeddingProviderReadiness
            {
                ProviderId = Descriptor.ProviderId, Status = MemoryModels.AgentProviderReadinessStatus.Ready, Message = "Ready.",
            });

        public ValueTask<AgentEmbeddingGenerationResult?> GenerateAsync(
            MemoryModels.CYounics_Ai_Agent_Embedding_Provider_EmbeddingProviderGenerateRequest request,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<AgentEmbeddingGenerationResult?>(new() { ModelId = request.ModelId, Values = CreateVector(request.Text) });

        public ValueTask<IReadOnlyList<AgentEmbeddingGenerationResult?>> GenerateBatchAsync(
            MemoryModels.CYounics_Ai_Agent_Embedding_Provider_EmbeddingProviderGenerateBatchRequest request,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<AgentEmbeddingGenerationResult?>>(request.Texts.Select(text =>
                (AgentEmbeddingGenerationResult?)new AgentEmbeddingGenerationResult { ModelId = request.ModelId, Values = CreateVector(text) }).ToArray());

        public ValueTask<string> SpaceIdentityAsync(string request, CancellationToken cancellationToken = default)
            => ValueTask.FromResult("evaluation-space");

        private static IReadOnlyList<double> CreateVector(string text)
        {
            var normalized = text.Trim().ToLowerInvariant();
            if (normalized.Contains("concise", StringComparison.Ordinal) || normalized.Contains("brief", StringComparison.Ordinal))
            {
                return [1f, 0f, 0f];
            }

            if (normalized.Contains("blazor", StringComparison.Ordinal) || normalized.Contains("framework", StringComparison.Ordinal) || normalized.Contains("project", StringComparison.Ordinal))
            {
                return [0f, 1f, 0f];
            }

            if (normalized.Contains("workspace", StringComparison.Ordinal) || normalized.Contains("working directory", StringComparison.Ordinal) || normalized.Contains("folder", StringComparison.Ordinal))
            {
                return [0f, 0f, 1f];
            }

            return [0.1f, 0.1f, 0f];
        }
    }

    private sealed class EvaluationPackageContext : IPackageContext
    {
        private readonly EvaluationPackageStorageContext _storage;

        public EvaluationPackageContext(string rootPath)
        {
            Directory.CreateDirectory(rootPath);
            ContentRootPath = rootPath;
            _storage = new EvaluationPackageStorageContext(rootPath);
        }

        public string PackageId => "test.memory.evaluation";

        public string Version => "1.0.0";

        public string ContentRootPath { get; }

        public IPackageStorageContext Storage => _storage;

        public IPackageSettings Settings { get; } = new EvaluationPackageSettings();

        public IPackageSecrets Secrets { get; } = new EvaluationPackageSecrets();


        public Sunder.Sdk.Logging.IPackageLogging Logging { get; } = Sunder.Sdk.Logging.NullPackageLogging.Instance;
    }

    private sealed class EvaluationPackageStorageContext : IPackageStorageContext
    {
        public EvaluationPackageStorageContext(string rootPath)
        {
            Directory.CreateDirectory(rootPath);
            Files = new EvaluationPackageFileStore(rootPath);
            State = new EvaluationPackageKeyValueStore();
            RoleLocalWorkspace = new TestPackageRoleLocalWorkspace(rootPath);
        }

        public IPackageFileStore Files { get; }

        public IPackageKeyValueStore State { get; }
        public IPackageRoleLocalWorkspace RoleLocalWorkspace { get; }
    }

    private sealed class EvaluationPackageFileStore(string rootPath) : TestPackageFileStoreBase(rootPath);

    private sealed class EvaluationPackageKeyValueStore : IPackageKeyValueStore
    {
        public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TestPackageStorageGuards.Key(key);
            return Task.FromResult<string?>(null);
        }

        public Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TestPackageStorageGuards.Key(key);
            TestPackageStorageGuards.Value(value);
            return Task.CompletedTask;
        }

        public Task<bool> ContainsKeyAsync(string key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TestPackageStorageGuards.Key(key);
            return Task.FromResult(false);
        }

        public Task DeleteValueAsync(string key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TestPackageStorageGuards.Key(key);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> ListKeysAsync(string? prefix = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TestPackageStorageGuards.Prefix(prefix);
            return Task.FromResult<IReadOnlyList<string>>([]);
        }
    }

    private sealed class EvaluationPackageSettings : EmptyPackageSettings;

    private sealed class EvaluationPackageSecrets : InMemoryPackageSecrets;
}
