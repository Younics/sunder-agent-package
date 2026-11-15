extern alias DockerWorker;
extern alias FilesWorker;

using System.Text.Json;
using DockerWorker::Sunder.Package.Agent.Execution.Docker;
using FilesWorker::Sunder.Package.Agent.Tools.Files;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Abstractions;
using Xunit;
using DockerModels = DockerWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Models;
using static Sunder.Package.Agent.Tests.WorkspaceDockerFixture;
using static Sunder.Package.Agent.Tests.WorkspaceServiceFixture;
using static Sunder.Package.Agent.Tests.WorkspaceStorageFixture;

namespace Sunder.Package.Agent.Tests;

[Collection("Workspace component behavior")]
public sealed class DockerFilesAcceptanceTests
{
    // This boundary is not covered by a host executor or a fake mount verifier:
    // real daemon challenges and generated Files permission/receipt dispatch must
    // compose on the same host without pulling or provisioning an image.
    [DockerIntegrationFact]
    [Trait("Category", "System")]
    [Trait("Capability", "Docker")]
    [Trait("Size", "Large")]
    public async Task RealDaemon_FilesReadWriteEditPatchAndDeleteUseTheVerifiedHostObject()
    {
        using var scope = TestScope.Create();
        using var lifecycle = new DockerContainerLifecycleService();
        var runner = new OwnedContainerRunner(scope.Context);
        var image = Environment.GetEnvironmentVariable("SUNDER_DOCKER_INTEGRATION_IMAGE") ?? "node:22-alpine";
        var inspected = await runner.RunAsync(["image", "inspect", "--format", "{{.Id}}", image], 30, CancellationToken.None);
        Assert.True(inspected.ExitCode == 0 && !inspected.TimedOut,
            $"Provision the selected integration image explicitly before running this test. {inspected.Output}");
        var images = new DockerImageCatalogService(scope.Context, runner);
        await images.AddImageAsync(image);
        var config = new DockerExecutionWorkspaceConfigService(scope.Context, images);
        var target = new DockerExecutionTarget(scope.Context, config, lifecycle, images, runner);
        var (workspace, hostRoot) = CreateDockerWorkspace(scope);
        var binding = CreateBinding(workspace.WorkspaceId, "docker");
        var containerName = "sunder-files-acceptance-" + Guid.NewGuid().ToString("N");
        await config.SaveConfigAsync(binding.BindingId, new DockerExecutionWorkspaceConfig(image, containerName, "/bin/sh"));
        using var catalog = new TestExtensionCatalog();
        catalog.AddProvider(RegressionTestBindings.ExecutionTargets, target);
        using var files = new GeneratedToolSourceProbe(catalog, new FilesToolSource(scope.Context), "younics.ai.agent.tools.files");
        var context = CreateToolExecutionContext(catalog, workspace, binding);
        var executionContext = new DockerModels.AgentExecutionTargetContext
        {
            Workspace = ToDockerWorkspace(workspace),
            Binding = new()
            {
                BindingId = binding.BindingId, WorkspaceId = binding.WorkspaceId,
                ExtensionPointId = binding.ExtensionPointId, ContributionId = binding.ContributionId,
                Role = binding.Role, IsEnabled = binding.IsEnabled, SortOrder = binding.SortOrder,
                CreatedAtUtc = DockerTime(binding.CreatedAtUtc), UpdatedAtUtc = DockerTime(binding.UpdatedAtUtc),
            },
            AllowOutsideConfiguredScope = false,
            ApprovedResourceReferences = [], ApprovedResourceClaims = [], ApprovedResourceCapabilities = [],
            CapturePostMutationResource = false,
        };
        try
        {
            var readiness = await target.ReadinessAsync(executionContext);
            Assert.True(readiness.Status.ToString() == nameof(AgentExecutionTargetReadinessStatus.Ready), readiness.Message);
            Assert.NotNull(runner.CreatedContainerId);
            await Execute("write", new { path = "nested/file.txt", content = "one\nneedle\nthree" });
            var hostFile = Path.Combine(hostRoot, "nested", "file.txt");
            using var before = DockerWorker::Sunder.Agent.Execution.Common.HostSecurePathEngine.Capture([hostRoot], hostFile);
            Assert.Contains("needle", (await Execute("read", new { path = "nested/file.txt" })).Content, StringComparison.Ordinal);
            await Execute("edit", new { path = "nested/file.txt", oldString = "needle", newString = "edited" });
            await Execute("apply_patch", new
            {
                patchText = "*** Begin Patch\n*** Update File: nested/file.txt\n@@\n-three\n+patched\n*** Add File: nested/added.txt\n+added\n*** End Patch",
            });
            Assert.Equal("one\nedited\npatched", await File.ReadAllTextAsync(hostFile));
            using (var after = DockerWorker::Sunder.Agent.Execution.Common.HostSecurePathEngine.Capture([hostRoot], hostFile))
                Assert.Equal(before.Binding.TargetIdentity, after.Binding.TargetIdentity);
            Assert.Contains("edited", (await Execute("grep", new { path = "nested", pattern = "edited" })).Content, StringComparison.Ordinal);
            var root = (await target.ExecutionScopeAsync(executionContext)).DefaultWorkingDirectory!;
            var visible = await target.ExecuteShellAsync(new()
            {
                Context = executionContext,
                Request = new()
                {
                    Command = "cat -- " + DockerCommandRunner.Quote(root + "/nested/file.txt"),
                    WorkingDirectory = null, TimeoutSeconds = null,
                },
            });
            Assert.Equal(0, visible.ExitCode);
            Assert.Equal("one\nedited\npatched", visible.Output);
            await Execute("apply_patch", new
            {
                patchText = "*** Begin Patch\n*** Delete File: nested/file.txt\n*** Delete File: nested/added.txt\n*** End Patch",
            });
            Assert.False(File.Exists(hostFile));
            Assert.False(File.Exists(Path.Combine(hostRoot, "nested", "added.txt")));
            var missing = await target.ExecuteShellAsync(new()
            {
                Context = executionContext,
                Request = new()
                {
                    Command = "test ! -e " + DockerCommandRunner.Quote(root + "/nested/file.txt"),
                    WorkingDirectory = null, TimeoutSeconds = null,
                },
            });
            Assert.Equal(0, missing.ExitCode);
            Assert.DoesNotContain(Directory.EnumerateFileSystemEntries(hostRoot, "*", SearchOption.AllDirectories),
                path => DockerWorker::Sunder.Agent.Execution.Common.HostSecurePathEngine.IsChallengeName(Path.GetFileName(path)));
        }
        finally
        {
            // Cancel name-based idle work before cleaning up the exact ID returned
            // by our successful create. Never infer cleanup ownership from a name.
            lifecycle.Dispose();
            if (runner.CreatedContainerId is { } ownedId)
            {
                var removed = await runner.RunAsync(["rm", "-f", ownedId], 30, CancellationToken.None);
                Assert.True(removed.ExitCode == 0 && !removed.TimedOut, removed.Output);
                var absent = await runner.RunAsync(["inspect", ownedId], 30, CancellationToken.None);
                Assert.False(absent.TimedOut, absent.Output);
                Assert.NotEqual(0, absent.ExitCode);
            }
        }

        async Task<AgentToolResult> Execute(string tool, object arguments)
        {
            var request = new AgentToolRequest(tool, JsonSerializer.Serialize(arguments));
            var operation = CreateResourceOperation(tool is "read" ? "files.read" : tool is "grep" ? "files.search" : "files.mutate");
            var planning = context with { ResourceOperation = operation };
            var permission = Assert.IsType<AgentPermissionRequest>(await files.BuildPermissionRequestAsync(planning, request));
            Assert.Equal(AgentPermissionBoundaryIds.ConfiguredScope, permission.BoundaryId);
            try
            {
                var result = await files.ExecuteAsync(planning with
                {
                    ApprovedResourceReferences = permission.ResourceReferences,
                    ApprovedResourceClaims = permission.ResourceClaims,
                    ApprovedResourceCapabilities = permission.ResourceCapabilities,
                    ResourceOperation = operation with { CanIssueOutsideAuthority = false },
                }, request);
                Assert.False(result.IsError, result.Content);
                return result;
            }
            finally { await target.ReleaseResourceAuthorityAsync(permission.ResourceCapabilities); }
        }
    }

    private sealed class OwnedContainerRunner(IPackageContext context) : DockerCliRunner(context)
    {
        internal string? CreatedContainerId { get; private set; }

        protected override async Task<DockerCliRunResult> RunCoreAsync(IReadOnlyList<string> args, int timeoutSeconds,
            CancellationToken cancellationToken, string? standardInput = null, IProgress<string>? progress = null)
        {
            var result = await base.RunCoreAsync(args, timeoutSeconds, cancellationToken, standardInput, progress);
            var logical = args.Count >= 2 && args[0] == "--host" ? args.Skip(2).ToArray() : args.ToArray();
            if (logical.Length > 0 && logical[0] == "run" && result.ExitCode == 0 && !result.TimedOut)
            {
                var id = result.Output.Trim();
                Assert.Equal(64, id.Length);
                Assert.All(id, value => Assert.True(char.IsAsciiHexDigit(value)));
                Assert.Null(CreatedContainerId);
                CreatedContainerId = id;
            }
            return result;
        }
    }
}
