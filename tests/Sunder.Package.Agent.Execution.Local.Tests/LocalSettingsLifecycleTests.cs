extern alias LocalApp;

using LocalApp::Sunder.Package.Agent.Execution.Local;
using Sunder.Package.Agent.TestSupport.Avalonia;
using Sunder.Sdk.Abstractions;
using Xunit;
using LocalPresentationService = LocalApp::Sunder.Generated.Packages.Younics_Ai_Agent_Execution_Local_Services.IPresentationService;

namespace Sunder.Package.Agent.Execution.Local.Tests;

public sealed class LocalSettingsLifecycleTests
{
    [Fact]
    public async Task DelayedInitialization_DisablesMutationsUntilRequiredSnapshotArrives()
    {
        var runtime = new BlockingLocalRuntimeClient();
        using var viewModel = new LocalExecutionSettingsViewModel(runtime);

        var first = viewModel.InitializeAsync();
        var second = viewModel.InitializeAsync();
        await runtime.Requests[0].Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var adapter = Adapt(viewModel);
        AdaptiveEditorLifecycleContract.SharedInitialization(adapter, first, second);
        AdaptiveEditorLifecycleContract.HydrationBlocksMutation(adapter);
        Assert.Same(first, second);
        Assert.True(viewModel.IsLoading);
        Assert.False(viewModel.AddShellCommand.CanExecute(null));
        viewModel.AddShellCommand.Execute(null);
        Assert.Empty(viewModel.CustomShells);
        Assert.False(runtime.Requests[1].Started.Task.IsCompleted);

        runtime.Requests[0].Response.TrySetResult(SettingsResponse(
            revision: 4,
            custom: [Shell("existing", "Existing", "/tmp/existing")]));
        await Task.WhenAll(first, second);

        Assert.True(viewModel.IsReady);
        Assert.True(viewModel.AddShellCommand.CanExecute(null));
        Assert.Equal("existing", Assert.Single(viewModel.CustomShells).ShellId);
    }

    [Fact]
    public async Task AddSaveDelete_UsesRevisionedCustomSnapshotAndPreservesNewerEdit()
    {
        var runtime = new BlockingLocalRuntimeClient();
        using var viewModel = new LocalExecutionSettingsViewModel(runtime);
        var initialization = viewModel.InitializeAsync();
        await runtime.Requests[0].Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        runtime.Requests[0].Response.TrySetResult(SettingsResponse(
            revision: 7,
            custom: [Shell("existing", "Existing", "/tmp/existing")]));
        await initialization;

        viewModel.AddShellCommand.Execute(null);
        var added = Assert.IsType<LocalShellRowViewModel>(viewModel.SelectedShell);
        added.DisplayName = "Sent name";
        Assert.True(added.ApplySelectedExecutablePath("/tmp/new-shell"));
        var save = viewModel.SaveShellsCommand.ExecuteAsync(null);
        await runtime.Requests[1].Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(7, runtime.Requests[1].ExpectedShellCatalogRevision);
        Assert.Equal(2, runtime.Requests[1].Shells?.Count);
        added.DisplayName = "Newer draft name";
        runtime.Requests[1].Response.TrySetResult(SettingsResponse(
            revision: 8,
            custom:
            [
                Shell("existing", "Existing", "/tmp/existing"),
                Shell(added.ShellId, "Sent name", "/tmp/new-shell"),
            ],
            message: "Shell settings saved."));
        await save;

        AdaptiveEditorLifecycleContract.EditDuringSaveRemainsDirty(
            Adapt(
                viewModel,
                isDirty: () => added.DisplayName == "Newer draft name",
                draftValue: () => added.DisplayName),
            "Newer draft name");
        Assert.Same(added, viewModel.CustomShells.Single(shell => shell.ShellId == added.ShellId));
        Assert.Equal("Newer draft name", added.DisplayName);
        Assert.Equal(added.ShellId, viewModel.SelectedShell?.ShellId);

        var delete = viewModel.DeleteSelectedShellCommand.ExecuteAsync(null);
        await runtime.Requests[2].Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(8, runtime.Requests[2].ExpectedShellCatalogRevision);
        Assert.DoesNotContain(
            runtime.Requests[2].Shells ?? [],
            shell => shell.ShellId == added.ShellId);
        runtime.Requests[2].Response.TrySetResult(SettingsResponse(
            revision: 9,
            custom: [Shell("existing", "Existing", "/tmp/existing")]));
        await delete;

        Assert.Equal("existing", Assert.Single(viewModel.CustomShells).ShellId);
        Assert.Equal("existing", viewModel.SelectedShell?.ShellId);
    }

