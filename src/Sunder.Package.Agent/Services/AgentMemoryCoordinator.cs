using System.Text.RegularExpressions;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Services;

public sealed class AgentMemoryCoordinator(
    AgentSessionService sessionService,
    IPackageExtensionCatalog extensionCatalog)
{
    private const int MaxRecentLiveBufferTurns = 8;
    private const int MaxPromptContextTurns = 64;

    private readonly AgentSessionService _sessionService = sessionService;
    private readonly IPackageExtensionCatalog _extensionCatalog = extensionCatalog;

    public async Task<AgentInstructionContext> BuildInstructionContextAsync(
        AgentSessionRecord session,
        AgentProfileRecord profile,
        Guid runId,
        long runRevision,
        string userMessage,
        DateTimeOffset runStartedAtUtc,
        AgentWorkspaceRecord? workspace = null,
        AgentWorkspaceBindingRecord? executionBinding = null,
        IReadOnlyList<AgentToolDescriptor>? availableTools = null,
        CancellationToken cancellationToken = default)
    {
        var turns = _sessionService.ListRecentTurns(session.SessionId, MaxPromptContextTurns);
        var recentLiveBufferTurns = BuildRecentLiveBufferTurns(turns);
        var workingSummary = _sessionService.GetLatestSessionContextCheckpoint(session.SessionId)?.SummaryText;
        var sessionContext = CreateSessionContext(session, profile, workingSummary);
        var runContext = new AgentRunContextRecord(runId, runRevision, AgentRunStatus.Running, IsInterrupted: false, runStartedAtUtc);
        var turnContext = new AgentTurnContextRecord(sessionContext, runContext, userMessage, workingSummary);
        var recallPlan = BuildRecallPlan(userMessage, workingSummary, recentLiveBufferTurns);
        var promptContextPlan = ToPromptContextPlan(recallPlan);

        var promptContextBlocks = new List<AgentPromptContextBlock>();
        if (!string.IsNullOrWhiteSpace(profile.Instructions))
        {
            promptContextBlocks.Add(new AgentPromptContextBlock(
                "Profile Instructions",
                profile.Instructions.Trim(),
                Priority: 300,
                SourceId: "sunder.package.agent.profile",
                Provenance: AgentContextProvenance.User,
                Trust: AgentContextTrust.UserProvided));
        }

        if (!string.IsNullOrWhiteSpace(workingSummary))
        {
            promptContextBlocks.Add(new AgentPromptContextBlock(
                "Session Continuity Summary",
                workingSummary,
                Priority: 200,
                SourceId: "sunder.package.agent.session-context",
                Provenance: AgentContextProvenance.TranscriptSummary,
                Trust: AgentContextTrust.Untrusted));
        }

        var promptContextRequest = new AgentPromptContextRequest(
            sessionContext,
            runContext,
            turnContext,
            turns,
            recentLiveBufferTurns,
            promptContextPlan)
        {
            Profile = profile,
            Workspace = workspace,
            ExecutionBinding = executionBinding,
            AvailableTools = availableTools ?? [],
        };
        promptContextBlocks.AddRange(await CollectPromptContextBlocksAsync(
            promptContextRequest,
            cancellationToken));
        return new AgentInstructionContext(null, workingSummary, RecallResult: null, recallPlan, promptContextBlocks);
    }

    public async Task PublishLifecycleEventAsync(
        AgentLifecycleEventKind kind,
        AgentSessionRecord session,
        AgentProfileRecord profile,
        Guid runId,
        long runRevision,
        AgentRunStatus status,
        DateTimeOffset runStartedAtUtc,
        string userMessage,
        AgentTurnRecord? triggerTurn = null,
        AgentRunCheckpointRecord? checkpoint = null,
        bool isInterrupted = false,
        CancellationToken cancellationToken = default)
    {
        var turns = _sessionService.ListRecentTurns(session.SessionId, MaxPromptContextTurns);
        var recentLiveBufferTurns = BuildRecentLiveBufferTurns(turns);
        var workingSummary = _sessionService.GetLatestSessionContextCheckpoint(session.SessionId)?.SummaryText;
        var sessionContext = CreateSessionContext(session, profile, workingSummary);
        var runContext = new AgentRunContextRecord(runId, runRevision, status, isInterrupted, runStartedAtUtc);
        var turnContext = new AgentTurnContextRecord(sessionContext, runContext, userMessage, workingSummary);
        var genericLifecycleEvent = new AgentLifecycleEvent(kind, sessionContext, runContext, turnContext, turns, recentLiveBufferTurns, triggerTurn, checkpoint);

        foreach (var observer in GetLifecycleObservers())
        {
            try
            {
                await observer.HandleLifecycleEventAsync(genericLifecycleEvent, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Optional runtime observers must not block the base chat flow.
            }
        }

    }

    private IReadOnlyList<IAgentPromptContextContributor> GetPromptContextContributors()
        => _extensionCatalog.GetExtensions(PackageExtensionPoints.PromptContextContributors)
            .OrderBy(contributor => contributor.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private IReadOnlyList<IAgentLifecycleObserver> GetLifecycleObservers()
        => _extensionCatalog.GetExtensions(PackageExtensionPoints.LifecycleObservers)
            .OrderBy(observer => observer.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static AgentSessionContextRecord CreateSessionContext(
        AgentSessionRecord session,
        AgentProfileRecord profile,
        string? workingSummary)
        => new(
            session.SessionId,
            profile.ProfileId,
            profile.DisplayName,
            session.Title,
            session.State,
            workingSummary);

    private async Task<IReadOnlyList<AgentPromptContextBlock>> CollectPromptContextBlocksAsync(
        AgentPromptContextRequest request,
        CancellationToken cancellationToken)
    {
        var blocks = new List<AgentPromptContextBlock>();
        foreach (var contributor in GetPromptContextContributors())
        {
            try
            {
                var contribution = await contributor.ContributeContextAsync(request, cancellationToken);
                if (contribution?.Blocks is { Count: > 0 })
                {
                    blocks.AddRange(contribution.Blocks);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Optional prompt context contributors must not block the base chat flow.
            }
        }

        return blocks;
    }

    private static AgentPromptContextPlan ToPromptContextPlan(AgentMemoryRecallPlan recallPlan)
        => recallPlan.ShouldRecall
            ? new AgentPromptContextPlan(
                recallPlan.Intent.ToString(),
                recallPlan.QueryText,
                recallPlan.Reason,
                recallPlan.PreferredCategories,
                recallPlan.MaxEntryCount,
                recallPlan.MaxChars)
            : AgentPromptContextPlan.None(recallPlan.Reason);

    private static IReadOnlyList<AgentTurnRecord> BuildRecentLiveBufferTurns(IReadOnlyList<AgentTurnRecord> turns)
        => turns.Count <= MaxRecentLiveBufferTurns
            ? turns
            : turns.TakeLast(MaxRecentLiveBufferTurns).ToArray();

    private static AgentMemoryRecallPlan BuildRecallPlan(
        string userMessage,
        string? workingSummary,
        IReadOnlyList<AgentTurnRecord> recentLiveBufferTurns)
    {
        var normalized = NormalizeRecallText(userMessage);
        var hasPriorContext = !string.IsNullOrWhiteSpace(workingSummary)
                              || recentLiveBufferTurns.Count > 1;

        if (string.IsNullOrWhiteSpace(normalized) || IsExplicitMemoryWriteRequest(normalized))
        {
            return AgentMemoryRecallPlan.None("Current turn is write-focused or lacks a recall signal.");
        }

        if (ContainsAny(normalized, "style", "preference", "prefer", "concise", "brief", "verbose", "detailed"))
        {
            return new AgentMemoryRecallPlan(
                AgentMemoryRecallIntent.Preference,
                userMessage,
                "The user is asking for behavior or stylistic preferences.",
                PreferredCategories: ["preference", "standing-instruction"],
                MaxEntryCount: 4,
                MaxChars: 1200);
        }

        if (ContainsAny(normalized, "always", "never", "should i", "should you", "instruction", "constraint", "rule"))
        {
            return new AgentMemoryRecallPlan(
                AgentMemoryRecallIntent.StandingInstruction,
                userMessage,
                "The user is asking about standing instructions or execution rules.",
                PreferredCategories: ["standing-instruction", "preference"],
                MaxEntryCount: 4,
                MaxChars: 1200);
        }

        if (ContainsAny(normalized, "who am i", "who am i?", "about me", "my name", "participant", "identity"))
        {
            return new AgentMemoryRecallPlan(
                AgentMemoryRecallIntent.ParticipantFact,
                userMessage,
                "The user is asking for participant-specific facts.",
                PreferredCategories: ["participant-fact", "remembered-fact"],
                MaxEntryCount: 4,
                MaxChars: 1200);
        }

        if (ContainsAny(normalized, "project", "repo", "repository", "stack", "framework", "architecture", "dependency", "dependencies", "tech stack"))
        {
            return new AgentMemoryRecallPlan(
                AgentMemoryRecallIntent.ProjectFact,
                userMessage,
                "The user is asking for project or repository facts.",
                PreferredCategories: ["project-fact", "remembered-fact"],
                MaxEntryCount: 5,
                MaxChars: 1500);
        }

        if (ContainsAny(normalized, "environment", "machine", "os", "path", "working directory", "folder", "localhost", "port"))
        {
            return new AgentMemoryRecallPlan(
                AgentMemoryRecallIntent.EnvironmentFact,
                userMessage,
                "The user is asking for environment or runtime facts.",
                PreferredCategories: ["environment-fact", "project-fact"],
                MaxEntryCount: 5,
                MaxChars: 1500);
        }

        if (ContainsAny(normalized, "why", "rationale", "reason", "decide", "decision", "recap", "summary", "summarize what", "what did we decide"))
        {
            return new AgentMemoryRecallPlan(
                AgentMemoryRecallIntent.Rationale,
                BuildRecallQuery(userMessage, workingSummary),
                "The user is asking for prior rationale, recap, or decision history.",
                PreferredCategories: ["remembered-fact", "project-fact", "standing-instruction", "preference"],
                MaxEntryCount: 5,
                MaxChars: 1600);
        }

        if (hasPriorContext && ContainsAny(normalized, "continue", "resume", "pick up", "carry on", "again", "same", "as before", "earlier", "previous", "that", "it", "still", "already"))
        {
            return new AgentMemoryRecallPlan(
                AgentMemoryRecallIntent.Continuity,
                BuildRecallQuery(userMessage, workingSummary),
                "The user message appears to depend on earlier session context.",
                PreferredCategories: ["standing-instruction", "project-fact", "environment-fact", "remembered-fact", "preference"],
                MaxEntryCount: 5,
                MaxChars: 1500);
        }

        if (ContainsAny(normalized, "remember", "earlier", "before", "previous", "what was", "what did", "did we", "do you know"))
        {
            return new AgentMemoryRecallPlan(
                AgentMemoryRecallIntent.GeneralFact,
                BuildRecallQuery(userMessage, workingSummary),
                "The user is explicitly asking for prior remembered context.",
                PreferredCategories: ["remembered-fact", "participant-fact", "project-fact", "environment-fact", "preference", "standing-instruction"],
                MaxEntryCount: 6,
                MaxChars: 1800);
        }

        return AgentMemoryRecallPlan.None("Current turn looks self-contained and does not justify durable memory recall.");
    }

    private static string BuildRecallQuery(string userMessage, string? workingSummary)
        => string.IsNullOrWhiteSpace(workingSummary)
            ? userMessage
            : userMessage + "\n\nWorking summary: " + workingSummary.Trim();

    private static bool IsExplicitMemoryWriteRequest(string normalizedText)
        => ContainsAny(normalizedText, "remember this", "remember that", "remember:", "forget this", "forget that");

    private static bool ContainsAny(string normalizedText, params string[] signals)
        => signals.Any(signal => normalizedText.Contains(signal, StringComparison.Ordinal));

    private static string NormalizeRecallText(string text)
        => Regex.Replace(text.Trim().ToLowerInvariant(), "[^a-z0-9]+", " ").Trim();
}

public sealed record AgentInstructionContext(
    string? SystemInstructions,
    string? WorkingSummary,
    AgentMemoryRecallResult? RecallResult,
    AgentMemoryRecallPlan RecallPlan,
    IReadOnlyList<AgentPromptContextBlock>? PromptContextBlocks = null)
{
    public bool HasSupplementaryContext
        => !string.IsNullOrWhiteSpace(WorkingSummary)
            || (RecallResult?.Entries.Count ?? 0) > 0
            || (PromptContextBlocks?.Count ?? 0) > 0;
}
