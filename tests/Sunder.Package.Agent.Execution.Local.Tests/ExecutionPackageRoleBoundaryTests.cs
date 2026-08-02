using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Execution.Docker;
using Sunder.Package.Agent.Execution.Local;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.Tests;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Avalonia;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Settings;
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
    public void AppModules_RegisterOnlyPresentationServices(bool local)
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
        var providerPrefix = local ? "local" : "docker";
        Assert.Contains($"{providerPrefix}.execution.target", registry.RpcProviderIds);
        Assert.Contains($"{providerPrefix}.workspace.path.migrator", registry.RpcProviderIds);
        Assert.Contains($"{providerPrefix}.workspace.editor", registry.RpcProviderIds);
    }

    [Fact]
    public async Task LocalRuntimeOperation_RejectsUnboundedShellPath()
    {
        using var scope = RegressionTestPackageScope.Create();
        var catalog = new LocalShellCatalogService(scope.Context);
        var config = new LocalExecutionWorkspaceConfigService(scope.Context);
        var editor = new LocalExecutionWorkspaceEditorContributor(config, catalog);
        var handler = new LocalExecutionRuntimeOperationHandler(scope.Context, catalog, editor);

        var response = await handler.HandleAsync(new LocalExecutionOperationRequest(
                LocalExecutionOperationKind.SaveShells,
                Shells:
                [
                    new LocalShellDefinition("custom", "Custom", "relative/shell", "custom", false),
                ],
                ExpectedShellCatalogRevision: 0));

        Assert.False(response.Success);
        Assert.Equal("local.shell.path-invalid", response.Error?.Code);
        Assert.Contains("absolute paths", response.Error?.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LocalExecutionTarget_RejectsStaleWorkspaceConfigurationGeneration()
    {
        using var scope = RegressionTestPackageScope.Create();
        var configService = new LocalExecutionWorkspaceConfigService(scope.Context);
        using var target = new LocalExecutionTarget(
            scope.Context,
            configService,
            new LocalShellCatalogService(scope.Context));
        var workspace = new AgentWorkspaceRecord(
            "workspace",
            "Workspace",
            null,
            default,
            default,
            [new AgentWorkspacePathRecord("path", "workspace", scope.RootPath, true, 0, default, default)]);
        var binding = new AgentWorkspaceBindingRecord(
            "binding",
            workspace.WorkspaceId,
            AgentRpcContractIds.ExecutionTarget,
            "local",
            "primary-execution-target",
            true,
            0,
            default,
            default);
        var context = new AgentExecutionTargetContext(null, null, workspace, binding);
        await configService.SaveConfigAsync(
            binding.BindingId,
            new LocalExecutionWorkspaceConfig("sh", [Path.Combine(scope.RootPath, "first-bin")]));
        var generation = await target.GetConfigurationGenerationAsync(context);

        await configService.SaveConfigAsync(
            binding.BindingId,
            new LocalExecutionWorkspaceConfig("sh", [Path.Combine(scope.RootPath, "second-bin")]));

        var staleContext = context with { ExpectedConfigurationGeneration = generation };
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await target.GetExecutionScopeAsync(staleContext));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await target.AddPathEntryAsync(staleContext, Path.Combine(scope.RootPath, "rejected-bin")));
        Assert.DoesNotContain(
            Path.Combine(scope.RootPath, "rejected-bin"),
            (await configService.GetConfigAsync(binding.BindingId)).PathEntries ?? []);
    }

    [Fact]
    public async Task LocalExecutionTarget_RejectsStaleCustomShellDefinitionGeneration()
    {
        using var scope = RegressionTestPackageScope.Create();
        var firstShell = Path.Combine(scope.RootPath, "first-shell");
        var secondShell = Path.Combine(scope.RootPath, "second-shell");
        File.WriteAllText(firstShell, string.Empty);
        File.WriteAllText(secondShell, string.Empty);
        var shellCatalog = new LocalShellCatalogService(scope.Context);
        await shellCatalog.SaveCustomShellsAsync(
            [new LocalShellDefinition("custom", "Custom", firstShell, AgentShellSyntaxKinds.PosixSh, false)],
            expectedRevision: 0);
        var configService = new LocalExecutionWorkspaceConfigService(scope.Context);
        using var target = new LocalExecutionTarget(scope.Context, configService, shellCatalog);
        var workspace = new AgentWorkspaceRecord("workspace", "Workspace", null, default, default);
        var binding = new AgentWorkspaceBindingRecord(
            "binding",
            workspace.WorkspaceId,
            AgentRpcContractIds.ExecutionTarget,
            "local",
            "primary-execution-target",
            true,
            0,
            default,
            default);
        var context = new AgentExecutionTargetContext(null, null, workspace, binding);
        await configService.SaveConfigAsync(
            binding.BindingId,
            new LocalExecutionWorkspaceConfig("custom", []));
        var generation = await target.GetConfigurationGenerationAsync(context);

        await shellCatalog.SaveCustomShellsAsync(
            [new LocalShellDefinition("custom", "Custom", secondShell, AgentShellSyntaxKinds.PosixSh, false)],
            expectedRevision: 1);

        var staleContext = context with { ExpectedConfigurationGeneration = generation };
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await target.GetShellAsync(staleContext));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await target.AddPathEntryAsync(staleContext, "/rejected"));
        Assert.DoesNotContain(
            "/rejected",
            (await configService.GetConfigAsync(binding.BindingId)).PathEntries ?? []);
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

        var response = await handler.HandleAsync(new DockerExecutionOperationRequest(
                DockerExecutionOperationKind.SaveSettings,
                TimeoutSeconds: "300",
                DockerCliPath: "relative/docker"));

        Assert.False(response.Success);
        Assert.Equal("docker.cli-path.invalid", response.Error?.Code);
        Assert.Contains("absolute executable path", response.Error?.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DockerExecutionTarget_RejectsStaleWorkspaceConfigurationGeneration()
    {
        using var scope = RegressionTestPackageScope.Create();
        var runner = new DockerCliRunner(scope.Context);
        var catalog = new DockerImageCatalogService(scope.Context, runner);
        var configService = new DockerExecutionWorkspaceConfigService(scope.Context, catalog);
        using var lifecycle = new DockerContainerLifecycleService();
        var target = new DockerExecutionTarget(scope.Context, configService, lifecycle, catalog, runner);
        var workspace = new AgentWorkspaceRecord(
            "workspace",
            "Workspace",
            null,
            default,
            default,
            [new AgentWorkspacePathRecord("path", "workspace", scope.RootPath, true, 0, default, default)]);
        var binding = new AgentWorkspaceBindingRecord(
            "binding",
            workspace.WorkspaceId,
            AgentRpcContractIds.ExecutionTarget,
            "docker",
            "primary-execution-target",
            true,
            0,
            default,
            default);
        var context = new AgentExecutionTargetContext(null, null, workspace, binding);
        await configService.SaveConfigAsync(
            binding.BindingId,
            new DockerExecutionWorkspaceConfig("repository/image:latest", null, "/bin/sh", ["/first"]));
        var generation = await target.GetConfigurationGenerationAsync(context);

        await configService.SaveConfigAsync(
            binding.BindingId,
            new DockerExecutionWorkspaceConfig("repository/image:latest", null, "/bin/sh", ["/second"]));

        var staleContext = context with { ExpectedConfigurationGeneration = generation };
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await target.GetShellAsync(staleContext));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await target.AddPathEntryAsync(staleContext, "/rejected"));
        Assert.DoesNotContain(
            "/rejected",
            (await configService.GetConfigAsync(binding.BindingId)).PathEntries ?? []);
    }

    [Fact]
    public async Task DockerExecutionTarget_RejectsStaleCliPathGeneration()
    {
        using var scope = RegressionTestPackageScope.Create();
        var firstCli = Path.Combine(scope.RootPath, "first-docker");
        var secondCli = Path.Combine(scope.RootPath, "second-docker");
        File.WriteAllText(firstCli, string.Empty);
        File.WriteAllText(secondCli, string.Empty);
        var settings = new MutablePackageSettings();
        await settings.SetValueAsync(DockerCli.ExecutablePathConfigurationKey, firstCli);
        var context = new SettingsOverridePackageContext(scope.Context, settings);
        var runner = new DockerCliRunner(context);
        var catalog = new DockerImageCatalogService(context, runner);
        var configService = new DockerExecutionWorkspaceConfigService(context, catalog);
        using var lifecycle = new DockerContainerLifecycleService();
        var target = new DockerExecutionTarget(context, configService, lifecycle, catalog, runner);
        var workspace = new AgentWorkspaceRecord("workspace", "Workspace", null, default, default);
        var binding = new AgentWorkspaceBindingRecord(
            "binding",
            workspace.WorkspaceId,
            AgentRpcContractIds.ExecutionTarget,
            "docker",
            "primary-execution-target",
            true,
            0,
            default,
            default);
        var targetContext = new AgentExecutionTargetContext(null, null, workspace, binding);
        await configService.SaveConfigAsync(
            binding.BindingId,
            new DockerExecutionWorkspaceConfig("repository/image:latest", null));
        var generation = await target.GetConfigurationGenerationAsync(targetContext);

        var missingIdentity = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await target.GetExecutionScopeAsync(targetContext with
            {
                ExpectedConfigurationGeneration = generation,
            }));
        Assert.Contains("image identity is not available", missingIdentity.Message, StringComparison.OrdinalIgnoreCase);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await target.AddPathEntryAsync(
                targetContext with { ExpectedConfigurationGeneration = generation },
                "/rejected-before-readiness"));
        Assert.DoesNotContain(
            "/rejected-before-readiness",
            (await configService.GetConfigAsync(binding.BindingId)).PathEntries ?? []);

        await settings.SetValueAsync(DockerCli.ExecutablePathConfigurationKey, secondCli);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await target.GetShellAsync(targetContext with
            {
                ExpectedConfigurationGeneration = generation,
            }));
    }

    [Theory]
    [InlineData(null, "docker.image-reference.required")]
    [InlineData("repository/image", "docker.image-reference.unpinned")]
    [InlineData("repository/image@sha256:abc", "docker.image-reference.invalid-digest")]
    public async Task DockerRuntimeOperation_ReturnsTypedPinnedReferenceValidation(
        string? imageReference,
        string expectedCode)
    {
        using var scope = RegressionTestPackageScope.Create();
        var runner = new DockerCliRunner(scope.Context);
        var catalog = new DockerImageCatalogService(scope.Context, runner);
        var config = new DockerExecutionWorkspaceConfigService(scope.Context, catalog);
        var editor = new DockerExecutionWorkspaceEditorContributor(config, catalog);
        var handler = new DockerExecutionRuntimeOperationHandler(scope.Context, runner, catalog, editor);

        var response = await handler.HandleAsync(new DockerExecutionOperationRequest(
            DockerExecutionOperationKind.AddImage,
            ImageReference: imageReference));

        Assert.False(response.Success);
        Assert.Equal(expectedCode, response.Error?.Code);
        Assert.NotNull(response.Error?.CorrelationId);
        Assert.Null(await scope.Context.Storage.State.GetValueAsync(DockerImageCatalogService.ImagesKey));
    }

    [Fact]
    public async Task DockerRuntimeOperation_AcceptsExplicitLatestTag()
    {
        using var scope = RegressionTestPackageScope.Create();
        var runner = new DockerCliRunner(scope.Context);
        var catalog = new DockerImageCatalogService(scope.Context, runner);
        var config = new DockerExecutionWorkspaceConfigService(scope.Context, catalog);
        var editor = new DockerExecutionWorkspaceEditorContributor(config, catalog);
        var handler = new DockerExecutionRuntimeOperationHandler(scope.Context, runner, catalog, editor);

        var response = await handler.HandleAsync(new DockerExecutionOperationRequest(
            DockerExecutionOperationKind.AddImage,
            ImageReference: "repository/image:latest"));

        Assert.True(response.Success);
        var image = Assert.Single(response.Images!);
        Assert.Equal("repository/image:latest", image.ImageReference);
        Assert.Equal(DockerImageStatus.NotPulled, image.Status);
    }

    [Fact]
    public async Task DockerRuntimeOperation_ReturnsCachedSemanticCatalogFailureAfterBackgroundStart()
    {
        using var scope = RegressionTestPackageScope.Create();
        const string json = "{\"Version\":1,\"Images\":[],\"UnknownPolicy\":true}";
        await scope.Context.Storage.State.SetValueAsync(DockerImageCatalogService.ImagesKey, json);
        var migration = new DockerPackageStorageMigration(scope.Context);
        await migration.StartAsync();
        var runner = new DockerCliRunner(scope.Context);
        var catalog = new DockerImageCatalogService(scope.Context, runner, migration);
        var config = new DockerExecutionWorkspaceConfigService(scope.Context, catalog, migration);
        var editor = new DockerExecutionWorkspaceEditorContributor(config, catalog);
        var handler = new DockerExecutionRuntimeOperationHandler(scope.Context, runner, catalog, editor);

        var response = await handler.HandleAsync(new DockerExecutionOperationRequest(
            DockerExecutionOperationKind.GetSettings));

        Assert.False(response.Success);
        Assert.Equal("docker.catalog.unknown-data", response.Error?.Code);
        Assert.NotNull(response.Error?.CorrelationId);
        Assert.Equal(json, await scope.Context.Storage.State.GetValueAsync(DockerImageCatalogService.ImagesKey));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AppRuntimeProxy_WhenRuntimeIsUnavailable_ThrowsSanitizedSdkFailure(bool local)
    {
        PackageRuntimeInvocationException exception;
        if (local)
        {
            var client = new LocalExecutionAppRuntimeClient(NullPackageRuntimeClient.Instance);
            exception = await Assert.ThrowsAsync<PackageRuntimeInvocationException>(async () =>
                await client.InvokeAsync(new LocalExecutionOperationRequest(
                    LocalExecutionOperationKind.GetWorkspaceEditor)));
        }
        else
        {
            var client = new DockerExecutionAppRuntimeClient(NullPackageRuntimeClient.Instance);
            exception = await Assert.ThrowsAsync<PackageRuntimeInvocationException>(async () =>
                await client.InvokeAsync(new DockerExecutionOperationRequest(
                    DockerExecutionOperationKind.GetWorkspaceEditor)));
        }

        Assert.Equal("runtime.v1.unavailable", exception.Code);
        Assert.True(exception.IsTransient);
        Assert.Equal(503, exception.StatusCode);
        Assert.Null(exception.InnerException);
    }

    [Fact]
    public async Task DockerTypedRuntimeFailure_DoesNotEscapePreparationOrRemoveSettingsContribution()
    {
        using var scope = RegressionTestPackageScope.Create();
        var services = new ServiceCollection();
        services.AddSingleton(scope.Context);
        services.AddSingleton<IPackageRuntimeClient>(new FailingDockerRuntimeClient());
        services.AddSingleton<IBackgroundProcessQueue, NoopBackgroundProcessQueue>();
        var module = new Sunder.Package.Agent.Execution.Docker.AppPackageModule();
        module.ConfigureAppServices(services, scope.Context);
        using var provider = services.BuildServiceProvider();
        var registry = new RecordingAppRegistry();
        module.RegisterAppContributions(registry, provider);
        using var viewModel = provider.GetRequiredService<DockerExecutionSettingsViewModel>();

        var prepared = await viewModel.PrepareNavigationAsync(new PackageViewNavigationContext(
            "settings:sunder.package.agent.execution.docker",
            new Dictionary<string, string?>()));

        Assert.True(prepared);
        Assert.True(viewModel.IsError);
        Assert.Equal("docker.command.failed", viewModel.RuntimeErrorCode);
        Assert.Equal([typeof(DockerExecutionSettingsView)], registry.SettingsViews);
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
        Assert.Equal("agent0ai/agent-zero:1.0", request.Metadata?[DockerExecutionSettingsViewModel.ImageReferenceMetadataKey]);
        Assert.Equal(DockerImageStatus.Pulling, viewModel.SelectedImage?.Status);
        Assert.False(viewModel.PullSelectedImageCommand.CanExecute(null));
    }

    private sealed class RecordingAppRegistry : IAvaloniaPackageContributionRegistry
    {
        public List<Type> SettingsViews { get; } = [];

        public void RegisterPackageView<TView>(PackageViewRegistration registration) where TView : Control { }
        public void RegisterSettingsView<TView>() where TView : Control => SettingsViews.Add(typeof(TView));
    }

    private sealed class RecordingRuntimeRegistry : ISunderRuntimeContributionRegistry
    {
        public List<string> OperationIds { get; } = [];
        public List<string> RpcProviderIds { get; } = [];

        public void RegisterBackgroundService<TService>() where TService : class, IPackageBackgroundService { }
        public void RegisterSettingsSchema(PackageSettingsSchema schema) { }
        public void RegisterRpcProvider(string providerId, ISunderRpcServiceHandler handler)
            => RpcProviderIds.Add(providerId);
        public void RegisterRuntimeOperation<TRequest, TResponse>(
            PackageRuntimeOperation<TRequest, TResponse> operation,
            IPackageRuntimeOperationHandler<TRequest, TResponse> handler)
            where TRequest : class where TResponse : class
            => OperationIds.Add(operation.OperationId);
        public void RegisterRuntimeStream<TRequest, TEvent>(
            PackageRuntimeStream<TRequest, TEvent> stream,
            IPackageRuntimeStreamHandler<TRequest, TEvent> handler)
            where TRequest : class where TEvent : class
        { }
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
                [new DockerImageDefinition("agent0ai/agent-zero:1.0", DockerImageStatus.NotPulled, null, null)],
                CatalogRevision: 1);
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

    private sealed class FailingDockerRuntimeClient : IPackageRuntimeClient
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
            return ValueTask.FromException<TResponse>(new PackageRuntimeInvocationException(
                "docker.command.failed",
                isTransient: true,
                statusCode: 503,
                correlationId: "docker-host-defense"));
        }

        public async IAsyncEnumerable<TEvent> SubscribeAsync<TRequest, TEvent>(
            PackageRuntimeStream<TRequest, TEvent> stream,
            TRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken = default)
            where TRequest : class
            where TEvent : class
        {
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

    private sealed class SettingsOverridePackageContext(
        IPackageContext inner,
        IPackageSettings settings) : IPackageContext
    {
        public string PackageId => inner.PackageId;
        public string Version => inner.Version;
        public string ContentRootPath => inner.ContentRootPath;
        public IPackageStorageContext Storage => inner.Storage;
        public IPackageSettings Settings => settings;
        public IPackageSecrets Secrets => inner.Secrets;
        public Sunder.Sdk.Logging.IPackageLogging Logging => inner.Logging;
    }

    private sealed class MutablePackageSettings : IPackageSettings
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

        public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_values.GetValueOrDefault(key));
        }

        public Task<string?> GetStoredValueAsync(string key, CancellationToken cancellationToken = default)
            => GetValueAsync(key, cancellationToken);

        public Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _values[key] = value;
            return Task.CompletedTask;
        }

        public Task DeleteValueAsync(string key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _values.Remove(key);
            return Task.CompletedTask;
        }
    }
}