    [Fact]
    public async Task NavigationRefresh_ReconcilesByShellIdAndPreservesDraftSelectionAndFieldRevisions()
    {
        var runtime = new BlockingLocalRuntimeClient();
        using var viewModel = new LocalExecutionSettingsViewModel(runtime);
        var initialization = viewModel.InitializeAsync();
        await runtime.Requests[0].Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        runtime.Requests[0].Response.TrySetResult(SettingsResponse(
            revision: 1,
            custom: [Shell("alpha", "Alpha", "/tmp/alpha")]));
        await initialization;
        var alpha = Assert.Single(viewModel.CustomShells);

        var refresh = viewModel.PrepareNavigationAsync(new PackageViewNavigationContext(
            "settings:local",
            new Dictionary<string, string?>())).AsTask();
        await runtime.Requests[1].Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        alpha.DisplayName = "Locally edited Alpha";
        viewModel.TimeoutSeconds = "777";
        viewModel.AddShellCommand.Execute(null);
        var draft = Assert.IsType<LocalShellRowViewModel>(viewModel.SelectedShell);
        runtime.Requests[1].Response.TrySetResult(SettingsResponse(
            timeout: "123",
            revision: 2,
            custom:
            [
                Shell("alpha", "Stale Alpha", "/tmp/alpha"),
                Shell("beta", "Beta", "/tmp/beta"),
            ]));
        await refresh;

        AdaptiveEditorLifecycleContract.CurrentSelectionWins(Adapt(viewModel), draft.ShellId);
        Assert.Same(alpha, viewModel.CustomShells.Single(shell => shell.ShellId == "alpha"));
        Assert.Equal("Locally edited Alpha", alpha.DisplayName);
        Assert.Contains(viewModel.CustomShells, shell => shell.ShellId == "beta");
        Assert.Contains(viewModel.CustomShells, shell => ReferenceEquals(shell, draft));
        Assert.Same(draft, viewModel.SelectedShell);
        Assert.Equal("777", viewModel.TimeoutSeconds);
    }

    [Fact]
    public async Task MissingRequiredFields_AreRetryableProtocolError()
    {
        var runtime = new BlockingLocalRuntimeClient();
        using var viewModel = new LocalExecutionSettingsViewModel(runtime);
        await AdaptiveEditorLifecycleContract.RuntimeCycleIsExplicitAsync(Adapt(viewModel), async () =>
        {
            var initialization = viewModel.InitializeAsync();
            await runtime.Requests[0].Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            runtime.Requests[0].Response.TrySetResult(new LocalExecutionOperationResponse(
                TimeoutSeconds: "300"));
            await initialization;

            Assert.True(viewModel.IsError);
            Assert.Equal("local.protocol.invalid-response", viewModel.RuntimeErrorCode);
            Assert.False(viewModel.AddShellCommand.CanExecute(null));
            Assert.True(viewModel.RetryInitializationCommand.CanExecute(null));

            var retry = viewModel.RetryInitializationCommand.ExecuteAsync(null);
            await runtime.Requests[1].Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            runtime.Requests[1].Response.TrySetResult(SettingsResponse(
                revision: 1,
                custom: [Shell("recovered", "Recovered", "/tmp/recovered")]));
            await retry;
        });
        Assert.True(viewModel.IsReady);
    }

