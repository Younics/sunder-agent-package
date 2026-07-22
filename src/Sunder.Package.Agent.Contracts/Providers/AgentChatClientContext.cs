using Sunder.Sdk.Logging;

namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Carries the selected provider/model identity and run-scoped diagnostics into chat client creation.
/// </summary>
/// <remarks>
/// The logger and attribute dictionary are borrowed for client use and remain owned by the host.
/// Correlation attributes are diagnostic metadata and must not contain prompts, credentials, tokens,
/// authorization headers, or other secret values.
/// </remarks>
/// <param name="ProviderId">The selected provider identifier, expected to match the provider descriptor.</param>
/// <param name="ModelId">The selected provider-resolvable model identifier.</param>
/// <param name="EventLogger">An optional host-owned sink for structured provider events.</param>
/// <param name="CorrelationAttributes">Optional host-provided attributes copied into each provider event.</param>
public sealed record AgentChatClientContext(
    string ProviderId,
    string ModelId,
    IPackageEventLogger? EventLogger = null,
    IReadOnlyDictionary<string, object?>? CorrelationAttributes = null);

/// <summary>
/// Provides failure-isolated structured logging for provider clients.
/// </summary>
public static class AgentChatClientContextLogExtensions
{
    /// <summary>Writes a provider event with standard identity, timing, correlation, and per-call attributes.</summary>
    /// <remarks>
    /// Correlation attributes are applied after the standard event, provider, and model fields;
    /// per-call attributes are applied last and can replace earlier keys. Implementations should
    /// therefore avoid reserved keys such as <c>event.name</c>, <c>provider.id</c>,
    /// <c>model.id</c>, and <c>duration.ms</c>. Logger failures are isolated from provider behavior,
    /// but cancellation is propagated. All messages, attributes, and exceptions must be safe to log.
    /// </remarks>
    /// <param name="context">The provider client context that supplies identity and the optional logger.</param>
    /// <param name="level">The event severity.</param>
    /// <param name="eventName">A stable event identifier used both as the logger event name and <c>event.name</c>.</param>
    /// <param name="message">A concise human-readable message with no secret content.</param>
    /// <param name="elapsedMilliseconds">Optional elapsed time written as <c>duration.ms</c>.</param>
    /// <param name="attributes">Optional event-specific attributes merged after correlation and timing fields.</param>
    /// <param name="exception">An optional already-redacted exception associated with the event.</param>
    /// <param name="cancellationToken">A token that cancels the logging operation.</param>
    /// <returns>A value task that completes when logging finishes or immediately when no logger is configured.</returns>
    /// <exception cref="OperationCanceledException">The cancellation token was canceled before or during logging.</exception>
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
