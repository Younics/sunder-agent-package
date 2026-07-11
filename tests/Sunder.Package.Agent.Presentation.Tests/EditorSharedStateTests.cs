using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Shared.Presentation;
using Xunit;

namespace Sunder.Package.Agent.Presentation.Tests;

public sealed class EditorSharedStateTests
{
    [Fact]
    public async Task ProviderModelLoader_StaleFirstSelectionCannotOverwriteSecondSelection()
    {
        var firstCompletion = new TaskCompletionSource<ProviderModelCatalogResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondCompletion = new TaskCompletionSource<ProviderModelCatalogResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var loader = new ProviderModelLoader(new ControlledCatalog(
            new Dictionary<string, Task<ProviderModelCatalogResult>>(StringComparer.OrdinalIgnoreCase)
            {
                ["provider-a"] = firstCompletion.Task,
                ["provider-b"] = secondCompletion.Task,
            }));

        var firstLoad = loader.LoadAsync("provider-a", TestContext.Current.CancellationToken);
        var secondLoad = loader.LoadAsync("provider-b", TestContext.Current.CancellationToken);
        secondCompletion.SetResult(Result("model-b"));
        firstCompletion.SetResult(Result("model-a"));

        var firstResult = await firstLoad;
        var secondResult = await secondLoad;

        Assert.Equal("model-b", Assert.Single(secondResult!.Models).Id);
        Assert.Null(firstResult);
    }

    [Fact]
    public async Task ModelBindingEditorState_SynchronousCatalogReadDoesNotBlockDispatcher()
    {
        using var dispatcher = new DedicatedPresentationDispatcher();
        using var releaseCatalog = new ManualResetEventSlim();
        var catalogStarted = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var catalog = new ProviderModelCatalogAdapter(
            () => [new ProviderCatalogOption("provider", "Provider")],
            (_, _) =>
            {
                catalogStarted.TrySetResult(Environment.CurrentManagedThreadId);
                releaseCatalog.Wait();
                return Task.FromResult(Result("model"));
            });
        using var state = new ModelBindingEditorState(
            new ProviderModelLoader(catalog),
            Options(),
            dispatcher);
        var notificationThreads = new List<int>();
        state.PropertyChanged += (_, _) => notificationThreads.Add(Environment.CurrentManagedThreadId);
        state.Models.CollectionChanged += (_, _) => notificationThreads.Add(Environment.CurrentManagedThreadId);
        Task refresh = Task.CompletedTask;

        var launch = dispatcher.InvokeAsync(() => refresh = state.RefreshAsync(
            new ModelBindingSelection("provider", "model"),
            TestContext.Current.CancellationToken));
        var catalogThread = await catalogStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        var heartbeatThread = 0;
        try
        {
            await dispatcher.InvokeAsync(() => heartbeatThread = Environment.CurrentManagedThreadId)
                .WaitAsync(TestContext.Current.CancellationToken);
        }
        finally
        {
            releaseCatalog.Set();
        }

        await launch;
        await refresh;

        Assert.Equal(dispatcher.ThreadId, heartbeatThread);
        Assert.NotEqual(dispatcher.ThreadId, catalogThread);
        Assert.Equal("model", state.SelectedModel?.Id);
        Assert.NotEmpty(notificationThreads);
        Assert.All(notificationThreads, threadId => Assert.Equal(dispatcher.ThreadId, threadId));
    }

    [Fact]
    public async Task ProviderModelLoader_BlockedFirstLoadDoesNotDelayLatestReplacement()
    {
        using var catalog = new NonCooperativeCatalog(blockedInvocations: 1);
        using var loader = new ProviderModelLoader(catalog);
        var firstLoad = loader.LoadAsync("provider-a", TestContext.Current.CancellationToken);
        await catalog.WaitForStartAsync(0, TestContext.Current.CancellationToken);

        var secondResult = await loader.LoadAsync("provider-b", TestContext.Current.CancellationToken);

        Assert.False(firstLoad.IsCompleted);
        Assert.Equal("provider-b", Assert.Single(secondResult!.Models).Id);
        Assert.Equal(2, catalog.InvocationCount);
        Assert.Equal(2, catalog.MaximumConcurrency);

        catalog.Release(0);
        Assert.Null(await firstLoad);
    }

