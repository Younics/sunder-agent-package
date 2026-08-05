using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.Services;
using Sunder.Package.Agent.Skills.Services;
using Sunder.Package.Agent.Storage;
using Sunder.Package.Agent.Tools.Files;
using Sunder.Package.Agent.Tools.Shell;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class ExecutionTargetDependencyRetirementTests
{
    private const string ToolSourcePackageId = "test.package.target-aware-source";

    [Fact]
    public async Task GeneratedBindings_StaleExactTargetPassesNoReferenceWithoutFaultingSourceCallbacks()
    {
        using var catalog = new RegressionTestExtensionCatalog();
        var target = new TestExecutionTarget();
        var source = new CapturingTargetConsumer();
        catalog.AddProvider(AgentRpcServices.ExecutionTargets, target, "test.package.target");
        catalog.AddProvider(AgentRpcServices.ToolSources, source, ToolSourcePackageId);
        catalog.AddProvider(AgentRpcServices.PromptContextContributors, source, ToolSourcePackageId);
        var targetReference = catalog.GetRequiredReference(AgentRpcServices.ExecutionTargets);
        var toolReference = catalog.GetRequiredReference(AgentRpcServices.ToolSources);
        var promptReference = catalog.GetRequiredReference(AgentRpcServices.PromptContextContributors);
        var (workspace, binding) = CreateWorkspace();

        await catalog.RetireProviderAsync(target);

        Assert.True(toolReference.TryAcquire(out var toolLease));
        Assert.True(promptReference.TryAcquire(out var promptLease));
        using (toolLease)
        using (promptLease)
        {
            var descriptors = await toolLease.Service.ListToolsAsync(
                CreateSourceContext(workspace, binding, targetReference));
            var readiness = await toolLease.Service.GetReadinessAsync(
                CapturingTargetConsumer.Descriptor.ToolId,
                CreateSourceContext(workspace, binding, targetReference));
            var result = await toolLease.Service.ExecuteAsync(
                CreateExecutionContext(workspace, binding, targetReference),
                new AgentToolRequest(CapturingTargetConsumer.Descriptor.ToolId, "{}"));
            var contribution = await promptLease.Service.ContributeContextAsync(
                CreatePromptRequest(workspace, binding, targetReference));

            Assert.Equal(CapturingTargetConsumer.Descriptor.ToolId, Assert.Single(descriptors).ToolId);
            Assert.Equal(AgentToolReadinessStatus.Failed, readiness?.Status);
            Assert.Equal(AgentToolResultErrorCodes.PackageUnavailable, result.ErrorCode);
            Assert.NotNull(contribution);
        }

        Assert.Equal(1, source.ListCount);
        Assert.Equal(1, source.ReadinessCount);
        Assert.Equal(1, source.ExecuteCount);
        Assert.Equal(1, source.ContextCount);
        Assert.All(source.ObservedTargetReferences, Assert.Null);
        Assert.True(toolReference.TryAcquire(out var survivingSourceLease));
        survivingSourceLease.Dispose();
    }

    [Fact]
    public async Task AgentToolCatalog_ListsDescriptorsWithoutPassingALiveExecutionTarget()
    {
        using var scope = RegressionTestPackageScope.Create();
        using var catalog = new RegressionTestExtensionCatalog();
        var target = new TestExecutionTarget();
        var source = new CapturingTargetConsumer();
        catalog.AddProvider(AgentRpcServices.ExecutionTargets, target, "test.package.target");
        catalog.AddProvider(AgentRpcServices.ToolSources, source, ToolSourcePackageId);
        var store = new AgentLocalStore(scope.Context);
        var sessions = new AgentSessionService(store, catalog);
        var workspaces = new AgentWorkspaceService(store, catalog, sessions);
        var workspace = workspaces.CreateWorkspace("Workspace");
        workspaces.SavePrimaryExecutionBinding(workspace.WorkspaceId, target.Descriptor.TargetId);
        var tools = new AgentToolService(
            sessions,
            workspaces,
            new AgentExecutionTargetService(catalog),
            catalog);

        var entries = await tools.ListToolCatalogAsync(workspace: workspace);

        Assert.Equal(CapturingTargetConsumer.Descriptor.ToolId, Assert.Single(entries).Descriptor.ToolId);
        Assert.Equal(1, source.ListCount);
        Assert.Equal(1, source.ReadinessCount);
        Assert.Null(source.ObservedTargetReferences[0]);
        Assert.NotNull(source.ObservedTargetReferences[1]);
    }

    [Fact]
    public async Task GeneratedBindings_UnavailableExactTargetLookupIsExpectedAbsence()
    {
        using var catalog = new RegressionTestExtensionCatalog();
        var target = new TestExecutionTarget();
        var source = new CapturingTargetConsumer();
        catalog.AddProvider(AgentRpcServices.ExecutionTargets, target, "test.package.target");
        catalog.AddProvider(AgentRpcServices.ToolSources, source, ToolSourcePackageId);
        catalog.AddProvider(AgentRpcServices.PromptContextContributors, source, ToolSourcePackageId);
        var targetReference = catalog.GetRequiredReference(AgentRpcServices.ExecutionTargets);
        var toolReference = catalog.GetRequiredReference(AgentRpcServices.ToolSources);
        var promptReference = catalog.GetRequiredReference(AgentRpcServices.PromptContextContributors);
        var (workspace, binding) = CreateWorkspace();
        Assert.True(toolReference.TryAcquire(out var toolLease));
        Assert.True(promptReference.TryAcquire(out var promptLease));
        catalog.SetProviderLookupFailure(ToolSourcePackageId, SunderRpcErrorKind.Unavailable);

        using (toolLease)
        using (promptLease)
        {
            Assert.Single(await toolLease.Service.ListToolsAsync(
                CreateSourceContext(workspace, binding, targetReference)));
            Assert.NotNull(await promptLease.Service.ContributeContextAsync(
                CreatePromptRequest(workspace, binding, targetReference)));
        }

        Assert.Equal(1, source.ListCount);
        Assert.Equal(1, source.ContextCount);
        Assert.All(source.ObservedTargetReferences, Assert.Null);
    }

    [Theory]
    [InlineData(SunderRpcErrorKind.PermissionDenied)]
    [InlineData(SunderRpcErrorKind.Validation)]
    [InlineData(SunderRpcErrorKind.DeadlineExceeded)]
    [InlineData(SunderRpcErrorKind.ProviderFaulted)]
    public async Task GeneratedBindings_StrictExactTargetLookupFailuresPropagate(SunderRpcErrorKind errorKind)
    {
        using var catalog = new RegressionTestExtensionCatalog();
        var target = new TestExecutionTarget();
        var source = new CapturingTargetConsumer();
        catalog.AddProvider(AgentRpcServices.ExecutionTargets, target, "test.package.target");
        catalog.AddProvider(AgentRpcServices.ToolSources, source, ToolSourcePackageId);
        catalog.AddProvider(AgentRpcServices.PromptContextContributors, source, ToolSourcePackageId);
        var targetReference = catalog.GetRequiredReference(AgentRpcServices.ExecutionTargets);
        var toolReference = catalog.GetRequiredReference(AgentRpcServices.ToolSources);
        var promptReference = catalog.GetRequiredReference(AgentRpcServices.PromptContextContributors);
        var (workspace, binding) = CreateWorkspace();
        Assert.True(toolReference.TryAcquire(out var toolLease));
        Assert.True(promptReference.TryAcquire(out var promptLease));
        catalog.SetProviderLookupFailure(ToolSourcePackageId, errorKind);

        using (toolLease)
        using (promptLease)
        {
            var toolFailure = await Assert.ThrowsAsync<SunderRpcException>(() =>
                toolLease.Service.ListToolsAsync(
                    CreateSourceContext(workspace, binding, targetReference)).AsTask());
            var promptFailure = await Assert.ThrowsAsync<SunderRpcException>(() =>
                promptLease.Service.ContributeContextAsync(
                    CreatePromptRequest(workspace, binding, targetReference)).AsTask());

            Assert.Equal(errorKind, toolFailure.Error.Kind);
            Assert.Equal(errorKind, promptFailure.Error.Kind);
        }

        Assert.Equal(0, source.ListCount);
        Assert.Equal(0, source.ContextCount);
    }

    [Fact]
    public async Task Shell_StaleTargetReturnsDomainOutcomesWithoutFallingBackToReplacement()
    {
        using var catalog = new RegressionTestExtensionCatalog();
        var target = new TestExecutionTarget();
        catalog.AddProvider(AgentRpcServices.ExecutionTargets, target);
        var targetReference = catalog.GetRequiredReference(AgentRpcServices.ExecutionTargets);
        var (workspace, binding) = CreateWorkspace();
        var source = new ShellToolSource();
        await catalog.RetireProviderAsync(target);
        var replacement = new TestExecutionTarget();
        catalog.AddProvider(AgentRpcServices.ExecutionTargets, replacement);

        var descriptors = await source.ListToolsAsync(CreateSourceContext(workspace, binding, targetReference));
        var readiness = await source.GetReadinessAsync(
            "shell",
            CreateSourceContext(workspace, binding, targetReference));
        var contribution = await source.ContributeContextAsync(
            CreatePromptRequest(
                workspace,
                binding,
                targetReference,
                availableTools: descriptors));
        var result = await source.ExecuteAsync(
            CreateExecutionContext(workspace, binding, targetReference),
            new AgentToolRequest("shell", "{\"command\":\"pwd\"}"));

        Assert.Single(descriptors);
        Assert.Equal(AgentToolReadinessStatus.Failed, readiness?.Status);
        Assert.Null(contribution);
        Assert.True(result.IsError);
        Assert.Equal("shell-target-required", result.ErrorCode);
        Assert.Equal(0, replacement.ShellExecutionCount);
    }

    [Fact]
    public async Task Files_StaleTargetReturnsFailedReadinessOmittedContextAndDomainExecutionError()
    {
        using var scope = RegressionTestPackageScope.Create();
        using var catalog = new RegressionTestExtensionCatalog();
        var target = new TestExecutionTarget();
        catalog.AddProvider(AgentRpcServices.ExecutionTargets, target);
        var targetReference = catalog.GetRequiredReference(AgentRpcServices.ExecutionTargets);
        var (workspace, binding) = CreateWorkspace();
        var source = new FilesToolSource(scope.Context);
        await catalog.RetireProviderAsync(target);

        var descriptors = await source.ListToolsAsync(CreateSourceContext(workspace, binding, targetReference));
        var readiness = await source.GetReadinessAsync(
            "read",
            CreateSourceContext(workspace, binding, targetReference));
        var contribution = await source.ContributeContextAsync(
            CreatePromptRequest(
                workspace,
                binding,
                targetReference,
                availableTools: descriptors));
        var result = await source.ExecuteAsync(
            CreateExecutionContext(workspace, binding, targetReference),
            new AgentToolRequest("read", "{\"path\":\"README.md\"}"));

        Assert.NotEmpty(descriptors);
        Assert.Equal(AgentToolReadinessStatus.Failed, readiness?.Status);
        Assert.Null(contribution);
        Assert.True(result.IsError);
        Assert.Equal("files-target-required", result.ErrorCode);
    }

    [Fact]
    public async Task Skills_StaleTargetKeepsHostContentAndOmitsOnlyExecutorPaths()
    {
        using var scope = RegressionTestPackageScope.Create();
        using var catalog = new RegressionTestExtensionCatalog();
        var profile = CreateProfileWithSkill();
        var feature = CreateSkillsFeature(scope, catalog, profile);
        var target = new TestExecutionTarget();
        catalog.AddProvider(AgentRpcServices.ExecutionTargets, target);
        var targetReference = catalog.GetRequiredReference(AgentRpcServices.ExecutionTargets);
        var (workspace, binding) = CreateWorkspace();
        await catalog.RetireProviderAsync(target);

        var contribution = await feature.ContributeContextAsync(
            CreatePromptRequest(workspace, binding, targetReference, profile));
        var result = await feature.ExecuteAsync(
            CreateExecutionContext(workspace, binding, targetReference, profile.ProfileId),
            new AgentToolRequest("skill", "{\"name\":\"test-skill\"}"));

        var block = Assert.Single(Assert.IsType<AgentPromptContextContribution>(contribution).Blocks);
        Assert.Contains("did not expose executor paths", block.Content, StringComparison.Ordinal);
        Assert.False(result.IsError, result.Summary);
        Assert.Contains("Executor path: not exposed", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Shell_TargetRetirementDuringCallbackReturnsPackageUnavailable()
    {
        using var catalog = new RegressionTestExtensionCatalog();
        var target = new BlockingExecutionTarget();
        catalog.AddProvider(AgentRpcServices.ExecutionTargets, target, "test.package.target");
        var targetReference = catalog.GetRequiredReference(AgentRpcServices.ExecutionTargets);
        var (workspace, binding) = CreateWorkspace();
        var execution = new ShellToolSource().ExecuteAsync(
            CreateExecutionContext(workspace, binding, targetReference),
            new AgentToolRequest("shell", "{\"command\":\"sleep\"}")).AsTask();
        await target.ShellStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var retirement = catalog.RetireProviderAsync(target);
        var result = await execution;
        await retirement;

        Assert.True(result.IsError);
        Assert.Equal(AgentToolResultErrorCodes.PackageUnavailable, result.ErrorCode);
    }

    [Fact]
    public async Task Files_TargetRetirementDuringCallbackReturnsPackageUnavailable()
    {
        using var scope = RegressionTestPackageScope.Create();
        using var catalog = new RegressionTestExtensionCatalog();
        var target = new BlockingExecutionTarget();
        catalog.AddProvider(AgentRpcServices.ExecutionTargets, target, "test.package.target");
        var targetReference = catalog.GetRequiredReference(AgentRpcServices.ExecutionTargets);
        var (workspace, binding) = CreateWorkspace();
        var execution = new FilesToolSource(scope.Context).ExecuteAsync(
            CreateExecutionContext(workspace, binding, targetReference),
            new AgentToolRequest("read", "{\"path\":\"README.md\"}")).AsTask();
        await target.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var retirement = catalog.RetireProviderAsync(target);
        var result = await execution;
        await retirement;

        Assert.True(result.IsError);
        Assert.Equal(AgentToolResultErrorCodes.PackageUnavailable, result.ErrorCode);
    }

    [Fact]
    public async Task Skills_TargetRetirementDuringContextCallbackOmitsExecutorPaths()
    {
        using var scope = RegressionTestPackageScope.Create();
        using var catalog = new RegressionTestExtensionCatalog();
        var profile = CreateProfileWithSkill();
        var feature = CreateSkillsFeature(scope, catalog, profile);
        var target = new BlockingExecutionTarget();
        catalog.AddProvider(AgentRpcServices.ExecutionTargets, target, "test.package.target");
        var targetReference = catalog.GetRequiredReference(AgentRpcServices.ExecutionTargets);
        var (workspace, binding) = CreateWorkspace();
        var contributionTask = feature.ContributeContextAsync(
            CreatePromptRequest(workspace, binding, targetReference, profile)).AsTask();
        await target.ResourcesStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var retirement = catalog.RetireProviderAsync(target);
        var contribution = await contributionTask;
        await retirement;

        var block = Assert.Single(Assert.IsType<AgentPromptContextContribution>(contribution).Blocks);
        Assert.Contains("did not expose executor paths", block.Content, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(SunderRpcErrorKind.PermissionDenied)]
    [InlineData(SunderRpcErrorKind.Validation)]
    [InlineData(SunderRpcErrorKind.DeadlineExceeded)]
    [InlineData(SunderRpcErrorKind.ProviderFaulted)]
    public async Task FirstPartySources_StrictTargetCallbackFailuresPropagate(SunderRpcErrorKind errorKind)
    {
        using var scope = RegressionTestPackageScope.Create();
        using var catalog = new RegressionTestExtensionCatalog();
        var profile = CreateProfileWithSkill();
        var feature = CreateSkillsFeature(scope, catalog, profile);
        var target = new FailingExecutionTarget(errorKind);
        catalog.AddProvider(AgentRpcServices.ExecutionTargets, target);
        var targetReference = catalog.GetRequiredReference(AgentRpcServices.ExecutionTargets);
        var (workspace, binding) = CreateWorkspace();

        var shellFailure = await Assert.ThrowsAsync<SunderRpcException>(() =>
            new ShellToolSource().GetReadinessAsync(
                "shell",
                CreateSourceContext(workspace, binding, targetReference)).AsTask());
        var filesFailure = await Assert.ThrowsAsync<SunderRpcException>(() =>
            new FilesToolSource(scope.Context).GetReadinessAsync(
                "read",
                CreateSourceContext(workspace, binding, targetReference)).AsTask());
        var skillsFailure = await Assert.ThrowsAsync<SunderRpcException>(() =>
            feature.ContributeContextAsync(
                CreatePromptRequest(workspace, binding, targetReference, profile)).AsTask());

        Assert.Equal(errorKind, shellFailure.Error.Kind);
        Assert.Equal(errorKind, filesFailure.Error.Kind);
        Assert.Equal(errorKind, skillsFailure.Error.Kind);
    }

    [Theory]
    [InlineData(SunderRpcErrorKind.PermissionDenied)]
    [InlineData(SunderRpcErrorKind.Validation)]
    [InlineData(SunderRpcErrorKind.DeadlineExceeded)]
    public async Task FirstPartyOperationalCallbacks_StrictTargetFailuresPropagate(SunderRpcErrorKind errorKind)
    {
        using var scope = RegressionTestPackageScope.Create();
        using var catalog = new RegressionTestExtensionCatalog();
        var profile = CreateProfileWithSkill();
        var feature = CreateSkillsFeature(scope, catalog, profile);
        var target = new FailingExecutionTarget(errorKind);
        catalog.AddProvider(AgentRpcServices.ExecutionTargets, target);
        var targetReference = catalog.GetRequiredReference(AgentRpcServices.ExecutionTargets);
        var (workspace, binding) = CreateWorkspace();

        var shellFailure = await Assert.ThrowsAsync<SunderRpcException>(() =>
            new ShellToolSource().ExecuteAsync(
                CreateExecutionContext(workspace, binding, targetReference),
                new AgentToolRequest("shell", "{\"command\":\"pwd\"}")).AsTask());
        var filesExecutionFailure = await Assert.ThrowsAsync<SunderRpcException>(() =>
            new FilesToolSource(scope.Context).ExecuteAsync(
                CreateExecutionContext(workspace, binding, targetReference),
                new AgentToolRequest("read", "{\"path\":\"README.md\"}")).AsTask());
        var filesPermissionFailure = await Assert.ThrowsAsync<SunderRpcException>(() =>
            new FilesToolSource(scope.Context).BuildPermissionRequestAsync(
                CreateExecutionContext(workspace, binding, targetReference),
                new AgentToolRequest("read", "{\"path\":\"README.md\"}")).AsTask());
        var skillsFailure = await Assert.ThrowsAsync<SunderRpcException>(() =>
            feature.ExecuteAsync(
                CreateExecutionContext(workspace, binding, targetReference, profile.ProfileId),
                new AgentToolRequest("skill", "{\"name\":\"test-skill\"}")).AsTask());

        Assert.Equal(errorKind, shellFailure.Error.Kind);
        Assert.Equal(errorKind, filesExecutionFailure.Error.Kind);
        Assert.Equal(errorKind, filesPermissionFailure.Error.Kind);
        Assert.Equal(errorKind, skillsFailure.Error.Kind);
    }

    [Fact]
    public async Task FirstPartySources_InternalTargetCallbackFailuresPropagate()
    {
        using var scope = RegressionTestPackageScope.Create();
        using var catalog = new RegressionTestExtensionCatalog();
        var profile = CreateProfileWithSkill();
        var feature = CreateSkillsFeature(scope, catalog, profile);
        var target = new FailingExecutionTarget(errorKind: null);
        catalog.AddProvider(AgentRpcServices.ExecutionTargets, target);
        var targetReference = catalog.GetRequiredReference(AgentRpcServices.ExecutionTargets);
        var (workspace, binding) = CreateWorkspace();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ShellToolSource().GetReadinessAsync(
                "shell",
                CreateSourceContext(workspace, binding, targetReference)).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new FilesToolSource(scope.Context).GetReadinessAsync(
                "read",
                CreateSourceContext(workspace, binding, targetReference)).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            feature.ContributeContextAsync(
                CreatePromptRequest(workspace, binding, targetReference, profile)).AsTask());
    }

    private static SkillsFeature CreateSkillsFeature(
        RegressionTestPackageScope scope,
        RegressionTestExtensionCatalog catalog,
        AgentProfileRecord profile)
    {
        var store = new SkillStore(scope.Context);
        var skillRoot = Path.Combine(store.SkillsRootPath, "test-skill");
        Directory.CreateDirectory(skillRoot);
        File.WriteAllText(Path.Combine(skillRoot, "SKILL.md"), """
            ---
            name: test-skill
            description: Test target retirement.
            ---
            # Test Skill
            Keep working when executor paths are unavailable.
            """);
        var now = DateTimeOffset.UtcNow;
        store.SaveSkill(new InstalledSkillRecord(
            "test-skill",
            "skills/test-skill",
            "test-skill",
            "Test target retirement.",
            null,
            null,
            "local",
            null,
            null,
            null,
            new string('a', 64),
            now,
            now,
            new Dictionary<string, string>(),
            []));
        catalog.AddProvider(AgentRpcServices.RuntimeCatalogs, new SingleProfileRuntimeCatalog(profile));
        return new SkillsFeature(store, catalog);
    }

    private static AgentProfileRecord CreateProfileWithSkill()
    {
        var now = DateTimeOffset.UtcNow;
        return new AgentProfileRecord(
            "profile",
            "Profile",
            null,
            null,
            null,
            null,
            null,
            null,
            now,
            now,
            [],
            [new AgentProfileSelectableCapabilityAssignmentRecord(
                "skill",
                "test-skill",
                "sunder.package.agent.skills")]);
    }

    private static (AgentWorkspaceRecord Workspace, AgentWorkspaceBindingRecord Binding) CreateWorkspace()
    {
        var now = DateTimeOffset.UtcNow;
        var workspace = new AgentWorkspaceRecord("workspace", "Workspace", null, now, now);
        return (
            workspace,
            new AgentWorkspaceBindingRecord(
                "binding",
                workspace.WorkspaceId,
                AgentRpcContractIds.ExecutionTarget,
                "test-target",
                "primary-execution-target",
                true,
                0,
                now,
                now));
    }

    private static AgentToolSourceContext CreateSourceContext(
        AgentWorkspaceRecord workspace,
        AgentWorkspaceBindingRecord binding,
        AgentRpcReference<IAgentExecutionTarget> targetReference)
        => new(Guid.NewGuid(), Profile: null, workspace, binding)
        {
            ExecutionTargetReference = targetReference,
        };

    private static AgentToolExecutionContext CreateExecutionContext(
        AgentWorkspaceRecord workspace,
        AgentWorkspaceBindingRecord binding,
        AgentRpcReference<IAgentExecutionTarget> targetReference,
        string? profileId = null)
        => new(Guid.NewGuid(), profileId, workspace, binding)
        {
            ExecutionTargetReference = targetReference,
        };

    private static AgentPromptContextRequest CreatePromptRequest(
        AgentWorkspaceRecord workspace,
        AgentWorkspaceBindingRecord binding,
        AgentRpcReference<IAgentExecutionTarget> targetReference,
        AgentProfileRecord? profile = null,
        IReadOnlyList<AgentToolDescriptor>? availableTools = null)
    {
        var session = new AgentSessionContextRecord(
            Guid.NewGuid(),
            profile?.ProfileId ?? "profile",
            profile?.DisplayName ?? "Profile",
            "Session",
            AgentSessionState.Active,
            null);
        var run = new AgentRunContextRecord(
            Guid.NewGuid(),
            1,
            AgentRunStatus.Running,
            false,
            DateTimeOffset.UtcNow);
        return new AgentPromptContextRequest(
            session,
            run,
            new AgentTurnContextRecord(session, run, "test", null),
            [],
            [],
            new AgentPromptContextPlan("general", "test"))
        {
            Profile = profile,
            Workspace = workspace,
            ExecutionBinding = binding,
            AvailableTools = availableTools ?? [],
            ExecutionTargetReference = targetReference,
        };
    }

    private class TestExecutionTarget :
        IAgentExecutionTarget,
        IAgentExecutionResourceResolver,
        IAgentExecutionScopeProvider,
        IAgentScopedInstructionDiscoveryTarget
    {
        public int ShellExecutionCount { get; private set; }

        public AgentExecutionTargetDescriptor Descriptor { get; } = new(
            "test",
            "test-target",
            "Test Target",
            null,
            SupportsShell: true,
            SupportsFiles: true);

        public virtual ValueTask<AgentExecutionTargetReadiness> GetReadinessAsync(
            AgentExecutionTargetContext context,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentExecutionTargetReadiness(
                "test",
                "test-target",
                AgentExecutionTargetReadinessStatus.Ready,
                "Ready."));

        public virtual ValueTask<AgentExecutionShellDescriptor> GetShellAsync(
            AgentExecutionTargetContext context,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentExecutionShellDescriptor(
                "sh",
                "Test shell",
                "/bin/sh",
                AgentShellSyntaxKinds.PosixSh,
                "Test shell."));

        public virtual ValueTask<AgentResolvedResource> ResolveFileResourceAsync(
            AgentExecutionTargetContext context,
            string path,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentResolvedResource(
                "file",
                path,
                path,
                AgentPermissionBoundaryIds.ConfiguredScope,
                true));

        public virtual ValueTask<AgentShellCommandResult> ExecuteShellAsync(
            AgentExecutionTargetContext context,
            AgentShellCommandRequest request,
            CancellationToken cancellationToken = default)
        {
            ShellExecutionCount++;
            return ValueTask.FromResult(new AgentShellCommandResult(0, "ok"));
        }

        public virtual ValueTask<AgentFileReadResult> ReadFileAsync(
            AgentExecutionTargetContext context,
            AgentFileReadRequest request,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentFileReadResult(request.Path, "content"));

        public virtual ValueTask<AgentFileMutationResult> WriteFileAsync(
            AgentExecutionTargetContext context,
            AgentFileWriteRequest request,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentFileMutationResult(request.Path, "wrote"));

        public virtual ValueTask<AgentFileMutationResult> DeleteFileAsync(
            AgentExecutionTargetContext context,
            AgentFileDeleteRequest request,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentFileMutationResult(request.Path, "deleted"));

        public virtual ValueTask<IReadOnlyList<AgentResolvedExecutionResource>> ResolveResourcesAsync(
            AgentExecutionTargetContext context,
            IReadOnlyList<AgentExecutionResourceDescriptor> resources,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<AgentResolvedExecutionResource>>(
                resources.Select(resource => new AgentResolvedExecutionResource(
                    resource.ResourceId,
                    resource.ResourceKind,
                    resource.SourceId,
                    resource.DisplayName,
                    resource.HostPath,
                    resource.PreferredExecutionPath,
                    resource.AccessMode,
                    resource.Metadata)).ToArray());

        public virtual ValueTask<AgentExecutionScopeDescriptor> GetExecutionScopeAsync(
            AgentExecutionTargetContext context,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentExecutionScopeDescriptor(
                "Test Target",
                ["/workspace"],
                "/workspace"));

        public virtual ValueTask<AgentScopedInstructionDiscoveryResult> DiscoverScopedInstructionsAsync(
            AgentExecutionTargetContext context,
            AgentScopedInstructionDiscoveryRequest request,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentScopedInstructionDiscoveryResult(
                "target",
                "scope",
                [])
            {
                ProcessedProbeCount = request.Probes.Count,
            });
    }

    private sealed class BlockingExecutionTarget : TestExecutionTarget
    {
        public TaskCompletionSource ShellStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ResourcesStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<AgentShellCommandResult> ExecuteShellAsync(
            AgentExecutionTargetContext context,
            AgentShellCommandRequest request,
            CancellationToken cancellationToken = default)
        {
            ShellStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new AgentShellCommandResult(0, "unreachable");
        }

        public override async ValueTask<AgentFileReadResult> ReadFileAsync(
            AgentExecutionTargetContext context,
            AgentFileReadRequest request,
            CancellationToken cancellationToken = default)
        {
            ReadStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new AgentFileReadResult(request.Path, "unreachable");
        }

        public override async ValueTask<IReadOnlyList<AgentResolvedExecutionResource>> ResolveResourcesAsync(
            AgentExecutionTargetContext context,
            IReadOnlyList<AgentExecutionResourceDescriptor> resources,
            CancellationToken cancellationToken = default)
        {
            ResourcesStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return [];
        }
    }

    private sealed class FailingExecutionTarget(SunderRpcErrorKind? errorKind) : TestExecutionTarget
    {
        public override ValueTask<AgentExecutionTargetReadiness> GetReadinessAsync(
            AgentExecutionTargetContext context,
            CancellationToken cancellationToken = default)
            => ValueTask.FromException<AgentExecutionTargetReadiness>(Failure());

        public override ValueTask<IReadOnlyList<AgentResolvedExecutionResource>> ResolveResourcesAsync(
            AgentExecutionTargetContext context,
            IReadOnlyList<AgentExecutionResourceDescriptor> resources,
            CancellationToken cancellationToken = default)
            => ValueTask.FromException<IReadOnlyList<AgentResolvedExecutionResource>>(Failure());

        public override ValueTask<AgentResolvedResource> ResolveFileResourceAsync(
            AgentExecutionTargetContext context,
            string path,
            CancellationToken cancellationToken = default)
            => ValueTask.FromException<AgentResolvedResource>(Failure());

        public override ValueTask<AgentShellCommandResult> ExecuteShellAsync(
            AgentExecutionTargetContext context,
            AgentShellCommandRequest request,
            CancellationToken cancellationToken = default)
            => ValueTask.FromException<AgentShellCommandResult>(Failure());

        public override ValueTask<AgentFileReadResult> ReadFileAsync(
            AgentExecutionTargetContext context,
            AgentFileReadRequest request,
            CancellationToken cancellationToken = default)
            => ValueTask.FromException<AgentFileReadResult>(Failure());

        private Exception Failure()
            => errorKind is { } kind
                ? new SunderRpcException(new SunderRpcError(
                    kind,
                    "test.execution-target.failure",
                    $"Injected {kind} target failure."))
                : new InvalidOperationException("Injected internal target failure.");
    }

    private sealed class CapturingTargetConsumer : IAgentToolSource, IAgentPromptContextContributor
    {
        public static AgentToolDescriptor Descriptor { get; } = new(
            "capturing-tool",
            "Capturing Tool",
            "Captures execution target references.",
            IsReadOnly: true);

        public string SourceId => "capturing-source";
        public string ContributorId => "capturing-context";
        public string DisplayName => "Capturing Source";
        public string SourceKind => "test";
        public int ListCount { get; private set; }
        public int ReadinessCount { get; private set; }
        public int ExecuteCount { get; private set; }
        public int ContextCount { get; private set; }
        public List<AgentRpcReference<IAgentExecutionTarget>?> ObservedTargetReferences { get; } = [];

        public ValueTask<IReadOnlyList<AgentToolDescriptor>> ListToolsAsync(
            AgentToolSourceContext context,
            CancellationToken cancellationToken = default)
        {
            ListCount++;
            ObservedTargetReferences.Add(context.ExecutionTargetReference);
            return ValueTask.FromResult<IReadOnlyList<AgentToolDescriptor>>([Descriptor]);
        }

        public ValueTask<AgentToolReadiness?> GetReadinessAsync(
            string toolId,
            AgentToolSourceContext context,
            CancellationToken cancellationToken = default)
        {
            ReadinessCount++;
            ObservedTargetReferences.Add(context.ExecutionTargetReference);
            return ValueTask.FromResult<AgentToolReadiness?>(new AgentToolReadiness(
                toolId,
                context.ExecutionTargetReference is null
                    ? AgentToolReadinessStatus.Failed
                    : AgentToolReadinessStatus.Ready,
                "Observed."));
        }

        public ValueTask<AgentToolResult> ExecuteAsync(
            AgentToolExecutionContext context,
            AgentToolRequest request,
            CancellationToken cancellationToken = default)
        {
            ExecuteCount++;
            ObservedTargetReferences.Add(context.ExecutionTargetReference);
            return ValueTask.FromResult(context.ExecutionTargetReference is null
                ? new AgentToolResult(
                    request.ToolId,
                    "Target unavailable.",
                    IsError: true,
                    ErrorCode: AgentToolResultErrorCodes.PackageUnavailable)
                : new AgentToolResult(request.ToolId, "Executed."));
        }

        public ValueTask<AgentPromptContextContribution?> ContributeContextAsync(
            AgentPromptContextRequest request,
            CancellationToken cancellationToken = default)
        {
            ContextCount++;
            ObservedTargetReferences.Add(request.ExecutionTargetReference);
            return ValueTask.FromResult<AgentPromptContextContribution?>(new AgentPromptContextContribution(
            [
                new AgentPromptContextBlock("Captured", "Captured.", SourceId: ContributorId),
            ]));
        }
    }

    private sealed class SingleProfileRuntimeCatalog(AgentProfileRecord profile) : IAgentRuntimeCatalog
    {
        public event Action<Guid>? SessionChanged { add { } remove { } }
        public event Action<Guid, AgentTurnRecord>? TurnChanged { add { } remove { } }
        public event Action<string>? ProfileChanged { add { } remove { } }
        public IReadOnlyList<AgentSessionRecord> ListSessions() => [];
        public IReadOnlyList<AgentSessionRecord> ListSessionsForProfile(string profileId) => [];
        public IReadOnlyList<AgentSessionRecord> ListSessionsForWorkspace(string workspaceId) => [];
        public AgentSessionRecord? GetSession(Guid sessionId) => null;
        public IReadOnlyList<AgentWorkspaceRecord> ListWorkspaces() => [];
        public AgentWorkspaceRecord? GetWorkspace(string workspaceId) => null;
        public AgentProfileRecord? GetSessionProfile(Guid sessionId) => null;
        public AgentWorkingSummaryRecord? GetWorkingSummary(Guid sessionId) => null;
        public AgentSessionContextCheckpointRecord? GetLatestSessionContextCheckpoint(Guid sessionId) => null;
        public AgentRunCheckpointRecord? GetLatestCheckpoint(Guid sessionId) => null;
        public IReadOnlyList<AgentTurnRecord> ListRecentTurns(Guid sessionId, int limit) => [];
        public IReadOnlyList<AgentTurnRecord> ListTurnsBefore(Guid sessionId, DateTimeOffset beforeCreatedAtUtc, Guid beforeTurnId, int limit) => [];
        public IReadOnlyList<AgentTurnRecord> ListTurnsAfter(Guid sessionId, DateTimeOffset afterCreatedAtUtc, Guid afterTurnId, int limit) => [];
        public IReadOnlyList<AgentProfileRecord> ListProfiles() => [profile];
        public AgentProfileRecord? GetProfile(string profileId) => string.Equals(profile.ProfileId, profileId, StringComparison.Ordinal) ? profile : null;
        public AgentProfileModelBindingRecord? GetSessionModelBinding(Guid sessionId, string capabilityKind) => null;
        public AgentProfileModelBindingRecord? GetModelBinding(string profileId, string capabilityKind) => null;
    }
}
