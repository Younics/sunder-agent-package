using Avalonia.Headless.XUnit;
using System.Collections.Specialized;
using Microsoft.Extensions.AI;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.Tests;
using Xunit;

namespace Sunder.Package.Agent.Presentation.Tests;

public sealed class SubagentHydrationTests
{
    [AvaloniaFact]
    public async Task SubagentsViewModel_SaveAndDelete_CompactLayout_ReturnToListAndClearSelection()
    {
        using var scope = RegressionTestPackageScope.Create();
        await using var extensions = new RegressionTestExtensionCatalog();
        var provider = new DelayedChatProvider();
        provider.Models.SetResult([new AgentModelDescriptor("model", "Model", 16_000, 2_000)]);
        extensions.AddProvider(RegressionTestBindings.ChatProviders, provider);
        var service = new SubagentService(new SubagentStore(scope.Context));
        var subagent = service.CreateSubagent("Alpha Subagent");
        await using var gateway = new WorkerBackedSubagentManagementGateway(service, extensions);
        using var viewModel = new SubagentsViewModel(gateway) { IsCompactLayout = true };
        await viewModel.InitializeAsync();
        await OpenReadyEditorAsync();
        viewModel.Description = "Handles focused tasks.";
        Assert.True(viewModel.SaveSubagentCommand.CanExecute(null));
        await viewModel.SaveSubagentCommand.ExecuteAsync(null);

        Assert.False(viewModel.IsEditorActive);
        Assert.Null(viewModel.SelectedSubagent);
        Assert.Empty(viewModel.StatusText);
        Assert.False(viewModel.HasStatusText);
        Assert.Equal("Handles focused tasks.", service.GetSubagent(subagent.SubagentId)?.Description);

        await OpenReadyEditorAsync();
        Assert.True(viewModel.DeleteSubagentCommand.CanExecute(null));
        await viewModel.DeleteSubagentCommand.ExecuteAsync(null);

        Assert.False(viewModel.IsEditorActive);
        Assert.Null(viewModel.SelectedSubagent);
        Assert.Empty(viewModel.Subagents);
        Assert.Null(service.GetSubagent(subagent.SubagentId));

        async Task OpenReadyEditorAsync()
        {
            viewModel.ActivateSubagent(viewModel.Subagents.Single(item => item.SubagentId == subagent.SubagentId));
            Assert.Equal(subagent.SubagentId, viewModel.SelectedSubagent?.SubagentId);
            Assert.True(viewModel.ShowCompactEditor);
            // Row activation navigates immediately; the bound editor cannot accept input until hydration finishes.
            await RenderedViewWaits.WaitUntilAsync(() => viewModel.IsEditorEnabled && !viewModel.IsBusy,
                () => $"Hydrating: {viewModel.IsHydrating}; status: {viewModel.StatusText}");
            Assert.False(viewModel.IsStatusError, viewModel.StatusText);
        }
    }

