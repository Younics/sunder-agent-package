using Sunder.Sdk.Logging;

namespace Sunder.Package.Agent.Services.BehaviorLoops;

internal sealed partial class AgentBehaviorLoopHost
{
    private IReadOnlyDictionary<string, object?> BuildProviderCorrelationAttributes(
        IReadOnlyDictionary<string, object?>? existingAttributes)
    {
        var attributes = existingAttributes is null
            ? new Dictionary<string, object?>(StringComparer.Ordinal)
            : new Dictionary<string, object?>(existingAttributes, StringComparer.Ordinal);
        attributes["session.id"] = _session.SessionId;
        attributes["run.id"] = _runId;
        attributes["run.revision"] = _runRevision;
        attributes["profile.id"] = _profile.ProfileId;
        return attributes;
    }

    public void LogEvent(
        PackageLogLevel level,
        string eventName,
        string message,
        long? elapsedMilliseconds = null,
        IReadOnlyDictionary<string, object?>? attributes = null,
        Exception? exception = null)
    {
        var mergedAttributes = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["session.id"] = _session.SessionId,
            ["run.id"] = _runId,
            ["run.revision"] = _runRevision,
            ["profile.id"] = _profile.ProfileId,
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

        _ = WriteEventSafelyAsync(level, eventName, message, mergedAttributes, exception);
    }

    private async Task WriteEventSafelyAsync(
        PackageLogLevel level,
        string eventName,
        string message,
        IReadOnlyDictionary<string, object?> attributes,
        Exception? exception)
    {
        try
        {
            await _eventLogger.WriteAsync(level, eventName, message, attributes, exception);
        }
        catch
        {
            // Logging must never interrupt agent execution.
        }
    }
}
