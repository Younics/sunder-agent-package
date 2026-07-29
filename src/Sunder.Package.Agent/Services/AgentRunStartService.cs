using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Models;
using Sunder.Package.Agent.Services.BehaviorLoops;
using Sunder.Sdk.Logging;

namespace Sunder.Package.Agent.Services;

internal sealed class AgentRunStartService(
    AgentSessionService sessionService,
    AgentActiveRunRegistry activeRunRegistry,
    AgentRunEventLogger runEventLogger,
    AgentSessionTitleService? sessionTitleService = null,
    AgentSessionTransitionGate? transitionGate = null)
{
    private const string SupersededBeforeExecution =
        "Superseded by a newer run before provider execution started.";

    private readonly AgentSessionService _sessionService = sessionService;
    private readonly AgentActiveRunRegistry _activeRunRegistry = activeRunRegistry;
    private readonly AgentRunEventLogger _runEventLogger = runEventLogger;
    private readonly AgentSessionTitleService? _sessionTitleService = sessionTitleService;
    private readonly AgentSessionTransitionGate _transitionGate =
        transitionGate ?? AgentSessionTransitionGate.Shared;

    internal Task<AgentRunStartResult> StartAsync(
        AgentRunPlan plan,
        Guid userTurnId,
        CancellationToken cancellationToken)
    {
        var userTurn = _sessionService.GetTurn(userTurnId)
            ?? throw new InvalidOperationException(
                $"Admitted user turn '{userTurnId}' is no longer available.");
        return StartAdmittedAsync(plan, userTurn, cancellationToken);
    }

    internal async Task<AgentRunStartResult> StartAdmittedAsync(
        AgentRunPlan plan,
        AgentTurnRecord userTurn,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsCurrent(plan))
        {
            return Interrupted(plan, SupersededBeforeExecution);
        }

        AgentRunTransitionResult? transition;
        var runningSummary = BuildRunningSummary(plan);
        using (await _transitionGate
                   .EnterAsync(plan.RunKey.SessionId, cancellationToken)
                   .ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            transition = IsCurrent(plan)
                ? _sessionService.TryBeginAdmittedRunExecution(
                    plan.RunHandle.DurableLease!,
                    runningSummary)
                : null;
        }

        if (transition is null)
        {
            return Interrupted(plan, SupersededBeforeExecution);
        }

        if (plan.ShouldGenerateSessionTitle)
        {
            _sessionTitleService?.ScheduleTitleFromFirstUserMessage(
                plan.Session,
                plan.Profile,
                plan.ProviderSelection,
                plan.UserMessage,
                plan.RunKey.RunId,
                plan.RunKey.RunRevision);
        }

        _runEventLogger.LogRunEvent(
            PackageLogLevel.Debug,
            plan.RunKey.SessionId,
            plan.RunKey.RunId,
            plan.RunKey.RunRevision,
            "run.running_checkpoint.saved",
            runningSummary,
            ElapsedMilliseconds(plan));
        return new AgentRunStarted(
            plan,
            userTurn,
            transition.Checkpoint,
            plan.RunHandle.CancellationTokenSource.Token,
            InterruptedCheckpoint: null);
    }

    private AgentRunStartInterrupted Interrupted(AgentRunPlan plan, string summary)
    {
        var checkpoint = GetStoppedOrInterruptedCheckpoint(plan)
            ?? _sessionService.TryTransitionRun(
                plan.RunHandle.DurableLease!,
                AgentRunStatus.Interrupted,
                summary)?.Checkpoint
            ?? _sessionService.GetLatestCheckpoint(
                plan.RunKey.SessionId,
                plan.RunKey.RunRevision)
            ?? throw new InvalidOperationException("Interrupted run has no durable checkpoint.");
        return new AgentRunStartInterrupted(checkpoint);
    }

    private AgentRunCheckpointRecord? GetStoppedOrInterruptedCheckpoint(AgentRunPlan plan)
    {
        var latest = _sessionService.GetLatestCheckpoint(
            plan.RunKey.SessionId,
            plan.RunKey.RunRevision);
        return latest is not null
               && latest.Status is AgentRunStatus.Stopped or AgentRunStatus.Interrupted
            ? latest
            : null;
    }

    private bool IsCurrent(AgentRunPlan plan)
        => _activeRunRegistry.IsCurrent(
            plan.RunKey.SessionId,
            plan.RunKey.RunId,
            plan.RunKey.RunRevision);

    private static string BuildRunningSummary(AgentRunPlan plan)
    {
        var profileHasCapabilityAssignments =
            (plan.Profile.SelectableCapabilityAssignments?.Count ?? 0) > 0;
        return plan.RunCapabilities.SupportsNativeToolCalling
            ? "User message queued. Provider execution is starting."
            : profileHasCapabilityAssignments
                ? $"User message queued. Provider execution is starting in text-only mode. {plan.RunCapabilities.Summary}"
                : "User message queued. Provider execution is starting.";
    }

    private static long ElapsedMilliseconds(AgentRunPlan plan)
        => Math.Max(0, (long)(DateTimeOffset.UtcNow - plan.StartedAtUtc).TotalMilliseconds);
}
