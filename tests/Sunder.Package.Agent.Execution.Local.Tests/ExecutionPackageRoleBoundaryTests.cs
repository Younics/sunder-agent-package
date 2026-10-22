extern alias LocalWorker;
extern alias LocalApp;
extern alias DockerWorker;
extern alias DockerApp;

using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using DockerWorker::Sunder.Package.Agent.Execution.Docker;
using LocalWorker::Sunder.Package.Agent.Execution.Local;
using LocalModels = LocalWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Models;
using DockerModels = DockerWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Models;
using DockerUi = DockerApp::Sunder.Package.Agent.Execution.Docker;
using LocalUi = LocalApp::Sunder.Package.Agent.Execution.Local;
using DockerExecutionSettingsViewModel = DockerApp::Sunder.Package.Agent.Execution.Docker.DockerExecutionSettingsViewModel;
using DockerExecutionSettingsView = DockerApp::Sunder.Package.Agent.Execution.Docker.DockerExecutionSettingsView;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.Tests;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Avalonia;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Settings;
using Sunder.Sdk.Services;
using Sunder.Sdk.Storage;
using Xunit;
using DockerPresentationService = DockerApp::Sunder.Generated.Packages.Younics_Ai_Agent_Execution_Docker_Services.IPresentationService;
using LocalPresentationService = LocalApp::Sunder.Generated.Packages.Younics_Ai_Agent_Execution_Local_Services.IPresentationService;

namespace Sunder.Package.Agent.Execution.Local.Tests;

public sealed class ExecutionPackageRoleBoundaryTests
{
    [Theory]
    [Trait("Lane", "PackageCompatibility")]
    [InlineData(true)]
    [InlineData(false)]
    public void AppModules_RegisterOnlyPresentationServices(bool local)
    {
        using var scope = RegressionTestPackageScope.Create();
        var services = new ServiceCollection();
        services.AddSingleton(scope.Context);
        services.AddSingleton<IBackgroundTaskQueue, NoopBackgroundTaskQueue>();

        if (local)
        {
            services.AddSingleton<LocalPresentationService>(CreatePresentationProxy<LocalPresentationService>());
            new LocalApp::Sunder.Generated.Package.AppModule().ConfigureAppServices(services, scope.Context);
        }
        else
        {
            services.AddSingleton<DockerPresentationService>(CreatePresentationProxy<DockerPresentationService>());
            new DockerApp::Sunder.Generated.Package.AppModule().ConfigureAppServices(services, scope.Context);
        }

        var serviceTypeNames = services.Select(descriptor => descriptor.ServiceType.Name).ToArray();
        Assert.DoesNotContain(nameof(LocalExecutionTarget), serviceTypeNames);
        Assert.DoesNotContain(nameof(LocalExecutionWorkspaceConfigService), serviceTypeNames);
        Assert.DoesNotContain(nameof(LocalShellCatalogService), serviceTypeNames);
        Assert.DoesNotContain(nameof(DockerExecutionTarget), serviceTypeNames);
        Assert.DoesNotContain(nameof(DockerExecutionWorkspaceConfigService), serviceTypeNames);
        Assert.DoesNotContain(nameof(DockerImageCatalogService), serviceTypeNames);
        Assert.Contains(serviceTypeNames, name => name.EndsWith("SettingsViewModel", StringComparison.Ordinal));
        Assert.DoesNotContain(serviceTypeNames, name => name.EndsWith("AppRuntimeClient", StringComparison.Ordinal));
        Assert.DoesNotContain(serviceTypeNames, name => name.EndsWith("WorkspaceEditorPresentationContributor", StringComparison.Ordinal));

        using var provider = services.BuildServiceProvider();
        var registry = new RecordingAppRegistry();
        if (local)
        {
            new LocalApp::Sunder.Generated.Package.AppModule().RegisterAppContributions(registry, provider);
        }
        else
        {
            new DockerApp::Sunder.Generated.Package.AppModule().RegisterAppContributions(registry, provider);
        }

        Assert.Single(registry.SettingsViews);
    }

