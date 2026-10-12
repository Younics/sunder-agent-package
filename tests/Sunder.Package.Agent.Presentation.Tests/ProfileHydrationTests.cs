extern alias AgentApp;

using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.AI;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using AgentApp::Sunder.Package.Agent.PackageViews;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.Services;
using Sunder.Package.Agent.Storage;
using Sunder.Package.Agent.Tests;
using Xunit;

namespace Sunder.Package.Agent.Presentation.Tests;

public sealed class ProfileHydrationTests
{
    [AvaloniaFact]
    public async Task HydrationDisablesSaveAndNeverMarksConcurrentEditClean()
    {
        using var scope = RegressionTestPackageScope.Create();
        using var extensions = new RegressionTestExtensionCatalog();
        var provider = new DelayedChatProvider();
        using var profileService = CreateProfileService(scope, extensions);
        var profile = await profileService.CreateProfileAsync("Profile");
        extensions.AddProvider(RegressionTestBindings.ChatProviders, provider);
        profileService.SaveProfile(
            profile.ProfileId,
            profile.DisplayName,
            profile.Description,
            profile.Instructions,
            provider.Descriptor.ProviderId,
            "model",
            embeddingProviderId: null,
            embeddingModelId: null);
        using var viewModel = RegressionTestAppViews.Profiles(profileService);
        var initialization = viewModel.InitializeAsync();
        var concurrentInitialization = viewModel.InitializeAsync();
        Assert.True(
            await Task.WhenAny(provider.LoadStarted.Task, initialization).WaitAsync(TestContext.Current.CancellationToken)
                == provider.LoadStarted.Task,
            $"Initialization completed before model hydration started: {viewModel.StatusText}");

        var adapter = Adapt(viewModel);
        AdaptiveEditorLifecycleContract.SharedInitialization(adapter, provider.LoadCount);
        AdaptiveEditorLifecycleContract.HydrationBlocksMutation(adapter);
        Assert.True(viewModel.IsHydrating);
        Assert.False(viewModel.IsEditorEnabled);
        Assert.False(viewModel.CanNavigateProfiles);
        Assert.False(viewModel.SaveProfileCommand.CanExecute(null));

        viewModel.DisplayName = "Edited during hydration";
        provider.Models.SetResult([new AgentModelDescriptor("model", "Model", 16_000, 2_000)]);
        await Task.WhenAll(initialization, concurrentInitialization);

        AdaptiveEditorLifecycleContract.NewerEditRemainsDirty(
            adapter,
            "Edited during hydration",
            "hydration completion");
        Assert.False(viewModel.IsHydrating);
        Assert.Equal("Edited during hydration", viewModel.DisplayName);
        Assert.True(viewModel.IsDirty);
    }

    [AvaloniaFact]
    public async Task CompactLayout_NullSelectionClearsInflightHydrationState()
    {
        using var scope = RegressionTestPackageScope.Create();
        using var extensions = new RegressionTestExtensionCatalog();
        var provider = new DelayedChatProvider();
        using var profileService = CreateProfileService(scope, extensions);
        var profile = await profileService.CreateProfileAsync("Profile");
        extensions.AddProvider(RegressionTestBindings.ChatProviders, provider);
        profileService.SaveProfile(
            profile.ProfileId,
            profile.DisplayName,
            profile.Description,
            profile.Instructions,
            provider.Descriptor.ProviderId,
            "model",
            embeddingProviderId: null,
            embeddingModelId: null);
        using var viewModel = RegressionTestAppViews.Profiles(profileService);
        var initialization = viewModel.InitializeAsync();
        Assert.True(
            await Task.WhenAny(provider.LoadStarted.Task, initialization).WaitAsync(TestContext.Current.CancellationToken)
                == provider.LoadStarted.Task,
            $"Initialization completed before model hydration started: {viewModel.StatusText}");
        Assert.True(viewModel.IsHydrating);

        viewModel.IsCompactLayout = true;

        Assert.Null(viewModel.SelectedProfile);
        Assert.False(viewModel.IsHydrating);
        Assert.False(viewModel.IsBusy);
        Assert.True(viewModel.CanNavigateProfiles);
        Assert.True(viewModel.ShowCompactList);

        provider.Models.SetResult([new AgentModelDescriptor("model", "Model", 16_000, 2_000)]);
        await initialization;

        Assert.False(viewModel.IsHydrating);
        Assert.False(viewModel.IsBusy);
        Assert.True(viewModel.CanNavigateProfiles);
        Assert.Null(viewModel.SelectedProfile);
    }

