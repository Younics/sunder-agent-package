extern alias LocalWorker;

using LocalWorker::Sunder.Package.Agent.Execution.Local;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.Tests;
using Xunit;

namespace Sunder.Package.Agent.Execution.Local.Tests;

public sealed partial class FilesToolSourceRefactorTests
{
    [PlatformTheory(TestPlatform.Unix, "Strict patch publication requires Unix file-descriptor semantics.")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApplyPatch_PreapprovedFiveFileProjectSharesCreatedParentsAndCompensatesOwnedDirectories(bool failSecond)
    {
        using var scope = RegressionTestPackageScope.Create();
        var root = Path.Combine(scope.RootPath, "workspace");
        Directory.CreateDirectory(root);
        var now = DateTimeOffset.UtcNow;
        var workspace = new AgentWorkspaceRecord("workspace", "Workspace", null, now, now,
            [new AgentWorkspacePathRecord("root", "workspace", root, true, 0, now, now)]);
        using var local = new LocalExecutionTarget(scope.Context, new LocalExecutionWorkspaceConfigService(scope.Context), new LocalShellCatalogService(scope.Context));
        var adapter = new GeneratedLocalExecutionTargetAdapter(local);
        IAgentExecutionTarget target = failSecond ? new FailSecondMutationExecutionTarget(adapter) : adapter;
        var catalog = CreateTargetCatalog(target);
        var source = CreateSourceProbe(catalog, scope.Context);
        var binding = new AgentWorkspaceBindingRecord("binding", workspace.WorkspaceId, RegressionTestBindings.ExecutionTargets,
            target.Descriptor.TargetId, "primary-execution-target", true, 0, now, now);
        var run = Guid.NewGuid();
        var operation = new AgentResourceOperationContext(run, 1, "five-file-patch", "files.mutate", -1,
            "workspace-generation", "binding-generation", "younics.ai.agent.tools.files", "younics.ai.agent.execution.local",
            Guid.NewGuid().ToString("N"), 1, true);
        var planning = new AgentToolExecutionContext(Guid.NewGuid(), "profile", workspace, binding,
            RunId: run, RunRevision: 1, UserTurnId: Guid.NewGuid(), ToolCallId: operation.ToolCallId)
        {
            ResourceOperation = operation,
            ExecutionTarget = (await catalog.GetRequiredProviderAsync(catalog.ExecutionTargets)).Reference,
        };
        var paths = new[] { "NewProject/global.json", "NewProject/App.csproj", "NewProject/Services/Weather.cs", "NewProject/Views/Main.cs", "NewProject/Program.cs" };
        var patch = "*** Begin Patch\n" + string.Join('\n', paths.Select(path => $"*** Add File: {path}\n+approved content")) + "\n*** End Patch";
        var request = new AgentToolRequest("apply_patch", PatchArguments(patch));
        var permission = Assert.IsType<AgentPermissionRequest>(await source.BuildPermissionRequestAsync(planning, request));
        Assert.NotEmpty(permission.ResourceCapabilities);
        var execution = planning with
        {
            ApprovedResourceReferences = permission.ResourceReferences,
            ApprovedResourceClaims = permission.ResourceClaims,
            ApprovedResourceCapabilities = permission.ResourceCapabilities,
            ResourceOperation = operation with { CanIssueOutsideAuthority = false },
        };
        var result = await source.ExecuteAsync(execution, request);
        try
        {
            if (failSecond)
            {
                Assert.True(result.IsError);
                Assert.Contains("Compensation restored", result.Content);
                Assert.All(paths, path => Assert.False(File.Exists(Path.Combine(root, path))));
            }
            else
            {
                Assert.False(result.IsError, result.Content);
                Assert.All(paths, path => Assert.Equal("approved content", File.ReadAllText(Path.Combine(root, path)).TrimEnd()));
            }
        }
        finally { adapter.ReleaseResourceAuthority(permission.ResourceCapabilities); }
        if (failSecond)
        {
            Assert.DoesNotContain(paths, path => File.Exists(Path.Combine(root, path)));
            // Platform quarantine entries are retained recovery evidence, not
            // permission to recursively remove a formerly populated parent.
            Assert.All(Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories), path =>
                Assert.StartsWith(".sunder-secure-quarantine-", Path.GetFileName(path)));
        }
        else Assert.True(Directory.Exists(Path.Combine(root, "NewProject")));
    }
}
