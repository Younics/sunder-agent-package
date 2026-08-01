using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.AI;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.PackageViews;
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
        extensions.AddProvider(AgentRpcServices.ChatProviders, provider);
        profileService.SaveProfile(
            profile.ProfileId,
            profile.DisplayName,
            profile.Description,
            profile.Instructions,
            provider.Descriptor.ProviderId,
            "model",
            embeddingProviderId: null,
            embeddingModelId: null);
        using var viewModel = new AgentProfilesViewModel(profileService);
        var initialization = viewModel.InitializeAsync();
        await provider.LoadStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.True(viewModel.IsHydrating);
        Assert.False(viewModel.IsEditorEnabled);
        Assert.False(viewModel.CanNavigateProfiles);
        Assert.False(viewModel.SaveProfileCommand.CanExecute(null));

        viewModel.DisplayName = "Edited during hydration";
        provider.Models.SetResult([new AgentModelDescriptor("model", "Model", 16_000, 2_000)]);
        await initialization;

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
        extensions.AddProvider(AgentRpcServices.ChatProviders, provider);
        profileService.SaveProfile(
            profile.ProfileId,
            profile.DisplayName,
            profile.Description,
            profile.Instructions,
            provider.Descriptor.ProviderId,
            "model",
            embeddingProviderId: null,
            embeddingModelId: null);
        using var viewModel = new AgentProfilesViewModel(profileService);
        var initialization = viewModel.InitializeAsync();
        await provider.LoadStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
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
        using var viewModel = new AgentProfilesViewModel(profileService);
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
        var sessionService = new AgentSessionService(store, extensionCatalog);
        var workspaceService = new AgentWorkspaceService(store, extensionCatalog, sessionService);
        var toolService = new AgentToolService(
            sessionService,
            workspaceService,
            new AgentExecutionTargetService(extensionCatalog),
            extensionCatalog);
        return new AgentProfileService(
            store,
            toolService,
            extensionCatalog,
            extensionCatalog.BehaviorLoops);
    }

    private sealed class DelayedChatProvider : IAgentChatProvider
    {
        public AgentProviderDescriptor Descriptor { get; } = new(
            "delayed-provider",
            "Delayed Provider",
            [],
            SupportsStreaming: true,
            SupportsInterruptibleRuns: true);

        public TaskCompletionSource LoadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<IReadOnlyList<AgentModelDescriptor>> Models { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<IReadOnlyList<AgentModelDescriptor>> GetAvailableModelsAsync(
            CancellationToken cancellationToken = default)
        {
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
