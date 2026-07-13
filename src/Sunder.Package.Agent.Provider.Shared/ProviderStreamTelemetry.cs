using System.Diagnostics;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Logging;

namespace Sunder.Package.Agent.Provider.Shared;

internal enum ProviderStreamFailureKind
{
    CallerCancellation,
    ProviderCancellation,
    Failure,
}

internal static class ProviderStreamFailureClassifier
{
    public static ProviderStreamFailureKind Classify(
        Exception exception,
        CancellationToken cancellationToken,
        bool inspectInnerExceptions = false,
        bool includeTimeouts = false)
    {
        var containsCancellation = exception is OperationCanceledException
                                   || includeTimeouts && exception is TimeoutException
                                   || inspectInnerExceptions && exception.InnerException is not null
                                   && Classify(
                                       exception.InnerException,
                                       cancellationToken,
                                       inspectInnerExceptions,
                                       includeTimeouts)
                                       != ProviderStreamFailureKind.Failure;
        if (!containsCancellation)
        {
            return ProviderStreamFailureKind.Failure;
        }

        return cancellationToken.IsCancellationRequested
            ? ProviderStreamFailureKind.CallerCancellation
            : ProviderStreamFailureKind.ProviderCancellation;
    }
}

internal sealed class ProviderStreamTelemetry(AgentChatClientContext context)
{
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private bool _firstEventRecorded;

    public long ElapsedMilliseconds => _stopwatch.ElapsedMilliseconds;

    public async ValueTask RequestStartedAsync(
        string modelId,
        int messageCount,
        int toolCount,
        int systemPromptLength,
        CancellationToken cancellationToken)
    {
        _firstEventRecorded = false;
        var requestAttributes = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["model.id"] = modelId,
            ["prompt.turn_count"] = messageCount,
            ["tool.available_count"] = toolCount,
            ["system_prompt.length"] = systemPromptLength,
        };
        await WriteAsync(
            PackageLogLevel.Debug,
            "provider.request.start",
            "Provider request started.",
            attributes: requestAttributes,
            cancellationToken: cancellationToken);
        await WriteAsync(
            PackageLogLevel.Debug,
            "provider.stream.start",
            "Provider stream started.",
            attributes: new Dictionary<string, object?>(requestAttributes, StringComparer.Ordinal)
            {
                ["provider.id"] = context.ProviderId,
                ["tool.count"] = toolCount,
                ["message.count"] = messageCount,
            },
            cancellationToken: cancellationToken);
        _stopwatch.Restart();
    }

    public async ValueTask RecordFirstEventAsync(
        string eventKind,
        CancellationToken cancellationToken)
    {
        if (_firstEventRecorded)
        {
            return;
        }

        _firstEventRecorded = true;
        await WriteAsync(
            PackageLogLevel.Debug,
            "provider.stream.first_event",
            eventKind,
            ElapsedMilliseconds,
            cancellationToken: cancellationToken);
    }

    public ValueTask CompletedAsync(string emptyMessage, CancellationToken cancellationToken)
        => WriteAsync(
            PackageLogLevel.Debug,
            "provider.stream.completed",
            _firstEventRecorded ? null : emptyMessage,
            ElapsedMilliseconds,
            cancellationToken: cancellationToken);

    public ValueTask CanceledAsync()
        => WriteAsync(
            PackageLogLevel.Warning,
            "provider.stream.canceled",
            "Provider stream was canceled.",
            ElapsedMilliseconds,
            cancellationToken: CancellationToken.None);

    public ValueTask FailedAsync(Exception exception)
        => WriteAsync(
            PackageLogLevel.Error,
            "provider.stream.failed",
            exception.Message,
            ElapsedMilliseconds,
            exception: exception,
            cancellationToken: CancellationToken.None);

    public ValueTask UnsupportedResponseAsync(
        string providerName,
        IReadOnlyList<string> contentKinds,
        CancellationToken cancellationToken)
        => WriteAsync(
            PackageLogLevel.Warning,
            "provider.response.unsupported_content",
            $"{providerName} returned unsupported response content: {string.Join(", ", contentKinds)}.",
            ElapsedMilliseconds,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["content.kinds"] = string.Join(",", contentKinds),
                ["content.count"] = contentKinds.Count,
            },
            cancellationToken: cancellationToken);

    private ValueTask WriteAsync(
        PackageLogLevel level,
        string eventName,
        string? message = null,
        long? elapsedMilliseconds = null,
        IReadOnlyDictionary<string, object?>? attributes = null,
        Exception? exception = null,
        CancellationToken cancellationToken = default)
        => context.LogProviderEventAsync(
            level,
            eventName,
            message ?? eventName,
            elapsedMilliseconds,
            attributes,
            exception,
            cancellationToken);
}
