using Microsoft.Extensions.Logging.Abstractions;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Memory.Semantic;
using Sunder.Package.Agent.Memory.Semantic.Services;
using Sunder.Sdk.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Sunder.Package.Agent.Tests;

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
        using var harness = new MemoryEvaluationHarness();

        for (var index = 0; index < 12; index++)
        {
            await harness.AddUserTurnAsync($"Quick progress check {index}.");
        }

        await harness.AddUserTurnAsync("My name is Micha.");
        await harness.AddUserTurnAsync("I prefer concise final summaries.");
        await harness.AddUserTurnAsync("Always use apply_patch for file edits.");
        await harness.AddAssistantTurnAsync("The project uses Blazor Server on .NET 10.");
        await harness.AddToolResultAsync("Inspect workspace", "Working directory is /workspace/app.");

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
        using var harness = new MemoryEvaluationHarness(enableEmbeddings: true);

        await harness.AddUserTurnAsync("I prefer concise final summaries.");
        await harness.AddUserTurnAsync("Always use apply_patch for file edits.");
        await harness.AddAssistantTurnAsync("The project uses Blazor Server on .NET 10.");
        await harness.AddToolResultAsync("Inspect workspace", "Working directory is /workspace/app.");
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
        using var harness = new MemoryEvaluationHarness(enableEmbeddings: true);

        await harness.AddUserTurnAsync("Always use apply_patch for file edits.");
        await harness.AddUserTurnAsync("I prefer concise final summaries.");
        await harness.AddAssistantTurnAsync("The project uses Blazor Server on .NET 10.");
        await harness.AddToolResultAsync("Inspect workspace", "Working directory is /workspace/app.");
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
        using var harness = new MemoryEvaluationHarness(enableEmbeddings: true);

        await harness.AddUserTurnAsync("Always use apply_patch for file edits.");
        await harness.AddUserTurnAsync("I prefer concise final summaries.");
        await harness.AddAssistantTurnAsync("The project uses Blazor Server on .NET 10.");
        await harness.AddAssistantTurnAsync("The project also uses SignalR for real-time updates.");
        await harness.AddToolResultAsync("Inspect workspace", "Working directory is /workspace/app.");
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
    public async Task ContestedEvaluation_RanksActiveMemoryAboveContestedMemory()
    {
        using var harness = new MemoryEvaluationHarness();

        var activeMemory = harness.Store.UpsertMemory(new MemoryUpsertRequest(
            harness.SessionId,
            Category: "preference",
            Content: "Prefer concise summaries.",
            NormalizedContent: "prefer concise summaries.",
            EvidenceText: "Active preference.",
            SourceTurnId: Guid.NewGuid(),
            IsPinned: true,
            Importance: 0.8f,
            Confidence: 0.85f));
        var contestedMemory = harness.Store.UpsertMemory(new MemoryUpsertRequest(
            harness.SessionId,
            Category: "preference",
            Content: "Prefer concise summaries for every response.",
            NormalizedContent: "prefer concise summaries for every response.",
            EvidenceText: "Contested preference.",
            SourceTurnId: Guid.NewGuid(),
            IsPinned: true,
            Importance: 0.8f,
            Confidence: 0.85f));
        harness.Store.SetContested(contestedMemory.MemoryId);

        var entries = await harness.RecallAsync(
            "What summary style should I use?",
            AgentMemoryRecallIntent.Preference,
            ["preference"],
            maxEntryCount: 4,
            maxChars: 1200);

        var report = BuildRecallReport("contested-vs-active", entries, maxEntryCount: 4, maxChars: 1200, entry => entry.TrustState == AgentMemoryTrustState.Active || entry.TrustState == AgentMemoryTrustState.Contested);
        WriteReport(report);

        Assert.NotEmpty(entries);
        Assert.Equal(AgentMemoryTrustState.Active, entries[0].TrustState);
        Assert.Equal(activeMemory.MemoryId.ToString("N"), entries[0].MemoryId);
        Assert.Contains(entries, entry => entry.TrustState == AgentMemoryTrustState.Contested);
    }

    [Fact]
    public async Task CorrectionEvaluation_PrefersCorrectedMemoryOverSupersededOriginal()
    {
        using var harness = new MemoryEvaluationHarness();

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
        Assert.DoesNotContain(entries, entry => entry.TrustState == AgentMemoryTrustState.Superseded);
        Assert.DoesNotContain(entries, entry => entry.MemoryId == source.MemoryId.ToString("N"));
    }

    [Fact]
    public async Task NoisySessionEvaluation_MaintainsUsefulPrecisionForEnvironmentRecall()
    {
        using var harness = new MemoryEvaluationHarness(enableEmbeddings: true);

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

        var report = BuildRecallReport("noisy-environment-recall", entries, maxEntryCount: 4, maxChars: 1200, entry => entry.Category is "environment-fact" or "project-fact");
        WriteReport(report);

        Assert.NotEmpty(entries);
        Assert.Contains(entries[0].Category, new[] { "environment-fact", "project-fact" });
        Assert.True(report.RelevantRatio >= 0.5d);
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

    private sealed class MemoryEvaluationHarness : IDisposable
    {
        private readonly EvalExtensionCatalog _extensionCatalog;
        private readonly EvaluationRuntimeCatalog _runtimeCatalog;
        private readonly EvaluationPackageContext _packageContext;
        private readonly SemanticMemoryIndexingBackgroundService _indexingBackgroundService;
        private readonly MemorySemanticFeature _feature;
        private readonly List<AgentTurnRecord> _turns = [];
        private long _nextRevision = 1;

        public MemoryEvaluationHarness(bool enableEmbeddings = false)
        {
            SessionId = Guid.NewGuid();
            ProfileId = "profile-eval";
            _extensionCatalog = new EvalExtensionCatalog();
            _runtimeCatalog = new EvaluationRuntimeCatalog(CreateProfile(enableEmbeddings), SessionId, ProfileId);
            _packageContext = new EvaluationPackageContext(
                Path.Combine(Path.GetTempPath(), "sunder-memory-eval", Guid.NewGuid().ToString("N")));
            Store = new MemoryLocalStore(_packageContext);
            Settings = new MemorySemanticSettingsService(_packageContext);

            _extensionCatalog.AddExtension(PackageExtensionPoints.RuntimeCatalogs, _runtimeCatalog);
            if (enableEmbeddings)
            {
                _extensionCatalog.AddExtension(PackageExtensionPoints.EmbeddingProviders, new EvaluationEmbeddingProvider("test-embeddings"));
            }

            var metrics = new SemanticMemoryMetricsService();
            RetrievalBackend = new SemanticMemoryRetrievalBackend(Store, new ProfileConfiguredEmbeddingProviderResolver(_extensionCatalog), Settings);
            _indexingBackgroundService = new SemanticMemoryIndexingBackgroundService(Store, _extensionCatalog, Settings, RetrievalBackend, metrics);
            _feature = new MemorySemanticFeature(
                Store,
                new SemanticMemoryRecallService(Store, RetrievalBackend, metrics),
                new SemanticMemoryPromotionService(Store, _indexingBackgroundService, metrics),
                new MemoryWorkingSummaryBuilder(Store));
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
            await _feature.HandleLifecycleEventAsync(CreateLifecycleEvent(AgentLifecycleEventKind.UserTurnAdded, text, turn));
        }

        public async Task AddAssistantTurnAsync(string text)
        {
            var turn = CreateTextTurn(AgentMessageRole.Assistant, text);
            _turns.Add(turn);
            await _feature.HandleLifecycleEventAsync(CreateLifecycleEvent(AgentLifecycleEventKind.AssistantTurnCompleted, text, turn));
        }

        public async Task AddToolResultAsync(string summary, string content)
        {
            var turnId = Guid.NewGuid();
            var turn = new AgentTurnRecord(
                turnId,
                SessionId,
                AgentMessageRole.Tool,
                AgentTurnKind.ToolResult,
                [new AgentTurnItemRecord(Guid.NewGuid(), turnId, 0, AgentTurnItemKind.ToolResult, content, Guid.NewGuid().ToString("N"), "inspect_workspace", null, summary, null, null, false, false, null, null)],
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow);
            _turns.Add(turn);
            await _feature.HandleLifecycleEventAsync(CreateLifecycleEvent(AgentLifecycleEventKind.ToolResultRecorded, summary, turn));
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
            var sessionContext = new AgentSessionContextRecord(SessionId, ProfileId, "Eval Profile", "Eval Session", AgentSessionState.Active, null);
            var runContext = new AgentRunContextRecord(Guid.NewGuid(), _nextRevision++, AgentRunStatus.Running, IsInterrupted: false, DateTimeOffset.UtcNow);
            var turnContext = new AgentTurnContextRecord(sessionContext, runContext, query, null);
            var recall = await _feature.RecallAsync(new AgentMemoryRecallRequest(
                sessionContext,
                runContext,
                turnContext,
                _turns,
                BuildRecentLiveBuffer(),
                new AgentMemoryRecallPlan(intent, query, PreferredCategories: preferredCategories, MaxEntryCount: maxEntryCount, MaxChars: maxChars)));
            return recall?.Entries ?? [];
        }

        public void Dispose()
        {
            _indexingBackgroundService.Dispose();
        }

        private AgentProfileRecord CreateProfile(bool enableEmbeddings)
            => new(
                ProfileId,
                "Eval Profile",
                null,
                null,
                null,
                null,
                enableEmbeddings ? "test-embeddings" : null,
                enableEmbeddings ? "semantic-v1" : null,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                []);

        private AgentTurnRecord CreateTextTurn(AgentMessageRole role, string text)
        {
            var turnId = Guid.NewGuid();
            return new AgentTurnRecord(
                turnId,
                SessionId,
                role,
                AgentTurnKind.Message,
                [new AgentTurnItemRecord(Guid.NewGuid(), turnId, 0, AgentTurnItemKind.Text, text, null, null, null, null, null, null, false, false, null, null)],
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow);
        }

        private AgentLifecycleEvent CreateLifecycleEvent(AgentLifecycleEventKind kind, string userMessage, AgentTurnRecord triggerTurn)
        {
            var sessionContext = new AgentSessionContextRecord(SessionId, ProfileId, "Eval Profile", "Eval Session", AgentSessionState.Active, null);
            var runContext = new AgentRunContextRecord(Guid.NewGuid(), _nextRevision++, AgentRunStatus.Running, IsInterrupted: false, DateTimeOffset.UtcNow);
            return new AgentLifecycleEvent(
                kind,
                sessionContext,
                runContext,
                new AgentTurnContextRecord(sessionContext, runContext, userMessage, null),
                _turns.ToArray(),
                BuildRecentLiveBuffer(),
                TriggerTurn: triggerTurn);
        }

        private IReadOnlyList<AgentTurnRecord> BuildRecentLiveBuffer()
            => _turns.Count <= 8 ? _turns.ToArray() : _turns.TakeLast(8).ToArray();
    }

    private sealed class EvalExtensionCatalog : IPackageExtensionCatalog
    {
        private readonly Dictionary<string, List<object>> _extensions = new(StringComparer.OrdinalIgnoreCase);

        public void AddExtension<TContract>(PackageExtensionPoint<TContract> extensionPoint, TContract extension)
        {
            if (!_extensions.TryGetValue(extensionPoint.Id, out var entries))
            {
                entries = [];
                _extensions[extensionPoint.Id] = entries;
            }

            entries.Add(extension!);
        }

        public IReadOnlyList<TContract> GetExtensions<TContract>(PackageExtensionPoint<TContract> extensionPoint)
            => !_extensions.TryGetValue(extensionPoint.Id, out var entries)
                ? []
                : entries.Cast<TContract>().ToArray();
    }

    private sealed class EvaluationRuntimeCatalog(AgentProfileRecord profile, Guid sessionId, string profileId) : IAgentRuntimeCatalog
    {
        private readonly AgentSessionRecord _session = new(sessionId, "Eval Session", AgentSessionState.Active, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        private readonly AgentWorkspaceRecord _workspace = new("workspace", "Workspace", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

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

        public IReadOnlyList<AgentSessionRecord> ListSessions() => [_session];

        public IReadOnlyList<AgentSessionRecord> ListSessionsForProfile(string requestedProfileId)
            => string.Equals(requestedProfileId, profileId, StringComparison.OrdinalIgnoreCase) ? [_session] : [];

        public AgentSessionRecord? GetSession(Guid requestedSessionId)
            => requestedSessionId == _session.SessionId ? _session : null;

        public IReadOnlyList<AgentWorkspaceRecord> ListWorkspaces() => [_workspace];

        public AgentWorkspaceRecord? GetWorkspace(string workspaceId)
            => string.Equals(workspaceId, _workspace.WorkspaceId, StringComparison.OrdinalIgnoreCase) ? _workspace : null;

        public AgentProfileRecord? GetSessionProfile(Guid requestedSessionId)
            => requestedSessionId == _session.SessionId ? profile : null;

        public AgentWorkingSummaryRecord? GetWorkingSummary(Guid sessionId) => null;

        public AgentRunCheckpointRecord? GetLatestCheckpoint(Guid sessionId) => null;

        public IReadOnlyList<AgentTurnRecord> ListRecentTurns(Guid sessionId, int limit) => [];

        public IReadOnlyList<AgentTurnRecord> ListTurnsBefore(Guid sessionId, DateTimeOffset beforeCreatedAtUtc, Guid beforeTurnId, int limit) => [];

        public IReadOnlyList<AgentProfileRecord> ListProfiles() => [profile];

        public AgentProfileRecord? GetProfile(string profileId)
            => string.Equals(profile.ProfileId, profileId, StringComparison.OrdinalIgnoreCase) ? profile : null;

        public AgentProfileModelBindingRecord? GetSessionModelBinding(Guid sessionId, string capabilityKind) => null;

        public AgentProfileModelBindingRecord? GetModelBinding(string profileId, string capabilityKind) => null;
    }

    private sealed class EvaluationEmbeddingProvider(string providerId) : IAgentEmbeddingProvider
    {
        public AgentEmbeddingProviderDescriptor Descriptor { get; } = new(providerId, "Evaluation Embeddings", []);

        public ValueTask<IReadOnlyList<AgentEmbeddingModelDescriptor>> GetAvailableModelsAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<AgentEmbeddingModelDescriptor>>([new AgentEmbeddingModelDescriptor("semantic-v1", "Semantic V1", Dimensions: 3, IsRecommended: true)]);

        public ValueTask<AgentEmbeddingProviderReadiness> GetReadinessAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentEmbeddingProviderReadiness(Descriptor.ProviderId, AgentProviderReadinessStatus.Ready, "Ready."));

        public ValueTask<AgentEmbeddingGenerationResult?> GenerateEmbeddingAsync(string modelId, string text, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<AgentEmbeddingGenerationResult?>(new AgentEmbeddingGenerationResult(modelId, CreateVector(text)));

        public ValueTask<IReadOnlyList<AgentEmbeddingGenerationResult?>> GenerateEmbeddingsAsync(string modelId, IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<AgentEmbeddingGenerationResult?>>(texts.Select(text => (AgentEmbeddingGenerationResult?)new AgentEmbeddingGenerationResult(modelId, CreateVector(text))).ToArray());

        private static IReadOnlyList<float> CreateVector(string text)
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
            InstallPath = rootPath;
            _storage = new EvaluationPackageStorageContext(rootPath);
        }

        public string PackageId => "test.memory.evaluation";

        public Version Version => new(1, 0, 0);

        public string InstallPath { get; }

        public IPackageStorageContext Storage => _storage;

        public IPackageConfiguration Configuration { get; } = new EvaluationPackageConfiguration();

        public IPackageSecrets Secrets { get; } = new EvaluationPackageSecrets();

        public Microsoft.Extensions.Logging.ILoggerFactory LoggerFactory => Logging.LoggerFactory;

        public Sunder.Sdk.Logging.IPackageLogging Logging { get; } = Sunder.Sdk.Logging.NullPackageLogging.Instance;
    }

    private sealed class EvaluationPackageStorageContext : IPackageStorageContext
    {
        public EvaluationPackageStorageContext(string rootPath)
        {
            DataRootPath = Path.Combine(rootPath, "data");
            CacheRootPath = Path.Combine(rootPath, "cache");
            LogsRootPath = Path.Combine(rootPath, "logs");
            Directory.CreateDirectory(DataRootPath);
            Directory.CreateDirectory(CacheRootPath);
            Directory.CreateDirectory(LogsRootPath);
            Files = new EvaluationPackageFileStore(rootPath);
            State = new EvaluationPackageKeyValueStore();
        }

        public string DataRootPath { get; }

        public string CacheRootPath { get; }

        public string LogsRootPath { get; }

        public IPackageFileStore Files { get; }

        public IPackageKeyValueStore State { get; }
    }

    private sealed class EvaluationPackageFileStore(string rootPath) : IPackageFileStore
    {
        public string RootPath { get; } = rootPath;

        public string GetPath(string relativePath) => Path.Combine(RootPath, relativePath);
    }

    private sealed class EvaluationPackageKeyValueStore : IPackageKeyValueStore
    {
        public string? GetValue(string key) => null;

        public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);

        public Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<bool> ContainsKeyAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult(false);

        public Task DeleteValueAsync(string key, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<string>> ListKeysAsync(string? prefix = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class EvaluationPackageConfiguration : IPackageConfiguration
    {
        public string? GetValue(string key) => null;
    }

    private sealed class EvaluationPackageSecrets : IPackageSecrets
    {
        public string? GetSecret(string key) => null;

        public void SetSecret(string key, string value)
        {
        }

        public void DeleteSecret(string key)
        {
        }
    }
}