    [Theory]
    [Trait("Lane", "PackageCompatibility")]
    [InlineData(true)]
    [InlineData(false)]
    public void WorkerLeaves_HaveNoAppModuleAndUseOwnerRpcV1(bool local)
    {
        var workerAssembly = local
            ? typeof(LocalExecutionTarget).Assembly
            : typeof(DockerExecutionTarget).Assembly;
        using var scope = RegressionTestPackageScope.Create();
        var runner = new DockerCliRunner(scope.Context);
        var registration = local
            ? new LocalExecutionPresentationService(scope.Context,
                new LocalShellCatalogService(scope.Context)).CreateSunderRpcProviderRegistration()
            : new DockerExecutionPresentationService(scope.Context, runner,
                new DockerImageCatalogService(scope.Context, runner)).CreateSunderRpcProviderRegistration();
        var descriptor = CurrentPrivateRpcDescriptors.Get(registration.ContractId);

        Assert.DoesNotContain(
            workerAssembly.GetTypes(),
            type => typeof(ISunderAppPackageModule).IsAssignableFrom(type));
        Assert.Equal(SunderRpcProtocol.DescriptorVersion, descriptor.DescriptorVersion);
        Assert.NotNull(descriptor.AbiSha256);
        Assert.Equal(descriptor.Sha256, registration.DescriptorSha256);
        Assert.Equal(descriptor.AbiSha256, registration.AbiSha256);
        Assert.Equal(descriptor.Version, registration.ContractVersion);
        Assert.Single(descriptor.Services);
    }

    [Fact]
    public async Task DockerPresentation_TransfersLargeCatalogThroughContentReference()
    {
        var images = Enumerable.Range(0, 2500)
            .Select(index => new DockerImageDefinition(
                $"registry.example.test/team/image-{index}:1.0",
                DockerImageStatus.Ready,
                DateTimeOffset.UtcNow,
                new string('x', 128)))
            .ToArray();
        using var scope = RegressionTestPackageScope.Create();
        var runner = new DockerCliRunner(scope.Context);
        var catalog = new DockerImageCatalogService(scope.Context, runner);
        await catalog.SaveImagesAsync(images);
        var service = new DockerExecutionPresentationService(scope.Context, runner, catalog);

        await using var content = await service.GetSettingsAsync();
        using var document = JsonDocument.Parse(await ReadContentAsync(content));
        var serializedImages = document.RootElement.GetProperty("images").EnumerateArray().ToArray();

        Assert.Equal(images.Length, serializedImages.Length);
        Assert.Equal(
            images.OrderBy(image => image.ImageReference, StringComparer.OrdinalIgnoreCase).Last().ImageReference,
            serializedImages[2499].GetProperty("imageReference").GetString());
        Assert.Equal("application/json", content.MediaType);
    }

