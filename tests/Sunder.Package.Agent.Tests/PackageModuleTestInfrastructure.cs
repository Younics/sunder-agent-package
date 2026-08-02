using Avalonia.Controls;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Avalonia;
using Sunder.Sdk.Runtime;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Settings;

namespace Sunder.Package.Agent.Tests;

internal sealed class RecordingPackageContributionRegistry : ISunderRuntimeContributionRegistry, IAvaloniaPackageContributionRegistry
{
    private readonly List<string> _registrations = [];

    public IReadOnlyList<string> Registrations => _registrations;

    public void RegisterPackageView<TView>(PackageViewRegistration registration) where TView : Control
        => Record($"package-view:{registration.Id}", typeof(TView));

    public void RegisterSettingsView<TView>() where TView : Control
        => Record("settings-view", typeof(TView));

    public void RegisterBackgroundService<TService>() where TService : class, IPackageBackgroundService
        => Record("background-service", typeof(TService));

    public void RegisterSettingsSchema(PackageSettingsSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        _registrations.Add("settings-schema");
    }

    public void RegisterRpcProvider(string providerId, ISunderRpcServiceHandler handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentNullException.ThrowIfNull(handler);
        _registrations.Add($"rpc-provider:{providerId}");
    }

    public void RegisterRuntimeOperation<TRequest, TResponse>(
        PackageRuntimeOperation<TRequest, TResponse> operation,
        IPackageRuntimeOperationHandler<TRequest, TResponse> handler)
        where TRequest : class
        where TResponse : class
        => Record($"runtime-operation:{operation.OperationId}", handler.GetType());

    public void RegisterRuntimeStream<TRequest, TEvent>(
        PackageRuntimeStream<TRequest, TEvent> stream,
        IPackageRuntimeStreamHandler<TRequest, TEvent> handler)
        where TRequest : class
        where TEvent : class
        => Record($"runtime-stream:{stream.StreamId}", handler.GetType());

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
