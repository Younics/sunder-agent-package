using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Services;

public sealed class AgentMemoryCoordinator(
    AgentSessionService sessionService,
    IPackageExtensionCatalog extensionCatalog,
    AgentLifecycleDispatcher? lifecycleDispatcher = null)
{
    private const int MaxRecentLiveBufferTurns = 8;
    private const int MaxPromptContextTurns = 64;

    private readonly AgentSessionService _sessionService = sessionService;
    private readonly IPackageExtensionCatalog _extensionCatalog = extensionCatalog;
    private readonly IPackageExtensionInvocationCatalog _invocationCatalog =
        AgentExtensionInvocation.Require(extensionCatalog);
    private readonly AgentLifecycleDispatcher _lifecycleDispatcher =
        lifecycleDispatcher ?? new AgentLifecycleDispatcher(sessionService.Store, extensionCatalog);
    private readonly ConcurrentDictionary<PromptContextAcknowledgmentKey,
        IReadOnlyList<AgentExtensionReference<IAgentPromptContextContributor, PromptContextContributorMetadata>>>
        _promptContextAcknowledgments = new();

    public Task<AgentInstructionContext> BuildInstructionContextAsync(
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
        => BuildInstructionContextCoreAsync(
            session,
            profile,
            runId,
            runRevision,
            userMessage,
            runStartedAtUtc,
            workspace,
            executionBinding,
            availableTools,
            _sessionService.GetLatestSessionContextCheckpoint(session.SessionId),
            projectedTurns: null,
            cancellationToken);

    internal Task<AgentInstructionContext> BuildInstructionContextForProjectionAsync(
        AgentSessionRecord session,
        AgentProfileRecord profile,
        Guid runId,
        long runRevision,
        string userMessage,
        DateTimeOffset runStartedAtUtc,
        AgentWorkspaceRecord? workspace,
        AgentWorkspaceBindingRecord? executionBinding,
        IReadOnlyList<AgentToolDescriptor>? availableTools,
        AgentSessionContextCheckpointRecord? selectedContextCheckpoint,
        IReadOnlyList<AgentTurnRecord> projectedTurns,
        CancellationToken cancellationToken)
        => BuildInstructionContextCoreAsync(
            session,
            profile,
            runId,
            runRevision,
            userMessage,
            runStartedAtUtc,
            workspace,
            executionBinding,
            availableTools,
            selectedContextCheckpoint,
            projectedTurns,
            cancellationToken);

    private async Task<AgentInstructionContext> BuildInstructionContextCoreAsync(
        AgentSessionRecord session,
        AgentProfileRecord profile,
        Guid runId,
        long runRevision,
        string userMessage,
        DateTimeOffset runStartedAtUtc,
        AgentWorkspaceRecord? workspace,
        AgentWorkspaceBindingRecord? executionBinding,
        IReadOnlyList<AgentToolDescriptor>? availableTools,
        AgentSessionContextCheckpointRecord? selectedContextCheckpoint,
        IReadOnlyList<AgentTurnRecord>? projectedTurns,
        CancellationToken cancellationToken)
    {
        var turns = projectedTurns ?? _sessionService.ListRecentTurns(session.SessionId, MaxPromptContextTurns);
        var recentLiveBufferTurns = BuildRecentLiveBufferTurns(turns);
        var workingSummary = selectedContextCheckpoint?.SummaryText;
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
            promptContextBlocks[^1] = promptContextBlocks[^1] with
            {
                Usage = AgentPromptContextUsage.StandingInstruction,
                Authority = AgentPromptContextAuthority.StandingInstruction,
                HostIdentity = AgentPromptContextHostPolicy.ProfileInstructionIdentity,
            };
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

        var transcriptEpoch = _sessionService.GetTranscriptEpoch(session.SessionId);
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
            MemoryConsistencyBarrier = _sessionService.GetMemoryConsistencyBarrier(runId),
            TranscriptEpoch = transcriptEpoch,
            ExecutionTargetReference = ResolveExecutionTargetReference(executionBinding),
        };
        var collected = await CollectPromptContextBlocksAsync(
            promptContextRequest,
            cancellationToken).ConfigureAwait(false);
        promptContextBlocks.AddRange(collected.Blocks);
        var acknowledgmentKey = new PromptContextAcknowledgmentKey(
            session.SessionId,
            runId,
            transcriptEpoch);
        if (collected.AcknowledgmentSinks.Count == 0)
        {
            _promptContextAcknowledgments.TryRemove(acknowledgmentKey, out _);
        }
        else
        {
            _promptContextAcknowledgments[acknowledgmentKey] = collected.AcknowledgmentSinks;
        }
        return new AgentInstructionContext(
            null,
            workingSummary,
            RecallResult: null,
            recallPlan,
            promptContextBlocks,
            transcriptEpoch);
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
        var isDurablyCaptured = _sessionService.Store.ContainsRunLifecycleEvent(kind, runId);
        await _lifecycleDispatcher.FlushAsync(cancellationToken).ConfigureAwait(false);
        if (isDurablyCaptured)
        {
            return;
        }

        var turns = _sessionService.ListRecentTurns(session.SessionId, MaxPromptContextTurns);
        var recentLiveBufferTurns = BuildRecentLiveBufferTurns(turns);
        var workingSummary = _sessionService.GetLatestSessionContextCheckpoint(session.SessionId)?.SummaryText;
        var sessionContext = CreateSessionContext(session, profile, workingSummary);
        var runContext = new AgentRunContextRecord(runId, runRevision, status, isInterrupted, runStartedAtUtc);
        var turnContext = new AgentTurnContextRecord(sessionContext, runContext, userMessage, workingSummary);
        var lifecycleEvent = new AgentLifecycleEvent(
            kind,
            sessionContext,
            runContext,
            turnContext,
            turns,
            recentLiveBufferTurns,
            triggerTurn,
            checkpoint);

        foreach (var observerReference in GetLifecycleObserverReferences())
        {
            if (!observerReference.Reference.TryAcquire(out var lease))
            {
                continue;
            }
            using (lease)
            {
                using var invocation = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    lease.RetirementToken);
                try
                {
                    await lease.Contribution.HandleLifecycleEventAsync(lifecycleEvent, invocation.Token);
                }
                catch (OperationCanceledException) when (
                    lease.RetirementToken.IsCancellationRequested
                    && !cancellationToken.IsCancellationRequested)
                {
                    // Owner retirement makes this optional compatibility callback unavailable.
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception) when (lease.RetirementToken.IsCancellationRequested)
                {
                    // Do not attribute a concurrent owner retirement as an observer failure.
                }
                catch
                {
                    // Optional runtime observers must not block the base chat flow.
                }
            }
        }
    }

    private IReadOnlyList<AgentExtensionReference<IAgentPromptContextContributor, PromptContextContributorMetadata>>
        GetPromptContextContributors()
        => AgentExtensionInvocation.Snapshot(
                _invocationCatalog,
                PackageExtensionPoints.PromptContextContributors,
                static contributor => new PromptContextContributorMetadata(
                    contributor.ContributorId,
                    contributor.DisplayName,
                    contributor is IAgentPromptContextAcknowledgmentSink))
            .OrderBy(contributor => contributor.Metadata.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(contributor => contributor.PackageId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private IReadOnlyList<LifecycleObserverReference> GetLifecycleObserverReferences()
    {
        var references = new List<LifecycleObserverReference>();
        foreach (var reference in _invocationCatalog.GetExtensionReferences(PackageExtensionPoints.LifecycleObservers))
        {
            if (reference.TryAcquire(out var lease))
            {
                using (lease)
                {
                    references.Add(new LifecycleObserverReference(reference, lease.Contribution.DisplayName));
                }
            }
        }

        return references
            .OrderBy(observer => observer.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private IPackageExtensionReference<IAgentExecutionTarget>? ResolveExecutionTargetReference(
        AgentWorkspaceBindingRecord? binding)
    {
        if (binding is null || !binding.IsEnabled)
        {
            return null;
        }

        foreach (var reference in _invocationCatalog.GetExtensionReferences(PackageExtensionPoints.ExecutionTargets))
        {
            if (!reference.TryAcquire(out var lease))
            {
                continue;
            }
            using (lease)
            {
                var descriptor = lease.Contribution.Descriptor;
                if (!lease.RetirementToken.IsCancellationRequested
                    && (string.Equals(descriptor.TargetId, binding.ContributionId, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(descriptor.TargetKind, binding.ContributionId, StringComparison.OrdinalIgnoreCase)))
                {
                    return reference;
                }
            }
        }

        return null;
    }

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

    private async Task<CollectedPromptContext> CollectPromptContextBlocksAsync(
        AgentPromptContextRequest request,
        CancellationToken cancellationToken)
    {
        var blocks = new List<AgentPromptContextBlock>();
        var acknowledgmentSinks = new List<
            AgentExtensionReference<IAgentPromptContextContributor, PromptContextContributorMetadata>>();
        foreach (var ownedContributor in GetPromptContextContributors())
        {
            var required = AgentPromptContextHostPolicy.IsRequiredScopedInstructionContributor(ownedContributor);
            if (required && ownedContributor.Metadata.SupportsAcknowledgment)
            {
                acknowledgmentSinks.Add(ownedContributor);
            }
            if (!request.ContextPlan.ShouldContribute && !required)
            {
                continue;
            }
            try
            {
                var contribution = await AgentExtensionInvocation.InvokeAsync(
                    ownedContributor,
                    cancellationToken,
                    (contributor, token) => contributor.ContributeContextAsync(request, token));
                if (contribution?.Blocks is { Count: > 0 })
                {
                    blocks.AddRange(contribution.Blocks.Select(block =>
                        AgentPromptContextHostPolicy.Normalize(block, required)));
                }
            }
            catch (AgentPackageUnavailableException ex)
            {
                if (required)
                {
                    throw new InvalidOperationException(
                        $"Required scoped instruction context is unavailable: {AgentPromptContextHostPolicy.BoundMessage(ex.Message)}",
                        ex);
                }
                // Retired optional contributors are omitted from this prompt.
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (required)
                {
                    throw new InvalidOperationException(
                        $"Required scoped instruction context could not be built: {AgentPromptContextHostPolicy.BoundMessage(ex.Message)}",
                        ex);
                }
                // Optional reference-context contributors must not block the base chat flow.
            }
        }

        return new CollectedPromptContext(blocks, acknowledgmentSinks);
    }

    internal async ValueTask AcknowledgePromptContextAsync(
        AgentPromptContextReceipt receipt,
        CancellationToken cancellationToken)
    {
        if (receipt.Blocks.Count == 0)
        {
            return;
        }

        var key = new PromptContextAcknowledgmentKey(
            receipt.SessionId,
            receipt.RunId,
            receipt.TranscriptEpoch);
        _promptContextAcknowledgments.TryRemove(key, out var sinks);
        sinks ??= [];
        if (sinks.Count != 1)
        {
            throw new InvalidOperationException("Required scoped instruction acknowledgment sink is unavailable or ambiguous.");
        }

        try
        {
            await AgentExtensionInvocation.InvokeAsync(
                sinks[0],
                cancellationToken,
                (contributor, token) =>
                    ((IAgentPromptContextAcknowledgmentSink)contributor)
                    .AcknowledgePromptContextAsync(receipt, token)).ConfigureAwait(false);
        }
        catch (AgentPackageUnavailableException ex)
        {
            throw new InvalidOperationException(
                $"Required scoped instruction acknowledgment is unavailable: {AgentPromptContextHostPolicy.BoundMessage(ex.Message)}",
                ex);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Required scoped instruction acknowledgment failed: {AgentPromptContextHostPolicy.BoundMessage(ex.Message)}",
                ex);
        }
    }

    internal void DiscardPromptContextAcknowledgment(Guid sessionId, Guid runId, long transcriptEpoch)
        => _promptContextAcknowledgments.TryRemove(
            new PromptContextAcknowledgmentKey(sessionId, runId, transcriptEpoch),
            out _);

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

    private sealed record LifecycleObserverReference(
        IPackageExtensionReference<IAgentLifecycleObserver> Reference,
        string DisplayName);

    internal sealed record PromptContextContributorMetadata(
        string ContributorId,
        string DisplayName,
        bool SupportsAcknowledgment);

    private readonly record struct PromptContextAcknowledgmentKey(
        Guid SessionId,
        Guid RunId,
        long TranscriptEpoch);

    private sealed record CollectedPromptContext(
        IReadOnlyList<AgentPromptContextBlock> Blocks,
        IReadOnlyList<AgentExtensionReference<IAgentPromptContextContributor, PromptContextContributorMetadata>>
            AcknowledgmentSinks);
}

internal static class AgentPromptContextHostPolicy
{
    internal const string ScopedInstructionPackageId = "sunder.package.agent.tools.files";
    internal const string ScopedInstructionContributorId = "workspace-files";
    internal const string ScopedInstructionIdentity = "sunder.host.scoped-instruction.v1";
    internal const string ProfileInstructionIdentity = "sunder.host.profile-standing-instruction.v1";

    public static bool IsRequiredScopedInstructionContributor(
        AgentExtensionReference<IAgentPromptContextContributor, AgentMemoryCoordinator.PromptContextContributorMetadata>
            contribution)
        => string.Equals(contribution.PackageId, ScopedInstructionPackageId, StringComparison.OrdinalIgnoreCase)
           && string.Equals(
                contribution.Metadata.ContributorId,
                ScopedInstructionContributorId,
                StringComparison.Ordinal);

    public static AgentPromptContextBlock Normalize(AgentPromptContextBlock block, bool requiredScopedContributor)
    {
        if (requiredScopedContributor
            && block.Usage == AgentPromptContextUsage.ScopedInstruction
            && IsValidScope(block.Scope))
        {
            return block with
            {
                Authority = AgentPromptContextAuthority.ScopedInstruction,
                HostIdentity = ScopedInstructionIdentity,
            };
        }

        return block with
        {
            Usage = AgentPromptContextUsage.Reference,
            Authority = AgentPromptContextAuthority.Reference,
            HostIdentity = null,
            Scope = null,
        };
    }

    public static string BoundMessage(string message)
    {
        var normalized = message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return normalized.Length <= 600 ? normalized : normalized[..600] + "...";
    }

    private static bool IsValidScope(AgentPromptContextScope? scope)
        => scope is not null
           && !string.IsNullOrWhiteSpace(scope.ScopeRoot)
           && !string.IsNullOrWhiteSpace(scope.AppliesToDirectory)
           && !string.IsNullOrWhiteSpace(scope.DocumentPath)
           && IsHash(scope.ContentHash)
           && IsHash(scope.ContextIdentity);

    private static bool IsHash(string value)
        => value.Length == 64
           && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}

public sealed record AgentInstructionContext(
    string? SystemInstructions,
    string? WorkingSummary,
    AgentMemoryRecallResult? RecallResult,
    AgentMemoryRecallPlan RecallPlan,
    IReadOnlyList<AgentPromptContextBlock>? PromptContextBlocks = null,
    long TranscriptEpoch = 0)
{
    public bool HasSupplementaryContext
        => !string.IsNullOrWhiteSpace(WorkingSummary)
            || (RecallResult?.Entries.Count ?? 0) > 0
            || (PromptContextBlocks?.Count ?? 0) > 0;
}