    [Fact]
    public async Task ProviderModelLoader_RepeatedRefreshesStayBoundedAndSkipCanceledQueue()
    {
        using var catalog = new NonCooperativeCatalog(blockedInvocations: 2);
        using var loader = new ProviderModelLoader(catalog);
        var firstLoad = loader.LoadAsync("provider-0", TestContext.Current.CancellationToken);
        await catalog.WaitForStartAsync(0, TestContext.Current.CancellationToken);
        var secondLoad = loader.LoadAsync("provider-1", TestContext.Current.CancellationToken);
        await catalog.WaitForStartAsync(1, TestContext.Current.CancellationToken);

        var laterLoads = Enumerable.Range(2, 19)
            .Select(index => loader.LoadAsync($"provider-{index}", TestContext.Current.CancellationToken))
            .ToArray();
        try
        {
            Assert.Equal(2, Volatile.Read(ref catalog.InvocationCount));
            Assert.Equal(2, Volatile.Read(ref catalog.MaximumConcurrency));
            catalog.Release(1);
            var latestResult = await laterLoads[^1];
            Assert.Equal("provider-20", Assert.Single(latestResult!.Models).Id);
            Assert.False(firstLoad.IsCompleted);
        }
        finally
        {
            catalog.Release(0);
            catalog.Release(1);
        }

        var firstResult = await firstLoad;
        var secondResult = await secondLoad;
        var laterResults = await Task.WhenAll(laterLoads);

        Assert.Null(firstResult);
        Assert.Null(secondResult);
        Assert.Equal(3, catalog.InvocationCount);
        Assert.Equal(2, catalog.MaximumConcurrency);
        Assert.All(laterResults[..^1], Assert.Null);
        Assert.Equal("provider-20", Assert.Single(laterResults[^1]!.Models).Id);
    }

    [Fact]
    public async Task ModelBindingEditorState_RoundTripsReasoningSpeedAndModeSettings()
    {
        using var dispatcher = new DedicatedPresentationDispatcher();
        var catalog = new ProviderModelCatalogAdapter(
            () => [new ProviderCatalogOption("provider", "Provider")],
            (_, _) => Task.FromResult(new ProviderModelCatalogResult(
            [
                new ProviderModelCatalogOption(
                    "model",
                    "Model",
                    Variants: [new AgentModelVariantDescriptor("high", "High")],
                    SpeedOptions: [new AgentModelSpeedOptionDescriptor("fast", "Fast")],
                    ModeOptions: [new AgentModelModeOptionDescriptor("pro", "Pro")]),
            ],
            "Ready")));
        using var state = new ModelBindingEditorState(
            new ProviderModelLoader(catalog),
            new ModelBindingEditorOptions(
                SelectFirstProvider: true,
                NoProvidersText: "None",
                NoProviderSelectedText: "None",
                LoadingText: "Loading",
                LoadFailurePrefix: "Provider"),
            dispatcher);
        var settingsJson = AgentChatModelSettingsJson.Serialize(new AgentChatModelSettings(
            ReasoningVariantId: "high",
            SpeedOptionId: "fast",
            ModeOptionId: "pro"));

        await state.RefreshAsync(
            new ModelBindingSelection("provider", "model", settingsJson),
            TestContext.Current.CancellationToken);
        var roundTripped = AgentChatModelSettingsJson.Parse(state.SettingsJson);

        Assert.Equal("high", state.SelectedReasoningOption?.VariantId);
        Assert.Equal("fast", state.SelectedSpeedOption?.SpeedOptionId);
        Assert.Equal("pro", state.SelectedModeOption?.ModeOptionId);
        Assert.Equal("high", roundTripped.ReasoningVariantId);
        Assert.Equal("fast", roundTripped.SpeedOptionId);
        Assert.Equal("pro", roundTripped.ModeOptionId);

        await state.RefreshAsync(state.Selection, TestContext.Current.CancellationToken);

        Assert.Equal("high", state.SelectedReasoningOption?.VariantId);
        Assert.Equal("fast", state.SelectedSpeedOption?.SpeedOptionId);
        Assert.Equal("pro", state.SelectedModeOption?.ModeOptionId);
    }

