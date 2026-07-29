using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.PackageViews;
using Sunder.Package.Agent.Services;
using Sunder.Package.Agent.Storage;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Runtime;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class WorkspaceEditorFailureContainmentTests
{
    [Fact]
    public async Task DiscoveryRuntimeFailure_KeepsHealthySectionAndLoadedContribution_ThenRetryReplacesError()
    {
        var failing = new ScriptedWorkspaceEditorContributor("local", "local-settings", "Local settings");
        failing.EnqueueDiscovery(_ => ValueTask.FromException<IReadOnlyList<AgentEditorSection>>(
            RuntimeFailure("runtime.editor.discovery", "discovery-123")));
        var healthy = new ScriptedWorkspaceEditorContributor("local", "healthy-settings", "Healthy settings");
        using var host = await EditorTestHost.CreateAsync(
            (failing, "local.package"),
            (healthy, "healthy.package"));

        var error = Assert.Single(host.ViewModel.EditorSections, static section => section.IsError);
        Assert.Equal("Healthy settings", Assert.Single(host.ViewModel.EditorSections, static section => !section.IsError).Title);
        Assert.Contains("runtime.editor.discovery", error.DiagnosticText, StringComparison.Ordinal);
        Assert.Contains("discovery-123", error.DiagnosticText, StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive runtime detail", error.ErrorMessage, StringComparison.Ordinal);
        Assert.Empty(host.Catalog.FaultReports);
        Assert.Contains(
            failing,
            host.Catalog.GetExtensions(PackageExtensionPoints.WorkspaceEditorContributors));

        error.RetryCommand!.Execute(null);
        await host.ViewModel.CurrentEditorSectionRetry.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.DoesNotContain(host.ViewModel.EditorSections, static section => section.IsError);
        Assert.Contains(host.ViewModel.EditorSections, section => section.Title == "Local settings");
        Assert.Contains(host.ViewModel.EditorSections, section => section.Title == "Healthy settings");
        Assert.Equal(2, failing.DiscoveryCount);
        Assert.Empty(host.Catalog.FaultReports);
    }

    [Fact]
    public async Task SaveRuntimeFailure_UpdatesOnlyFailedSection_SavesHealthySection_AndRetrySucceeds()
    {
        var failing = new ScriptedWorkspaceEditorContributor("local", "local-settings", "Local settings");
        failing.EnqueueSave(_ => ValueTask.FromException<AgentEditorSaveResult>(
            RuntimeFailure("runtime.editor.save", "save-456")));
        var healthy = new ScriptedWorkspaceEditorContributor("local", "healthy-settings", "Healthy settings");
        using var host = await EditorTestHost.CreateAsync(
            (failing, "local.package"),
            (healthy, "healthy.package"));
        var healthySection = Assert.Single(host.ViewModel.EditorSections, section => section.Title == "Healthy settings");

        await host.ViewModel.SaveWorkspaceCommand.ExecuteAsync(null);

        var error = Assert.Single(host.ViewModel.EditorSections, static section => section.IsError);
        Assert.Contains("runtime.editor.save", error.DiagnosticText, StringComparison.Ordinal);
        Assert.Same(healthySection, Assert.Single(host.ViewModel.EditorSections, section => section.Title == "Healthy settings"));
        Assert.Equal(1, failing.SaveCount);
        Assert.Equal(1, healthy.SaveCount);
        Assert.Empty(host.Catalog.FaultReports);

        error.RetryCommand!.Execute(null);
        await host.ViewModel.CurrentEditorSectionRetry.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.DoesNotContain(host.ViewModel.EditorSections, static section => section.IsError);
        Assert.Contains(host.ViewModel.EditorSections, section => section.Title == "Local settings");
        Assert.Same(healthySection, Assert.Single(host.ViewModel.EditorSections, section => section.Title == "Healthy settings"));
        Assert.Equal(2, failing.SaveCount);
    }

    [Fact]
    public async Task FieldRefreshRuntimeFailure_UpdatesOnlyOwningSection_AndRetryRehydratesIt()
    {
        var failing = new ScriptedWorkspaceEditorContributor("local", "local-settings", "Local settings");
        failing.EnqueueDiscovery(_ => ValueTask.FromResult<IReadOnlyList<AgentEditorSection>>(
            [CreateRefreshSection("local-settings", "Local settings", "first")]));
        failing.EnqueueDiscovery(_ => ValueTask.FromException<IReadOnlyList<AgentEditorSection>>(
            RuntimeFailure("runtime.editor.refresh", "refresh-789")));
        failing.EnqueueDiscovery(_ => ValueTask.FromResult<IReadOnlyList<AgentEditorSection>>(
            [CreateRefreshSection("local-settings", "Local settings", "second")]));
        var healthy = new ScriptedWorkspaceEditorContributor("local", "healthy-settings", "Healthy settings");
        using var host = await EditorTestHost.CreateAsync(
            (failing, "local.package"),
            (healthy, "healthy.package"));
        var healthySection = Assert.Single(host.ViewModel.EditorSections, section => section.Title == "Healthy settings");
        var localSection = Assert.Single(host.ViewModel.EditorSections, section => section.Title == "Local settings");
        var field = Assert.IsType<AgentEditorSelectFieldViewModel>(Assert.Single(localSection.Fields));

        await host.ViewModel.ExecuteEditorActionAsync(Assert.Single(field.IconActions));

        var error = Assert.Single(host.ViewModel.EditorSections, static section => section.IsError);
        Assert.Contains("runtime.editor.refresh", error.DiagnosticText, StringComparison.Ordinal);
        Assert.Same(healthySection, Assert.Single(host.ViewModel.EditorSections, section => section.Title == "Healthy settings"));
        Assert.Empty(host.Catalog.FaultReports);

        error.RetryCommand!.Execute(null);
        await host.ViewModel.CurrentEditorSectionRetry.WaitAsync(TimeSpan.FromSeconds(2));

        var refreshed = Assert.Single(host.ViewModel.EditorSections, section => section.Title == "Local settings");
        var refreshedField = Assert.IsType<AgentEditorSelectFieldViewModel>(Assert.Single(refreshed.Fields));
        Assert.Equal("second", Assert.Single(refreshedField.Options).Value);
        Assert.Same(healthySection, Assert.Single(host.ViewModel.EditorSections, section => section.Title == "Healthy settings"));
    }

    [Fact]
    public async Task ContributorRetirementCancellation_BecomesUnavailableSectionWithoutFaultingOwner()
    {
        var retiring = new BlockingRetirementWorkspaceEditorContributor("local");
        var healthy = new ScriptedWorkspaceEditorContributor("local", "healthy-settings", "Healthy settings");
        using var host = await EditorTestHost.CreateWithoutWaitingAsync(
            (retiring, "local.package"),
            (healthy, "healthy.package"));
        await retiring.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var retirement = host.Catalog.RetireExtensionAsync(
            PackageExtensionPoints.WorkspaceEditorContributors,
            retiring);
        await host.ViewModel.CurrentEditorSectionRefresh.WaitAsync(TimeSpan.FromSeconds(2));
        await retirement.WaitAsync(TimeSpan.FromSeconds(2));

        var error = Assert.Single(host.ViewModel.EditorSections, static section => section.IsError);
        Assert.Contains("package-unavailable", error.DiagnosticText, StringComparison.Ordinal);
        Assert.Equal("Healthy settings", Assert.Single(host.ViewModel.EditorSections, static section => !section.IsError).Title);
        Assert.Empty(host.Catalog.FaultReports);
    }

    [Fact]
    public async Task UnknownContributorException_FaultsExactOwnerWithoutMisattributingAgent()
    {
        var failing = new ScriptedWorkspaceEditorContributor("local", "local-settings", "Local settings");
        var invariant = new InvalidOperationException("local contributor invariant");
        failing.EnqueueDiscovery(_ => ValueTask.FromException<IReadOnlyList<AgentEditorSection>>(invariant));
        var healthy = new ScriptedWorkspaceEditorContributor("local", "healthy-settings", "Healthy settings");
        using var host = await EditorTestHost.CreateAsync(
            (failing, "local.package"),
            (healthy, "healthy.package"));

        var report = Assert.Single(host.Catalog.FaultReports);
        Assert.Equal("local.package", report.PackageId);
        Assert.Same(invariant, report.Exception);
        Assert.DoesNotContain(host.Catalog.FaultReports, report => report.PackageId == "sunder.package.agent");
        Assert.Equal("Healthy settings", Assert.Single(host.ViewModel.EditorSections, static section => !section.IsError).Title);
        Assert.Contains("package-invariant-failure", Assert.Single(host.ViewModel.EditorSections, static section => section.IsError).DiagnosticText);
        await host.ViewModel.CurrentEditorSectionRefresh;
    }

    [Theory]
    [InlineData("null")]
    [InlineData("lazy")]
    [InlineData("concurrent-mutation")]
    [InlineData("over-budget")]
    public async Task MalformedContributorGraph_FaultsOnlyExactOwnerAndKeepsHealthySection(string failureKind)
    {
        var malformed = new ScriptedWorkspaceEditorContributor("local", "malformed-settings", "Malformed settings");
        malformed.EnqueueDiscovery(_ => ValueTask.FromResult(CreateMalformedGraph(failureKind)));
        var healthy = new ScriptedWorkspaceEditorContributor("local", "healthy-settings", "Healthy settings");

        using var host = await EditorTestHost.CreateAsync(
            (malformed, "malformed.package"),
            (healthy, "healthy.package"));

        var report = Assert.Single(host.Catalog.FaultReports);
        Assert.Equal("malformed.package", report.PackageId);
        Assert.Equal("Healthy settings", Assert.Single(
            host.ViewModel.EditorSections,
            static section => !section.IsError).Title);
        Assert.Contains(
            "package-invariant-failure",
            Assert.Single(host.ViewModel.EditorSections, static section => section.IsError).DiagnosticText,
            StringComparison.Ordinal);
    }

    [Fact]
    public void GraphSnapshot_DeepClonesAllNestedCollections()
    {
        var parameters = new Dictionary<string, string?> { ["tab"] = "runtime" };
        var options = new List<AgentEditorOption> { new("one", "One", "First") };
        var items = new List<AgentEditorListItem>
        {
            new("item", "/workspace", IsDefault: true) { SecondaryValue = "/cache" },
        };
        var actions = new List<AgentEditorAction>
        {
            new("settings", "Settings", AgentEditorActionKind.OpenPackageSettings, "target.package", parameters),
        };
        var fields = new List<AgentEditorField>
        {
            new("field", "Field", AgentEditorFieldKind.PathList, Options: options, Items: items)
            {
                Actions = actions,
            },
        };
        var sections = new List<AgentEditorSection>
        {
            new("section", "Section", "Description", fields),
        };

        var snapshot = AgentEditorGraphSnapshot.Capture(sections);
        parameters.Clear();
        options.Clear();
        items.Clear();
        actions.Clear();
        fields.Clear();
        sections.Clear();

        var field = Assert.Single(Assert.Single(snapshot).Fields);
        Assert.Equal("one", Assert.Single(field.Options!).Value);
        Assert.Equal("/workspace", Assert.Single(field.Items!).Value);
        Assert.Equal("runtime", Assert.Single(Assert.Single(field.Actions!).Parameters!).Value);
    }

    [Fact]
    public async Task Discovery_MaterializesGraphBeforeContributionLeaseIsReleased()
    {
        var state = new LeaseTrackingState();
        var contributor = new LeaseCheckingContributor(state);
        var reference = new LeaseTrackingReference(contributor, state);
        var now = DateTimeOffset.UtcNow;
        var context = new AgentWorkspaceEditorContext(
            new AgentWorkspaceRecord("workspace", "Workspace", null, now, now),
            "local",
            "binding");

        var result = await AgentWorkspaceEditorInvocation.InvokeAsync(
            reference,
            RejectingInvocationCatalog.Instance,
            AgentEditorInvocationOperation.Discovery,
            CancellationToken.None,
            (leasedContributor, token) => AgentWorkspaceEditorInvocation.GetSectionsSnapshotAsync(
                leasedContributor,
                context,
                token));

        Assert.True(result.Success);
        Assert.Equal("leased", Assert.Single(result.Result).SectionId);
        Assert.False(state.IsActive);
    }

    public static TheoryData<string> RetrySupersessionKinds => new()
    {
        "workspace",
        "target",
        "adaptive",
    };

    [Theory]
    [MemberData(nameof(RetrySupersessionKinds))]
    public async Task RetryCompletion_CannotApplyAfterEditorIntentIsSuperseded(string supersessionKind)
    {
        var contributor = new ScriptedWorkspaceEditorContributor("local", "local-settings", "Local settings");
        contributor.EnqueueDiscovery(_ => ValueTask.FromException<IReadOnlyList<AgentEditorSection>>(
            RuntimeFailure("runtime.editor.discovery", "stale-retry")));
        var retryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRetry = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        contributor.EnqueueDiscovery(async _ =>
        {
            retryStarted.TrySetResult();
            await releaseRetry.Task;
            return [new AgentEditorSection("local-settings", "Recovered settings", null, [])];
        });
        using var host = await EditorTestHost.CreateWithSecondWorkspaceAsync(
            (contributor, "local.package"));
        var error = Assert.Single(host.ViewModel.EditorSections, static section => section.IsError);

        error.RetryCommand!.Execute(null);
        await retryStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        switch (supersessionKind)
        {
            case "workspace":
                host.ViewModel.ActivateWorkspace(host.ViewModel.Workspaces.Single(workspace =>
                    workspace.WorkspaceId == host.SecondWorkspace!.WorkspaceId));
                break;
            case "target":
                host.ViewModel.SelectedExecutionTarget = ExecutionTargetOption.Unconfigured;
                host.ViewModel.SelectedExecutionTarget = host.ViewModel.ExecutionTargets.Single(target =>
                    string.Equals(target.TargetId, "local", StringComparison.OrdinalIgnoreCase));
                break;
            case "adaptive":
                host.ViewModel.IsCompactLayout = true;
                host.ViewModel.IsCompactLayout = false;
                break;
            default:
                throw new InvalidOperationException($"Unknown supersession kind '{supersessionKind}'.");
        }

        releaseRetry.TrySetResult();
        await host.ViewModel.CurrentEditorSectionRetry.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.DoesNotContain(host.ViewModel.EditorSections, section => section.Title == "Recovered settings");
    }

    private static PackageRuntimeInvocationException RuntimeFailure(string code, string correlationId)
        => new(
            code,
            isTransient: true,
            statusCode: 503,
            correlationId);

    private static IReadOnlyList<AgentEditorSection> CreateMalformedGraph(string failureKind)
        => failureKind switch
        {
            "null" => null!,
            "lazy" =>
            [
                new AgentEditorSection(
                    "lazy",
                    "Lazy",
                    null,
                    new ThrowingReadOnlyList<AgentEditorField>(new InvalidOperationException("lazy field failure"))),
            ],
            "concurrent-mutation" => new MutatingReadOnlyList<AgentEditorSection>(
                [new AgentEditorSection("mutable", "Mutable", null, [])]),
            "over-budget" => Enumerable.Range(0, 33)
                .Select(index => new AgentEditorSection($"section-{index}", $"Section {index}", null, []))
                .ToArray(),
            _ => throw new ArgumentOutOfRangeException(nameof(failureKind), failureKind, null),
        };

    private static AgentEditorSection CreateRefreshSection(
        string sectionId,
        string title,
        string option)
        => new(
            sectionId,
            title,
            null,
            [
                new AgentEditorField(
                    "image",
                    "Image",
                    AgentEditorFieldKind.Select,
                    Options: [new AgentEditorOption(option, option)])
                {
                    Actions =
                    [
                        new AgentEditorAction(
                            "refresh-image",
                            "Refresh images",
                            AgentEditorActionKind.RefreshField),
                    ],
                },
            ]);

    private sealed class EditorTestHost : IDisposable
    {
        private readonly RegressionTestPackageScope _scope;

        private EditorTestHost(
            RegressionTestPackageScope scope,
            RegressionTestExtensionCatalog catalog,
            AgentWorkspacesViewModel viewModel,
            AgentWorkspaceRecord? secondWorkspace)
        {
            _scope = scope;
            Catalog = catalog;
            ViewModel = viewModel;
            SecondWorkspace = secondWorkspace;
        }

        public RegressionTestExtensionCatalog Catalog { get; }

        public AgentWorkspacesViewModel ViewModel { get; }

        public AgentWorkspaceRecord? SecondWorkspace { get; }

        public static Task<EditorTestHost> CreateAsync(
            params (IAgentWorkspaceEditorContributor Contributor, string PackageId)[] contributors)
            => CreateAsync(waitForEditor: true, createSecondWorkspace: false, contributors);

        public static Task<EditorTestHost> CreateWithSecondWorkspaceAsync(
            params (IAgentWorkspaceEditorContributor Contributor, string PackageId)[] contributors)
            => CreateAsync(waitForEditor: true, createSecondWorkspace: true, contributors);

        public static Task<EditorTestHost> CreateWithoutWaitingAsync(
            params (IAgentWorkspaceEditorContributor Contributor, string PackageId)[] contributors)
            => CreateAsync(waitForEditor: false, createSecondWorkspace: false, contributors);

        private static async Task<EditorTestHost> CreateAsync(
            bool waitForEditor,
            bool createSecondWorkspace,
            params (IAgentWorkspaceEditorContributor Contributor, string PackageId)[] contributors)
        {
            var scope = RegressionTestPackageScope.Create();
            try
            {
                var catalog = new RegressionTestExtensionCatalog();
                catalog.AddExtension(
                    PackageExtensionPoints.ExecutionTargets,
                    new TestExecutionTarget("local"),
                    "local.package");
                foreach (var (contributor, packageId) in contributors)
                {
                    catalog.AddExtension(
                        PackageExtensionPoints.WorkspaceEditorContributors,
                        contributor,
                        packageId);
                }

                var workspaceService = new AgentWorkspaceService(new AgentLocalStore(scope.Context));
                var firstWorkspace = workspaceService.CreateWorkspace("First workspace");
                workspaceService.SavePrimaryExecutionBinding(firstWorkspace.WorkspaceId, "local");
                var secondWorkspace = createSecondWorkspace
                    ? workspaceService.CreateWorkspace("Second workspace")
                    : null;
                var targetService = new AgentExecutionTargetService(catalog);
                var viewModel = new AgentWorkspacesViewModel(
                    workspaceService,
                    targetService,
                    catalog,
                    extensionInvocationCatalog: catalog);
                await viewModel.InitializeAsync();
                if (waitForEditor)
                {
                    await viewModel.CurrentEditorSectionRefresh.WaitAsync(TimeSpan.FromSeconds(2));
                }
                return new EditorTestHost(scope, catalog, viewModel, secondWorkspace);
            }
            catch
            {
                scope.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            ViewModel.Dispose();
            _scope.Dispose();
        }
    }

    private sealed class ScriptedWorkspaceEditorContributor(
        string targetId,
        string sectionId,
        string title) : IAgentWorkspaceEditorContributor
    {
        private readonly Queue<Func<CancellationToken, ValueTask<IReadOnlyList<AgentEditorSection>>>> _discoveries = [];
        private readonly Queue<Func<CancellationToken, ValueTask<AgentEditorSaveResult>>> _saves = [];

        public string ContributorId => sectionId + "-contributor";

        public int DiscoveryCount { get; private set; }

        public int SaveCount { get; private set; }

        public bool CanEdit(AgentWorkspaceEditorContext context)
            => string.Equals(context.TargetId, targetId, StringComparison.OrdinalIgnoreCase);

        public void EnqueueDiscovery(
            Func<CancellationToken, ValueTask<IReadOnlyList<AgentEditorSection>>> discovery)
            => _discoveries.Enqueue(discovery);

        public void EnqueueSave(Func<CancellationToken, ValueTask<AgentEditorSaveResult>> save)
            => _saves.Enqueue(save);

        public ValueTask<IReadOnlyList<AgentEditorSection>> GetSectionsAsync(
            AgentWorkspaceEditorContext context,
            CancellationToken cancellationToken = default)
        {
            DiscoveryCount++;
            return _discoveries.Count > 0
                ? _discoveries.Dequeue()(cancellationToken)
                : ValueTask.FromResult<IReadOnlyList<AgentEditorSection>>(
                    [new AgentEditorSection(sectionId, title, null, [])]);
        }

        public ValueTask<AgentEditorSaveResult> SaveSectionAsync(
            AgentWorkspaceEditorContext context,
            AgentEditorSaveRequest request,
            CancellationToken cancellationToken = default)
        {
            SaveCount++;
            return _saves.Count > 0
                ? _saves.Dequeue()(cancellationToken)
                : ValueTask.FromResult(AgentEditorSaveResult.Ok("Saved."));
        }
    }

    private sealed class BlockingRetirementWorkspaceEditorContributor(string targetId)
        : IAgentWorkspaceEditorContributor
    {
        public string ContributorId => "retiring-contributor";

        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool CanEdit(AgentWorkspaceEditorContext context)
            => string.Equals(context.TargetId, targetId, StringComparison.OrdinalIgnoreCase);

        public async ValueTask<IReadOnlyList<AgentEditorSection>> GetSectionsAsync(
            AgentWorkspaceEditorContext context,
            CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return [];
        }

        public ValueTask<AgentEditorSaveResult> SaveSectionAsync(
            AgentWorkspaceEditorContext context,
            AgentEditorSaveRequest request,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(AgentEditorSaveResult.Ok("Saved."));
    }

    private sealed class ThrowingReadOnlyList<T>(Exception exception) : IReadOnlyList<T>
    {
        public int Count => 1;

        public T this[int index] => throw exception;

        public IEnumerator<T> GetEnumerator() => throw exception;

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class MutatingReadOnlyList<T>(IEnumerable<T> values) : IReadOnlyList<T>
    {
        private readonly List<T> _values = [.. values];

        public int Count => _values.Count;

        public T this[int index]
        {
            get
            {
                _values.Clear();
                return _values[index];
            }
        }

        public IEnumerator<T> GetEnumerator() => _values.GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class LeaseTrackingState
    {
        public bool IsActive { get; set; }
    }

    private sealed class LeaseCheckingContributor(LeaseTrackingState state) : IAgentWorkspaceEditorContributor
    {
        public string ContributorId => "lease-checking";

        public bool CanEdit(AgentWorkspaceEditorContext context) => true;

        public ValueTask<IReadOnlyList<AgentEditorSection>> GetSectionsAsync(
            AgentWorkspaceEditorContext context,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<AgentEditorSection>>(
                new LeaseCheckingReadOnlyList<AgentEditorSection>(
                    state,
                    [new AgentEditorSection("leased", "Leased", null, [])]));

        public ValueTask<AgentEditorSaveResult> SaveSectionAsync(
            AgentWorkspaceEditorContext context,
            AgentEditorSaveRequest request,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(AgentEditorSaveResult.Ok("Saved."));
    }

    private sealed class LeaseCheckingReadOnlyList<T>(LeaseTrackingState state, IReadOnlyList<T> values)
        : IReadOnlyList<T>
    {
        public int Count
        {
            get
            {
                Assert.True(state.IsActive);
                return values.Count;
            }
        }

        public T this[int index]
        {
            get
            {
                Assert.True(state.IsActive);
                return values[index];
            }
        }

        public IEnumerator<T> GetEnumerator() => values.GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class LeaseTrackingReference(
        IAgentWorkspaceEditorContributor contributor,
        LeaseTrackingState state) : IPackageExtensionReference<IAgentWorkspaceEditorContributor>
    {
        public bool TryAcquire(
            [System.Diagnostics.CodeAnalysis.NotNullWhen(true)]
            out IPackageExtensionLease<IAgentWorkspaceEditorContributor>? lease)
        {
            lease = new LeaseTrackingLease(contributor, state);
            return true;
        }
    }

    private sealed class LeaseTrackingLease : IPackageExtensionLease<IAgentWorkspaceEditorContributor>
    {
        private readonly LeaseTrackingState _state;
        private IAgentWorkspaceEditorContributor? _contributor;

        public LeaseTrackingLease(IAgentWorkspaceEditorContributor contributor, LeaseTrackingState state)
        {
            _contributor = contributor;
            _state = state;
            _state.IsActive = true;
        }

        public string PackageId => "leased.package";

        public IAgentWorkspaceEditorContributor Contribution
            => _contributor ?? throw new ObjectDisposedException(nameof(LeaseTrackingLease));

        public CancellationToken RetirementToken => CancellationToken.None;

        public void Dispose()
        {
            _contributor = null;
            _state.IsActive = false;
        }
    }

    private sealed class RejectingInvocationCatalog : IPackageExtensionInvocationCatalog
    {
        public static RejectingInvocationCatalog Instance { get; } = new();

        public IReadOnlyList<IPackageExtensionReference<TContract>> GetExtensionReferences<TContract>(
            PackageExtensionPoint<TContract> extensionPoint)
            => [];

        public bool TryReportInvariantViolation<TContract>(
            IPackageExtensionReference<TContract> reference,
            Exception exception)
            => false;
    }

    private sealed class TestExecutionTarget(string targetId) : IAgentExecutionTarget
    {
        public AgentExecutionTargetDescriptor Descriptor { get; } =
            new(targetId, targetId, "Test target", null, SupportsShell: false, SupportsFiles: false);

        public ValueTask<AgentExecutionTargetReadiness> GetReadinessAsync(
            AgentExecutionTargetContext context,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentExecutionTargetReadiness(
                targetId,
                targetId,
                AgentExecutionTargetReadinessStatus.Ready,
                "Ready."));

        public ValueTask<AgentExecutionShellDescriptor> GetShellAsync(
            AgentExecutionTargetContext context,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<AgentResolvedResource> ResolveFileResourceAsync(
            AgentExecutionTargetContext context,
            string path,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<AgentShellCommandResult> ExecuteShellAsync(
            AgentExecutionTargetContext context,
            AgentShellCommandRequest request,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<AgentFileReadResult> ReadFileAsync(
            AgentExecutionTargetContext context,
            AgentFileReadRequest request,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<AgentFileMutationResult> WriteFileAsync(
            AgentExecutionTargetContext context,
            AgentFileWriteRequest request,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<AgentFileMutationResult> DeleteFileAsync(
            AgentExecutionTargetContext context,
            AgentFileDeleteRequest request,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
