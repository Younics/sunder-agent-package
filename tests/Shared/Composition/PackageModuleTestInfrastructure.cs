using Avalonia.Controls;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Avalonia;

namespace Sunder.Package.Agent.Tests;

internal sealed class RecordingPackageContributionRegistry : IAvaloniaPackageContributionRegistry
{
    private readonly List<string> _registrations = [];

    public IReadOnlyList<string> Registrations => _registrations;

    public void RegisterPackageView<TView>(PackageViewRegistration registration) where TView : Control
        => Record($"package-view:{registration.Id}", typeof(TView));

    public void RegisterSettingsView<TView>() where TView : Control
        => Record("settings-view", typeof(TView));

    private void Record(string kind, Type implementationType)
        => _registrations.Add($"{kind}:{implementationType.FullName}");
}

internal sealed class CompositionDevelopmentHost : IPackageDevelopmentService, IPackageLogNavigationService
{
    public Task<IPackageDevelopmentSession> LoadAsync(string developmentFolder, bool watch, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("Composition must not load a development package.");
    public Task OpenPackageLogsAsync(string packageId, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("Composition must not navigate to package logs.");
}

internal sealed class CompositionBackgroundTaskQueue : IBackgroundTaskQueue
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
