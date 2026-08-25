using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Sunder.Sdk.Logging;

namespace Sunder.Package.Agent.TestSupport;

public sealed record RecordedPackageEvent(
    PackageLogLevel Level,
    string EventName,
    string Message,
    IReadOnlyDictionary<string, object?> Attributes,
    Exception? Exception);

public sealed class RecordingPackageEventLogger : IPackageEventLogger
{
    private readonly ConcurrentQueue<RecordedPackageEvent> _events = new();

    public IReadOnlyList<RecordedPackageEvent> Events => _events.ToArray();

    public ValueTask WriteAsync(
        PackageLogLevel level,
        string eventName,
        string message,
        IReadOnlyDictionary<string, object?>? attributes = null,
        Exception? exception = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _events.Enqueue(new RecordedPackageEvent(
            level,
            eventName,
            message,
            attributes is null
                ? new Dictionary<string, object?>()
                : new Dictionary<string, object?>(attributes, StringComparer.Ordinal),
            exception));
        return ValueTask.CompletedTask;
    }

    public string Describe()
        => string.Join(
            Environment.NewLine,
            Events.Select(entry => $"{entry.Level}:{entry.EventName}:{entry.Message}"));
}

public sealed class RecordingPackageLogging(RecordingPackageEventLogger? events = null) : IPackageLogging
{
    public ILoggerFactory LoggerFactory => NullPackageLogging.Instance.LoggerFactory;

    public RecordingPackageEventLogger RecordingEvents { get; } = events ?? new RecordingPackageEventLogger();

    public IPackageEventLogger Events => RecordingEvents;
}
