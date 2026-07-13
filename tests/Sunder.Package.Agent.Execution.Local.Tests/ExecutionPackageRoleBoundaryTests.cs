using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Execution.Docker;
using Sunder.Package.Agent.Execution.Local;
using Sunder.Package.Agent.Tests;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Avalonia;
using Sunder.Sdk.Configuration;
using Sunder.Sdk.Runtime;
using Xunit;
using LocalPackageModule = Sunder.Package.Agent.Execution.Local.PackageModule;
using DockerPackageModule = Sunder.Package.Agent.Execution.Docker.PackageModule;

namespace Sunder.Package.Agent.Execution.Local.Tests;

public sealed class ExecutionPackageRoleBoundaryTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AppModules_RegisterOnlyPresentationServicesAndWorkspaceEditorProxy(bool local)
    {
        using var scope = RegressionTestPackageScope.Create();
        var services = new ServiceCollection();
        services.AddSingleton(scope.Context);
        services.AddSingleton<IPackageRuntimeClient>(NullPackageRuntimeClient.Instance);
        services.AddSingleton<IBackgroundProcessQueue, NoopBackgroundProcessQueue>();

        if (local)
        {
            new Sunder.Package.Agent.Execution.Local.AppPackageModule().ConfigureAppServices(services, scope.Context);
        }
        else
        {
            new Sunder.Package.Agent.Execution.Docker.AppPackageModule().ConfigureAppServices(services, scope.Context);
        }

        var serviceTypeNames = services.Select(descriptor => descriptor.ServiceType.Name).ToArray();
        Assert.DoesNotContain(nameof(LocalExecutionTarget), serviceTypeNames);
        Assert.DoesNotContain(nameof(LocalExecutionWorkspaceConfigService), serviceTypeNames);
        Assert.DoesNotContain(nameof(LocalShellCatalogService), serviceTypeNames);
        Assert.DoesNotContain(nameof(DockerExecutionTarget), serviceTypeNames);
        Assert.DoesNotContain(nameof(DockerExecutionWorkspaceConfigService), serviceTypeNames);
        Assert.DoesNotContain(nameof(DockerImageCatalogService), serviceTypeNames);
        Assert.Contains(serviceTypeNames, name => name.EndsWith("SettingsViewModel", StringComparison.Ordinal));
        Assert.Contains(serviceTypeNames, name => name.EndsWith("WorkspaceEditorPresentationContributor", StringComparison.Ordinal));

        using var provider = services.BuildServiceProvider();
        var registry = new RecordingAppRegistry();
        if (local)
        {
            new Sunder.Package.Agent.Execution.Local.AppPackageModule().RegisterAppContributions(registry, provider);
        }
        else
        {
            new Sunder.Package.Agent.Execution.Docker.AppPackageModule().RegisterAppContributions(registry, provider);
        }

        Assert.Single(registry.SettingsViews);
        var extension = Assert.Single(registry.Extensions);
        Assert.Equal(PackageExtensionPoints.WorkspaceEditorContributors.Id, extension.ExtensionPointId);
        Assert.EndsWith("WorkspaceEditorPresentationContributor", extension.ContributionType.Name, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, "local-execution.presentation.v1")]
    [InlineData(false, "docker-execution.presentation.v1")]
    public void RuntimeModules_OwnExecutionAndRegisterTypedPresentationOperation(bool local, string operationId)
    {
        using var scope = RegressionTestPackageScope.Create();
        var services = new ServiceCollection();
        services.AddSingleton(scope.Context);

        if (local)
        {
            new LocalPackageModule().ConfigureRuntimeServices(services, scope.Context);
        }
        else
        {
            new DockerPackageModule().ConfigureRuntimeServices(services, scope.Context);
        }

        var serviceTypeNames = services.Select(descriptor => descriptor.ServiceType.Name).ToArray();
        Assert.Contains(local ? nameof(LocalExecutionTarget) : nameof(DockerExecutionTarget), serviceTypeNames);
        Assert.DoesNotContain(serviceTypeNames, name => name.EndsWith("SettingsViewModel", StringComparison.Ordinal));
        Assert.DoesNotContain(serviceTypeNames, name => name.EndsWith("WorkspaceEditorPresentationContributor", StringComparison.Ordinal));

        using var provider = services.BuildServiceProvider();
        var registry = new RecordingRuntimeRegistry();
        if (local)
        {
            new LocalPackageModule().RegisterRuntimeContributions(registry, provider);
        }
        else
        {
            new DockerPackageModule().RegisterRuntimeContributions(registry, provider);
        }

        Assert.Contains(operationId, registry.OperationIds);
        Assert.Contains(PackageExtensionPoints.ExecutionTargets.Id, registry.ExtensionPointIds);
        Assert.Contains(PackageExtensionPoints.WorkspaceBindingContributors.Id, registry.ExtensionPointIds);
        Assert.Contains(PackageExtensionPoints.WorkspacePathMigrationContributors.Id, registry.ExtensionPointIds);
        Assert.Contains(PackageExtensionPoints.WorkspaceEditorContributors.Id, registry.ExtensionPointIds);
    }

    [Fact]
    public async Task LocalRuntimeOperation_RejectsUnboundedShellPath()
    {
        using var scope = RegressionTestPackageScope.Create();
        var catalog = new LocalShellCatalogService(scope.Context);
        var config = new LocalExecutionWorkspaceConfigService(scope.Context);
        var editor = new LocalExecutionWorkspaceEditorContributor(config, catalog);
        var handler = new LocalExecutionRuntimeOperationHandler(scope.Context, catalog, editor);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await handler.HandleAsync(new LocalExecutionOperationRequest(
                LocalExecutionOperationKind.SaveShells,
                Shells:
                [
                    new LocalShellDefinition("custom", "Custom", "relative/shell", "custom", false),
                ])));

        Assert.Contains("absolute paths", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DockerRuntimeOperation_RejectsUnselectedCliPath()
    {
        using var scope = RegressionTestPackageScope.Create();
        var runner = new DockerCliRunner(scope.Context);
        var catalog = new DockerImageCatalogService(scope.Context, runner);
        var config = new DockerExecutionWorkspaceConfigService(scope.Context, catalog);
        var editor = new DockerExecutionWorkspaceEditorContributor(config, catalog);
        var handler = new DockerExecutionRuntimeOperationHandler(scope.Context, runner, catalog, editor);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await handler.HandleAsync(new DockerExecutionOperationRequest(
                DockerExecutionOperationKind.SaveSettings,
                TimeoutSeconds: "300",
                DockerCliPath: "relative/docker")));

        Assert.Contains("absolute executable path", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DockerSettingsPresentation_QueuesTypedRuntimePull()
    {
        var runtimeClient = new RecordingDockerRuntimeClient();
        var queue = new RecordingBackgroundProcessQueue();
        using var viewModel = new DockerExecutionSettingsViewModel(
            new DockerExecutionAppRuntimeClient(runtimeClient),
            queue);
        await viewModel.InitializeAsync();
        viewModel.SelectedImage = Assert.Single(viewModel.Images);

        viewModel.PullSelectedImageCommand.Execute(null);

        var request = Assert.Single(queue.Requests);
        Assert.Equal(DockerExecutionSettingsViewModel.ImagePullGroupKey, request.GroupKey);
        Assert.Equal(BackgroundProcessIndicator.Settings, request.Indicator);
        Assert.Equal(BackgroundProcessConcurrencyMode.SequentialWithinGroup, request.ConcurrencyMode);
        Assert.Equal("agent0ai/agent-zero:latest", request.Metadata?[DockerExecutionSettingsViewModel.ImageReferenceMetadataKey]);
        Assert.Equal(DockerImageStatus.Pulling, viewModel.SelectedImage?.Status);
        Assert.False(viewModel.PullSelectedImageCommand.CanExecute(null));
    }

    private sealed class RecordingAppRegistry : IAvaloniaPackageContributionRegistry
    {
        public List<Type> SettingsViews { get; } = [];
        public List<(string ExtensionPointId, Type ContributionType)> Extensions { get; } = [];

        public void RegisterPackageView<TView>(PackageViewRegistration registration) where TView : Control { }
        public void RegisterPackageViewFactory<TFactory>(PackageViewRegistration registration) where TFactory : class, IPackageWorkspaceFactory { }
        public void RegisterSettingsView<TView>() where TView : Control => SettingsViews.Add(typeof(TView));
        public void RegisterSettingsViewFactory<TFactory>() where TFactory : class, IPackageWorkspaceFactory { }
        public void RegisterExtension<TContract>(PackageExtensionPoint<TContract> extensionPoint, TContract contribution)
            => Extensions.Add((extensionPoint.Id, contribution!.GetType()));
    }

    private sealed class RecordingRuntimeRegistry : ISunderRuntimeContributionRegistry
    {
        public List<string> ExtensionPointIds { get; } = [];
        public List<string> OperationIds { get; } = [];

        public void RegisterBackgroundService<TService>() where TService : class, IPackageBackgroundService { }
        public void RegisterExtension<TContract>(PackageExtensionPoint<TContract> extensionPoint, TContract contribution)
            => ExtensionPointIds.Add(extensionPoint.Id);
        public void RegisterConfigurationSchema(PackageConfigurationSchema schema) { }
        public void RegisterRuntimeOperation<TRequest, TResponse>(
            PackageRuntimeOperation<TRequest, TResponse> operation,
            IPackageRuntimeOperationHandler<TRequest, TResponse> handler)
            where TRequest : class where TResponse : class
            => OperationIds.Add(operation.OperationId);
        public void RegisterRuntimeStream<TRequest, TEvent>(
            PackageRuntimeStream<TRequest, TEvent> stream,
            IPackageRuntimeStreamHandler<TRequest, TEvent> handler)
            where TRequest : class where TEvent : class { }
    }

    private sealed class NoopBackgroundProcessQueue : IBackgroundProcessQueue
    {
        public event EventHandler<BackgroundProcessChangedEventArgs>? ProcessChanged { add { } remove { } }
        public BackgroundProcessSnapshot Enqueue(BackgroundProcessRequest request) => throw new NotSupportedException();
        public IReadOnlyList<BackgroundProcessSnapshot> ListProcesses(string? groupKey = null) => [];
        public bool Cancel(Guid processId) => false;
    }

    private sealed class RecordingDockerRuntimeClient : IPackageRuntimeClient
    {
        public bool IsAvailable => true;

        public ValueTask<TResponse> InvokeAsync<TRequest, TResponse>(
            PackageRuntimeOperation<TRequest, TResponse> operation,
            TRequest request,
            CancellationToken cancellationToken = default)
            where TRequest : class
            where TResponse : class
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(DockerExecutionRuntimeOperations.Execute.OperationId, operation.OperationId);
            var response = new DockerExecutionOperationResponse(
                "300",
                string.Empty,
                [new DockerImageDefinition("agent0ai/agent-zero:latest", DockerImageStatus.NotPulled, null, null)]);
            return ValueTask.FromResult((TResponse)(object)response);
        }

        public async IAsyncEnumerable<TEvent> SubscribeAsync<TRequest, TEvent>(
            PackageRuntimeStream<TRequest, TEvent> stream,
            TRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
            where TRequest : class
            where TEvent : class
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class RecordingBackgroundProcessQueue : IBackgroundProcessQueue
    {
        private readonly List<BackgroundProcessSnapshot> _snapshots = [];

        public List<BackgroundProcessRequest> Requests { get; } = [];
        public event EventHandler<BackgroundProcessChangedEventArgs>? ProcessChanged { add { } remove { } }

        public BackgroundProcessSnapshot Enqueue(BackgroundProcessRequest request)
        {
            Requests.Add(request);
            var snapshot = new BackgroundProcessSnapshot(
                Guid.NewGuid(), request.Title, request.GroupKey, request.Indicator, request.ConcurrencyMode,
                BackgroundProcessState.Queued, string.Empty, null, request.CanCancel,
                request.Metadata ?? new Dictionary<string, string>(), null, DateTimeOffset.UtcNow, null, null);
            _snapshots.Add(snapshot);
            return snapshot;
        }

        public IReadOnlyList<BackgroundProcessSnapshot> ListProcesses(string? groupKey = null)
            => _snapshots.Where(snapshot => groupKey is null || string.Equals(snapshot.GroupKey, groupKey, StringComparison.Ordinal)).ToArray();

        public bool Cancel(Guid processId) => false;
    }
}
