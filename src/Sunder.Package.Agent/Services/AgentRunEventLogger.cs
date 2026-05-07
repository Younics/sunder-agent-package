using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Services.BehaviorLoops;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Logging;

namespace Sunder.Package.Agent.Services;

public sealed class AgentRunEventLogger(IPackageContext packageContext)
{
    private readonly IPackageEventLogger _eventLogger = packageContext.Logging.Events;

    public IPackageEventLogger EventLogger => _eventLogger;

    public void LogRunCompletion(
        Guid sessionId,
        Guid runId,
        long runRevision,
        AgentBehaviorLoopResult loopResult,
        long elapsedMilliseconds)
    {
        var eventName = loopResult.Checkpoint.Status switch
        {
            AgentRunStatus.Failed => "run.failed",
            AgentRunStatus.Interrupted => "run.interrupted",
            AgentRunStatus.Stopped => "run.stopped",
            AgentRunStatus.WaitingForApproval => "run.waiting_for_approval",
            _ => "run.completed",
        };
        var level = loopResult.Checkpoint.Status == AgentRunStatus.Failed
            ? PackageLogLevel.Error
            : PackageLogLevel.Information;
        LogRunEvent(
            level,
            sessionId,
            runId,
            runRevision,
            eventName,
            loopResult.CompletionKind.ToString(),
            elapsedMilliseconds,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["run.status"] = loopResult.Checkpoint.Status,
                ["run.completion_kind"] = loopResult.CompletionKind,
            });
    }

    public void LogRunEvent(
        PackageLogLevel level,
        Guid sessionId,
        Guid runId,
        long runRevision,
        string eventName,
        string message,
        long? elapsedMilliseconds = null,
        IReadOnlyDictionary<string, object?>? attributes = null,
        Exception? exception = null)
    {
        var mergedAttributes = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["session.id"] = sessionId,
            ["run.id"] = runId,
            ["run.revision"] = runRevision,
        };
        if (elapsedMilliseconds is not null)
        {
            mergedAttributes["duration.ms"] = elapsedMilliseconds.Value;
        }

        if (attributes is not null)
        {
            foreach (var attribute in attributes)
            {
                mergedAttributes[attribute.Key] = attribute.Value;
            }
        }

        try
        {
            _eventLogger.WriteAsync(level, eventName, message, mergedAttributes, exception).GetAwaiter().GetResult();
        }
        catch
        {
            // Logging must never interrupt agent execution.
        }
    }
}
