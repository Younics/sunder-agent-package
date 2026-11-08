extern alias DockerWorker;

using System.Text.Json;
using DockerWorker::Sunder.Package.Agent.Execution.Docker;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Services;
using Sunder.Package.Agent.Storage;
using Sunder.Sdk.Stacks;
using Xunit;
using TestPackageContext = Sunder.Package.Agent.Tests.WorkspaceStorageFixture.TestPackageContext;
using static Sunder.Package.Agent.Tests.WorkspaceServiceFixture;
using static Sunder.Package.Agent.Tests.WorkspaceStorageFixture;

namespace Sunder.Package.Agent.Tests;

[Collection("Workspace component behavior")]
public sealed class WorkspaceStackTests
{
    [Fact]
    public async Task AgentProfileStackContributor_ExportAsync_UsesExplicitDetailsAndMinimumVersion()
    {
        using var scope = TestScope.Create();
        using var services = CreateWorkspaceViewServices(scope.Context);
        using var profileService = services.ProfileService;
        services.Store.SaveProfile(CreateProfile("profile.stack") with
        {
            DisplayName = "Stack Profile",
            Description = "Profile description.",
            Instructions = "Use concise responses.",
            ChatProviderId = "openai",
            ChatModelId = "gpt-test",
        });
        var contributor = new AgentProfileStackContributor(profileService, scope.Context);

        var item = Assert.Single(await contributor.ListExportItemsAsync(
            new StackExportDiscoveryContext(scope.Context.PackageId)));
        var contribution = await contributor.ExportAsync(new StackExportRequest(
            [new StackExportItemSelection("profile.stack")]));

        Assert.All(item.Details ?? [], detail => Assert.NotNull(detail.Sensitivity));
        var fragment = Assert.Single(contribution.Fragments);
        var requirement = Assert.Single(contribution.PackageRequirements);
        Assert.Equal("younics.ai.agent", requirement.PackageId);
        Assert.Equal("1.1.0", requirement.MinimumVersion);

        using var targetScope = TestScope.Create();
        using var targetServices = CreateWorkspaceViewServices(targetScope.Context);
        var target = new AgentProfileStackContributor(targetServices.ProfileService, targetScope.Context);
        var importFragment = ToImportFragment(fragment, contributor.ContributorId);
        var preview = await target.PreviewImportAsync(new StackImportPreviewRequest([importFragment], new Dictionary<string, string>(), new Dictionary<string, string>()));
        var action = Assert.Single(preview.Actions);
        await AssertStackRejectsOldIdentityAsync(target, importFragment, action.ActionId);
        Assert.Null(targetServices.ProfileService.GetProfile("profile.stack"));
        var imported = await target.ImportAsync(new StackImportRequest([importFragment], new Dictionary<string, string>(), new Dictionary<string, string>(), [action.ActionId]));
        Assert.Single(imported.ImportedItems);
        Assert.Equal("Use concise responses.", targetServices.ProfileService.GetProfile("profile.stack")?.Instructions);
    }