    [Fact]
    public async Task ModelBindingEditorState_AppliesAsyncResultsAndNotificationsOnDispatcher()
    {
        using var dispatcher = new DedicatedPresentationDispatcher();
        var completion = new TaskCompletionSource<ProviderModelCatalogResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var catalog = new ProviderModelCatalogAdapter(
            () => [new ProviderCatalogOption("provider", "Provider")],
            (_, _) => completion.Task);
        using var state = new ModelBindingEditorState(
            new ProviderModelLoader(catalog),
            Options(),
            dispatcher);
        var notificationThreads = new List<int>();
        state.PropertyChanged += (_, _) => notificationThreads.Add(Environment.CurrentManagedThreadId);
        state.Models.CollectionChanged += (_, _) => notificationThreads.Add(Environment.CurrentManagedThreadId);

        var refresh = state.RefreshAsync(
            new ModelBindingSelection("provider", "model"),
            TestContext.Current.CancellationToken);
        completion.SetResult(Result("model"));
        await refresh;

        Assert.NotEmpty(notificationThreads);
        Assert.All(notificationThreads, threadId => Assert.Equal(dispatcher.ThreadId, threadId));
        Assert.Equal("model", state.SelectedModel?.Id);
    }

    [Fact]
    public async Task ModelBindingEditorState_DisposalPreventsLateProviderCallbacks()
    {
        using var dispatcher = new DedicatedPresentationDispatcher();
        var completion = new TaskCompletionSource<ProviderModelCatalogResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var catalog = new ProviderModelCatalogAdapter(
            () => [new ProviderCatalogOption("provider", "Provider")],
            (_, _) => completion.Task);
        var state = new ModelBindingEditorState(
            new ProviderModelLoader(catalog),
            Options(),
            dispatcher);
        var notifications = 0;
        state.PropertyChanged += (_, _) => Interlocked.Increment(ref notifications);

        var refresh = state.RefreshAsync(
            new ModelBindingSelection("provider", "model"),
            TestContext.Current.CancellationToken);
        await dispatcher.WaitForIdleAsync();
        state.Dispose();
        var notificationsAtDisposal = Volatile.Read(ref notifications);
        completion.SetResult(Result("model"));
        await refresh;

        Assert.Equal(notificationsAtDisposal, Volatile.Read(ref notifications));
        Assert.Empty(state.Models);
    }

    [Fact]
    public void CapabilitySelectionState_RemovedAssignmentIsRestoredWhenCapabilityReturns()
    {
        var state = new CapabilitySelectionState();
        var definition = Definition("tool-a");
        var assignment = new AgentProfileSelectableCapabilityAssignmentRecord(
            "tool",
            "tool-a",
            "source");
        var changes = 0;
        state.Changed += () => changes++;
        state.Load([definition], [assignment]);

        state.Reconcile([]);

        Assert.Equal(assignment, Assert.Single(state.UnresolvedAssignments));
        Assert.Empty(state.Options);

        state.Reconcile([definition]);

        Assert.True(Assert.Single(state.Options).IsEnabled);
        Assert.Empty(state.UnresolvedAssignments);
        Assert.Equal(assignment, Assert.Single(state.Assignments));

        state.Options[0].IsEnabled = false;
        Assert.Equal(1, changes);
        Assert.Empty(state.Assignments);
    }

