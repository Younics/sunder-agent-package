extern alias BuilderApp;

using System.Collections.Concurrent;
using BuilderApp::Sunder.Package.Agent.Builder;
using Sunder.Sdk.Abstractions;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class BuilderViewModelTests
{
    [Fact]
    public async Task PackageLogNavigationUsesInspectedIdentityNotMakerIdOrUnsavedIdentityDraft()
    {
        await using var fixture = new BuilderUiFixture();
        var row = await fixture.OpenAsync("logs");
        row.EditedPackageId = "local.unsaved";
        await fixture.Model.OpenPackageLogsAsync();
        Assert.Equal(new[] { "local.logs" }, fixture.Development.OpenedPackageLogs);
        var current = row.Inspection! with { PackageId = "local.current", SourceRevision = "external" };
        row.ApplyInspection(current);
        await fixture.Model.OpenPackageLogsAsync();
        Assert.Equal(new[] { "local.logs", "local.current" }, fixture.Development.OpenedPackageLogs);
        Assert.Equal("local.unsaved", row.EditedPackageId);
        Assert.Empty(fixture.Backend.Starts);
    }

    [Fact]
    public void ProjectNoticesCanBeDismissedWithoutErasingFaultsOrDraftsAndUpgradeRequiresCheckedEvidence()
    {
        var project = BuilderUiFixture.Project("notices");
        var row = new BuilderProjectViewModel(BuilderUiFixture.Record(project));
        row.ApplyInspection(project);
        row.EditedDisplayName = "Draft";
        row.SetError("SDK selection excludes a version; unrelated errors must not fabricate upgrade evidence.");
        Assert.False(row.ShowUpgradeToolchain);
        Assert.False(row.HasToolchainNotice);
        Assert.True(row.ShowAttentionNotice);
        row.DismissAttentionNotice();
        Assert.False(row.ShowAttentionNotice);
        Assert.True(row.HasError);
        Assert.Equal("Draft", row.EditedDisplayName);
        row.ApplyInspection(project with { ToolchainAssessment = new(true, true, "Explicit upgrade available") });
        Assert.True(row.ShowUpgradeToolchain);
        Assert.True(row.CanUpgradeToolchain);
        row.ApplyInspection(project with { ToolchainAssessment = new(true, false, "Update inherited pin manually") });
        Assert.False(row.ShowUpgradeToolchain);
        Assert.True(row.HasToolchainNotice);
        row.ApplyInspection(project with { ToolchainAssessment = new(false, false, null) });
        Assert.False(row.HasToolchainNotice);
        row.BeginOperation("build");
        row.SetError("New failure");
        Assert.True(row.ShowAttentionNotice);
    }

    [Fact]
    public void AppliedOutputObservationIsIndependentOfBuildSuccessAndUnrelatedGenerationChanges()
    {
        var row = new BuilderProjectViewModel(BuilderUiFixture.Record(BuilderUiFixture.Project("applied")));
        row.SetSession(true);
        var revision = new PackageDevelopmentRevision(Guid.NewGuid(), 4, new string('a', 64));
        var status = new PackageDevelopmentStatus(row.PackageId, true, true, PackageDevelopmentRuntimeReadiness.Ready, null, false)
            { HostState = PackageDevelopmentHostState.Ready, AppReadiness = PackageDevelopmentAppReadiness.Pending, RuntimeRevision = revision };
        row.ApplySnapshot(new() { Id = "build", Operation = "build", State = "succeeded", Message = "built", Output = "output",
            Phase = BuilderJobPhase.BuildReady, LastSuccessfulBuildAtUtc = DateTimeOffset.UtcNow });
        row.SetDevelopmentStatus(status);
        Assert.True(row.HasBuildActivity);
        Assert.False(row.HasAppliedActivity);
        Assert.Null(row.LastAppliedObservedAtUtc);
        Assert.Equal("Runtime loaded · UI pending", row.LoadStateText);
        Assert.Equal("Activating package UI…", row.ActivityText);
        row.SetDevelopmentStatus(status with { AppReadiness = PackageDevelopmentAppReadiness.Activated, AppliedRevision = revision });
        Assert.Equal(revision, row.LastAppliedRevision);
        var observed = row.LastAppliedObservedAtUtc;
        Assert.NotNull(observed);
        var nextGeneration = new PackageDevelopmentRevision(revision.RuntimeInstanceId, 5, revision.SourceContentIdentity);
        row.SetDevelopmentStatus(status with { AppReadiness = PackageDevelopmentAppReadiness.Activated, RuntimeRevision = nextGeneration,
            AppliedRevision = nextGeneration });
        Assert.Equal(observed, row.LastAppliedObservedAtUtc); // Another package's generation cannot manufacture a reload event.
        row.SetError("new compile failed");
        row.SetDevelopmentStatus(status with { AppReadiness = PackageDevelopmentAppReadiness.Activated, AppliedRevision = revision });
        Assert.True(row.HasError); // A retained applied output is not a new successful build.
        row.SetDevelopmentStatus(status with { RuntimeRevision = new(revision.RuntimeInstanceId, 6, new string('b', 64)) });
        Assert.Equal(revision.SourceContentIdentity, row.LastAppliedRevision!.SourceContentIdentity);
        Assert.Equal(observed, row.LastAppliedObservedAtUtc);
        Assert.Contains("not yet confirmed", row.ApplicationStatusText);
    }

    [Fact]
    public void DelayedUiActivationRemainsPendingAndResetsOnlyForNewSourceOrReadiness()
    {
        var time = new Sunder.Package.Agent.TestSupport.ManualTimeProvider();
        var row = new BuilderProjectViewModel(BuilderUiFixture.Record(BuilderUiFixture.Project("node-pending", BuilderProjectKind.Node)), time);
        row.SetSession(true);
        var revision = new PackageDevelopmentRevision(Guid.NewGuid(), 1, new string('a', 64));
        var status = new PackageDevelopmentStatus(row.PackageId, true, false, PackageDevelopmentRuntimeReadiness.Ready, null, false)
        { HostState = PackageDevelopmentHostState.Ready, AppReadiness = PackageDevelopmentAppReadiness.Pending, RuntimeRevision = revision };
        row.SetDevelopmentStatus(status);
        time.Advance(TimeSpan.FromSeconds(16));
        row.SetDevelopmentStatus(status with { RuntimeRevision = new(revision.RuntimeInstanceId, 2, revision.SourceContentIdentity) });
        Assert.Contains("taking longer", row.ActivityText);
        Assert.Contains("package logs", row.ActivityText);
        Assert.Equal("Runtime loaded · UI pending", row.LoadStateText);
        Assert.False(row.HasAppliedActivity);
        Assert.Null(row.LastAppliedRevision);
        row.SetDevelopmentStatus(status with { RuntimeRevision = new(revision.RuntimeInstanceId, 3, new string('b', 64)) });
        Assert.Equal("Activating package UI…", row.ActivityText);
        row.SetDevelopmentStatus(status with { AppReadiness = PackageDevelopmentAppReadiness.Quarantined, ErrorMessage = "view registration failed" });
        Assert.Equal("Runtime loaded · UI failed", row.LoadStateText);
        Assert.Contains("activation failed", row.ActivityText);
        Assert.Equal("view registration failed", row.RuntimeError);
        row.SetDevelopmentStatus(status with { AppReadiness = PackageDevelopmentAppReadiness.Unavailable });
        Assert.Equal("Runtime loaded · UI unavailable", row.LoadStateText);
        row.SetDevelopmentStatus(status with { AppReadiness = PackageDevelopmentAppReadiness.NotApplicable, AppliedRevision = revision });
        Assert.Equal("Loaded (Runtime only)", row.LoadStateText);
        Assert.Contains("no App UI is required", row.ApplicationStatusText);
        Assert.DoesNotContain("Activating", row.ActivityText);
        Assert.Equal(revision, row.LastAppliedRevision);
    }

    [Fact]
    public void ProjectStatusSeparatesBuildEvidenceFromRuntimeAndAppApplication()
    {
        var row = new BuilderProjectViewModel(BuilderUiFixture.Record(BuilderUiFixture.Project("status")) with { SetupIncomplete = true });
        Assert.Equal("Unloaded", row.LoadStateText);
        Assert.False(row.IsBuildRunning);
        Assert.False(row.HasBuildActivity);
        Assert.False(row.IsOutputExpanded);

        row.SetSession(true);
        Assert.Equal("Connecting…", row.LoadStateText);
        row.SetDevelopmentStatus(new("local.status", false, true, (PackageDevelopmentRuntimeReadiness)999, null, false)
            { HostState = (PackageDevelopmentHostState)999, AppReadiness = (PackageDevelopmentAppReadiness)999 });
        Assert.Equal("Host: Unknown · Runtime: Unknown · App: Unknown", row.RuntimeStatusText);
        Assert.Equal("Connecting…", row.LoadStateText);
        Assert.False(row.HasAppliedActivity);
        row.SetDevelopmentStatus(new("local.status", true, true, PackageDevelopmentRuntimeReadiness.Ready, null, false)
            { HostState = PackageDevelopmentHostState.Ready, RequestedWatch = true, AppReadiness = PackageDevelopmentAppReadiness.Activated });
        Assert.Equal("Loaded", row.LoadStateText);
        row.BeginOperation("build");
        row.IsBusy = true;

        var changed = DateTimeOffset.Parse("2026-10-20T10:00:00Z");
        var built = changed.AddSeconds(5);
        var snapshot = new BuilderJobSnapshot { Id = "build", Operation = "build", State = "running", Message = "building",
            Output = "compiler output", Phase = BuilderJobPhase.Building, LastSourceChangeAtUtc = changed };
        row.ApplySnapshot(snapshot);
        Assert.True(row.IsBuildRunning);
        Assert.Equal("Loaded", row.LoadStateText); // Building does not unload the last good activation.
        Assert.Equal(changed, row.LastSourceChangeAtUtc);
        Assert.Null(row.LastSuccessfulBuildAtUtc); // Observing a save cannot claim a successful build or reload.

        row.ApplySnapshot(snapshot with { State = "failed", Phase = BuilderJobPhase.BuildFailed, DevelopmentFolder = "/historical",
            LastSuccessfulBuildAtUtc = built.AddSeconds(-1) });
        Assert.True(row.SetupIncomplete); // A terminal failed attempt cannot qualify setup using historical output.
        row.ApplySnapshot(snapshot with { State = "succeeded", Phase = BuilderJobPhase.BuildReady, DevelopmentFolder = "/validated", LastSuccessfulBuildAtUtc = built });
        Assert.False(row.IsBuildRunning);
        Assert.False(row.HasError);
        Assert.Equal(built, row.LastSuccessfulBuildAtUtc);
        Assert.False(row.SetupIncomplete);
        Assert.False(row.HasAppliedActivity); // Valid build output is not Runtime/App application evidence.
        row.SetError("new compile failed");
        row.ApplySnapshot(snapshot with { Phase = BuilderJobPhase.Building, LastSourceChangeAtUtc = changed.AddSeconds(-1),
            DevelopmentFolder = "/validated", LastSuccessfulBuildAtUtc = built });
        Assert.True(row.HasError); // Repeated last-good output/timestamp cannot clear the later failure.
        Assert.Equal(changed, row.LastSourceChangeAtUtc);

        row.IsBusy = false;
        row.SetSession(false);
        Assert.Equal("Unloaded", row.LoadStateText);
        Assert.False(row.IsBuildRunning);
    }

    [Fact]
    public void FiniteBuildFailureKeepsDiagnosticsUntilTheNextExplicitOperation()
    {
        var row = new BuilderProjectViewModel(BuilderUiFixture.Record(BuilderUiFixture.Project("node-status", BuilderProjectKind.Node)));
        row.SetSession(true);
        var ready = new BuilderJobSnapshot { Id = "build", Operation = "build", State = "succeeded", Message = "built", Output = "output",
            Phase = BuilderJobPhase.BuildReady, LastSuccessfulBuildAtUtc = DateTimeOffset.UtcNow };
        row.ApplySnapshot(ready);
        row.ApplySnapshot(ready with { State = "failed", Phase = BuilderJobPhase.BuildFailed, Error = "Compilation failed" });
        Assert.True(row.HasError);
        Assert.False(row.IsBuildRunning);
        Assert.True(row.IsOutputExpanded);
        row.IsOutputExpanded = false;
        row.ApplySnapshot(ready with { State = "failed", Phase = BuilderJobPhase.BuildFailed, Error = "Compilation failed" });
        Assert.True(row.IsOutputExpanded);
        Assert.True(row.HasError);
        row.ApplySnapshot(ready with { LastSuccessfulBuildAtUtc = ready.LastSuccessfulBuildAtUtc!.Value.AddSeconds(1) });
        Assert.False(row.HasError);
    }

    [Theory]
    [InlineData(BuilderJobPhase.Restoring, "Restoring dependencies…")]
    [InlineData(BuilderJobPhase.Building, "Building package…")]
    [InlineData(BuilderJobPhase.Validating, "Validating package output…")]
    [InlineData(BuilderJobPhase.Packaging, "Checking contracts and creating archive…")]
    public void ProjectStatusUsesTypedBuildPhaseAndKeepsFailureOutputDiscoverable(BuilderJobPhase phase, string activity)
    {
        var row = new BuilderProjectViewModel(BuilderUiFixture.Record(BuilderUiFixture.Project("phase")));
        row.BeginOperation("build");
        row.IsBusy = true;
        row.ApplySnapshot(new() { Id = "build", Operation = "build", State = "running", Message = "not parsed for state", Output = "output", Phase = phase });
        Assert.Equal("Building…", row.StateText);
        Assert.Equal(activity, row.ActivityText);
        Assert.True(row.IsBuildRunning);
        Assert.Equal("Unloaded", row.LoadStateText);
        Assert.False(row.IsOutputExpanded);
        row.ApplySnapshot(new() { Id = "build", Operation = "build", State = "failed", Message = "failed", Error = "compile failure", Output = "compiler details", Phase = phase });
        row.IsBusy = false;
        Assert.False(row.IsBuildRunning);
        Assert.True(row.IsOutputExpanded);
        Assert.Equal("Needs attention", row.StateText);
        row.IsOutputExpanded = false;
        row.SetStatus("unrelated readiness observation");
        Assert.False(row.IsOutputExpanded); // Normal property refresh must not override the user's expansion choice.
        row.BeginOperation("setup");
        Assert.Empty(row.Output);
        row.SetError(new InvalidOperationException("The new attempt failed before dispatch.", new IOException("token=private-value")));
        Assert.DoesNotContain("compiler details", row.Output, StringComparison.Ordinal);
        Assert.Contains("[setup] App operation failed:", row.Output, StringComparison.Ordinal);
        Assert.Contains(nameof(InvalidOperationException), row.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("private-value", row.Output, StringComparison.Ordinal);
        Assert.Contains("token=[redacted]", row.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("compile failure", row.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal("The new attempt failed before dispatch.", row.ErrorMessage);
    }

    [Fact]
    public async Task ConstructionIsCold_ConcurrentInitializationSharesLoadAndCallerCancellationDoesNotCancelOwner()
    {
        var started = BuilderUiFixture.Signal();
        var release = BuilderUiFixture.Signal();
        var store = new BuilderUiStore { Load = async token => { started.TrySetResult(); await release.Task.WaitAsync(token); return []; } };
        await using var fixture = new BuilderUiFixture(store: store);
        Assert.Equal(0, store.LoadCount);
        Assert.Empty(fixture.Backend.Starts);
        var first = fixture.Model.InitializeAsync();
        var second = fixture.Model.InitializeAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Same(first, second);
        using var cancellation = new CancellationTokenSource();
        var canceled = fixture.Model.InitializeAsync(cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
        release.TrySetResult();
        await Task.WhenAll(first, second);
        Assert.Equal(1, store.LoadCount);
    }

    [Fact]
    public async Task CreateOpensEphemeralWizard_SuggestionsStopIndependently_ChosenPathAndSdkReachBackend()
    {
        await using var fixture = new BuilderUiFixture();
        await fixture.Model.InitializeAsync();
        await fixture.Model.CreateProjectAsync();
        Assert.Empty(fixture.Model.Projects);
        Assert.Empty(fixture.Store.Saves);
        Assert.Empty(fixture.Backend.Starts);
        await fixture.Model.SelectTechnologyAsync(BuilderProjectKind.Node);
        var creation = fixture.Model.Creation;
        Assert.True(creation.IsDetailsStep);
        creation.DisplayName = "First package";
        Assert.Equal("local.first.package", creation.PackageId);
        Assert.Equal("Firstpackage", creation.Subfolder);
        creation.PackageId = "example.fixed";
        creation.DisplayName = "Second package";
        Assert.Equal("example.fixed", creation.PackageId);
        Assert.Equal("Secondpackage", creation.Subfolder);
        creation.Subfolder = "chosen-folder";
        creation.DisplayName = "Final package";
        creation.ParentFolder = Path.GetTempPath();
        var expectedPath = creation.FullPath;
        await fixture.Model.CompleteCreationAsync();
        var request = Assert.Single(fixture.Backend.Starts, request => request.Operation == "create");
        Assert.Equal(expectedPath, request.ProjectFolder);
        Assert.Equal(BuilderProjectKind.Node, request.Kind);
        Assert.Equal("example.fixed", request.PackageId);
        Assert.Equal(creation.Installation, request.Installation);
        var row = Assert.Single(fixture.Model.Projects);
        Assert.Equal(creation.Installation, row.Installation);
        Assert.True(row.CanLoad);
        Assert.Empty(fixture.Development.Loads);
    }

    [Fact]
    public async Task FailedPreSourceCreationPreservesValues_PartialCancellationBecomesRetryableBookmark()
    {
        await using var fixture = new BuilderUiFixture();
        await fixture.Model.CreateProjectAsync();
        await fixture.Model.SelectTechnologyAsync(BuilderProjectKind.Dotnet);
        fixture.Model.Creation.DisplayName = "Preserved";
        fixture.Model.Creation.ParentFolder = Path.GetTempPath();
        fixture.Backend.Result = request => request.Operation == "create"
            ? BuilderUiBackend.Snapshot(request, "failed") with { Error = "Download unavailable" }
            : fixture.Backend.DefaultResult(request);
        await fixture.Model.CompleteCreationAsync();
        Assert.Empty(fixture.Model.Projects);
        Assert.Equal("Preserved", fixture.Model.Creation.DisplayName);
        Assert.True(fixture.Model.Creation.HasError);
        var source = BuilderUiFixture.Project("partial");
        fixture.Backend.Projects[source.EntryPoint] = source;
        fixture.Backend.Result = request => request.Operation == "create"
            ? BuilderUiBackend.Snapshot(request, "cancelled") with { Project = source, Installation = BuilderUiFixture.Sdk() }
            : fixture.Backend.DefaultResult(request);
        await fixture.Model.CompleteCreationAsync();
        var row = Assert.Single(fixture.Model.Projects);
        Assert.True(row.SetupIncomplete);
        await fixture.Model.RetrySelectedSetupAsync();
        Assert.False(row.SetupIncomplete);
        Assert.Equal("setup", fixture.Backend.Starts.Last().Operation);
        Assert.Empty(fixture.Development.Loads);
    }

    [Fact]
    public async Task CreationFailureShowsOneSummaryAndKeepsDiagnosticsSeparate_RetryPreservesPreparedSdk()
    {
        await using var fixture = new BuilderUiFixture();
        await fixture.Model.CreateProjectAsync();
        await fixture.Model.SelectTechnologyAsync(BuilderProjectKind.Dotnet);
        var creation = fixture.Model.Creation;
        var sdk = creation.Installation;
        creation.DisplayName = "Preserved name";
        creation.PackageId = "local.preserved";
        creation.ParentFolder = Path.GetTempPath();
        creation.Subfolder = "chosen-folder";
        var destination = creation.FullPath;
        const string summary = "dotnet new install failed (exit code 134). See the command output for details.";
        const string details = "Unhandled exception. System.ArgumentException\n   at Microsoft.TemplateEngine.Edge.Settings.AsyncMutex.WaitLoop(Object state)";
        fixture.Backend.Result = request => BuilderUiBackend.Snapshot(request, "failed") with { Message = summary, Error = summary, Output = details };
        await fixture.Model.CompleteCreationAsync();
        Assert.Equal(summary, creation.ErrorMessage);
        Assert.True(creation.HasError);
        Assert.False(creation.ShowMessage);
        Assert.Equal(details, creation.Output);
        Assert.True(creation.HasOutput);
        Assert.Equal(destination, creation.FullPath);
        Assert.Equal(sdk, creation.Installation);
        fixture.Backend.Result = null;
        await fixture.Model.CompleteCreationAsync();
        Assert.Single(fixture.Backend.Starts, request => request.Operation == "ensure-sdk");
        Assert.All(fixture.Backend.Starts.Where(request => request.Operation == "create"), request =>
        {
            Assert.Equal(destination, request.ProjectFolder);
            Assert.Equal(sdk, request.Installation);
        });
        Assert.Single(fixture.Model.Projects);
    }

    [Fact]
    public async Task OpenIsSourceOnly_MultipleCandidatesAreChosen_CanonicalEntryPointDeduplicatesNotPackageId()
    {
        await using var fixture = new BuilderUiFixture();
        var first = BuilderUiFixture.Project("one");
        var second = BuilderUiFixture.Project("two") with { PackageId = first.PackageId };
        fixture.Backend.Projects[first.EntryPoint] = first;
        fixture.Backend.Projects[second.EntryPoint] = second;
        fixture.Backend.Discovery = _ => [first, second];
        await fixture.Model.OpenFolderAsync(Path.GetTempPath());
        Assert.True(fixture.Model.ShowCandidateChooser);
        Assert.Empty(fixture.Model.Projects);
        fixture.Model.SelectedCandidate = second;
        await fixture.Model.OpenSelectedCandidateAsync();
        fixture.Backend.Discovery = _ => [first];
        await fixture.Model.OpenFolderAsync(first.ProjectFolder);
        await fixture.Model.OpenFolderAsync(first.ProjectFolder);
        Assert.Equal(2, fixture.Model.Projects.Count);
        Assert.Equal(first.EntryPoint, fixture.Model.SelectedProject?.EntryPoint);
        Assert.Empty(fixture.Backend.Starts);
        Assert.Equal(0, fixture.Backend.ToolchainChecks);
    }

    [Fact]
    public async Task MissingBookmarkRemainsLocatable_OpeningDoesNotRequireSdk()
    {
        var record = BuilderUiFixture.Record(BuilderUiFixture.Project("missing"));
        await using var fixture = new BuilderUiFixture(store: new BuilderUiStore { Records = [record] });
        await fixture.Model.InitializeAsync();
        var row = Assert.Single(fixture.Model.Projects);
        Assert.True(row.IsMissing);
        fixture.Model.ActivateProject(row);
        var found = BuilderUiFixture.Project("relocated", BuilderProjectKind.Node);
        fixture.Backend.Projects[found.EntryPoint] = found;
        await fixture.Model.LocateSelectedProjectAsync(found.ProjectFolder);
        Assert.Same(row, Assert.Single(fixture.Model.Projects));
        Assert.Equal(record.Id, row.Id);
        Assert.Equal(found.EntryPoint, row.EntryPoint);
        Assert.Equal(BuilderProjectKind.Node, row.Kind);
        Assert.False(row.IsMissing);
        Assert.Empty(fixture.Backend.Starts);
    }

    [Fact]
    public async Task ExplicitLoadAcquiresNonWatchingSession_NavigationAndIndependentBuildDoNotReleaseIt()
    {
        await using var fixture = new BuilderUiFixture();
        var row = await fixture.OpenAsync("loaded");
        await fixture.Model.LoadSelectedProjectAsync();
        var session = Assert.Single(fixture.Development.Sessions);
        Assert.Equal("setup", Assert.Single(fixture.Backend.Starts).Operation);
        Assert.Single(fixture.Queue.ListTasks());
        Assert.False(Assert.Single(fixture.Development.Loads).Watch);
        fixture.Model.BackToProjectList();
        fixture.Model.IsCompactLayout = true;
        // A loaded development owner is not a compiler or an SDK selection lease.
        var independent = BuilderUiFixture.Project("independent-node", BuilderProjectKind.Node);
        fixture.Backend.Projects[independent.EntryPoint] = independent;
        await fixture.Application.RunJobAsync(new() { Operation = "build", Kind = independent.Kind,
            ProjectFolder = independent.ProjectFolder, EntryPoint = independent.EntryPoint }).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(session.Disposed);
        var revision = new PackageDevelopmentRevision(Guid.NewGuid(), 4, new string('c', 64));
        session.Status = session.Status with { RuntimeRevision = revision, AppliedRevision = revision };
        await BuilderUiFixture.WaitAsync(() => row.LastAppliedRevision == revision);
        var starts = fixture.Backend.Starts.Count;
        session.BeforeGet = _ => Task.FromException(new IOException("Host observation disconnected"));
        await BuilderUiFixture.WaitAsync(() => row.RuntimeStatusText.Contains("Reconnecting", StringComparison.Ordinal));
        Assert.True(row.HasSession);
        Assert.False(session.Disposed);
        Assert.False(row.IsLoaded);
        Assert.Equal(revision, row.LastAppliedRevision); // History is not current applied evidence.
        Assert.DoesNotContain("match this output", row.ApplicationStatusText, StringComparison.Ordinal);
        Assert.Contains("Unknown", row.RuntimeStatusText, StringComparison.Ordinal);
        Assert.Equal(starts, fixture.Backend.Starts.Count);
        session.BeforeGet = null;
        await BuilderUiFixture.WaitAsync(() => row.IsLoaded && row.ApplicationStatusText.Contains("match this output", StringComparison.Ordinal));
        Assert.Single(fixture.Development.Loads);
        session.Status = session.Status with { AppReadiness = PackageDevelopmentAppReadiness.Quarantined,
            AppliedRevision = null, ErrorMessage = "App failed to activate" };
        await BuilderUiFixture.WaitAsync(() => row.RuntimeStatusText.Contains("Quarantined", StringComparison.Ordinal));
        Assert.Equal("App failed to activate", row.RuntimeError);
        Assert.Equal("Runtime loaded · UI failed", row.LoadStateText);
        Assert.Contains("activation failed", row.ActivityText);
        fixture.Model.ActivateProject(row);
        await fixture.Model.UnloadSelectedProjectAsync();
        Assert.True(session.Disposed);
        Assert.False(row.HasSession);
        Assert.Contains("Other owners", row.StatusText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FiniteLoadOwnsOneBuildUntilTerminalOutputAndDrainsItOnCancellation(bool cancel)
    {
        await using var fixture = new BuilderUiFixture();
        var row = await fixture.OpenAsync("pending-finite-load");
        row.ApplySnapshot(new() { Id = "old-setup", Operation = "setup", State = "failed", Message = "Setup incomplete", Output = string.Empty });
        fixture.Backend.Result = request => fixture.Backend.DefaultResult(request) with
            { State = "running", DevelopmentFolder = null, LastSuccessfulBuildAtUtc = null, Phase = BuilderJobPhase.Building };
        var loading = fixture.Model.LoadSelectedProjectAsync();
        await BuilderUiFixture.WaitAsync(() => fixture.Backend.Starts.Count == 1 && row.IsBuildRunning);
        var request = Assert.Single(fixture.Backend.Starts);
        Assert.Equal("setup", request.Operation);
        Assert.Empty(fixture.Development.Loads);
        Assert.False(loading.IsCompleted);
        if (cancel) fixture.Model.CancelCurrentOperation();
        else fixture.Backend.Jobs[request.Id!] = fixture.Backend.DefaultResult(request);
        await loading.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Single(fixture.Backend.Starts);
        Assert.Equal(cancel ? 0 : 1, fixture.Development.Loads.Count);
        Assert.Equal(!cancel, row.HasSession);
        if (cancel) Assert.Equal("cancelled", fixture.Backend.Jobs[request.Id!].State);
        Assert.Equal(cancel, row.SetupIncomplete);
        await fixture.DisposeAsync();
        Assert.Equal(cancel, Assert.Single(fixture.Store.Records).SetupIncomplete);
    }

    [Fact]
    public async Task FailedFiniteSnapshotCannotLoadHistoricalSuccessfulOutput()
    {
        await using var fixture = new BuilderUiFixture();
        var row = await fixture.OpenAsync("failed-finite-output");
        fixture.Backend.Result = request => fixture.Backend.DefaultResult(request) with { State = "failed", Error = "Build failed" };
        await fixture.Model.LoadSelectedProjectAsync().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Contains("Build failed", row.ErrorMessage, StringComparison.Ordinal);
        Assert.Empty(fixture.Development.Loads);
        Assert.False(row.HasSession);
        Assert.Single(fixture.Backend.Starts);
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("cancelled")]
    public async Task InterruptedFiniteBuildRetainsLastGoodDevelopmentSession(string outcome)
    {
        await using var fixture = new BuilderUiFixture();
        var row = await fixture.OpenAsync("retained-build");
        await fixture.Model.LoadSelectedProjectAsync();
        var session = Assert.Single(fixture.Development.Sessions);
        fixture.Backend.Result = request => fixture.Backend.DefaultResult(request) with { State = outcome, DevelopmentFolder = null,
            LastSuccessfulBuildAtUtc = null, Error = outcome == "failed" ? "Compilation failed" : null };
        await fixture.Model.BuildSelectedProjectAsync();
        Assert.True(row.HasSession);
        Assert.False(session.Disposed);
        Assert.False(session.Status.RequestedWatch);
        Assert.Single(fixture.Development.Loads);
        Assert.Equal(2, fixture.Backend.Starts.Count);
        fixture.Backend.Result = null;
        await fixture.Model.BuildSelectedProjectAsync();
        Assert.False(row.HasError);
        Assert.False(session.Disposed);
        Assert.Single(fixture.Development.Loads);
    }

    [Theory]
    [InlineData("build", BuilderProjectKind.Dotnet)]
    [InlineData("setup", BuilderProjectKind.Dotnet)]
    [InlineData("save-metadata", BuilderProjectKind.Dotnet)]
    [InlineData("build", BuilderProjectKind.Node)]
    public async Task BuildSetupAndNameEditsNeverReplaceAnAlreadyLoadedSession(string operation, BuilderProjectKind kind)
    {
        await using var fixture = new BuilderUiFixture();
        var unrelated = await fixture.OpenAsync("unrelated-reload");
        await fixture.Model.LoadSelectedProjectAsync();
        var unrelatedSession = Assert.Single(fixture.Development.Sessions);
        var row = await fixture.OpenAsync("manual-reload", kind);
        await fixture.Model.LoadSelectedProjectAsync();
        if (operation == "save-metadata") row.EditedDisplayName = "Updated source name";
        var validatedFolder = Path.Combine(row.ProjectFolder, "validated-current-output");
        fixture.Backend.Result = request => fixture.Backend.DefaultResult(request) with { DevelopmentFolder = validatedFolder };
        var old = fixture.Development.Sessions[1];
        var applied = new PackageDevelopmentRevision(Guid.NewGuid(), 4, new string('b', 64));
        old.Status = old.Status with { RuntimeRevision = applied, AppliedRevision = applied };
        fixture.Backend.BeforeGet = (request, token) =>
        {
            if (request.Operation is "build" or "setup")
            {
                var fresh = fixture.Backend.Projects[row.EntryPoint!] with { SourceRevision = "fresh-after-build" };
                fixture.Backend.Projects[row.EntryPoint!] = fresh;
                fixture.Backend.Jobs[request.Id!] = fixture.Backend.Jobs[request.Id!] with { Project = fresh };
            }
            return Task.CompletedTask;
        };
        var work = operation switch
        {
            "setup" => fixture.Model.RetrySelectedSetupAsync(),
            "save-metadata" => fixture.Model.SaveSelectedMetadataAsync(),
            _ => fixture.Model.BuildSelectedProjectAsync(),
        };
        await work.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(row.HasSession);
        Assert.False(row.HasError);
        Assert.False(old.Disposed);
        Assert.Equal(0, old.DisposeCount);
        Assert.Equal(2, fixture.Development.Sessions.Count);
        Assert.Equal(2, fixture.Development.Loads.Count);
        Assert.All(fixture.Development.Loads, load => Assert.False(load.Watch));
        Assert.DoesNotContain(fixture.Backend.Starts, request => request.Operation == "watch");
        Assert.True(unrelated.HasSession);
        Assert.False(unrelatedSession.Disposed);
        Assert.Equal(0, unrelatedSession.DisposeCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SuccessfulBuildWithoutValidatedOutputOrFreshInspectionRetainsSession(bool inspectionFails)
    {
        await using var fixture = new BuilderUiFixture();
        var row = await fixture.OpenAsync("invalid-reload");
        await fixture.Model.LoadSelectedProjectAsync();
        var original = Assert.Single(fixture.Development.Sessions);
        fixture.Backend.Result = request => fixture.Backend.DefaultResult(request) with { DevelopmentFolder = inspectionFails ? "validated-output" : null };
        fixture.Backend.BeforeGet = (request, token) =>
        {
            if (inspectionFails && request.Operation == "build") fixture.Backend.Projects.TryRemove(row.EntryPoint!, out _);
            return Task.CompletedTask;
        };
        await fixture.Model.BuildSelectedProjectAsync();
        Assert.True(row.HasError);
        Assert.Contains(inspectionFails ? "Source missing" : "validated development folder", row.ErrorMessage, StringComparison.Ordinal);
        Assert.True(row.HasSession);
        Assert.False(original.Disposed);
        Assert.Single(fixture.Development.Loads);
    }

    [Fact]
    public async Task FailedExplicitUnloadRetainsExactOwnerAndHistoricalRevisionUntilRetry()
    {
        await using var fixture = new BuilderUiFixture();
        var row = await fixture.OpenAsync("failed-retirement");
        await fixture.Model.LoadSelectedProjectAsync();
        var original = Assert.Single(fixture.Development.Sessions);
        var applied = new PackageDevelopmentRevision(Guid.NewGuid(), 7, new string('a', 64));
        original.Status = original.Status with { RuntimeRevision = applied, AppliedRevision = applied };
        row.SetDevelopmentStatus(original.Status);
        original.BeforeDispose = () => Task.FromException(new IOException("Staged retirement unavailable; retry Unload."));
        try
        {
            await fixture.Model.UnloadSelectedProjectAsync();
            Assert.True(row.HasSession);
            Assert.False(row.IsLoaded); // No monitor remains to attest the old positive readiness.
            Assert.Equal("Release unconfirmed", row.LoadStateText);
            Assert.Equal("Host: Unknown · Runtime: Unknown · App: Unknown", row.RuntimeStatusText);
            Assert.Contains("historical", row.ApplicationStatusText, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(applied, row.LastAppliedRevision);
            Assert.True(row.CanUnload);
            Assert.False(row.CanLoad);
            Assert.Contains("retirement unavailable", row.ErrorMessage, StringComparison.Ordinal);
            Assert.False(original.Disposed);
            Assert.Single(fixture.Development.Loads);
            Assert.False(original.Status.RequestedWatch);
            await fixture.Model.LoadSelectedProjectAsync();
            await fixture.Model.BuildSelectedProjectAsync();
            Assert.Contains("retry Unload", row.ErrorMessage, StringComparison.Ordinal);
            Assert.Single(fixture.Development.Loads);
            Assert.DoesNotContain(fixture.Backend.Starts, request => request.Operation == "build");
            Assert.Single(fixture.Backend.Starts);
            Assert.False(original.Status.RequestedWatch);
        }
        finally { original.BeforeDispose = null; }
        await fixture.Model.UnloadSelectedProjectAsync();
        Assert.True(original.Disposed);
        Assert.Equal(1, original.DisposeCount);
        Assert.False(row.HasSession);
        await fixture.Model.LoadSelectedProjectAsync();
        Assert.Equal(2, fixture.Development.Sessions.Count);
        Assert.True(row.HasSession);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitLoadFailureAfterAcknowledgedUnloadLeavesRowRetryable(bool cancelDuringLoad)
    {
        await using var fixture = new BuilderUiFixture();
        var row = await fixture.OpenAsync("failed-replacement");
        await fixture.Model.LoadSelectedProjectAsync();
        var original = Assert.Single(fixture.Development.Sessions);
        await fixture.Model.UnloadSelectedProjectAsync();
        if (cancelDuringLoad) fixture.Development.BeforeLoad = (_, _, token) =>
        { fixture.Model.CancelCurrentOperation(); token.ThrowIfCancellationRequested(); return Task.CompletedTask; };
        else fixture.Development.BeforeLoad = (_, _, _) => Task.FromException(new IOException("New output unavailable."));
        await fixture.Model.LoadSelectedProjectAsync();
        Assert.True(original.Disposed);
        Assert.False(row.HasSession);
        Assert.True(row.CanLoad);
        Assert.Single(fixture.Development.Sessions);
        if (cancelDuringLoad)
        {
            Assert.Equal(2, fixture.Development.Loads.Count);
            Assert.Contains("cancelled", row.StatusText, StringComparison.OrdinalIgnoreCase);
        }
        else Assert.Contains("Retry Load into Sunder", row.ErrorMessage, StringComparison.Ordinal);
        fixture.Development.BeforeLoad = null;
        await fixture.Model.LoadSelectedProjectAsync();
        Assert.Equal(2, fixture.Development.Sessions.Count);
        Assert.Equal(1, original.DisposeCount);
        Assert.True(row.HasSession);
    }

    [Fact]
    public async Task ArchiveRetainsLoadedOwnerWithoutRebuildingOrReloadingIt()
    {
        await using var fixture = new BuilderUiFixture();
        var row = await fixture.OpenAsync("archive-retained");
        await fixture.Model.LoadSelectedProjectAsync();
        var original = Assert.Single(fixture.Development.Sessions);
        await fixture.Model.CreatePackageArchiveAsync();
        Assert.True(row.HasSession);
        Assert.False(original.Disposed);
        Assert.False(original.Status.RequestedWatch);
        Assert.Single(fixture.Development.Loads);
        Assert.NotNull(fixture.Backend.Jobs.Values.Single(job => job.Operation == "archive").ArchivePath);
    }

    [Fact]
    public async Task UncertainFiniteBuildDoesNotReloadAndRequiresExactJobReconciliation()
    {
        await using var fixture = new BuilderUiFixture();
        var row = await fixture.OpenAsync("uncertain-loaded");
        await fixture.Model.LoadSelectedProjectAsync();
        var observed = BuilderUiFixture.Signal();
        var polls = 0;
        var connected = false;
        fixture.Backend.Result = request => fixture.Backend.DefaultResult(request) with { State = "running", Phase = BuilderJobPhase.Building,
            DevelopmentFolder = null, LastSuccessfulBuildAtUtc = null };
        fixture.Backend.BeforeGet = (request, _) =>
        {
            if (request.Operation != "build") return Task.CompletedTask;
            if (Interlocked.Increment(ref polls) == 1) { observed.TrySetResult(); return Task.CompletedTask; }
            return connected ? Task.CompletedTask : Task.FromException(new IOException("Transport disconnected"));
        };
        var build = fixture.Model.BuildSelectedProjectAsync();
        await observed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        fixture.Model.CancelCurrentOperation();
        await build.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(row.HasSession);
        Assert.False(Assert.Single(fixture.Development.Sessions).Status.RequestedWatch);
        Assert.Single(fixture.Backend.Starts, request => request.Operation == "build");
        connected = true;
        await fixture.Application.StopJobAsync(fixture.Backend.Starts.Single(request => request.Operation == "build").Id!);
        fixture.Backend.Result = null;
        await fixture.Model.BuildSelectedProjectAsync();
        Assert.Equal(2, fixture.Backend.Starts.Count(request => request.Operation == "build"));
        Assert.Single(fixture.Development.Loads);
        await fixture.Model.UnloadSelectedProjectAsync();
    }

    [Fact]
    public async Task LateBuildUpdatesCapturedRowOnDispatcher_SelectionAndOtherProjectOutputRemainUntouched()
    {
        await using var fixture = new BuilderUiFixture();
        var first = await fixture.OpenAsync("first");
        var second = await fixture.OpenAsync("second");
        var started = BuilderUiFixture.Signal();
        var release = BuilderUiFixture.Signal();
        fixture.Backend.BeforeGet = async (request, token) =>
        {
            if (request.Operation == "build") { started.TrySetResult(); await release.Task.WaitAsync(token); }
        };
        fixture.Backend.Result = request => fixture.Backend.DefaultResult(request) with { Output = new string('x', 200_000) };
        fixture.Model.ActivateProject(first);
        var offDispatcher = 0;
        first.PropertyChanged += (_, _) => { if (!fixture.Ui.CheckAccess()) Interlocked.Increment(ref offDispatcher); };
        var build = fixture.Model.BuildSelectedProjectAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        fixture.Model.ActivateProject(second);
        fixture.Model.IsCompactLayout = true;
        release.TrySetResult();
        await build;
        Assert.Same(second, fixture.Model.SelectedProject);
        Assert.Empty(second.Output);
        Assert.Equal(BuilderProjectViewModel.MaximumOutputCharacters, first.Output.Length);
        Assert.Equal(0, offDispatcher);
    }

    [Fact]
    public async Task MetadataDraftIsNotPersisted_ExternalConflictPreservesDraftUntilDiscard()
    {
        await using var fixture = new BuilderUiFixture();
        var row = await fixture.OpenAsync("metadata");
        var before = row.ToRecord();
        row.EditedDisplayName = "Unsaved name";
        Assert.Equal(before, row.ToRecord());
        var source = fixture.Backend.Projects[row.EntryPoint!] with { DisplayName = "External name", SourceRevision = "external" };
        fixture.Backend.Projects[row.EntryPoint!] = source;
        await fixture.Model.BuildSelectedProjectAsync();
        Assert.Equal("Unsaved name", row.EditedDisplayName);
        Assert.Equal("External name", row.DisplayName);
        Assert.True(row.HasSourceConflict);
        Assert.False(row.CanSave);
        fixture.Model.DiscardSelectedMetadata();
        Assert.Equal("External name", row.EditedDisplayName);
        Assert.False(row.IsDirty);
        Assert.False(row.HasSourceConflict);
    }

    [Fact]
    public async Task LoadedIdentitySaveReleasesOldSessionBeforeMutationWithoutImplicitBuildOrLoad()
    {
        await using var fixture = new BuilderUiFixture();
        var row = await fixture.OpenAsync("identity");
        await fixture.Model.LoadSelectedProjectAsync();
        var oldSession = Assert.Single(fixture.Development.Sessions);
        var expected = row.Inspection;
        row.EditedPackageId = "example.changed";
        fixture.Backend.OnStart = request =>
        {
            if (request.Operation == "save-metadata")
            {
                Assert.True(oldSession.Disposed);
                Assert.Equal(expected, request.Expected);
            }
        };
        await fixture.Model.SaveSelectedMetadataAsync();
        Assert.Equal("example.changed", row.PackageId);
        Assert.False(row.IsDirty);
        Assert.Single(fixture.Development.Sessions);
        Assert.DoesNotContain(fixture.Backend.Starts, request => request.Operation == "build");
        Assert.DoesNotContain(fixture.Backend.Starts, request => request.Operation == "watch");
        Assert.False(row.HasSession);
    }

    [Fact]
    public async Task IdentityReadOnlyReasonPreventsMutationAndDoesNotUnload()
    {
        await using var fixture = new BuilderUiFixture();
        var source = BuilderUiFixture.Project("locked") with { CanEditPackageId = false, PackageIdReadOnlyReason = "Public API history locks this identity." };
        fixture.Backend.Projects[source.EntryPoint] = source;
        await fixture.Model.OpenFolderAsync(source.ProjectFolder);
        var row = fixture.Model.SelectedProject!;
        await fixture.Model.LoadSelectedProjectAsync();
        row.EditedPackageId = "example.forbidden";
        await fixture.Model.SaveSelectedMetadataAsync();
        Assert.False(row.CanSave);
        Assert.Equal(source.PackageIdReadOnlyReason, row.ErrorMessage);
        Assert.DoesNotContain(fixture.Backend.Starts, request => request.Operation == "save-metadata");
        Assert.False(Assert.Single(fixture.Development.Sessions).Disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActivationDisposalDrainsMonitorReleasesHandleAndFlushesBookmarks(bool settlementBeforeDisposal)
    {
        using var settlement = new CancellationTokenSource();
        var updates = new BuilderUiStateUpdates { SettlementRequested = settlement.Token };
        var fixture = new BuilderUiFixture(updates: updates);
        var row = await fixture.OpenAsync("dispose");
        await fixture.Model.LoadSelectedProjectAsync();
        var session = Assert.Single(fixture.Development.Sessions);
        if (settlementBeforeDisposal)
        {
            // Normal Core retirement closes NEW state admission and settles accepted
            // work BEFORE disposing the package's services. Disposal may not acquire
            // a new journal/save capability to make this test succeed.
            updates.Accepting = false;
            await settlement.CancelAsync();
            await Task.WhenAll(updates.Accepted).WaitAsync(TimeSpan.FromSeconds(3));
        }
        await fixture.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(session.Disposed);
        Assert.All(fixture.Backend.Jobs.Values, job => Assert.NotEqual("running", job.State));
        Assert.Contains(fixture.Store.Saves.Last(), record => record.EntryPoint == row.EntryPoint);
        using var journal = System.Text.Json.JsonDocument.Parse(fixture.JournalStorage.Bytes!);
        Assert.Empty(journal.RootElement.GetProperty("pending").EnumerateArray());
        Assert.All(updates.Accepted, accepted => Assert.True(accepted.IsCompletedSuccessfully));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnconfirmedFiniteBuildRetainsSessionUntilUnloadReconcilesExactJob(bool cancellationAccepted)
    {
        var fixture = new BuilderUiFixture();
        var row = await fixture.OpenAsync("unconfirmed-unload");
        await fixture.Model.LoadSelectedProjectAsync();
        var (building, build) = await fixture.StartHeldBuildAsync();
        var session = Assert.Single(fixture.Development.Sessions);
        var cancelIds = new ConcurrentQueue<string>();
        var disconnected = true;
        var terminalObserved = false;
        var releasedWithProof = false;
        fixture.Backend.BeforeCancel = (id, token) =>
        {
            cancelIds.Enqueue(id);
            if (disconnected && !cancellationAccepted) throw new IOException("Cancellation transport unavailable.");
            return Task.CompletedTask;
        };
        fixture.Backend.BeforeGet = (request, _) =>
        {
            if (request.Id != build.Id) return Task.CompletedTask;
            if (disconnected) throw new IOException("Final job acknowledgement unavailable.");
            if (fixture.Backend.Jobs[request.Id!].State != "running") terminalObserved = true;
            return Task.CompletedTask;
        };
        session.OnDispose = () => releasedWithProof = terminalObserved;
        try
        {
            fixture.Model.CancelCurrentOperation();
            await building.WaitAsync(TimeSpan.FromSeconds(3));
            await fixture.Model.UnloadSelectedProjectAsync().WaitAsync(TimeSpan.FromSeconds(3));

            Assert.False(session.Disposed);
            Assert.Equal(0, session.DisposeCount);
            Assert.True(row.HasSession);
            Assert.False(row.IsBusy);
            Assert.Contains(build.Id!, row.ErrorMessage, StringComparison.Ordinal);
            Assert.Contains("retry Unload", row.ErrorMessage, StringComparison.Ordinal);
            Assert.False(terminalObserved);
            Assert.Contains("unconfirmed", row.ErrorMessage, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(cancellationAccepted ? "cancelled" : "running", fixture.Backend.Jobs[build.Id!].State);

            // A retained owner cannot be replaced by another Load while cleanup is uncertain.
            await fixture.Model.LoadSelectedProjectAsync();
            Assert.Single(fixture.Development.Sessions);
            Assert.Single(fixture.Backend.Starts, request => request.Operation == "build");
            await fixture.Model.BuildSelectedProjectAsync();
            Assert.Single(fixture.Backend.Starts, request => request.Operation == "build");
            Assert.Equal(0, session.DisposeCount);
            Assert.True(row.HasSession);

            disconnected = false;
            await fixture.Model.UnloadSelectedProjectAsync().WaitAsync(TimeSpan.FromSeconds(3));

            Assert.True(releasedWithProof);
            Assert.True(session.Disposed);
            Assert.Equal(1, session.DisposeCount);
            Assert.False(row.HasSession);
            Assert.NotEmpty(cancelIds);
            Assert.All(cancelIds, id => Assert.Equal(build.Id, id));
            Assert.Equal("cancelled", fixture.Backend.Jobs[build.Id!].State);
            Assert.True(fixture.Backend.CleanupUsedIndependentToken);
        }
        finally
        {
            disconnected = false;
            await fixture.Application.StopJobAsync(build.Id!);
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task ActivationDisposalKeepsUnconfirmedFiniteBuildOwnerButReleasesProvenOwners()
    {
        var fixture = new BuilderUiFixture();
        var row = await fixture.OpenAsync("unconfirmed-retirement");
        await fixture.Model.LoadSelectedProjectAsync();
        var retained = Assert.Single(fixture.Development.Sessions);
        await fixture.OpenAsync("proven-retirement");
        await fixture.Model.LoadSelectedProjectAsync();
        var released = fixture.Development.Sessions[1];
        fixture.Model.ActivateProject(row);
        var (building, build) = await fixture.StartHeldBuildAsync();
        var cancelIds = new ConcurrentQueue<string>();
        fixture.Backend.BeforeCancel = (id, _) =>
        {
            cancelIds.Enqueue(id);
            return Task.FromException(new IOException("Build cancellation unavailable."));
        };
        fixture.Backend.BeforeGet = (request, _) => request.Id == build.Id
            ? Task.FromException(new IOException("Final build acknowledgement unavailable.")) : Task.CompletedTask;
        try
        {
            fixture.Model.CancelCurrentOperation();
            await building.WaitAsync(TimeSpan.FromSeconds(3));
            var disposal = fixture.Model.DisposeAsync().AsTask();
            var failure = await Assert.ThrowsAsync<AggregateException>(() => disposal.WaitAsync(TimeSpan.FromSeconds(3)));

            Assert.False(retained.Disposed);
            Assert.Equal(0, retained.DisposeCount);
            Assert.True(released.Disposed);
            Assert.Equal(1, released.DisposeCount);
            Assert.Equal("running", fixture.Backend.Jobs[build.Id!].State);
            var cleanup = Assert.Single(failure.Flatten().InnerExceptions.OfType<BuilderJobCleanupException>());
            Assert.Contains(build.Id!, cleanup.Message, StringComparison.Ordinal);
            Assert.Contains("remains owned", cleanup.Message, StringComparison.Ordinal);
            Assert.Equal(2, fixture.Store.Records.Count);
            Assert.NotEmpty(cancelIds);
            Assert.All(cancelIds, id => Assert.Equal(build.Id, id));

            // A failed activation disposal retains the exact owner in the shared
            // operations service, independently of the quarantined presentation.
            var owner = Assert.Single(fixture.PackageOperations.Sessions);
            Assert.Equal(row.EntryPoint, owner.SourceEntryPoint);
            Assert.Same(retained, owner.Handle);
            Assert.Same(disposal, fixture.Model.DisposeAsync().AsTask());
        }
        finally
        {
            fixture.Backend.BeforeCancel = null;
            fixture.Backend.BeforeGet = null;
            await fixture.Application.StopJobAsync(build.Id!);
            await fixture.Application.DisposeAsync();
        }
    }

    [Theory]
    [InlineData("load", true)]
    [InlineData("load", false)]
    [InlineData("sdk-settings", false)]
    [InlineData("sdk-wizard", false)]
    [InlineData("creation", false)]
    public async Task QueuedCancellationReleasesPresentationWithoutStartingBackend(string flow, bool disposeOwner)
    {
        await using var fixture = new BuilderUiFixture();
        await using var manager = new BuilderSdkManager(fixture.Application, fixture.Ui);
        BuilderProjectViewModel? row = null;
        if (flow == "load")
        {
            row = await fixture.OpenAsync("queued-load");
        }
        if (flow is "sdk-wizard" or "creation")
        {
            await fixture.Model.CreateProjectAsync();
            if (flow == "creation") await fixture.Model.SelectTechnologyAsync(BuilderProjectKind.Node);
            fixture.Model.Creation.DisplayName = "Preserved queued input";
            fixture.Model.Creation.ParentFolder = Path.GetTempPath();
        }
        var startsBeforeCancellation = fixture.Backend.Starts.Count;
        var priorActivities = fixture.Queue.ListTasks().Select(item => item.TaskId).ToHashSet();
        fixture.Queue.AutoStart = false;
        Func<Task> start = flow switch
        {
            "load" => fixture.Model.LoadSelectedProjectAsync,
            "sdk-settings" => () => manager.InstallAsync(BuilderProjectKind.Node),
            "sdk-wizard" => () => fixture.Model.SelectTechnologyAsync(BuilderProjectKind.Node),
            _ => fixture.Model.CompleteCreationAsync,
        };
        var failures = new PresentationTaskScopeTests.FailureRecordingSynchronizationContext();
        var previous = SynchronizationContext.Current;
        Task operation;
        try
        {
            SynchronizationContext.SetSynchronizationContext(failures);
            operation = start();
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
        await BuilderUiFixture.WaitAsync(() => fixture.Queue.ListTasks().Any(item => !priorActivities.Contains(item.TaskId)));
        var activity = fixture.Queue.ListTasks().Single(item => !priorActivities.Contains(item.TaskId));

        if (disposeOwner)
            await Task.WhenAll(operation, fixture.DisposeAsync().AsTask()).WaitAsync(TimeSpan.FromSeconds(3));
        else
        {
            fixture.Queue.Cancel(activity.TaskId);
            await operation.WaitAsync(TimeSpan.FromSeconds(3));
            if (row is not null)
            {
                Assert.False(row.IsBusy);
                Assert.False(row.HasSession);
                Assert.Contains("cancelled", row.StatusText, StringComparison.OrdinalIgnoreCase);
            }
            else if (flow == "sdk-settings")
            {
                var card = manager.Cards.Single(card => card.Kind == BuilderProjectKind.Node);
                Assert.False(card.IsBusy);
                Assert.Contains("cancelled", card.ErrorMessage, StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                Assert.False(fixture.Model.Creation.IsBusy);
                Assert.Equal("Preserved queued input", fixture.Model.Creation.DisplayName);
                Assert.Contains("cancelled", fixture.Model.Creation.ErrorMessage, StringComparison.OrdinalIgnoreCase);
                Assert.Empty(fixture.Model.Projects);
            }
        }

        Assert.Equal(startsBeforeCancellation, fixture.Backend.Starts.Count);
        Assert.Empty(fixture.Development.Sessions);
        Assert.Equal(BackgroundTaskState.Cancelled, fixture.Queue.ListTasks().Single(item => item.TaskId == activity.TaskId).State);
        Assert.Equal(0, failures.PostCount);
        Assert.Equal(0, fixture.Queue.SubscriberCount);
        if (disposeOwner) Assert.NotEmpty(fixture.Store.Records);
    }

    [Theory]
    [InlineData("foreign-token")]
    [InlineData("tokenless")]
    [InlineData("callback")]
    public async Task RunningJobFailureAtShutdownIsPreservedAfterIndependentJobCleanup(string failureKind)
    {
        await using var fixture = new BuilderUiFixture();
        var row = await fixture.OpenAsync("job-callback");
        using var foreign = new CancellationTokenSource();
        foreign.Cancel();
        var callbackFailure = failureKind == "callback";
        Exception failure = failureKind switch
        {
            "callback" => new IOException("Progress callback failed after shutdown."),
            "foreign-token" => new OperationCanceledException(foreign.Token),
            _ => new OperationCanceledException(),
        };
        fixture.Backend.Result = request => fixture.Backend.DefaultResult(request) with { State = "running", Message = "Publishing progress" };
        if (callbackFailure)
        {
            row.PropertyChanged += (_, _) =>
            {
                if (row.StatusText != "Publishing progress") return;
                fixture.Ui.Shutdown();
                throw failure;
            };
        }
        else
        {
            var polls = 0;
            fixture.Backend.BeforeGet = (_, _) =>
            {
                if (Interlocked.Increment(ref polls) != 1) return Task.CompletedTask;
                fixture.Ui.Shutdown();
                return Task.FromException(failure);
            };
        }
        var failures = new PresentationTaskScopeTests.FailureRecordingSynchronizationContext();
        var previous = SynchronizationContext.Current;
        Task build;
        try
        {
            SynchronizationContext.SetSynchronizationContext(failures);
            build = fixture.Model.BuildSelectedProjectAsync();
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }

        var observed = await Record.ExceptionAsync(() => build.WaitAsync(TimeSpan.FromSeconds(3)));
        var posted = await failures.Failure.Task.WaitAsync(TimeSpan.FromSeconds(3));

        if (callbackFailure)
            Assert.Same(failure, Assert.IsType<BuilderApp::Sunder.Package.Agent.Shared.Presentation.PresentationCallbackException>(observed).InnerException);
        else Assert.Same(failure, observed);
        Assert.Same(observed, posted);
        Assert.Equal(1, failures.PostCount);
        Assert.Equal("cancelled", Assert.Single(fixture.Backend.Jobs.Values).State);
        Assert.True(fixture.Backend.CleanupUsedIndependentToken);
        Assert.Equal(0, fixture.Queue.SubscriberCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DispatcherShutdownBetweenAdmissionAndSubmissionCancelsWithoutRunningTheCallback(bool query)
    {
        var ui = new BuilderUiDispatcher();
        IBuilderUiDispatcher dispatcher = ui;
        var callbacks = 0;
        ui.BeforeNextInvocation = ui.Shutdown;
        Func<Task> invoke = query
            ? () => dispatcher.InvokeAsync(() => ++callbacks, default)
            : () => dispatcher.InvokeAsync(() => { callbacks++; }, default);

        var stopped = await Assert.ThrowsAsync<BuilderApp::Sunder.Package.Agent.Shared.Presentation.PresentationDispatchCanceledException>(invoke);

        Assert.Equal(BuilderApp::Sunder.Package.Agent.Shared.Presentation.PresentationDispatchCancellationReason.DispatcherShutdown, stopped.Reason);
        Assert.Equal(ui.ShutdownToken, stopped.CancellationToken);
        Assert.Equal(0, callbacks);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActivationStopCancelsMonitorBeforeManualJobDrainAndCleansUpWithoutUi(bool dispatcherShutdown)
    {
        var fixture = new BuilderUiFixture();
        var row = await fixture.OpenAsync("stop-race");
        await fixture.Model.LoadSelectedProjectAsync();
        var session = Assert.Single(fixture.Development.Sessions);
        var monitorEntered = BuilderUiFixture.Signal();
        var monitorCancelled = BuilderUiFixture.Signal();
        session.BeforeGet = async token =>
        {
            monitorEntered.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { monitorCancelled.TrySetResult(); throw; }
        };
        await monitorEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));

        var manualEntered = BuilderUiFixture.Signal();
        var cleanupEntered = BuilderUiFixture.Signal();
        var releaseCleanup = BuilderUiFixture.Signal();
        fixture.Backend.Result = request => fixture.Backend.DefaultResult(request) with { State = "running", Phase = BuilderJobPhase.Building,
            DevelopmentFolder = null, LastSuccessfulBuildAtUtc = null };
        fixture.Backend.BeforeGet = (request, _) =>
        {
            if (request.Operation == "build") manualEntered.TrySetResult();
            return Task.CompletedTask;
        };
        fixture.Backend.BeforeCancel = async (id, token) =>
        {
            if (fixture.Backend.Requests[id].Operation != "build") return;
            Assert.False(token.IsCancellationRequested);
            cleanupEntered.TrySetResult();
            await releaseCleanup.Task.WaitAsync(token);
        };
        var build = fixture.Model.BuildSelectedProjectAsync();
        await manualEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        if (dispatcherShutdown) fixture.Ui.Shutdown();
        var disposal = fixture.DisposeAsync().AsTask();
        try
        {
            await cleanupEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            // This must complete while _tasks is still draining the blocked manual job cleanup.
            await monitorCancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(disposal.IsCompleted);
        }
        finally { releaseCleanup.TrySetResult(); }
        await Task.WhenAll(build, disposal).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(session.Disposed);
        Assert.True(fixture.Backend.CleanupUsedIndependentToken);
        Assert.All(fixture.Backend.Jobs.Values, job => Assert.NotEqual("running", job.State));
        Assert.Single(fixture.Backend.Starts, request => request.Operation == "build");
        Assert.Contains(fixture.Store.Records, record => record.EntryPoint == row.EntryPoint);
        var operations = Assert.IsType<ConcurrentDictionary<string, CancellationTokenSource>>(typeof(BuilderViewModel)
            .GetField("_operations", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(fixture.Model));
        Assert.Empty(operations);
    }

    [Fact]
    public async Task ForeignSessionCancellationSurvivesDispatcherShutdownWithoutLosingLeaseCleanup()
    {
        var fixture = new BuilderUiFixture();
        await fixture.OpenAsync("revoked-status");
        await fixture.Model.LoadSelectedProjectAsync();
        var session = Assert.Single(fixture.Development.Sessions);
        var releasedWithJobProof = false;
        session.OnDispose = () => releasedWithJobProof = fixture.Backend.Jobs.Values.All(job => job.State != "running");
        using var revoked = new CancellationTokenSource();
        revoked.Cancel();
        var failure = new OperationCanceledException(revoked.Token);
        var entered = BuilderUiFixture.Signal();
        var release = BuilderUiFixture.Signal();
        session.BeforeGet = async _ =>
        {
            entered.TrySetResult();
            await release.Task;
            throw failure;
        };
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        fixture.Ui.Shutdown();
        release.TrySetResult();
        var observed = await Assert.ThrowsAsync<AggregateException>(() => fixture.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Contains(observed.Flatten().InnerExceptions, error => ReferenceEquals(error, failure));
        Assert.True(session.Disposed);
        Assert.True(releasedWithJobProof);
        Assert.Equal(1, session.DisposeCount);
        Assert.All(fixture.Backend.Jobs.Values, job => Assert.NotEqual("running", job.State));
        Assert.NotEmpty(fixture.Store.Records);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task MonitorPresentationCallbackFailureStillReleasesEverySessionAndFlushesPersistence(bool cancellation, bool releasePresentation)
    {
        var fixture = new BuilderUiFixture();
        var first = await fixture.OpenAsync("callback-failure");
        await fixture.Model.LoadSelectedProjectAsync();
        await fixture.OpenAsync("other-owner");
        await fixture.Model.LoadSelectedProjectAsync();
        using var foreign = new CancellationTokenSource();
        foreign.Cancel();
        Exception failure = cancellation ? new OperationCanceledException(foreign.Token) : new IOException("Monitor callback failed.");
        var callbackRan = BuilderUiFixture.Signal();
        first.PropertyChanged += (_, _) =>
        {
            if (releasePresentation ? first.LoadStateText != "Release unconfirmed" : first.RuntimeError != "CallbackFailure") return;
            if (releasePresentation && callbackRan.Task.IsCompleted) return;
            callbackRan.TrySetResult();
            throw failure;
        };
        if (releasePresentation)
        {
            // Live Unload posts presentation; activation shutdown correctly abandons
            // UI work and must not be used to simulate this callback boundary.
            fixture.Model.ActivateProject(first);
            var failures = new PresentationTaskScopeTests.FailureRecordingSynchronizationContext();
            var previous = SynchronizationContext.Current;
            Task unload;
            try
            {
                SynchronizationContext.SetSynchronizationContext(failures);
                unload = fixture.Model.UnloadSelectedProjectAsync();
            }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }
            var observedRelease = await Assert.ThrowsAsync<BuilderApp::Sunder.Package.Agent.Shared.Presentation.PresentationCallbackException>(
                () => unload.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.Same(failure, observedRelease.InnerException);
            Assert.Same(observedRelease, await failures.Failure.Task.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.Equal(1, failures.PostCount);
            Assert.True(callbackRan.Task.IsCompletedSuccessfully);
            Assert.True(fixture.Development.Sessions[0].Disposed);
            Assert.False(first.HasSession);
            await fixture.DisposeAsync();
        }
        else
        {
            fixture.Development.Sessions[0].Status = fixture.Development.Sessions[0].Status with
                { RuntimeReadiness = PackageDevelopmentRuntimeReadiness.Failed, ErrorMessage = "CallbackFailure" };
            await callbackRan.Task.WaitAsync(TimeSpan.FromSeconds(3));
            fixture.Ui.Shutdown();

            var observed = await Assert.ThrowsAsync<AggregateException>(() => fixture.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)));

            var callbackError = Assert.Single(observed.Flatten().InnerExceptions);
            Assert.Same(failure, Assert.IsType<BuilderApp::Sunder.Package.Agent.Shared.Presentation.PresentationCallbackException>(callbackError).InnerException);
        }
        Assert.True(callbackRan.Task.IsCompletedSuccessfully);
        Assert.All(fixture.Development.Sessions, session => Assert.True(session.Disposed));
        Assert.Equal(2, fixture.Store.Records.Count);
    }

    [Fact]
    public async Task CancellationCallbackFailureDoesNotSkipDrainSessionReleaseOrPersistence()
    {
        var fixture = new BuilderUiFixture();
        await fixture.OpenAsync("cancel-callback");
        await fixture.Model.LoadSelectedProjectAsync();
        var session = Assert.Single(fixture.Development.Sessions);
        var failure = new InvalidOperationException("Cancellation callback failed.");
        var entered = BuilderUiFixture.Signal();
        session.BeforeGet = async token =>
        {
            var pending = Task.Delay(Timeout.Infinite, token);
            using var registration = token.Register(() => throw failure);
            entered.TrySetResult();
            await pending;
        };
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));

        var observed = await Assert.ThrowsAsync<AggregateException>(() => fixture.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)));

        Assert.Contains(observed.Flatten().InnerExceptions, error => ReferenceEquals(error, failure));
        Assert.True(session.Disposed);
        Assert.NotEmpty(fixture.Store.Records);
    }

    [Fact]
    public async Task FailedInitializationCanRetry_DisposalNeverOverwritesUnknownState()
    {
        var store = new BuilderUiStore { Load = _ => throw new InvalidDataException("Corrupt builder state.") };
        var fixture = new BuilderUiFixture(store: store);
        await fixture.Model.InitializeAsync();
        Assert.Contains("Corrupt builder state", fixture.Model.StatusText, StringComparison.Ordinal);
        await fixture.DisposeAsync();
        Assert.Empty(store.Saves);
        store.Load = null;
        await using var retry = new BuilderUiFixture(store: store);
        await retry.Model.InitializeAsync();
        Assert.Empty(retry.Model.StatusText);
    }

    [Fact]
    public async Task TransientLoadFailureRetriesInSameActivation()
    {
        await using var fixture = new BuilderUiFixture();
        fixture.Store.Load = _ => throw new IOException("Temporary state failure");
        await fixture.Model.InitializeAsync();
        fixture.Store.Load = null;
        await fixture.Model.InitializeAsync();
        Assert.Equal(2, fixture.Store.LoadCount);
        Assert.Empty(fixture.Model.StatusText);
        Assert.Empty(fixture.Store.Saves);
    }

    [Fact]
    public async Task ClickCapturesProjectBeforeAsyncDispatch()
    {
        await using var fixture = new BuilderUiFixture();
        var first = await fixture.OpenAsync("clicked");
        var second = await fixture.OpenAsync("selected-later");
        fixture.Model.ActivateProject(first);
        var building = fixture.Model.BuildSelectedProjectAsync();
        fixture.Model.ActivateProject(second);
        await building;
        Assert.Equal(first.EntryPoint, Assert.Single(fixture.Backend.Starts).EntryPoint);
        Assert.Same(second, fixture.Model.SelectedProject);
    }

    [Fact]
    public async Task FailedBookmarkRemovalRestoresPendingGenerationWithoutChangingNewSelection()
    {
        var fixture = new BuilderUiFixture();
        var first = await fixture.OpenAsync("remove");
        var second = await fixture.OpenAsync("retain");
        var started = BuilderUiFixture.Signal();
        var release = BuilderUiFixture.Signal();
        var writes = 0;
        fixture.Store.Save = async (_, _) =>
        {
            if (Interlocked.Increment(ref writes) == 1)
            {
                started.TrySetResult();
                await release.Task;
                throw new IOException("Cannot save removal");
            }
        };
        fixture.Model.ActivateProject(first);
        var removal = fixture.Model.DeleteSelectedProjectAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        fixture.Model.ActivateProject(second);
        release.TrySetResult();
        await removal;
        Assert.Same(second, fixture.Model.SelectedProject);
        Assert.Contains(first, fixture.Model.Projects);
        await fixture.DisposeAsync();
        Assert.Contains(fixture.Store.Records, record => record.Id == first.Id);
    }
}

internal sealed class BuilderUiFixture : IAsyncDisposable
{
    public BuilderUiBackend Backend { get; }
    public BuilderUiStore Store { get; }
    public BuilderUiDispatcher Ui { get; } = new();
    public BuilderUiDevelopment Development { get; } = new();
    public BuilderUiBackgroundQueue Queue { get; }
    public BuilderProjectApplicationService Application { get; }
    public BuilderPackageOperations PackageOperations { get; }
    public BuilderViewModel Model { get; }
    public BuilderMemoryJournalStorage JournalStorage { get; } = new();
    public BuilderUiFixture(BuilderUiBackend? backend = null, BuilderUiStore? store = null, BuilderUiBackgroundQueue? queue = null,
        BuilderUiStateUpdates? updates = null)
    {
        Backend = backend ?? new(); Store = store ?? new();
        Queue = queue ?? new();
        updates ??= new();
        Application = new(Backend, Store, new(Queue), new BuilderJobJournal(JournalStorage, updates));
        Development.PackageIdResolver = folder => Backend.Projects.Values.Single(project => folder.StartsWith(project.ProjectFolder + Path.DirectorySeparatorChar, StringComparison.Ordinal)).PackageId;
        PackageOperations = new(Application, Development);
        Model = new(Application, new(Store, updates, TimeSpan.FromMinutes(1)), new(), Ui, Development, Development, NullPackageSettingsNavigationService.Instance, PackageOperations);
    }
    public async Task<BuilderProjectViewModel> OpenAsync(string name, BuilderProjectKind kind = BuilderProjectKind.Dotnet)
    {
        var source = Project(name, kind);
        Backend.Projects[source.EntryPoint] = source;
        await Model.OpenFolderAsync(source.ProjectFolder);
        return Model.SelectedProject!;
    }
    internal async Task<(Task Work, BuilderJobRequest Request)> StartHeldBuildAsync()
    {
        Backend.HoldBuilds = true;
        var work = Model.BuildSelectedProjectAsync();
        await WaitAsync(() => Backend.Starts.Any(request => request.Operation == "build" && Backend.Jobs.ContainsKey(request.Id!)));
        return (work, Backend.Starts.Last(request => request.Operation == "build"));
    }
    public async ValueTask DisposeAsync()
    {
        try { await Model.DisposeAsync(); }
        finally { await Application.DisposeAsync(); }
    }
    public static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    public static async Task WaitAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }
    public static BuilderProjectInspection Project(string name, BuilderProjectKind kind = BuilderProjectKind.Dotnet)
    {
        var folder = Path.Combine(Path.GetTempPath(), "builder-ui-tests", name);
        return new() { Kind = kind, ProjectFolder = folder, EntryPoint = Path.Combine(folder, kind == BuilderProjectKind.Dotnet ? "Package.csproj" : "package.json"),
            PackageId = "local." + name, DisplayName = name, SourceRevision = "initial", CanEditDisplayName = true, CanEditPackageId = true };
    }
    public static BuilderProjectRecord Record(BuilderProjectInspection project) => new(Guid.NewGuid().ToString("N"), project.DisplayName,
        project.PackageId, project.ProjectFolder, project.EntryPoint, project.Kind, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
    public static BuilderToolchainInstallation Sdk(BuilderProjectKind kind = BuilderProjectKind.Dotnet) => new()
    {
        Kind = kind, Version = kind == BuilderProjectKind.Dotnet ? "10.0.100" : "24.1.0", RootPath = Path.GetTempPath(),
        ExecutablePath = Path.Combine(Path.GetTempPath(), kind == BuilderProjectKind.Dotnet ? "dotnet" : "node"),
        IsManaged = true, IsHealthy = true, NpmVersion = kind == BuilderProjectKind.Node ? "11.0.0" : null,
    };
}

internal sealed class BuilderUiBackend : IBuilderBackend
{
    public Task<IBuilderJobExecution> CaptureExecutionAsync(CancellationToken cancellationToken = default)
        => BuilderTestExecution.CaptureAsync(this, cancellationToken);
    public Task<BuilderRecordedJobRecovery?> RecoverRecordedJobAsync(BuilderJobReservation reservation,
        CancellationToken cancellationToken = default)
        // Domain fake only: it has no authenticated catalog/execution bridge.
        => Task.FromResult<BuilderRecordedJobRecovery?>(null);
    public string BackendId { get; set; } = new('a', 32);
    public Func<CancellationToken, Task>? BeforeGetBackendId { get; set; }
    public async Task<string> GetBackendIdAsync(CancellationToken cancellationToken = default)
    {
        if (BeforeGetBackendId is { } before) await before(cancellationToken);
        return BackendId;
    }
    public ConcurrentDictionary<string, BuilderProjectInspection> Projects { get; } = new();
    public ConcurrentDictionary<string, BuilderJobSnapshot> Jobs { get; } = new();
    public ConcurrentDictionary<string, BuilderJobRequest> Requests { get; } = new();
    public ConcurrentQueue<BuilderJobRequest> Starts { get; } = new();
    public Func<string, IReadOnlyList<BuilderProjectInspection>>? Discovery { get; set; }
    public Func<string, CancellationToken, Task<BuilderProjectInspection>>? Inspection { get; set; }
    public Func<BuilderJobRequest, BuilderJobSnapshot>? Result { get; set; }
    public Func<BuilderJobRequest, CancellationToken, Task>? BeforeGet { get; set; }
    public Func<string, CancellationToken, Task>? BeforeCancel { get; set; }
    public Func<BuilderProjectKind, BuilderToolchainStatus>? Toolchains { get; set; }
    public Func<BuilderProjectKind, bool, CancellationToken, Task>? BeforeToolchainCheck { get; set; }
    public Action<BuilderJobRequest>? OnStart { get; set; }
    public bool LoseFirstStartResponse { get; set; }
    public bool HoldBuilds { get; set; }
    public bool CleanupUsedIndependentToken { get; private set; }
    public int ToolchainChecks;
    public Task<IReadOnlyList<BuilderProjectInspection>> DiscoverAsync(string folder, CancellationToken cancellationToken = default)
        => Task.FromResult(Discovery?.Invoke(folder) ?? Projects.Values.Where(project => project.ProjectFolder == folder).ToArray());
    public Task<BuilderProjectInspection> InspectAsync(string entryPoint, CancellationToken cancellationToken = default)
        => Inspection?.Invoke(entryPoint, cancellationToken)
            ?? Task.FromResult(Projects.TryGetValue(entryPoint, out var source) ? source : throw new FileNotFoundException("Source missing"));
    public async Task<BuilderToolchainStatus> GetToolchainsAsync(BuilderProjectKind kind, bool checkUpdates, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref ToolchainChecks);
        if (BeforeToolchainCheck is { } before) await before(kind, checkUpdates, cancellationToken);
        return Toolchains?.Invoke(kind) ?? new(kind, [BuilderUiFixture.Sdk(kind)], "recommended", DateTimeOffset.UtcNow, false, null);
    }
    public Task<string> StartJobAsync(BuilderJobRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Starts.Enqueue(request);
        OnStart?.Invoke(request);
        RequireBackend(request.BackendId!);
        if (Requests.TryAdd(request.Id!, request)) Jobs[request.Id!] = Result?.Invoke(request) ?? DefaultResult(request);
        if (LoseFirstStartResponse) { LoseFirstStartResponse = false; throw new IOException("Start response lost"); }
        return Task.FromResult(request.Id!);
    }
    public async Task<BuilderJobSnapshot> GetJobAsync(string jobId, string backendId, CancellationToken cancellationToken = default)
    {
        RequireBackend(backendId);
        if (BeforeGet is { } before) await before(Requests[jobId], cancellationToken);
        return Jobs[jobId];
    }
    public async Task CancelJobAsync(string jobId, string backendId, CancellationToken cancellationToken = default)
    {
        RequireBackend(backendId);
        if (BeforeCancel is { } before) await before(jobId, cancellationToken);
        CleanupUsedIndependentToken = !cancellationToken.IsCancellationRequested;
        if (Jobs.TryGetValue(jobId, out var result) && result.State == "running") Jobs[jobId] = result with { State = "cancelled" };
    }
    private void RequireBackend(string backendId)
    {
        if (backendId != BackendId) throw new Sunder.Sdk.Rpc.SunderRpcException(new(Sunder.Sdk.Rpc.SunderRpcErrorKind.Domain,
            "agent.builder.backend-changed", "The backend was replaced."));
    }
    public BuilderJobSnapshot DefaultResult(BuilderJobRequest request)
    {
        BuilderProjectInspection? project = request.EntryPoint is { } entry ? Projects.GetValueOrDefault(entry) : null;
        if (request.Operation == "create")
        {
            project = BuilderUiFixture.Project("created", request.Kind) with
            {
                ProjectFolder = request.ProjectFolder!, EntryPoint = Path.Combine(request.ProjectFolder!, request.Kind == BuilderProjectKind.Dotnet ? "Package.csproj" : "package.json"),
                DisplayName = request.DisplayName!, PackageId = request.PackageId!,
            };
            Projects[project.EntryPoint] = project;
        }
        if (request.Operation == "save-metadata" && project is not null)
        {
            project = project with { DisplayName = request.DisplayName!, PackageId = request.PackageId!, SourceRevision = "saved" };
            Projects[project.EntryPoint] = project;
        }
        return Snapshot(request, HoldBuilds && request.Operation == "build" ? "running" : "succeeded") with
        {
            Project = project, Installation = BuilderUiFixture.Sdk(request.Kind),
            DevelopmentFolder = project is null || request.Operation is "archive" or "save-metadata" ? null : Path.Combine(project.ProjectFolder, "sunder-dev"),
            DevelopmentContentIdentity = project is null ? null : new string('c', 64),
            ArchivePath = request.Operation == "archive" ? Path.Combine(project!.ProjectFolder, "package.sunderpkg") : null,
            Phase = HoldBuilds && request.Operation == "build" ? BuilderJobPhase.Building : BuilderJobPhase.BuildReady,
            LastSuccessfulBuildAtUtc = project is not null && !(HoldBuilds && request.Operation == "build")
                && request.Operation is "build" or "setup" ? DateTimeOffset.UtcNow : null,
        };
    }
    public static BuilderJobSnapshot Snapshot(BuilderJobRequest request, string state) => new()
    {
        Id = request.Id!, BackendId = request.BackendId, Operation = request.Operation, State = state, Message = request.Operation + " " + state, Output = string.Empty,
    };
}

internal sealed class BuilderUiStateUpdates : IPackageAppStateUpdates
{
    internal bool Accepting = true;
    internal CancellationToken SettlementRequested;
    internal ConcurrentQueue<Task> Accepted { get; } = new();
    public Task RunAsync(string key, Func<PackageAppStateUpdateContext, Task> update, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Accepting) throw new InvalidOperationException("Test App admission is closed.");
        var accepted = Task.Run(() => update(new(SettlementRequested, cancellationToken)));
        Accepted.Enqueue(accepted);
        return accepted;
    }
}

internal sealed class BuilderUiStore : IBuilderProjectStore
{
    public IReadOnlyList<BuilderProjectRecord> Records { get; set; } = [];
    public Func<CancellationToken, Task<IReadOnlyList<BuilderProjectRecord>>>? Load { get; set; }
    public Func<IReadOnlyList<BuilderProjectRecord>, CancellationToken, Task>? Save { get; set; }
    public ConcurrentQueue<IReadOnlyList<BuilderProjectRecord>> Saves { get; } = new();
    public int LoadCount;
    public Task<IReadOnlyList<BuilderProjectRecord>> LoadAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref LoadCount);
        return Load?.Invoke(cancellationToken) ?? Task.FromResult(Records);
    }
    public async Task SaveAsync(IReadOnlyList<BuilderProjectRecord> projects, CancellationToken cancellationToken = default)
    {
        if (Save is not null) await Save(projects, cancellationToken);
        Records = projects.ToArray();
        Saves.Enqueue(Records);
    }
}

internal sealed class BuilderUiDispatcher : IBuilderUiDispatcher
{
    private readonly object _gate = new();
    private readonly AsyncLocal<int> _depth = new();
    private readonly CancellationTokenSource _shutdown = new();
    public Action? BeforeNextInvocation { get; set; }
    public CancellationToken ShutdownToken => _shutdown.Token;
    public void Shutdown() => _shutdown.Cancel();
    public bool CheckAccess() => _depth.Value > 0;
    public Task InvokeAsync(Action action) => InvokeAsync(() => { action(); return true; });
    public Task<T> InvokeAsync<T>(Func<T> action)
    {
        lock (_gate)
        {
            var before = BeforeNextInvocation;
            BeforeNextInvocation = null;
            before?.Invoke();
            // Shutdown can race the adapter's preflight while this invocation
            // waits for the gate. No callback ran; preserve that exact stop proof.
            BuilderApp::Sunder.Package.Agent.Shared.Presentation.PresentationCancellation.ThrowIfStopped(default, ShutdownToken);
            _depth.Value++;
            try { return Task.FromResult(action()); }
            finally { _depth.Value--; }
        }
    }
}

internal sealed class BuilderUiDevelopment : IPackageDevelopmentService, IPackageLogNavigationService
{
    public List<(string Folder, bool Watch)> Loads { get; } = [];
    public List<BuilderUiSession> Sessions { get; } = [];
    public List<string> OpenedPackageLogs { get; } = [];
    public Func<string, bool, CancellationToken, Task>? BeforeLoad { get; set; }
    public Func<string, string>? PackageIdResolver { get; set; }
    public Action<BuilderUiSession>? AfterLoad { get; set; }
    public PackageDevelopmentLogQuery? LastLogQuery { get; private set; }
    public Func<string, PackageDevelopmentLogQuery, PackageDevelopmentLogs>? ReadLogs { get; set; }
    public Task<PackageDevelopmentLogs> GetLogsAsync(string packageId, PackageDevelopmentLogQuery query, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Assert.Contains(Sessions, session => session.PackageId == packageId);
        LastLogQuery = query;
        return Task.FromResult(ReadLogs?.Invoke(packageId, query) ?? new PackageDevelopmentLogs(packageId, [], false));
    }
    public async Task<IPackageDevelopmentSession> LoadAsync(string developmentFolder, bool watch, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Loads.Add((developmentFolder, watch));
        if (BeforeLoad is { } before) await before(developmentFolder, watch, cancellationToken);
        var session = new BuilderUiSession(watch, PackageIdResolver?.Invoke(developmentFolder) ?? "local." + Path.GetFileName(Path.GetDirectoryName(developmentFolder)));
        Sessions.Add(session);
        AfterLoad?.Invoke(session);
        return session;
    }
    public Task OpenPackageLogsAsync(string packageId, CancellationToken cancellationToken = default)
    { OpenedPackageLogs.Add(packageId); return Task.CompletedTask; }
}

internal sealed class BuilderUiSession(bool watch, string packageId = "local.test") : IPackageDevelopmentSession
{
    public string PackageId => packageId;
    public bool Disposed { get; private set; }
    public int DisposeCount { get; private set; }
    public Func<Task>? BeforeDispose { get; set; }
    public Action? OnDispose { get; set; }
    public ConcurrentQueue<bool> WatchRequests { get; } = new();
    public bool OtherOwnerWatching { get; set; }
    public PackageDevelopmentStatus Status { get; set; } = new(packageId, true, watch, PackageDevelopmentRuntimeReadiness.Ready, null, false)
        { HostState = PackageDevelopmentHostState.Ready, RequestedWatch = watch, AppReadiness = PackageDevelopmentAppReadiness.Activated };
    public Func<CancellationToken, Task>? BeforeGet { get; set; }
    public Func<string, string, CancellationToken, Task<PackageDevelopmentRevision>>? OnReload { get; set; }
    public ConcurrentQueue<(string OperationId, string Identity)> Reloads { get; } = new();
    public Func<string, CancellationToken, Task<PackageDevelopmentViewSnapshot>>? OnInspectView { get; set; }
    public Func<PackageDevelopmentViewInput, CancellationToken, Task<PackageDevelopmentViewInputResult>>? OnViewInput { get; set; }
    public ConcurrentQueue<PackageDevelopmentViewInput> ViewInputs { get; } = new();
    public IReadOnlyList<PackageDevelopmentView> Views { get; set; } = [];
    public ConcurrentQueue<string> OpenedViews { get; } = new();
    public Func<string, PackageDevelopmentViewQuery, CancellationToken, Task<PackageDevelopmentViewPage>>? OnQueryView { get; set; }
    public Task<IReadOnlyList<PackageDevelopmentView>> GetViewsAsync(CancellationToken cancellationToken = default) => Task.FromResult(Views);
    public Task OpenViewAsync(string viewId, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); OpenedViews.Enqueue(viewId); return Task.CompletedTask; }
    public Task<PackageDevelopmentViewPage> QueryViewAsync(string viewId, PackageDevelopmentViewQuery query, CancellationToken cancellationToken = default)
        => OnQueryView?.Invoke(viewId, query, cancellationToken) ?? throw new NotSupportedException();
    public Task<PackageDevelopmentViewSnapshot> InspectViewAsync(string viewId, CancellationToken cancellationToken = default)
        => OnInspectView?.Invoke(viewId, cancellationToken) ?? throw new NotSupportedException();
    public Task<PackageDevelopmentViewInputResult> InteractViewAsync(PackageDevelopmentViewInput request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ViewInputs.Enqueue(request);
        return OnViewInput?.Invoke(request, cancellationToken) ?? Task.FromResult(new PackageDevelopmentViewInputResult(request.OperationId, "dispatched", false));
    }
    private readonly Guid _runtimeId = Guid.NewGuid();
    public async Task<PackageDevelopmentRevision> ReloadAsync(string operationId, string sourceContentIdentity, CancellationToken cancellationToken = default)
    {
        Reloads.Enqueue((operationId, sourceContentIdentity));
        if (OnReload is { } reload) return await reload(operationId, sourceContentIdentity, cancellationToken);
        var applied = new PackageDevelopmentRevision(_runtimeId, 5, sourceContentIdentity);
        Status = Status with { RuntimeRevision = applied, AppliedRevision = applied };
        return applied;
    }
    public async Task<PackageDevelopmentStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (BeforeGet is { } before) await before(cancellationToken);
        return Status;
    }
    public Task SetWatchAsync(bool watch, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        WatchRequests.Enqueue(watch);
        Status = Status with { RequestedWatch = watch, WatchEnabled = watch || OtherOwnerWatching };
        return Task.CompletedTask;
    }
    public async ValueTask DisposeAsync()
    {
        if (BeforeDispose is { } before) await before();
        OnDispose?.Invoke(); DisposeCount++; Disposed = true;
    }
}

internal sealed class BuilderUiBackgroundQueue : IBackgroundTaskQueue
{
    private readonly object _sync = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<Guid, Item> _items = [];
    private EventHandler<BackgroundTaskChangedEventArgs>? _changed;
    public bool AutoStart { get; set; } = true;
    public bool PublishEvents { get; set; } = true;
    public Action<BackgroundTaskSnapshot>? BeforeEnqueueReturns { get; set; }
    public int SubscriberCount { get; private set; }
    public int DelegateInvocations;
    public event EventHandler<BackgroundTaskChangedEventArgs>? TaskChanged
    {
        add { lock (_sync) { _changed += value; SubscriberCount++; } }
        remove { lock (_sync) { _changed -= value; SubscriberCount--; } }
    }
    public BackgroundTaskSnapshot Enqueue(BackgroundTaskRequest request)
    {
        var snapshot = new BackgroundTaskSnapshot(Guid.NewGuid(), request.Title, request.GroupKey, request.Indicator,
            request.ConcurrencyMode, BackgroundTaskState.Queued, "Queued", null, request.CanCancel,
            request.Metadata ?? new Dictionary<string, string>(), null, DateTimeOffset.UtcNow, null, null);
        var item = new Item(request, snapshot);
        lock (_sync) _items.Add(snapshot.TaskId, item);
        Publish(snapshot);
        BeforeEnqueueReturns?.Invoke(snapshot);
        if (AutoStart) _ = Task.Run(() => ExecuteAsync(item));
        return snapshot; // Deliberately stale when a terminal event occurred during enrollment.
    }
    public IReadOnlyList<BackgroundTaskSnapshot> ListTasks(string? groupKey = null)
    {
        lock (_sync) return _items.Values.Select(item => item.Snapshot).Where(item => groupKey is null || item.GroupKey == groupKey).ToArray();
    }
    public bool Cancel(Guid processId)
    {
        Item item;
        BackgroundTaskSnapshot snapshot;
        lock (_sync)
        {
            if (!_items.TryGetValue(processId, out item!) || !item.Snapshot.CanCancel) return false;
            var queued = item.Snapshot.State == BackgroundTaskState.Queued;
            snapshot = item.Snapshot = item.Snapshot with { State = queued ? BackgroundTaskState.Cancelled : BackgroundTaskState.Cancelling,
                StatusText = queued ? "Cancelled" : "Cancelling", CompletedAtUtc = queued ? DateTimeOffset.UtcNow : null };
        }
        item.Cancellation.Cancel();
        Publish(snapshot);
        return true;
    }
    public void FailQueued(Guid id)
    {
        BackgroundTaskSnapshot snapshot;
        lock (_sync) snapshot = _items[id].Snapshot = _items[id].Snapshot with
        { State = BackgroundTaskState.Failed, ErrorMessage = "Host rejected queued work", CompletedAtUtc = DateTimeOffset.UtcNow };
        Publish(snapshot);
    }
    public Task StartAsync(Guid id) { lock (_sync) return Task.Run(() => ExecuteAsync(_items[id])); }
    public Task InvokeStaleDelegateAsync(Guid id)
    {
        Item item;
        lock (_sync) item = _items[id];
        return item.Request.ExecuteAsync(Context(item));
    }
    private BackgroundTaskContext Context(Item item) => new(item.Cancellation.Token,
        text => Progress(item, text, item.Snapshot.ProgressPercent),
        (percent, text) => Progress(item, text, percent), text => Progress(item, text, null));
    private async Task ExecuteAsync(Item item)
    {
        await _gate.WaitAsync();
        try
        {
            lock (_sync)
            {
                if (item.Snapshot.State != BackgroundTaskState.Queued) return;
                item.Snapshot = item.Snapshot with { State = BackgroundTaskState.Running, StartedAtUtc = DateTimeOffset.UtcNow };
            }
            Publish(item.Snapshot);
            Interlocked.Increment(ref DelegateInvocations);
            try
            {
                await item.Request.ExecuteAsync(Context(item));
                Finish(item, item.Cancellation.IsCancellationRequested ? BackgroundTaskState.Cancelled : BackgroundTaskState.Completed);
            }
            catch (OperationCanceledException error) when (item.Cancellation.IsCancellationRequested && error.CancellationToken == item.Cancellation.Token)
            { Finish(item, BackgroundTaskState.Cancelled); }
            catch (Exception error) { Finish(item, BackgroundTaskState.Failed, error.Message); }
        }
        finally { _gate.Release(); }
    }
    private void Progress(Item item, string? text, double? progress)
    {
        BackgroundTaskSnapshot snapshot;
        lock (_sync) snapshot = item.Snapshot = item.Snapshot with { StatusText = text ?? item.Snapshot.StatusText, ProgressPercent = progress };
        Publish(snapshot);
    }
    private void Finish(Item item, BackgroundTaskState state, string? error = null)
    {
        BackgroundTaskSnapshot snapshot;
        lock (_sync) snapshot = item.Snapshot = item.Snapshot with { State = state, ErrorMessage = error, CompletedAtUtc = DateTimeOffset.UtcNow };
        Publish(snapshot);
    }
    private void Publish(BackgroundTaskSnapshot snapshot)
    {
        EventHandler<BackgroundTaskChangedEventArgs>? changed;
        lock (_sync) changed = _changed;
        if (PublishEvents) changed?.Invoke(this, new(snapshot));
    }
    private sealed class Item(BackgroundTaskRequest request, BackgroundTaskSnapshot snapshot)
    {
        public BackgroundTaskRequest Request { get; } = request;
        public BackgroundTaskSnapshot Snapshot { get; set; } = snapshot;
        public CancellationTokenSource Cancellation { get; } = new();
    }
}