    [Fact]
    public async Task AgentWorkspaceStackContributor_ExportAsync_IncludesSelectedLocalPaths()
    {
        using var scope = TestScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var service = new AgentWorkspaceService(store);
        var workspaceRoot = Path.Combine(scope.RootPath, "repo");
        var documentPath = Path.Combine(scope.RootPath, "project-guide.md");
        service.ImportWorkspace(
            new AgentWorkspaceRecord("workspace.stack", "Stack Workspace", "Private workspace notes.", default, default),
            [new AgentWorkspacePathRecord("root", "workspace.stack", workspaceRoot, IsDefault: true, 0, default, default)],
            [new AgentWorkspaceDocumentRecord("doc", "workspace.stack", documentPath, 0, default, default)]);
        service.SavePrimaryExecutionBinding("workspace.stack", "local");
        using var catalog = new TestExtensionCatalog();
        var contributor = new AgentWorkspaceStackContributor(service, scope.Context, catalog.ExecutionTargets);
        var selections = new List<StackExportItemSelection>
        {
            new("workspace.stack"),
        };
        var request = new StackExportRequest(selections);
        selections.Clear();

        var item = Assert.Single(await contributor.ListExportItemsAsync(
            new StackExportDiscoveryContext(scope.Context.PackageId)));
        var contribution = await contributor.ExportAsync(request);

        var fragment = Assert.Single(contribution.Fragments);
        Assert.Single(request.ItemSelections);
        Assert.Throws<NotSupportedException>(() => ((IList<StackExportItemSelection>)request.ItemSelections).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<StackFragmentExport>)contribution.Fragments).Clear());
        Assert.All(item.Details ?? [], detail => Assert.NotNull(detail.Sensitivity));
        var requirement = Assert.Single(contribution.PackageRequirements);
        Assert.Equal("younics.ai.agent", requirement.PackageId);
        Assert.Equal("1.1.0", requirement.MinimumVersion);
        Assert.Equal("younics.ai.agent.workspaces", contributor.ContributorId);
        Assert.Empty(fragment.RequiredInputs ?? []);
        using var payload = JsonDocument.Parse(fragment.JsonPayload);
        Assert.Equal(workspaceRoot, Assert.Single(payload.RootElement.GetProperty("paths").EnumerateArray()).GetProperty("hostPath").GetString());
        Assert.Equal(documentPath, Assert.Single(payload.RootElement.GetProperty("documents").EnumerateArray()).GetProperty("filePath").GetString());
        Assert.Contains("Stack Workspace", fragment.JsonPayload, StringComparison.Ordinal);
        Assert.Contains("local", fragment.JsonPayload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AgentWorkspaceStackContributor_ExportAsync_UsesCurrentStoredDiscriminator()
    {
        using var scope = TestScope.Create();
        var workspace = CreateWorkspace();
        var now = DateTimeOffset.UtcNow;
        // Seed the current stored value independently of the production constant.
        var binding = new AgentWorkspaceBindingRecord(
            "binding.persisted", workspace.WorkspaceId, "younics.ai.agent.execution.target", "local",
            "primary-execution-target", true, 0, now, now);
        using (var initialStore = new AgentLocalStore(scope.Context))
        {
            initialStore.SaveWorkspace(workspace);
            initialStore.SaveWorkspaceBinding(binding);
        }

        using var store = new AgentLocalStore(scope.Context);
        var service = new AgentWorkspaceService(store);
        var persisted = Assert.Single(service.ListBindings(workspace.WorkspaceId));
        Assert.Equal(binding, persisted);
        using var catalog = new TestExtensionCatalog();
        catalog.AddProvider(
            RegressionTestBindings.ExecutionTargets,
            new CountingExecutionTarget("local"),
            packageId: "younics.ai.agent.execution.local");
        var executionTargets = new AgentExecutionTargetService(catalog.ExecutionTargets);
        var provider = await executionTargets.ResolveTargetAsync(persisted);
        Assert.NotNull(provider);
        Assert.Equal("younics.ai.agent.execution.target", provider.ContractId);
        Assert.Equal("younics.ai.agent.execution.local", provider.PackageId);
        Assert.Empty((await catalog.RpcForCaller("younics.ai.agent")
            .DiscoverAsync("sunder.agent.execution.target")).Providers);

        var contributor = new AgentWorkspaceStackContributor(service, scope.Context, catalog.ExecutionTargets);
        var item = Assert.Single(await contributor.ListExportItemsAsync(
            new StackExportDiscoveryContext(scope.Context.PackageId)));
        var detail = Assert.Single(item.Details ?? [], value => value.DetailId == "primary-execution-target");
        Assert.Equal("local", detail.Value);

        var contribution = await contributor.ExportAsync(new StackExportRequest(
            [new StackExportItemSelection(workspace.WorkspaceId)]));
        var fragment = Assert.Single(contribution.Fragments);
        Assert.Equal("younics.ai.agent.workspaces", contributor.ContributorId);
        Assert.Equal("younics.ai.agent/workspace", fragment.SchemaId);
        using var payload = JsonDocument.Parse(fragment.JsonPayload);
        Assert.Equal("local", payload.RootElement.GetProperty("primaryExecutionBinding")
            .GetProperty("contributionId").GetString());
        Assert.Equal(
            new[] { "younics.ai.agent", "younics.ai.agent.execution.local" },
            contribution.PackageRequirements.Select(requirement => requirement.PackageId));
        Assert.Equal(binding, Assert.Single(service.ListBindings(workspace.WorkspaceId)));
    }

    [Fact]
    public async Task AgentWorkspaceStackContributor_ExportAsync_DoesNotInventThirdPartyMinimumVersion()
    {
        using var scope = TestScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var service = new AgentWorkspaceService(store);
        var workspace = service.CreateWorkspace("Third-party target workspace");
        service.SavePrimaryExecutionBinding(workspace.WorkspaceId, "contoso-target");
        using var catalog = new TestExtensionCatalog();
        catalog.AddProvider(
            RegressionTestBindings.ExecutionTargets,
            new CountingExecutionTarget("contoso-target"),
            packageId: "contoso.execution");
        var contributor = new AgentWorkspaceStackContributor(service, scope.Context, catalog.ExecutionTargets);

        var contribution = await contributor.ExportAsync(new StackExportRequest(
            [new StackExportItemSelection(workspace.WorkspaceId)]));

        var core = Assert.Single(
            contribution.PackageRequirements,
            requirement => requirement.PackageId == "younics.ai.agent");
        var thirdParty = Assert.Single(
            contribution.PackageRequirements,
            requirement => requirement.PackageId == "contoso.execution");
        Assert.Equal("1.1.0", core.MinimumVersion);
        Assert.Null(thirdParty.CreatedWithVersion);
        Assert.Null(thirdParty.MinimumVersion);
    }

    [Fact]
    public async Task AgentWorkspaceStackContributor_ImportAsync_CreatesWorkspaceFromPublicPaths()
    {
        using var scope = TestScope.Create();
        var sourceContext = new TestPackageContext(Path.Combine(scope.RootPath, "source"));
        var sourceService = new AgentWorkspaceService(new AgentLocalStore(sourceContext));
        sourceService.ImportWorkspace(
            new AgentWorkspaceRecord("workspace.stack", "Stack Workspace", "Workspace docs.", default, default),
            [new AgentWorkspacePathRecord("root", "workspace.stack", Path.Combine(scope.RootPath, "source-repo"), IsDefault: true, 0, default, default)],
            [new AgentWorkspaceDocumentRecord("doc", "workspace.stack", Path.Combine(scope.RootPath, "source-guide.md"), 0, default, default)]);
        sourceService.SavePrimaryExecutionBinding("workspace.stack", "local");
        using var catalog = new TestExtensionCatalog();
        var sourceContributor = new AgentWorkspaceStackContributor(sourceService, sourceContext, catalog.ExecutionTargets);
        var sourceRoot = Path.Combine(scope.RootPath, "source-repo");
        var sourceDocumentPath = Path.Combine(scope.RootPath, "source-guide.md");
        var fragment = Assert.Single((await sourceContributor.ExportAsync(new StackExportRequest(
            [new StackExportItemSelection("workspace.stack")]))).Fragments);

        var targetContext = new TestPackageContext(Path.Combine(scope.RootPath, "target"));
        var targetService = new AgentWorkspaceService(new AgentLocalStore(targetContext));
        var targetContributor = new AgentWorkspaceStackContributor(targetService, targetContext, catalog.ExecutionTargets);
        var importFragment = ToImportFragment(fragment, sourceContributor.ContributorId);
        var preview = await targetContributor.PreviewImportAsync(new StackImportPreviewRequest(
            [importFragment],
            new Dictionary<string, string>(),
            new Dictionary<string, string>()));
        var action = Assert.Single(preview.Actions);
        Assert.Empty(preview.RequiredInputs);

        await AssertStackRejectsOldIdentityAsync(targetContributor, importFragment, action.ActionId);
        Assert.Null(targetService.GetWorkspace("workspace.stack"));

        var result = await targetContributor.ImportAsync(new StackImportRequest(
            [importFragment],
            new Dictionary<string, string>(),
            new Dictionary<string, string>(),
            [action.ActionId]));

        Assert.Equal(StackImportOutcome.Completed, result.Outcome);
        var imported = targetService.GetWorkspace("workspace.stack");
        Assert.NotNull(imported);
        Assert.Equal("Stack Workspace", imported!.DisplayName);
        Assert.Equal("Workspace docs.", imported.Description);
        Assert.Equal(Path.GetFullPath(sourceRoot), Assert.Single(imported.Paths).HostPath);
        Assert.Equal(Path.GetFullPath(sourceDocumentPath), Assert.Single(imported.Documents).FilePath);
        Assert.Equal("local", Assert.Single(targetService.ListBindings("workspace.stack")).ContributionId);
    }

    [Fact]
    public async Task AgentWorkspaceStackContributor_ImportFailureRollsBackWorkspaceAndBindingTogether()
    {
        using var scope = TestScope.Create();
        var sourceContext = new TestPackageContext(Path.Combine(scope.RootPath, "source-atomic"));
        var sourceService = new AgentWorkspaceService(new AgentLocalStore(sourceContext));
        sourceService.ImportWorkspace(
            new AgentWorkspaceRecord("workspace.atomic", "Atomic Workspace", null, default, default),
            [new AgentWorkspacePathRecord("root", "workspace.atomic", scope.RootPath, true, 0, default, default)],
            primaryExecutionTargetId: "local");
        using var catalog = new TestExtensionCatalog();
        var sourceContributor = new AgentWorkspaceStackContributor(
            sourceService,
            sourceContext,
            catalog.ExecutionTargets);
        var fragment = Assert.Single((await sourceContributor.ExportAsync(new StackExportRequest(
            [new StackExportItemSelection("workspace.atomic")]))).Fragments);
        var importFragment = ToImportFragment(fragment, sourceContributor.ContributorId);

        var targetContext = new TestPackageContext(Path.Combine(scope.RootPath, "target-atomic"));
        var targetStore = new AgentLocalStore(targetContext)
        {
            WorkspaceAggregateStageCompleted = stage =>
            {
                if (stage == "bindings")
                {
                    throw new InvalidOperationException("injected binding-stage failure");
                }
            },
        };
        var targetService = new AgentWorkspaceService(targetStore);
        var targetContributor = new AgentWorkspaceStackContributor(
            targetService,
            targetContext,
            catalog.ExecutionTargets);
        var preview = await targetContributor.PreviewImportAsync(new StackImportPreviewRequest(
            [importFragment],
            new Dictionary<string, string>(),
            new Dictionary<string, string>()));

        var result = await targetContributor.ImportAsync(new StackImportRequest(
            [importFragment],
            new Dictionary<string, string>(),
            new Dictionary<string, string>(),
            [Assert.Single(preview.Actions).ActionId]));

        Assert.Equal(StackImportOutcome.Failed, result.Outcome);
        Assert.Null(targetService.GetWorkspace("workspace.atomic"));
        Assert.Empty(targetService.ListBindings("workspace.atomic"));
    }

    [Fact]
    public async Task DockerImageStackContributor_ExportAsync_ExportsSelectedImageReferencesOnly()
    {
        using var scope = TestScope.Create();
        var imageCatalog = new DockerImageCatalogService(scope.Context);
        await imageCatalog.SaveImagesAsync(
        [
            new DockerImageDefinition("custom:1.0", DockerImageStatus.Ready, DateTimeOffset.UtcNow, "ready"),
            new DockerImageDefinition("other:1.0", DockerImageStatus.NotPulled, null, null),
        ]);
        var contributor = new DockerImageStackContributor(imageCatalog, scope.Context);

        var item = Assert.Single(await contributor.ListExportItemsAsync(
            new StackExportDiscoveryContext(scope.Context.PackageId)));
        var contribution = await contributor.ExportAsync(new StackExportRequest(
            [new StackExportItemSelection("docker-images",
            [
                new StackExportDetailSelection("custom:1.0"),
                new StackExportDetailSelection("other:1.0", IsSelected: false),
            ])]));

        var fragment = Assert.Single(contribution.Fragments);
        Assert.All(item.Details ?? [], detail => Assert.NotNull(detail.Sensitivity));
        Assert.Equal("docker-images", fragment.FragmentId);
        Assert.Contains("custom:1.0", fragment.JsonPayload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("other:1.0", fragment.JsonPayload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ready", fragment.JsonPayload, StringComparison.OrdinalIgnoreCase);
        var requirement = Assert.Single(contribution.PackageRequirements);
        Assert.Equal(scope.Context.PackageId, requirement.PackageId);
        Assert.Equal("1.1.0", requirement.MinimumVersion);
    }

    [Fact]
    public async Task DockerImageStackContributor_ImportAsync_ConfiguresReferencesWithoutMarkingReady()
    {
        using var sourceScope = TestScope.Create();
        var sourceCatalog = new DockerImageCatalogService(sourceScope.Context);
        await sourceCatalog.SaveImagesAsync([new DockerImageDefinition("custom:1.0", DockerImageStatus.Ready, DateTimeOffset.UtcNow, "ready")]);
        var sourceContributor = new DockerImageStackContributor(sourceCatalog, sourceScope.Context);
        var fragment = Assert.Single((await sourceContributor.ExportAsync(new StackExportRequest(
            [new StackExportItemSelection("docker-images")]))).Fragments);

        using var targetScope = TestScope.Create();
        var targetCatalog = new DockerImageCatalogService(targetScope.Context);
        var targetContributor = new DockerImageStackContributor(targetCatalog, targetScope.Context);
        var importFragment = ToImportFragment(fragment, sourceContributor.ContributorId) with
        {
            OwnerPackageId = "younics.ai.agent.execution.docker",
        };
        var preview = await targetContributor.PreviewImportAsync(new StackImportPreviewRequest(
            [importFragment],
            new Dictionary<string, string>(),
            new Dictionary<string, string>()));
        var action = Assert.Single(preview.Actions);

        await AssertStackRejectsOldIdentityAsync(targetContributor, importFragment, action.ActionId);
        Assert.Empty(await targetCatalog.ListImagesAsync());

        var result = await targetContributor.ImportAsync(new StackImportRequest(
            [importFragment],
            new Dictionary<string, string>(),
            new Dictionary<string, string>(),
            [action.ActionId]));

        Assert.Equal(StackImportOutcome.Completed, result.Outcome);
        var image = Assert.Single(await targetCatalog.ListImagesAsync());
        Assert.Equal("custom:1.0", image.ImageReference);
        Assert.Equal(DockerImageStatus.NotPulled, image.Status);
    }

    private static async Task AssertStackRejectsOldIdentityAsync(
        IPackageStackImporter importer,
        StackFragmentImport current,
        string selectedActionId)
    {
        foreach (var invalid in new[]
                 {
                     current with { OwnerPackageId = current.OwnerPackageId.Replace("younics.ai.agent", "sunder.package.agent", StringComparison.Ordinal) },
                     current with { SchemaId = current.SchemaId.Replace("younics.ai.agent", "sunder.package.agent", StringComparison.Ordinal) },
                      current with { SchemaVersion = 0 },
                      current with { SchemaVersion = 2 },
                 })
        {
            var preview = await importer.PreviewImportAsync(new StackImportPreviewRequest([invalid], new Dictionary<string, string>(), new Dictionary<string, string>()));
            Assert.Empty(preview.Actions);
            Assert.NotEmpty(preview.Warnings);
            Assert.Contains(preview.Conflicts, conflict => conflict.FragmentId == invalid.FragmentId && conflict.Severity == StackImportConflictSeverity.Error);
            var rejected = await importer.ImportAsync(new StackImportRequest([invalid], new Dictionary<string, string>(), new Dictionary<string, string>(), [selectedActionId]));
            Assert.Empty(rejected.ImportedItems);
            Assert.NotEmpty(rejected.Warnings);
            Assert.Equal(StackImportOutcome.Failed, rejected.Outcome);
            Assert.NotEmpty(rejected.Errors);
        }
    }

    private static StackFragmentImport ToImportFragment(StackFragmentExport fragment, string contributorId)
        => new(
            fragment.FragmentId,
            "younics.ai.agent",
            contributorId,
            fragment.SchemaId,
            fragment.SchemaVersion,
            fragment.DisplayName,
            fragment.JsonPayload,
            fragment.Description,
            fragment.Files?.Select(file => new StackImportPayloadHandle(
                file.RelativePath,
                file.OpenReadAsync,
                file.Length ?? throw new InvalidOperationException("Test export payload length is required."))).ToArray());
}
