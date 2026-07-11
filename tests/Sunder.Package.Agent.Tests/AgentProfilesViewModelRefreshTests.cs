using Microsoft.Extensions.AI;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.PackageViews;
using Sunder.Package.Agent.Services;
using Sunder.Package.Agent.Storage;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class AgentProfilesViewModelRefreshTests
{
    [Fact]
    public async Task ProviderRefresh_RestoresSpeedAndModeOptions()
    {
        using var scope = RegressionTestPackageScope.Create();
        var profileService = CreateProfileService(scope, out var provider);
        using (profileService)
        {
            var profile = await profileService.CreateProfileAsync("Profile");
            using var viewModel = new AgentProfilesViewModel(profileService);
            await WaitUntilAsync(() =>
                viewModel.SelectedProfile?.ProfileId == profile.ProfileId
                && !viewModel.IsBusy
                && viewModel.ShowSpeedOptions
                && viewModel.ShowModeOptions);
            var selectedProvider = viewModel.SelectedChatProvider;

            viewModel.SelectedChatProvider = null;
            await WaitUntilAsync(() => viewModel.ChatModels.Count == 0);
            viewModel.SelectedChatProvider = selectedProvider;

            await WaitUntilAsync(() =>
                !viewModel.IsBusy
                && viewModel.ShowSpeedOptions
                && viewModel.ShowModeOptions);
            Assert.Equal(2, viewModel.SpeedOptions.Count);
            Assert.Equal(2, viewModel.ModeOptions.Count);
            Assert.Null(viewModel.SelectedSpeedOption?.SpeedOptionId);
            Assert.Null(viewModel.SelectedModeOption?.ModeOptionId);
            Assert.Equal(provider.Descriptor.ProviderId, viewModel.SelectedChatProvider?.Id);
        }
    }

    [Fact]
    public async Task ChatProviderStateChange_NotifiesSpeedAndModeVisibility()
    {
        using var scope = RegressionTestPackageScope.Create();
        var profileService = CreateProfileService(scope, out _);
        using (profileService)
        {
            var profile = await profileService.CreateProfileAsync("Profile");
            using var viewModel = new AgentProfilesViewModel(profileService);
            await WaitUntilAsync(() =>
                viewModel.SelectedProfile?.ProfileId == profile.ProfileId
                && !viewModel.IsBusy
                && viewModel.ShowSpeedOptions
                && viewModel.ShowModeOptions);
            var changedProperties = new List<string?>();
            viewModel.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);

            viewModel.HasChatProviderWarning = true;

            Assert.False(viewModel.ShowSpeedOptions);
            Assert.False(viewModel.ShowModeOptions);
            Assert.Contains(nameof(AgentProfilesViewModel.ShowSpeedOptions), changedProperties);
            Assert.Contains(nameof(AgentProfilesViewModel.ShowModeOptions), changedProperties);
        }
    }

    private static AgentProfileService CreateProfileService(
        RegressionTestPackageScope scope,
        out OptionProvider provider)
    {
        var extensionCatalog = new RegressionTestExtensionCatalog();
        provider = new OptionProvider();
        extensionCatalog.AddExtension(PackageExtensionPoints.ChatProviders, provider);
        var store = new AgentLocalStore(scope.Context);
        var sessionService = new AgentSessionService(store, extensionCatalog);
        var workspaceService = new AgentWorkspaceService(store, extensionCatalog, sessionService);
        var toolService = new AgentToolService(
            new InstalledPackageToolSource(extensionCatalog),
            sessionService,
            workspaceService,
            new AgentExecutionTargetService(extensionCatalog),
            extensionCatalog);
        return new AgentProfileService(store, toolService, extensionCatalog);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var timeout = DateTimeOffset.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= timeout)
            {
                throw new TimeoutException("Timed out waiting for the view model state.");
            }

            await Task.Delay(10);
        }
    }

    private sealed class OptionProvider : IAgentChatProvider
    {
        public AgentProviderDescriptor Descriptor { get; } = new(
            "option-provider",
            "Option Provider",
            [],
            SupportsStreaming: true,
            SupportsInterruptibleRuns: true);

        public ValueTask<IReadOnlyList<AgentModelDescriptor>> GetAvailableModelsAsync(
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<AgentModelDescriptor>>(
            [
                new AgentModelDescriptor(
                    "option-model",
                    "Option Model",
                    128_000,
                    4_096,
                    SpeedOptions:
                    [
                        new AgentModelSpeedOptionDescriptor("fast", "Fast"),
                    ],
                    ModeOptions:
                    [
                        new AgentModelModeOptionDescriptor("pro", "Pro"),
                    ]),
            ]);

        public ValueTask<AgentProviderReadiness> GetReadinessAsync(
            CancellationToken cancellationToken = default)
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
