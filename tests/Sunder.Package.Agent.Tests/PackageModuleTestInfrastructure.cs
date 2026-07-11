using Avalonia.Controls;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Avalonia;
using Sunder.Sdk.Configuration;

namespace Sunder.Package.Agent.Tests;

internal sealed class RecordingPackageContributionRegistry : ISunderRuntimeContributionRegistry, IAvaloniaPackageContributionRegistry
{
    private readonly List<string> _registrations = [];
    private readonly List<object> _activatedContributions = [];

    public IReadOnlyList<string> Registrations => _registrations;

    public IReadOnlyList<object> ActivatedContributions => _activatedContributions;

    public void RegisterPackageView<TView>(PackageViewRegistration registration) where TView : Control
        => Record($"package-view:{registration.Id}", typeof(TView));

    public void RegisterPackageViewFactory<TFactory>(PackageViewRegistration registration)
        where TFactory : class, IPackageWorkspaceFactory
        => Record($"package-view-factory:{registration.Id}", typeof(TFactory));

    public void RegisterSettingsView<TView>() where TView : Control
        => Record("settings-view", typeof(TView));

    public void RegisterSettingsViewFactory<TFactory>() where TFactory : class, IPackageWorkspaceFactory
        => Record("settings-view-factory", typeof(TFactory));

    public void RegisterBackgroundService<TService>() where TService : class, IPackageBackgroundService
        => Record("background-service", typeof(TService));

    public void RegisterExtension<TContract>(PackageExtensionPoint<TContract> extensionPoint, TContract contribution)
    {
        ArgumentNullException.ThrowIfNull(contribution);
        var registration = $"extension:{extensionPoint.Id}:{contribution.GetType().FullName}";
        if (!_registrations.Contains(registration, StringComparer.Ordinal))
        {
            _registrations.Add(registration);
            _activatedContributions.Add(contribution);
        }
    }

    public void RegisterConfigurationSchema(PackageConfigurationSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        _registrations.Add($"configuration:{schema.PackageId}");
    }

    private void Record(string kind, Type implementationType)
        => _registrations.Add($"{kind}:{implementationType.FullName}");
}

internal sealed class CompositionBackgroundProcessQueue : IBackgroundProcessQueue
{
    public event EventHandler<BackgroundProcessChangedEventArgs>? ProcessChanged
    {
        add { }
        remove { }
    }

    public BackgroundProcessSnapshot Enqueue(BackgroundProcessRequest request)
        => new(
            Guid.NewGuid(),
            request.Title,
            request.GroupKey,
            request.Indicator,
            request.ConcurrencyMode,
            BackgroundProcessState.Queued,
            string.Empty,
            ProgressPercent: null,
            request.CanCancel,
            request.Metadata ?? new Dictionary<string, string>(),
            ErrorMessage: null,
            DateTimeOffset.UtcNow,
            StartedAtUtc: null,
            CompletedAtUtc: null);

    public IReadOnlyList<BackgroundProcessSnapshot> ListProcesses(string? groupKey = null) => [];

    public bool Cancel(Guid processId) => false;
}