    [Fact]
    public void CapabilitySelectionState_PreservesMissingAndCurrentlyUnavailableAssignments()
    {
        var state = new CapabilitySelectionState();
        var unavailableDefinition = Definition("known") with { CanSelect = false };
        var known = new AgentProfileSelectableCapabilityAssignmentRecord("tool", "known", "source");
        var missing = new AgentProfileSelectableCapabilityAssignmentRecord("tool", "missing", "source");

        state.Load([unavailableDefinition], [known, missing]);

        Assert.False(Assert.Single(state.Options).IsEnabled);
        Assert.Equal([known, missing], state.UnresolvedAssignments);
        Assert.Equal([known, missing], state.Assignments);
    }

    private static ProviderModelCatalogResult Result(string modelId)
        => new([new ProviderModelCatalogOption(modelId, modelId)], "Ready");

    private static ModelBindingEditorOptions Options() => new(
        SelectFirstProvider: true,
        NoProvidersText: "None",
        NoProviderSelectedText: "None",
        LoadingText: "Loading",
        LoadFailurePrefix: "Provider");

    private static CapabilityOptionDefinition Definition(string capabilityId)
        => new(
            "tools",
            "tool",
            capabilityId,
            "source",
            capabilityId,
            Description: null,
            StatusText: string.Empty,
            CanSelect: true,
            new CapabilityGroupDefinition("tools", "Tools", null, 10));

    private sealed class ControlledCatalog(
        IReadOnlyDictionary<string, Task<ProviderModelCatalogResult>> loads)
        : IProviderModelCatalog
    {
        public IReadOnlyList<ProviderCatalogOption> ListProviders() =>
        [
            new ProviderCatalogOption("provider-a", "Provider A"),
            new ProviderCatalogOption("provider-b", "Provider B"),
        ];

        public Task<ProviderModelCatalogResult> LoadAsync(
            string providerId,
            CancellationToken cancellationToken) => loads[providerId];
    }

    private sealed class NonCooperativeCatalog : IProviderModelCatalog, IDisposable
    {
        private int _activeInvocations;
        private readonly TaskCompletionSource[] _started;
        private readonly ManualResetEventSlim[] _releases;

        public NonCooperativeCatalog(int blockedInvocations)
        {
            _started = Enumerable.Range(0, blockedInvocations)
                .Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
                .ToArray();
            _releases = Enumerable.Range(0, blockedInvocations)
                .Select(_ => new ManualResetEventSlim())
                .ToArray();
        }

        public int InvocationCount;

        public int MaximumConcurrency;

        public IReadOnlyList<ProviderCatalogOption> ListProviders() => [];

        public Task WaitForStartAsync(int index, CancellationToken cancellationToken)
            => _started[index].Task.WaitAsync(cancellationToken);

        public void Release(int index) => _releases[index].Set();

        public Task<ProviderModelCatalogResult> LoadAsync(
            string providerId,
            CancellationToken cancellationToken)
        {
            var invocation = Interlocked.Increment(ref InvocationCount);
            var active = Interlocked.Increment(ref _activeInvocations);
            InterlockedExtensions.Max(ref MaximumConcurrency, active);
            try
            {
                if (invocation <= _started.Length)
                {
                    _started[invocation - 1].TrySetResult();
                    _releases[invocation - 1].Wait();
                }

                return Task.FromResult(Result(providerId));
            }
            finally
            {
                Interlocked.Decrement(ref _activeInvocations);
            }
        }

        public void Dispose()
        {
            foreach (var release in _releases)
            {
                release.Set();
                release.Dispose();
            }
        }
    }

    private static class InterlockedExtensions
    {
        public static void Max(ref int target, int value)
        {
            var current = Volatile.Read(ref target);
            while (current < value)
            {
                var observed = Interlocked.CompareExchange(ref target, value, current);
                if (observed == current)
                {
                    return;
                }

                current = observed;
            }
        }
    }
}
