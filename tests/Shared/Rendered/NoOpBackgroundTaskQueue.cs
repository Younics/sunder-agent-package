using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Presentation.Tests;

internal sealed class NoOpBackgroundTaskQueue : IBackgroundTaskQueue
{
    public event EventHandler<BackgroundTaskChangedEventArgs>? TaskChanged
    {
        add { }
        remove { }
    }

    public BackgroundTaskSnapshot Enqueue(BackgroundTaskRequest request)
        => new(
            Guid.NewGuid(),
            request.Title,
            request.GroupKey,
            request.Indicator,
            request.ConcurrencyMode,
            BackgroundTaskState.Queued,
            string.Empty,
            ProgressPercent: null,
            request.CanCancel,
            request.Metadata ?? new Dictionary<string, string>(),
            ErrorMessage: null,
            DateTimeOffset.UtcNow,
            StartedAtUtc: null,
            CompletedAtUtc: null);

    public IReadOnlyList<BackgroundTaskSnapshot> ListTasks(string? groupKey = null) => [];

    public bool Cancel(Guid processId) => false;
}