    [Fact]
    public async Task LocalRuntimeOperation_RejectsUnboundedShellPath()
    {
        using var scope = RegressionTestPackageScope.Create();
        var catalog = new LocalShellCatalogService(scope.Context);
        var response = await new LocalExecutionPresentationService(scope.Context, catalog).SaveShellsAsync(
            [new LocalShellDefinition("custom", "Custom", "relative/shell", "custom", false)],
            expectedShellCatalogRevision: 0);

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
            RegressionTestBindings.ExecutionTargets,
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
        var generation = await target.ConfigurationGenerationAsync(ToLocalContext(context));

        await configService.SaveConfigAsync(
            binding.BindingId,
            new LocalExecutionWorkspaceConfig("sh", [Path.Combine(scope.RootPath, "second-bin")]));

        var staleContext = context with { ExpectedConfigurationGeneration = generation };
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await target.ExecutionScopeAsync(ToLocalContext(staleContext)));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await target.AddPathAsync(new LocalModels.CYounics_Ai_Agent_Execution_Target_ExecutionTargetAddPathRequest
            {
                Context = ToLocalContext(staleContext),
                Path = Path.Combine(scope.RootPath, "rejected-bin"),
            }));
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
            RegressionTestBindings.ExecutionTargets,
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
        var generation = await target.ConfigurationGenerationAsync(ToLocalContext(context));

        await shellCatalog.SaveCustomShellsAsync(
            [new LocalShellDefinition("custom", "Custom", secondShell, AgentShellSyntaxKinds.PosixSh, false)],
            expectedRevision: 1);

        var staleContext = context with { ExpectedConfigurationGeneration = generation };
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await target.ShellAsync(ToLocalContext(staleContext)));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await target.AddPathAsync(new LocalModels.CYounics_Ai_Agent_Execution_Target_ExecutionTargetAddPathRequest
            {
                Context = ToLocalContext(staleContext),
                Path = "/rejected",
            }));
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
        var response = await new DockerExecutionPresentationService(scope.Context, runner, catalog)
            .SaveSettingsAsync("300", "relative/docker");

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
            RegressionTestBindings.ExecutionTargets,
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
        var generation = await target.ConfigurationGenerationAsync(ToDockerContext(context));

        await configService.SaveConfigAsync(
            binding.BindingId,
            new DockerExecutionWorkspaceConfig("repository/image:latest", null, "/bin/sh", ["/second"]));

        var staleContext = context with { ExpectedConfigurationGeneration = generation };
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await target.ShellAsync(ToDockerContext(staleContext)));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await target.AddPathAsync(new DockerModels.CYounics_Ai_Agent_Execution_Target_ExecutionTargetAddPathRequest
            {
                Context = ToDockerContext(staleContext),
                Path = "/rejected",
            }));
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
            RegressionTestBindings.ExecutionTargets,
            "docker",
            "primary-execution-target",
            true,
            0,
            default,
            default);
        var targetContext = ToDockerContext(new AgentExecutionTargetContext(null, null, workspace, binding));
        await configService.SaveConfigAsync(
            binding.BindingId,
            new DockerExecutionWorkspaceConfig("repository/image:latest", null));
        var generation = await target.ConfigurationGenerationAsync(targetContext);

        var missingIdentity = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await target.ExecutionScopeAsync(targetContext with
            {
                ExpectedConfigurationGeneration = generation,
            }));
        Assert.Contains("image identity is not available", missingIdentity.Message, StringComparison.OrdinalIgnoreCase);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await target.AddPathAsync(new DockerModels.CYounics_Ai_Agent_Execution_Target_ExecutionTargetAddPathRequest
            {
                Context = targetContext with { ExpectedConfigurationGeneration = generation },
                Path = "/rejected-before-readiness",
            }));
        Assert.DoesNotContain(
            "/rejected-before-readiness",
            (await configService.GetConfigAsync(binding.BindingId)).PathEntries ?? []);

        await settings.SetValueAsync(DockerCli.ExecutablePathConfigurationKey, secondCli);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await target.ShellAsync(targetContext with
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
        await using var content = await new DockerExecutionPresentationService(scope.Context, runner, catalog)
            .AddImageAsync(imageReference);
        var response = ReadDockerResponse(await ReadContentAsync(content));

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
        await using var content = await new DockerExecutionPresentationService(scope.Context, runner, catalog)
            .AddImageAsync("repository/image:latest");
        var response = ReadDockerResponse(await ReadContentAsync(content));

        Assert.True(response.Success);
        var image = Assert.Single(response.Images!);
        Assert.Equal("repository/image:latest", image.ImageReference);
        Assert.Equal(DockerImageStatus.NotPulled, image.Status);
    }

    [Fact]
    public async Task DockerActivation_RejectsSemanticCatalogFailureBeforeRuntimeOperations()
    {
        using var scope = RegressionTestPackageScope.Create();
        const string json = "{\"schemaVersion\":3,\"revision\":1,\"images\":[],\"unknownPolicy\":true}";
        await scope.Context.Storage.State.SetValueAsync(DockerImageCatalogService.ImagesKey, json);
        var validation = new DockerPackageStorageValidation(scope.Context);

        var exception = await Assert.ThrowsAsync<DockerExecutionDomainException>(
            () => validation.StartAsync());

        Assert.Equal("docker.catalog.unknown-data", exception.Code);
        Assert.Equal(json, await scope.Context.Storage.State.GetValueAsync(DockerImageCatalogService.ImagesKey));
    }

    [Fact]
    public async Task DockerTypedRuntimeFailure_DoesNotEscapePreparationOrRemoveSettingsContribution()
    {
        using var scope = RegressionTestPackageScope.Create();
        var services = new ServiceCollection();
        services.AddSingleton(scope.Context);
        var runtimeClient = new FailingDockerRuntimeClient();
        services.AddSingleton<DockerPresentationService>(runtimeClient.Service);
        services.AddSingleton<IBackgroundTaskQueue, NoopBackgroundTaskQueue>();
        var module = new DockerApp::Sunder.Generated.Package.AppModule();
        module.ConfigureAppServices(services, scope.Context);
        using var provider = services.BuildServiceProvider();
        var registry = new RecordingAppRegistry();
        module.RegisterAppContributions(registry, provider);
        using var viewModel = provider.GetRequiredService<DockerExecutionSettingsViewModel>();

        var prepared = await viewModel.PrepareNavigationAsync(new PackageViewNavigationContext(
            "settings:younics.ai.agent.execution.docker",
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
        var queue = new RecordingBackgroundTaskQueue();
        using var viewModel = (DockerExecutionSettingsViewModel)AgentRoleBoundary.Create(
            typeof(DockerExecutionSettingsViewModel), runtimeClient.Service, queue, null, null);
        await viewModel.InitializeAsync();
        viewModel.SelectedImage = Assert.Single(viewModel.Images);

        viewModel.PullSelectedImageCommand.Execute(null);

        var request = Assert.Single(queue.Requests);
        Assert.Equal(DockerExecutionSettingsViewModel.ImagePullGroupKey, request.GroupKey);
        Assert.Equal(BackgroundTaskIndicator.Settings, request.Indicator);
        Assert.Equal(BackgroundTaskConcurrencyMode.SequentialWithinGroup, request.ConcurrencyMode);
        Assert.Equal("agent0ai/agent-zero:1.0", request.Metadata?[DockerExecutionSettingsViewModel.ImageReferenceMetadataKey]);
        Assert.Equal(DockerUi.DockerImageStatus.Pulling, viewModel.SelectedImage?.Status);
        Assert.False(viewModel.PullSelectedImageCommand.CanExecute(null));
    }

    private sealed class RecordingAppRegistry : IAvaloniaPackageContributionRegistry
    {
        public List<Type> SettingsViews { get; } = [];

        public void RegisterPackageView<TView>(PackageViewRegistration registration) where TView : Control { }
        public void RegisterSettingsView<TView>() where TView : Control => SettingsViews.Add(typeof(TView));
    }

    private sealed class NoopBackgroundTaskQueue : IBackgroundTaskQueue
    {
        public event EventHandler<BackgroundTaskChangedEventArgs>? TaskChanged { add { } remove { } }
        public BackgroundTaskSnapshot Enqueue(BackgroundTaskRequest request) => throw new NotSupportedException();
        public IReadOnlyList<BackgroundTaskSnapshot> ListTasks(string? groupKey = null) => [];
        public bool Cancel(Guid processId) => false;
    }

    private static LocalModels.AgentExecutionTargetContext ToLocalContext(AgentExecutionTargetContext context)
        => new()
        {
            SessionId = context.SessionId,
            ProfileId = context.ProfileId,
            Workspace = new LocalModels.AgentWorkspaceRecord
            {
                WorkspaceId = context.Workspace.WorkspaceId,
                DisplayName = context.Workspace.DisplayName,
                Description = context.Workspace.Description,
                CreatedAtUtc = new SunderRpcOffsetDateTime(context.Workspace.CreatedAtUtc.ToString("O")),
                UpdatedAtUtc = new SunderRpcOffsetDateTime(context.Workspace.UpdatedAtUtc.ToString("O")),
                Paths = context.Workspace.Paths.Select(path => new LocalModels.AgentWorkspacePathRecord
                {
                    PathId = path.PathId,
                    WorkspaceId = path.WorkspaceId,
                    HostPath = path.HostPath,
                    IsDefault = path.IsDefault,
                    SortOrder = path.SortOrder,
                    CreatedAtUtc = new SunderRpcOffsetDateTime(path.CreatedAtUtc.ToString("O")),
                    UpdatedAtUtc = new SunderRpcOffsetDateTime(path.UpdatedAtUtc.ToString("O")),
                }).ToArray(),
                Documents = [],
            },
            Binding = new LocalModels.AgentWorkspaceBindingRecord
            {
                BindingId = context.Binding.BindingId,
                WorkspaceId = context.Binding.WorkspaceId,
                ExtensionPointId = context.Binding.ExtensionPointId,
                ContributionId = context.Binding.ContributionId,
                Role = context.Binding.Role,
                IsEnabled = context.Binding.IsEnabled,
                SortOrder = context.Binding.SortOrder,
                CreatedAtUtc = new SunderRpcOffsetDateTime(context.Binding.CreatedAtUtc.ToString("O")),
                UpdatedAtUtc = new SunderRpcOffsetDateTime(context.Binding.UpdatedAtUtc.ToString("O")),
            },
            AllowOutsideConfiguredScope = context.AllowOutsideConfiguredScope,
            ApprovedResourceReferences = context.ApprovedResourceReferences,
            ApprovedResourceClaims = [],
            ApprovedResourceCapabilities = context.ApprovedResourceCapabilities,
            CapturePostMutationResource = context.CapturePostMutationResource,
            ExpectedConfigurationGeneration = context.ExpectedConfigurationGeneration,
        };

    private static DockerModels.AgentExecutionTargetContext ToDockerContext(AgentExecutionTargetContext context)
        => new()
        {
            SessionId = context.SessionId,
            ProfileId = context.ProfileId,
            Workspace = new DockerModels.AgentWorkspaceRecord
            {
                WorkspaceId = context.Workspace.WorkspaceId,
                DisplayName = context.Workspace.DisplayName,
                Description = context.Workspace.Description,
                CreatedAtUtc = new SunderRpcOffsetDateTime(context.Workspace.CreatedAtUtc.ToString("O")),
                UpdatedAtUtc = new SunderRpcOffsetDateTime(context.Workspace.UpdatedAtUtc.ToString("O")),
                Paths = context.Workspace.Paths.Select(path => new DockerModels.AgentWorkspacePathRecord
                {
                    PathId = path.PathId,
                    WorkspaceId = path.WorkspaceId,
                    HostPath = path.HostPath,
                    IsDefault = path.IsDefault,
                    SortOrder = path.SortOrder,
                    CreatedAtUtc = new SunderRpcOffsetDateTime(path.CreatedAtUtc.ToString("O")),
                    UpdatedAtUtc = new SunderRpcOffsetDateTime(path.UpdatedAtUtc.ToString("O")),
                }).ToArray(),
                Documents = [],
            },
            Binding = new DockerModels.AgentWorkspaceBindingRecord
            {
                BindingId = context.Binding.BindingId,
                WorkspaceId = context.Binding.WorkspaceId,
                ExtensionPointId = context.Binding.ExtensionPointId,
                ContributionId = context.Binding.ContributionId,
                Role = context.Binding.Role,
                IsEnabled = context.Binding.IsEnabled,
                SortOrder = context.Binding.SortOrder,
                CreatedAtUtc = new SunderRpcOffsetDateTime(context.Binding.CreatedAtUtc.ToString("O")),
                UpdatedAtUtc = new SunderRpcOffsetDateTime(context.Binding.UpdatedAtUtc.ToString("O")),
            },
            AllowOutsideConfiguredScope = context.AllowOutsideConfiguredScope,
            ApprovedResourceReferences = context.ApprovedResourceReferences,
            ApprovedResourceClaims = [],
            ApprovedResourceCapabilities = context.ApprovedResourceCapabilities,
            CapturePostMutationResource = context.CapturePostMutationResource,
            ExpectedConfigurationGeneration = context.ExpectedConfigurationGeneration,
        };

    private sealed class RecordingDockerRuntimeClient
    {
        public RecordingDockerRuntimeClient()
        {
            var proxy = DispatchProxy.Create<DockerPresentationService, DockerPresentationProxy>();
            ((DockerPresentationProxy)(object)proxy).Runtime = this;
            Service = proxy;
        }

        public DockerPresentationService Service { get; }

        private ValueTask<SunderContent> GetSettingsAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(DockerPresentationTestContent.Create(
                new DockerUi.DockerExecutionOperationResponse(
                    "300",
                    string.Empty,
                    [new DockerUi.DockerImageDefinition("agent0ai/agent-zero:1.0", DockerUi.DockerImageStatus.NotPulled, null, null)],
                    CatalogRevision: 1)));
        }

        private class DockerPresentationProxy : DispatchProxy
        {
            internal RecordingDockerRuntimeClient Runtime { get; set; } = null!;

            protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? arguments)
            {
                ArgumentNullException.ThrowIfNull(targetMethod);
                if (targetMethod.Name == "GetSettingsAsync")
                {
                    return Runtime.GetSettingsAsync(arguments?.OfType<CancellationToken>().SingleOrDefault() ?? default);
                }

                throw new NotSupportedException($"Unexpected Docker presentation method '{targetMethod.Name}'.");
            }
        }
    }

    private sealed class FailingDockerRuntimeClient
    {
        public FailingDockerRuntimeClient()
        {
            var proxy = DispatchProxy.Create<DockerPresentationService, DockerPresentationProxy>();
            Service = proxy;
        }

        public DockerPresentationService Service { get; }

        private class DockerPresentationProxy : DispatchProxy
        {
            protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? arguments)
            {
                ArgumentNullException.ThrowIfNull(targetMethod);
                if (targetMethod.Name == "GetSettingsAsync")
                {
                    var cancellationToken = arguments?.OfType<CancellationToken>().SingleOrDefault() ?? default;
                    cancellationToken.ThrowIfCancellationRequested();
                    return ValueTask.FromException<SunderContent>(new SunderRpcException(new SunderRpcError(
                        SunderRpcErrorKind.Unavailable,
                        "docker.command.failed",
                        "Docker is unavailable.")));
                }

                throw new NotSupportedException($"Unexpected Docker presentation method '{targetMethod.Name}'.");
            }
        }
    }

    private static TService CreatePresentationProxy<TService>() where TService : class
        => DispatchProxy.Create<TService, UnimplementedPresentationProxy>();

    private class UnimplementedPresentationProxy : DispatchProxy
    {
        protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? arguments)
            => throw new NotSupportedException($"Unexpected presentation method '{targetMethod?.Name}'.");
    }

    private static async Task<byte[]> ReadContentAsync(SunderContent content)
    {
        await using var stream = await content.OpenReadAsync();
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    private static DockerExecutionOperationResponse ReadDockerResponse(byte[] content)
        => JsonSerializer.Deserialize<DockerExecutionOperationResponse>(content, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = false,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower) },
        }) ?? throw new InvalidDataException("Docker presentation response was empty.");

    private sealed class RecordingBackgroundTaskQueue : IBackgroundTaskQueue
    {
        private readonly List<BackgroundTaskSnapshot> _snapshots = [];

        public List<BackgroundTaskRequest> Requests { get; } = [];
        public event EventHandler<BackgroundTaskChangedEventArgs>? TaskChanged { add { } remove { } }

        public BackgroundTaskSnapshot Enqueue(BackgroundTaskRequest request)
        {
            Requests.Add(request);
            var snapshot = new BackgroundTaskSnapshot(
                Guid.NewGuid(), request.Title, request.GroupKey, request.Indicator, request.ConcurrencyMode,
                BackgroundTaskState.Queued, string.Empty, null, request.CanCancel,
                request.Metadata ?? new Dictionary<string, string>(), null, DateTimeOffset.UtcNow, null, null);
            _snapshots.Add(snapshot);
            return snapshot;
        }

        public IReadOnlyList<BackgroundTaskSnapshot> ListTasks(string? groupKey = null)
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
