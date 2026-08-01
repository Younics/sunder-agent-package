using Avalonia.Headless.XUnit;
using Microsoft.Extensions.AI;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.Subagents.PackageViews;
using Sunder.Package.Agent.Subagents.Services;
using Sunder.Package.Agent.Tests;
using Xunit;

namespace Sunder.Package.Agent.Presentation.Tests;

public sealed class SubagentHydrationTests
{
    [AvaloniaFact]
    public async Task HydrationDisablesSaveAndNeverMarksConcurrentEditClean()
    {
        using var scope = RegressionTestPackageScope.Create();
        var extensions = new RegressionTestExtensionCatalog();
        var provider = new DelayedChatProvider();
        extensions.AddProvider(AgentRpcServices.ChatProviders, provider);
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
        using var viewModel = new SubagentsViewModel(service, extensions);

        var initialization = viewModel.InitializeAsync();
        await provider.LoadStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.True(viewModel.IsHydrating);
        Assert.False(viewModel.IsEditorEnabled);
        Assert.False(viewModel.CanNavigateSubagents);
        Assert.False(viewModel.SaveSubagentCommand.CanExecute(null));

        viewModel.DisplayName = "Edited during hydration";
        provider.Models.SetResult([new AgentModelDescriptor("model", "Model", 16_000, 2_000)]);
        await initialization;

        Assert.False(viewModel.IsHydrating);
        Assert.Equal("Edited during hydration", viewModel.DisplayName);
        Assert.True(viewModel.IsDirty);
    }

    [AvaloniaFact]
    public async Task BackRetiresNonCooperativeHydrationWithoutWaitingForProvider()
    {
        using var scope = RegressionTestPackageScope.Create();
        var extensions = new RegressionTestExtensionCatalog();
        var provider = new DelayedChatProvider();
        extensions.AddProvider(AgentRpcServices.ChatProviders, provider);
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
        using var viewModel = new SubagentsViewModel(service, extensions);

        var initialization = viewModel.InitializeAsync();
        await provider.LoadStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        viewModel.BackToSubagentListCommand.Execute(null);

        await initialization.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Null(viewModel.SelectedSubagent);
        Assert.False(viewModel.IsHydrating);
        Assert.False(viewModel.IsBusy);
        provider.Models.TrySetResult([new AgentModelDescriptor("model", "Model", 16_000, 2_000)]);
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