    [Fact]
    public async Task Dispose_CancelsHydrationAndPreventsLateMutation()
    {
        var runtime = new BlockingLocalRuntimeClient();
        var viewModel = new LocalExecutionSettingsViewModel(runtime);
        var initialization = viewModel.InitializeAsync();
        await runtime.Requests[0].Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var notifications = 0;
        viewModel.PropertyChanged += (_, _) => Interlocked.Increment(ref notifications);

        var lateResponse = SettingsResponse(
            revision: 99,
            custom: [Shell("late", "Late", "/tmp/late")]);
        await AdaptiveEditorLifecycleContract.RetiredWorkCannotPublishAsync(
            Adapt(viewModel, publicationCount: () => Volatile.Read(ref notifications)),
            viewModel.Dispose,
            () => runtime.Requests[0].Response.TrySetResult(lateResponse),
            initialization.WaitAsync(TimeSpan.FromSeconds(2)));

        Assert.Empty(viewModel.CustomShells);
    }

    private static IAdaptiveEditorLifecycleAdapter Adapt(
        LocalExecutionSettingsViewModel viewModel,
        Func<bool>? isDirty = null,
        Func<string?>? draftValue = null,
        Func<int>? publicationCount = null)
        => new DelegateAdaptiveEditorLifecycleAdapter(
            "Local",
            () => new AdaptiveEditorSnapshot(
                IsHydrating: !viewModel.IsDisposed && viewModel.IsLoading,
                CanMutate: !viewModel.IsDisposed && viewModel.CanMutate,
                IsBusy: !viewModel.IsDisposed && viewModel.IsBusy,
                IsDirty: isDirty?.Invoke() ?? false,
                viewModel.SelectedShell?.ShellId,
                draftValue?.Invoke() ?? viewModel.TimeoutSeconds,
                publicationCount?.Invoke() ?? 0,
                viewModel.IsError
                    ? AdaptiveEditorRuntimeState.Unavailable
                    : viewModel.IsReady
                        ? AdaptiveEditorRuntimeState.Available
                        : AdaptiveEditorRuntimeState.Unknown),
            viewModel);

    private static LocalExecutionOperationResponse SettingsResponse(
        long revision,
        IReadOnlyList<LocalShellDefinition>? custom = null,
        string timeout = "300",
        string? message = null)
        => new(
            TimeoutSeconds: timeout,
            DetectedShells:
            [
                new LocalShellDefinition(
                    "detected",
                    "Detected",
                    "/bin/sh",
                    "posix-sh",
                    IsDetected: true),
            ],
            CustomShells: custom ?? [],
            ShellCatalogRevision: revision,
            Message: message);

    private static LocalShellDefinition Shell(string id, string name, string path)
        => new(id, name, path, "custom", IsDetected: false);

    private sealed class BlockingLocalRuntimeClient : LocalPresentationService
    {
        private int _requestIndex;

        public PendingRequest[] Requests { get; } =
            Enumerable.Range(0, 12).Select(_ => new PendingRequest()).ToArray();

        public ValueTask<LocalExecutionOperationResponse> GetSettingsAsync(CancellationToken cancellationToken = default)
            => ExecuteAsync(cancellationToken);

        public ValueTask<LocalExecutionOperationResponse> SaveSettingsAsync(
            string? timeoutSeconds,
            CancellationToken cancellationToken = default)
            => ExecuteAsync(cancellationToken);

        public async ValueTask<LocalExecutionOperationResponse> SaveShellsAsync(
            IReadOnlyList<LocalShellDefinition>? shells,
            long? expectedShellCatalogRevision,
            CancellationToken cancellationToken = default)
        {
            var pending = Requests[Interlocked.Increment(ref _requestIndex) - 1];
            pending.Shells = shells;
            pending.ExpectedShellCatalogRevision = expectedShellCatalogRevision;
            pending.Started.TrySetResult();
            return await pending.Response.Task.WaitAsync(cancellationToken);
        }

        private async ValueTask<LocalExecutionOperationResponse> ExecuteAsync(CancellationToken cancellationToken)
        {
            var pending = Requests[Interlocked.Increment(ref _requestIndex) - 1];
            pending.Started.TrySetResult();
            return await pending.Response.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class PendingRequest
    {
        public IReadOnlyList<LocalShellDefinition>? Shells { get; set; }

        public long? ExpectedShellCatalogRevision { get; set; }

        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<LocalExecutionOperationResponse> Response { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