    [AvaloniaFact]
    public async Task SubagentsViewModel_Save_WideLayout_KeepsEditorLoadedAndAutoClearsSuccess()
    {
        using var scope = RegressionTestPackageScope.Create();
        await using var extensions = new RegressionTestExtensionCatalog();
        var provider = new DelayedChatProvider();
        provider.Models.SetResult([new AgentModelDescriptor("model", "Model", 16_000, 2_000)]);
        extensions.AddProvider(RegressionTestBindings.ChatProviders, provider);
        var service = new SubagentService(new SubagentStore(scope.Context));
        var subagent = service.CreateSubagent("Alpha Subagent");
        await using var gateway = new WorkerBackedSubagentManagementGateway(service, extensions);
        using var viewModel = new SubagentsViewModel(gateway);
        await viewModel.InitializeAsync();
        Assert.Equal(subagent.SubagentId, viewModel.SelectedSubagent?.SubagentId);
        viewModel.Subagents.CollectionChanged += (_, args) =>
        {
            if (args.Action == NotifyCollectionChangedAction.Reset)
                viewModel.SelectedSubagent = null;
        };
        viewModel.Description = "Handles focused tasks.";

        await viewModel.SaveSubagentCommand.ExecuteAsync(null);

        Assert.False(viewModel.IsEditorActive);
        Assert.Equal(subagent.SubagentId, viewModel.SelectedSubagent?.SubagentId);
        Assert.Equal("Handles focused tasks.", viewModel.Description);
        Assert.Equal("Handles focused tasks.", service.GetSubagent(subagent.SubagentId)?.Description);
        Assert.Equal("Subagent saved.", viewModel.StatusText);
        Assert.True(viewModel.HasStatusText);
        Assert.True(viewModel.IsStatusSuccess);
        Assert.False(viewModel.IsStatusWarning);
        Assert.False(viewModel.IsStatusError);

        var cleared = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(viewModel.HasStatusText) && !viewModel.HasStatusText)
                cleared.TrySetResult();
        };
        await cleared.Task.WaitAsync(TimeSpan.FromSeconds(4), TestContext.Current.CancellationToken);
        Assert.Empty(viewModel.StatusText);
        Assert.Equal(SubagentStatusKind.None, viewModel.StatusKind);
    }

    [AvaloniaFact]
    public async Task HydrationDisablesSaveAndNeverMarksConcurrentEditClean()
    {
        using var scope = RegressionTestPackageScope.Create();
        using var extensions = new RegressionTestExtensionCatalog();
        var provider = new DelayedChatProvider();
        extensions.AddProvider(RegressionTestBindings.ChatProviders, provider);
        var service = new SubagentService(new SubagentStore(scope.Context));
        var subagent = service.CreateSubagent("Subagent");
        service.SaveSubagent(
            subagent.SubagentId,
            subagent.DisplayName,
            "Required description",
            subagent.Instructions,
            provider.Descriptor.ProviderId,
            "model",
            []);
        using var gateway = new WorkerBackedSubagentManagementGateway(service, extensions);
        using var viewModel = new SubagentsViewModel(gateway);

        var initialization = viewModel.InitializeAsync();
        var concurrentInitialization = viewModel.InitializeAsync();
        await provider.LoadStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

        var adapter = Adapt(viewModel);
        AdaptiveEditorLifecycleContract.SharedInitialization(adapter, provider.LoadCount);
        AdaptiveEditorLifecycleContract.HydrationBlocksMutation(adapter);
        Assert.True(viewModel.IsHydrating);
        Assert.False(viewModel.IsEditorEnabled);
        Assert.False(viewModel.CanNavigateSubagents);
        Assert.False(viewModel.SaveSubagentCommand.CanExecute(null));

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
    public async Task BackRetiresNonCooperativeHydrationWithoutWaitingForProvider()
    {
        using var scope = RegressionTestPackageScope.Create();
        using var extensions = new RegressionTestExtensionCatalog();
        var provider = new DelayedChatProvider();
        extensions.AddProvider(RegressionTestBindings.ChatProviders, provider);
        var service = new SubagentService(new SubagentStore(scope.Context));
        var subagent = service.CreateSubagent("Subagent");
        service.SaveSubagent(
            subagent.SubagentId,
            subagent.DisplayName,
            "Required description",
            subagent.Instructions,
            provider.Descriptor.ProviderId,
            "model",
            []);
        using var gateway = new WorkerBackedSubagentManagementGateway(service, extensions);
        using var viewModel = new SubagentsViewModel(gateway);

        var initialization = viewModel.InitializeAsync();
        await provider.LoadStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        var lateChanges = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var retired = false;
        viewModel.PropertyChanged += (_, change) =>
        {
            if (Volatile.Read(ref retired)) lateChanges.Enqueue($"{change.PropertyName ?? "*"}: {Environment.StackTrace}");
        };
        try
        {
            await AdaptiveEditorLifecycleContract.RetiredWorkCannotPublishAsync(
                Adapt(viewModel),
                () => { viewModel.BackToSubagentListCommand.Execute(null); Volatile.Write(ref retired, true); },
                () => provider.Models.TrySetResult([new AgentModelDescriptor("model", "Model", 16_000, 2_000)]),
                initialization.WaitAsync(TimeSpan.FromSeconds(2)));
        }
        catch (AdaptiveEditorContractException error)
        {
            throw new InvalidOperationException($"{error.Message} Properties: {string.Join(", ", lateChanges)}. Status: {viewModel.StatusText}", error);
        }
        Assert.Null(viewModel.SelectedSubagent);
        Assert.False(viewModel.IsHydrating);
        Assert.False(viewModel.IsBusy);
    }

    private static IAdaptiveEditorLifecycleAdapter Adapt(SubagentsViewModel viewModel)
        => new DelegateAdaptiveEditorLifecycleAdapter(
            "Subagents",
            () => new AdaptiveEditorSnapshot(
                viewModel.IsHydrating,
                viewModel.SaveSubagentCommand.CanExecute(null),
                viewModel.IsBusy,
                viewModel.IsDirty,
                viewModel.SelectedSubagent?.SubagentId,
                viewModel.DisplayName,
                PublicationCount: 0,
                viewModel.IsRuntimeAvailable
                    ? AdaptiveEditorRuntimeState.Available
                    : AdaptiveEditorRuntimeState.Unavailable),
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
