using Sunder.Sdk.Logging;

namespace Sunder.Package.Agent.Contracts.Models;

public sealed record AgentChatClientContext(
    string ProviderId,
    string ModelId,
    IPackageEventLogger? EventLogger = null,
    IReadOnlyDictionary<string, object?>? CorrelationAttributes = null);

public static class AgentChatClientContextLogExtensions
{
    public static async ValueTask LogProviderEventAsync(
        this AgentChatClientContext context,
        PackageLogLevel level,
        string eventName,
        string message,
        long? elapsedMilliseconds = null,
        IReadOnlyDictionary<string, object?>? attributes = null,
        Exception? exception = null,
        CancellationToken cancellationToken = default)
    {
        if (context.EventLogger is null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return;
        }

        var mergedAttributes = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["event.name"] = eventName,
            ["provider.id"] = context.ProviderId,
            ["model.id"] = context.ModelId,
        };
        if (context.CorrelationAttributes is not null)
        {
            foreach (var attribute in context.CorrelationAttributes)
            {
                mergedAttributes[attribute.Key] = attribute.Value;
            }
        }

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
            await context.EventLogger.WriteAsync(level, eventName, message, mergedAttributes, exception, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return;
        }
    }
}