    [AvaloniaFact]
    public async Task RuntimeOutageBanner_IsSharedByCompactListAndEditorAndClearsOnRecovery()
    {
        using var scope = RegressionTestPackageScope.Create();
        using var extensions = new RegressionTestExtensionCatalog();
        using var profileService = CreateProfileService(scope, extensions);
        var profile = await profileService.CreateProfileAsync("Profile");
        using var viewModel = RegressionTestAppViews.Profiles(profileService);
        using var view = new AgentProfilesView { DataContext = viewModel };
        var window = new Window { Width = 480, Height = 640, Content = view };
        window.Show();
        try
        {
            await viewModel.InitializeAsync();
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            Assert.True(viewModel.IsCompactLayout);
            Assert.True(viewModel.ShowCompactList);

            viewModel.RuntimeNoticeText = "Agent Runtime is unavailable. Reconnecting...";
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            var banner = Assert.IsType<Border>(view.FindControl<Border>("ProfileRuntimeNoticeBanner"));
            Assert.True(banner.IsVisible);
            Assert.Single(
                view.GetVisualDescendants().OfType<Border>(),
                control => control.Name == "ProfileRuntimeNoticeBanner");

            viewModel.ActivateProfile(viewModel.Profiles.Single(item => item.ProfileId == profile.ProfileId));
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            Assert.True(viewModel.ShowCompactEditor);
            Assert.True(banner.IsVisible);
            Assert.Same(banner, view.FindControl<Border>("ProfileRuntimeNoticeBanner"));

            viewModel.RuntimeNoticeText = string.Empty;
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            Assert.False(banner.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    private static AgentProfileService CreateProfileService(
        RegressionTestPackageScope scope,
        RegressionTestExtensionCatalog extensionCatalog)
    {
        var store = new AgentLocalStore(scope.Context);
        var sessionService = new AgentSessionService(store, extensionCatalog.SessionCleaners);
        var workspaceService = new AgentWorkspaceService(store, extensionCatalog.SessionCleaners, sessionService, extensionCatalog.WorkspacePathMigrators);
        var toolService = new AgentToolService(
            sessionService,
            workspaceService,
            new AgentExecutionTargetService(extensionCatalog.ExecutionTargets),
            extensionCatalog.ToolSources,
            extensionCatalog.ObservedToolExecutions);
        return extensionCatalog.CreateProfileService(store, toolService);
    }

    private static IAdaptiveEditorLifecycleAdapter Adapt(AgentProfilesViewModel viewModel)
        => new DelegateAdaptiveEditorLifecycleAdapter(
            "Profiles",
            () => new AdaptiveEditorSnapshot(
                viewModel.IsHydrating,
                viewModel.SaveProfileCommand.CanExecute(null),
                viewModel.IsBusy,
                viewModel.IsDirty,
                viewModel.SelectedProfile?.ProfileId,
                viewModel.DisplayName,
                PublicationCount: 0),
            viewModel);

    private sealed class DelayedChatProvider : IAgentChatProvider
    {
        private int _loadCount;

        public AgentProviderDescriptor Descriptor { get; } = new(
            "delayed-provider",
            "Delayed Provider",
            [],
            SupportsStreaming: true,
            SupportsInterruptibleRuns: true);

        public TaskCompletionSource LoadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<IReadOnlyList<AgentModelDescriptor>> Models { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int LoadCount => Volatile.Read(ref _loadCount);

        public ValueTask<IReadOnlyList<AgentModelDescriptor>> GetAvailableModelsAsync(
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _loadCount);
            LoadStarted.TrySetResult();
            return new ValueTask<IReadOnlyList<AgentModelDescriptor>>(Models.Task);
        }

        public ValueTask<AgentProviderReadiness> GetReadinessAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentProviderReadiness(
                Descriptor.ProviderId,
                AgentProviderReadinessStatus.Ready,
                "Ready."));

        public ValueTask<AgentProviderRunCapabilities> GetRunCapabilitiesAsync(
            string? modelId,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<IChatClient> CreateChatClientAsync(
            AgentChatClientContext context,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
