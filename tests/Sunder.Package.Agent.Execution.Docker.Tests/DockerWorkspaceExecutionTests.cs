extern alias DockerWorker;

using System.Runtime.Versioning;
using DockerWorker::Sunder.Package.Agent.Execution.Docker;
using Sunder.Package.Agent.Contracts.Models;
using Xunit;
using DockerWorkerModels = DockerWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Models;
using static Sunder.Package.Agent.Tests.WorkspaceDockerFixture;
using static Sunder.Package.Agent.Tests.WorkspaceServiceFixture;
using static Sunder.Package.Agent.Tests.WorkspaceStorageFixture;

namespace Sunder.Package.Agent.Tests;

[Collection("Workspace component behavior")]
public sealed class DockerWorkspaceExecutionTests
{
    [UnixFact("Docker strict writes require supported host bind-mount verification.")]
    public async Task DockerExecutionTarget_WriteFileAsync_UsesApprovedVerifiedHostBind()
    {
        using var scope = TestScope.Create();
        using var lifecycle = new DockerContainerLifecycleService();
        var runner = CreateReadyDockerCliRunner(scope.Context);
        var configService = new DockerExecutionWorkspaceConfigService(scope.Context);
        var mountVerifier = new PassThroughDockerMountIdentityVerifier();
        var target = new DockerExecutionTarget(
            scope.Context,
            configService,
            lifecycle,
            imageCatalogService: null,
            runner,
            mountVerifier);
        var (workspace, hostPath) = CreateDockerWorkspace(scope);
        var binding = CreateBinding(workspace.WorkspaceId, "docker");
        await configService.SaveConfigAsync(
            binding.BindingId,
            new DockerExecutionWorkspaceConfig("test-image:1.0", "sunder-agent-test", "/bin/sh"));
        var content = string.Join("\n", Enumerable.Range(0, 5000).Select(index => $"line-{index}"));

        var context = new AgentExecutionTargetContext(null, null, workspace, binding);
        var (resource, approvedContext) = await ApproveDockerResourceAsync(
            target,
            context,
            "app/page.tsx",
            "files.mutate");
        var result = await WriteDockerFileAsync(target, approvedContext, new AgentFileWriteRequest("app/page.tsx", content));
        var replay = await WriteDockerFileAsync(target, approvedContext, new AgentFileWriteRequest("app/page.tsx", "replay"));

        Assert.False(result.IsError, result.Summary);
        Assert.Empty(resource.AuthorityReferences);
        Assert.Equal(AgentToolResultErrorCodes.PermissionReapprovalRequired, replay.ErrorCode);
        Assert.True(mountVerifier.VerifyCount >= 3);
        Assert.Equal(content, await File.ReadAllTextAsync(Path.Combine(hostPath, "app", "page.tsx")));
    }

    [UnixFact("Docker target reads require supported host bind-mount verification.")]
    public async Task DockerExecutionTarget_ReadFileAsync_ReportsMissingFileLikeLocal()
    {
        using var scope = TestScope.Create();
        using var lifecycle = new DockerContainerLifecycleService();
        var runner = CreateReadyDockerCliRunner(scope.Context);
        var configService = new DockerExecutionWorkspaceConfigService(scope.Context);
        var target = new DockerExecutionTarget(
            scope.Context,
            configService,
            lifecycle,
            imageCatalogService: null,
            runner,
            new PassThroughDockerMountIdentityVerifier());
        var (workspace, _) = CreateDockerWorkspace(scope);
        var binding = CreateBinding(workspace.WorkspaceId, "docker");
        await configService.SaveConfigAsync(binding.BindingId, new DockerExecutionWorkspaceConfig("test-image:1.0", "sunder-agent-test", "/bin/sh"));
        var containerRoot = (await GetDockerExecutionScopeAsync(target, new AgentExecutionTargetContext(null, null, workspace, binding))).DefaultWorkingDirectory!;

        var context = new AgentExecutionTargetContext(null, null, workspace, binding);
        var (_, approvedContext) = await ApproveDockerResourceAsync(
            target,
            context,
            "missing.txt",
            "files.read");
        var result = await ReadDockerFileAsync(target, approvedContext, new AgentFileReadRequest("missing.txt"));

        Assert.False(result.IsDirectory);
        Assert.Equal($"{containerRoot}/missing.txt", result.Path);
        Assert.True(result.IsError);
        Assert.Equal(AgentFileReadErrorCodes.FileNotFound, result.ErrorCode);
        Assert.Equal($"File not found: {containerRoot}/missing.txt", result.ErrorMessage);
        Assert.Empty(result.Content);
    }

    [UnixFact("Docker target reads require supported host bind-mount verification.")]
    public async Task DockerExecutionTarget_ReadFileAsync_RejectsBinaryFilesLikeLocal()
    {
        using var scope = TestScope.Create();
        using var lifecycle = new DockerContainerLifecycleService();
        var runner = CreateReadyDockerCliRunner(scope.Context);
        var configService = new DockerExecutionWorkspaceConfigService(scope.Context);
        var target = new DockerExecutionTarget(
            scope.Context,
            configService,
            lifecycle,
            imageCatalogService: null,
            runner,
            new PassThroughDockerMountIdentityVerifier());
        var (workspace, hostPath) = CreateDockerWorkspace(scope);
        var binding = CreateBinding(workspace.WorkspaceId, "docker");
        await configService.SaveConfigAsync(binding.BindingId, new DockerExecutionWorkspaceConfig("test-image:1.0", "sunder-agent-test", "/bin/sh"));
        await File.WriteAllBytesAsync(Path.Combine(hostPath, "image.png"), [1, 0, 2]);

        var context = new AgentExecutionTargetContext(null, null, workspace, binding);
        var (_, approvedContext) = await ApproveDockerResourceAsync(
            target,
            context,
            "image.png",
            "files.read");
        var result = await ReadDockerFileAsync(target, approvedContext, new AgentFileReadRequest("image.png"));

        Assert.True(result.IsError);
        Assert.Equal(AgentFileReadErrorCodes.BinaryFile, result.ErrorCode);
        Assert.Empty(result.Content);
    }

    [UnixFact("Docker target deletes require supported host bind-mount verification.")]
    public async Task DockerExecutionTarget_DeleteFileAsync_ReportsMissingPathLikeLocal()
    {
        using var scope = TestScope.Create();
        using var lifecycle = new DockerContainerLifecycleService();
        var runner = CreateReadyDockerCliRunner(scope.Context);
        var configService = new DockerExecutionWorkspaceConfigService(scope.Context);
        var target = new DockerExecutionTarget(
            scope.Context,
            configService,
            lifecycle,
            imageCatalogService: null,
            runner,
            new PassThroughDockerMountIdentityVerifier());
        var (workspace, _) = CreateDockerWorkspace(scope);
        var binding = CreateBinding(workspace.WorkspaceId, "docker");
        await configService.SaveConfigAsync(binding.BindingId, new DockerExecutionWorkspaceConfig("test-image:1.0", "sunder-agent-test", "/bin/sh"));
        var containerRoot = (await GetDockerExecutionScopeAsync(target, new AgentExecutionTargetContext(null, null, workspace, binding))).DefaultWorkingDirectory!;

        var context = new AgentExecutionTargetContext(null, null, workspace, binding);
        var (_, approvedContext) = await ApproveDockerResourceAsync(
            target,
            context,
            "missing.txt",
            "files.mutate");
        var result = await DeleteDockerFileAsync(target, approvedContext, new AgentFileDeleteRequest("missing.txt"));

        Assert.True(result.IsError);
        Assert.Equal("path-not-found", result.ErrorCode);
        Assert.Equal("Path does not exist.", result.Summary);
        Assert.Equal($"{containerRoot}/missing.txt", result.Path);
    }

    [Fact]
    public async Task DockerExecutionTarget_OutsideApprovalContextCannotBypassHostBindRequirement()
    {
        using var scope = TestScope.Create();
        using var lifecycle = new DockerContainerLifecycleService();
        var runner = CreateReadyDockerCliRunner(scope.Context);
        var configService = new DockerExecutionWorkspaceConfigService(scope.Context);
        var target = new DockerExecutionTarget(
            scope.Context,
            configService,
            lifecycle,
            imageCatalogService: null,
            runner,
            new PassThroughDockerMountIdentityVerifier());
        var (workspace, _) = CreateDockerWorkspace(scope);
        var binding = CreateBinding(workspace.WorkspaceId, "docker");
        await configService.SaveConfigAsync(
            binding.BindingId,
            new DockerExecutionWorkspaceConfig("test-image:1.0", "sunder-agent-test", "/bin/sh"));
        var context = new AgentExecutionTargetContext(
            null,
            null,
            workspace,
            binding,
            AllowOutsideConfiguredScope: true)
        {
            ApprovedResourceReferences = ["docker-resource-v1:legacy"],
        };

        var result = await ReadDockerFileAsync(target, context, new AgentFileReadRequest("/container-private/secret.txt"));

        Assert.True(result.IsError);
        Assert.Equal(DockerPathResolver.StructuredBindRequiredErrorCode, result.ErrorCode);
        Assert.Equal(DockerPathResolver.StructuredBindRequiredMessage, result.ErrorMessage);
    }

    [UnixFact("Docker scoped discovery requires supported host bind-mount verification.")]
    public async Task DockerExecutionTarget_ScopedDiscoveryUsesVerifiedHostBind()
    {
        using var scope = TestScope.Create();
        using var lifecycle = new DockerContainerLifecycleService();
        var runner = CreateReadyDockerCliRunner(scope.Context);
        var configService = new DockerExecutionWorkspaceConfigService(scope.Context);
        var target = new DockerExecutionTarget(
            scope.Context,
            configService,
            lifecycle,
            imageCatalogService: null,
            runner,
            new PassThroughDockerMountIdentityVerifier());
        var (workspace, hostPath) = CreateDockerWorkspace(scope);
        var binding = CreateBinding(workspace.WorkspaceId, "docker");
        await configService.SaveConfigAsync(
            binding.BindingId,
            new DockerExecutionWorkspaceConfig("test-image:1.0", "sunder-agent-test", "/bin/sh"));
        await File.WriteAllTextAsync(Path.Combine(hostPath, "AGENTS.md"), "container policy");
        var context = new AgentExecutionTargetContext(null, null, workspace, binding);
        var containerRoot = (await GetDockerExecutionScopeAsync(target, context)).DefaultWorkingDirectory!;

        var result = await DiscoverDockerInstructionsAsync(
            target,
            context,
            new AgentScopedInstructionDiscoveryRequest(
                [new AgentScopedInstructionProbe(containerRoot, IsDirectory: true)]));

        var document = Assert.Single(Assert.Single(result.Scopes).Documents);
        Assert.Equal($"{containerRoot}/AGENTS.md", document.Path);
        Assert.Equal("container policy", document.Content);
    }

    [DockerIntegrationFact]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    [Trait("Category", "System")]
    [Trait("Capability", "Docker")]
    [Trait("Size", "Large")]
    public async Task DockerExecutionTarget_RealDaemon_VerifiesMountAndContainerCanReadNestedHostCreation()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("SUNDER_DOCKER_INTEGRATION"),
                "1",
                StringComparison.Ordinal))
        {
            TestPrerequisites.ThrowIfRequired(
                "Set SUNDER_DOCKER_INTEGRATION=1 and provide a reachable Docker daemon.");
        }

        using var scope = TestScope.Create();
        await using var lifecycle = new DockerContainerLifecycleService();
        var runner = new DockerCliRunner(scope.Context);
        const string image = "node:22-alpine";
        var pull = await runner.RunAsync(["pull", image], 120, CancellationToken.None);
        Assert.False(pull.TimedOut, pull.Output);
        Assert.Equal(0, pull.ExitCode);
        var configService = new DockerExecutionWorkspaceConfigService(scope.Context);
        var target = new DockerExecutionTarget(scope.Context, configService, lifecycle, dockerCliRunner: runner);
        var (workspace, hostPath) = CreateDockerWorkspace(scope);
        var binding = CreateBinding(workspace.WorkspaceId, "docker");
        var container = "sunder-strict-integration-" + Guid.NewGuid().ToString("N");
        await configService.SaveConfigAsync(
            binding.BindingId,
            new DockerExecutionWorkspaceConfig(image, container, "/bin/sh"));
        var context = new AgentExecutionTargetContext(null, null, workspace, binding);
        try
        {
            var (_, approvedContext) = await ApproveDockerResourceAsync(
                target,
                context,
                "nested/file.txt",
                "files.mutate");
            var write = await WriteDockerFileAsync(
                target,
                approvedContext,
                new AgentFileWriteRequest("nested/file.txt", "container-readable", Overwrite: false));
            Assert.False(write.IsError, write.Summary);

            var scopeDescriptor = await GetDockerExecutionScopeAsync(target, context);
            var shell = await ExecuteDockerShellAsync(
                target,
                context,
                new AgentShellCommandRequest(
                    $"cat {DockerCommandRunner.Quote(scopeDescriptor.DefaultWorkingDirectory + "/nested/file.txt")}"));
            Assert.Equal(0, shell.ExitCode);
            Assert.Contains("container-readable", shell.Output, StringComparison.Ordinal);
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(Path.Combine(hostPath, "nested", "file.txt")) & (UnixFileMode)0x0fff);
        }
        finally
        {
            await runner.RunAsync(["rm", "-f", container], 30, CancellationToken.None);
        }
    }

    [UnixFact("Docker endpoint qualification requires supported host bind-mount verification.")]
    public async Task DockerExecutionTarget_PinsExplicitEndpointAcrossAmbientChange()
    {
        using var scope = TestScope.Create();
        using var lifecycle = new DockerContainerLifecycleService();
        var ambientEndpoint = "unix:///first.sock";
        var endpointCalls = 0;
        var runner = new FakeDockerCliRunner(scope.Context, (args, _, _, _, _) =>
        {
            if (args.Count > 0 && args[0] == "info")
            {
                return Task.FromResult(new DockerCliRunResult(0, "test-daemon", false, false));
            }
            if (args.Count > 1 && args[0] == "image" && args[1] == "inspect")
            {
                return Task.FromResult(new DockerCliRunResult(
                    0,
                    "sha256:" + new string('a', 64),
                    false,
                    false));
            }
            if (args.Count > 0 && args[0] == "inspect")
            {
                return Task.FromResult(new DockerCliRunResult(1, string.Empty, false, false));
            }
            return Task.FromResult(new DockerCliRunResult(0, string.Empty, false, false));
        }, () =>
        {
            endpointCalls++;
            return ambientEndpoint;
        });
        var configService = new DockerExecutionWorkspaceConfigService(scope.Context);
        var target = new DockerExecutionTarget(
            scope.Context,
            configService,
            lifecycle,
            imageCatalogService: null,
            runner,
            new PassThroughDockerMountIdentityVerifier());
        var (workspace, _) = CreateDockerWorkspace(scope);
        var binding = CreateBinding(workspace.WorkspaceId, "docker");
        await configService.SaveConfigAsync(
            binding.BindingId,
            new DockerExecutionWorkspaceConfig("test-image:1.0", "sunder-agent-test", "/bin/sh"));
        var context = new AgentExecutionTargetContext(null, null, workspace, binding);
        var (_, approvedContext) = await ApproveDockerResourceAsync(
            target,
            context,
            "file.txt",
            "files.read");
        ambientEndpoint = "unix:///second.sock";

        var result = await ReadDockerFileAsync(target, approvedContext, new AgentFileReadRequest("file.txt"));

        Assert.Equal(AgentFileReadErrorCodes.FileNotFound, result.ErrorCode);
        Assert.Equal(1, endpointCalls);
        Assert.NotEmpty(runner.RawCalls);
        Assert.All(runner.RawCalls, args =>
        {
            Assert.True(args.Count >= 3);
            Assert.Equal("--host", args[0]);
            Assert.Equal("unix:///first.sock", args[1]);
        });
    }

    [UnixTheory("Docker mount verification requires Unix file identities.")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DockerExecutionTarget_VerifiesMountAfterCreateAndReuseOrStart(bool runningOnSecondAcquire)
    {
        using var scope = TestScope.Create();
        using var lifecycle = new DockerContainerLifecycleService();
        var inspectCount = 0;
        string? signature = null;
        IReadOnlyList<string>? runArgs = null;
        var runner = new FakeDockerCliRunner(scope.Context, (args, _, _, _, _) =>
        {
            if (args.Count > 0 && args[0] == "context")
            {
                return Task.FromResult(new DockerCliRunResult(0, "unix:///var/run/docker.sock", false, false));
            }
            if (args.Count > 0 && args[0] == "info")
            {
                return Task.FromResult(new DockerCliRunResult(0, "test-daemon", false, false));
            }
            if (args.Count > 1 && args[0] == "image" && args[1] == "inspect")
            {
                return Task.FromResult(new DockerCliRunResult(0, "sha256:" + new string('a', 64), false, false));
            }
            if (args.Count > 0 && args[0] == "inspect")
            {
                inspectCount++;
                Assert.Contains("younics.ai.agent.execution.docker.resources.signature", args[2], StringComparison.Ordinal);
                return Task.FromResult(inspectCount == 1
                    ? new DockerCliRunResult(1, string.Empty, false, false)
                    : new DockerCliRunResult(0, $"{runningOnSecondAcquire.ToString().ToLowerInvariant()} {signature}", false, false));
            }
            if (args.Count > 0 && args[0] == "run")
            {
                runArgs = args.ToArray();
                var labelIndex = args.ToList().IndexOf("--label");
                Assert.StartsWith("younics.ai.agent.execution.docker.resources.signature=", args[labelIndex + 1], StringComparison.Ordinal);
                signature = args[labelIndex + 1].Split('=', 2)[1];
            }
            return Task.FromResult(new DockerCliRunResult(0, string.Empty, false, false));
        });
        var verifier = new PassThroughDockerMountIdentityVerifier();
        var imageRunner = new FakeDockerCliRunner(scope.Context, (_, _, _, _, _) =>
            Task.FromResult(new DockerCliRunResult(0, string.Empty, false, false)));
        var imageCatalog = new DockerImageCatalogService(scope.Context, imageRunner);
        await imageCatalog.AddImageAsync("test-image:1.0");
        var configService = new DockerExecutionWorkspaceConfigService(scope.Context, imageCatalog);
        var target = new DockerExecutionTarget(
            scope.Context,
            configService,
            lifecycle,
            imageCatalog,
            runner,
            verifier);
        var (workspace, _) = CreateDockerWorkspace(scope);
        var binding = CreateBinding(workspace.WorkspaceId, "docker");
        await configService.SaveConfigAsync(
            binding.BindingId,
            new DockerExecutionWorkspaceConfig("test-image:1.0", "sunder-agent-test", "/bin/sh"));
        var context = new AgentExecutionTargetContext(null, null, workspace, binding);

        var first = await GetDockerReadinessAsync(target, context);
        var second = await GetDockerReadinessAsync(target, context);

        Assert.True(first.Status.ToString() == nameof(AgentExecutionTargetReadinessStatus.Ready), first.Message);
        Assert.True(second.Status.ToString() == nameof(AgentExecutionTargetReadinessStatus.Ready), second.Message);

        Assert.Equal(2, verifier.VerifyCount);
        var createdWith = Assert.IsAssignableFrom<IReadOnlyList<string>>(runArgs);
        var userIndex = createdWith.ToList().IndexOf("--user");
        Assert.True(userIndex >= 0);
        Assert.Equal(
            DockerHostAccessPolicy.Resolve().ContainerUser,
            createdWith[userIndex + 1]);
        Assert.Equal(
            runningOnSecondAcquire ? 0 : 1,
            runner.Calls.Count(args => args.Count > 0 && args[0] == "start"));
    }

    [Fact]
    public async Task DockerCli_CreateStartInfo_OnlySetsInputEncodingWhenInputIsRedirected()
    {
        using var scope = TestScope.Create();
        ConfigureFakeDockerCli(scope);

        var runStartInfo = await DockerCli.CreateStartInfoAsync(scope.Context, ["run", "image"], redirectStandardInput: false);
        var writeStartInfo = await DockerCli.CreateStartInfoAsync(scope.Context, ["exec", "-i", "container"], redirectStandardInput: true);

        Assert.False(runStartInfo.RedirectStandardInput);
        Assert.Null(runStartInfo.StandardInputEncoding);
        Assert.True(writeStartInfo.RedirectStandardInput);
        Assert.NotNull(writeStartInfo.StandardInputEncoding);
    }

    [Fact]
    public void DockerCli_ResolveExecutable_FindsFallback_WhenInheritedPathOmitsDocker()
    {
        var fallbackPath = "/opt/homebrew/bin/docker";
        var pathValue = string.Join(Path.PathSeparator, "/usr/bin", "/bin");

        var resolution = DockerCli.ResolveExecutable(
            configuredPath: null,
            environmentPath: null,
            pathValue,
            [fallbackPath],
            path => string.Equals(path, fallbackPath, StringComparison.Ordinal),
            isWindows: false);

        Assert.Equal(fallbackPath, resolution.ExecutablePath);
    }

    [Fact]
    public void DockerCli_ResolveExecutable_UsesConfiguredPathBeforePath()
    {
        const string configuredPath = "/custom/docker";
        const string pathDocker = "/usr/local/bin/docker";
        var pathValue = "/usr/local/bin";

        var resolution = DockerCli.ResolveExecutable(
            configuredPath,
            environmentPath: null,
            pathValue,
            fallbackPaths: [],
            path => path is configuredPath or pathDocker,
            isWindows: false);

        Assert.Equal(configuredPath, resolution.ExecutablePath);
    }

    [Fact]
    public async Task DockerCli_CreateStartInfo_UsesConfiguredPathAndAugmentsPath()
    {
        using var scope = TestScope.Create();
        var dockerPath = ConfigureFakeDockerCli(scope);

        var startInfo = await DockerCli.CreateStartInfoAsync(scope.Context, ["pull", "custom:1.0"], redirectStandardInput: false);

        Assert.Equal(dockerPath, startInfo.FileName);
        Assert.False(startInfo.RedirectStandardInput);
        Assert.Contains("pull", startInfo.ArgumentList);
        var startInfoPath = startInfo.Environment["PATH"];
        Assert.NotNull(startInfoPath);
        Assert.Contains(Path.GetDirectoryName(dockerPath)!, startInfoPath.Split(Path.PathSeparator));
    }

    [Fact]
    public async Task DockerExecutionTarget_GetReadinessAsync_StopsBeforeContainerWhenImageIsNotReady()
    {
        using var scope = TestScope.Create();
        using var lifecycle = new DockerContainerLifecycleService();
        var targetCallCount = 0;
        var imageRunner = new FakeDockerCliRunner(scope.Context, (args, _, _, _, _) =>
        {
            Assert.Equal(["image", "inspect", "custom:1.0"], args);
            return Task.FromResult(new DockerCliRunResult(1, "No such image", TimedOut: false, WasTruncated: false));
        });
        var targetRunner = new FakeDockerCliRunner(scope.Context, (_, _, _, _, _) =>
        {
            targetCallCount++;
            return Task.FromResult(new DockerCliRunResult(0, string.Empty, TimedOut: false, WasTruncated: false));
        });
        var imageCatalog = new DockerImageCatalogService(scope.Context, imageRunner);
        await imageCatalog.AddImageAsync("custom:1.0");
        var configService = new DockerExecutionWorkspaceConfigService(scope.Context, imageCatalog);
        var target = new DockerExecutionTarget(
            scope.Context,
            configService,
            lifecycle,
            imageCatalog,
            targetRunner,
            new PassThroughDockerMountIdentityVerifier());
        var (workspace, _) = CreateDockerWorkspace(scope);
        var binding = CreateBinding(workspace.WorkspaceId, "docker");
        await configService.SaveConfigAsync(binding.BindingId, new DockerExecutionWorkspaceConfig("custom:1.0", "sunder-agent-test", "/bin/sh"));

        var readiness = await GetDockerReadinessAsync(target, new AgentExecutionTargetContext(null, null, workspace, binding));

        Assert.Equal(nameof(AgentExecutionTargetReadinessStatus.Failed), readiness.Status.ToString());
        Assert.Contains("Pull it", readiness.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, targetCallCount);
    }

    [Fact]
    public async Task DockerExecutionTarget_GetReadinessAsync_ReportsDockerRunOutput()
    {
        using var scope = TestScope.Create();
        using var lifecycle = new DockerContainerLifecycleService();
        var runArgs = Array.Empty<string>();
        var imageRunner = new FakeDockerCliRunner(scope.Context, (_, _, _, _, _) =>
            Task.FromResult(new DockerCliRunResult(0, string.Empty, TimedOut: false, WasTruncated: false)));
        var targetRunner = new FakeDockerCliRunner(scope.Context, (args, _, _, _, _) =>
        {
            if (args.Count > 0 && string.Equals(args[0], "context", StringComparison.Ordinal))
            {
                return Task.FromResult(new DockerCliRunResult(0, "unix:///var/run/docker.sock", TimedOut: false, WasTruncated: false));
            }
            if (args.Count > 0 && string.Equals(args[0], "info", StringComparison.Ordinal))
            {
                return Task.FromResult(new DockerCliRunResult(0, "test-daemon", TimedOut: false, WasTruncated: false));
            }
            if (args.Count > 1 && args[0] == "image" && args[1] == "inspect")
            {
                return Task.FromResult(new DockerCliRunResult(0, "sha256:" + new string('a', 64), TimedOut: false, WasTruncated: false));
            }
            if (args.Count > 0 && string.Equals(args[0], "inspect", StringComparison.Ordinal))
            {
                return Task.FromResult(new DockerCliRunResult(1, string.Empty, TimedOut: false, WasTruncated: false));
            }

            if (args.Count > 0 && string.Equals(args[0], "run", StringComparison.Ordinal))
            {
                runArgs = args.ToArray();
                return Task.FromResult(new DockerCliRunResult(1, "No such image: custom:1.0", TimedOut: false, WasTruncated: false));
            }

            return Task.FromResult(new DockerCliRunResult(0, string.Empty, TimedOut: false, WasTruncated: false));
        });
        var imageCatalog = new DockerImageCatalogService(scope.Context, imageRunner);
        await imageCatalog.AddImageAsync("custom:1.0");
        var configService = new DockerExecutionWorkspaceConfigService(scope.Context, imageCatalog);
        var target = new DockerExecutionTarget(
            scope.Context,
            configService,
            lifecycle,
            imageCatalog,
            targetRunner,
            new PassThroughDockerMountIdentityVerifier());
        var (workspace, _) = CreateDockerWorkspace(scope);
        var binding = CreateBinding(workspace.WorkspaceId, "docker");
        await configService.SaveConfigAsync(binding.BindingId, new DockerExecutionWorkspaceConfig("custom:1.0", "sunder-agent-test", "/bin/sh"));

        var readiness = await GetDockerReadinessAsync(target, new AgentExecutionTargetContext(null, null, workspace, binding));

        Assert.Equal(nameof(AgentExecutionTargetReadinessStatus.Failed), readiness.Status.ToString());
        if (OperatingSystem.IsWindows())
        {
            const string unavailable = "Strict Docker bind-mount identity and mutation verification is unavailable on Windows.";
            Assert.Equal(unavailable, readiness.Message);
            var failure = await Assert.ThrowsAsync<PlatformNotSupportedException>(async () =>
                await ApproveDockerResourceAsync(target, new AgentExecutionTargetContext(null, null, workspace, binding), "file.txt", "files.read"));
            Assert.Equal(unavailable, failure.Message);
            Assert.DoesNotContain(targetRunner.Calls, args => args.Count > 0 && args[0] is "run" or "start" or "exec");
            Assert.Empty(runArgs);
        }
        else
        {
            Assert.Contains("failed to start from image 'custom:1.0'", readiness.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("No such image", readiness.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("--pull", runArgs);
            Assert.Contains("never", runArgs);
        }
    }

    [UnixFact("Docker container image replacement requires supported host bind-mount verification.")]
    public async Task DockerExecutionTarget_MutableTagChangeRecreatesContainerByImageId()
    {
        using var scope = TestScope.Create();
        using var lifecycle = new DockerContainerLifecycleService();
        var firstImageId = "sha256:" + new string('a', 64);
        var secondImageId = "sha256:" + new string('b', 64);
        var imageIdentityCallCount = 0;
        var containerInspectCallCount = 0;
        string? firstSignature = null;
        var runCalls = new List<IReadOnlyList<string>>();
        var targetRunner = new FakeDockerCliRunner(scope.Context, (args, _, _, _, _) =>
        {
            if (args.Count > 0 && args[0] == "context")
            {
                return Task.FromResult(new DockerCliRunResult(0, "unix:///var/run/docker.sock", TimedOut: false, WasTruncated: false));
            }
            if (args.Count > 0 && args[0] == "info")
            {
                return Task.FromResult(new DockerCliRunResult(0, "test-daemon", TimedOut: false, WasTruncated: false));
            }
            if (args.Count >= 5
                && args[0] == "image"
                && args[1] == "inspect"
                && args[2] == "--format")
            {
                imageIdentityCallCount++;
                return Task.FromResult(new DockerCliRunResult(
                    0,
                    imageIdentityCallCount == 1 ? firstImageId : secondImageId,
                    TimedOut: false,
                    WasTruncated: false));
            }

            if (args.Count > 0 && args[0] == "inspect")
            {
                containerInspectCallCount++;
                return Task.FromResult(containerInspectCallCount == 1
                    ? new DockerCliRunResult(1, string.Empty, TimedOut: false, WasTruncated: false)
                    : new DockerCliRunResult(0, $"true {firstSignature}", TimedOut: false, WasTruncated: false));
            }

            if (args.Count > 0 && args[0] == "run")
            {
                runCalls.Add(args.ToArray());
                var labelIndex = args.ToList().IndexOf("--label");
                firstSignature ??= args[labelIndex + 1].Split('=', 2)[1];
            }

            return Task.FromResult(new DockerCliRunResult(0, string.Empty, TimedOut: false, WasTruncated: false));
        });
        var imageRunner = new FakeDockerCliRunner(scope.Context, (_, _, _, _, _) =>
            Task.FromResult(new DockerCliRunResult(0, string.Empty, TimedOut: false, WasTruncated: false)));
        var imageCatalog = new DockerImageCatalogService(scope.Context, imageRunner);
        await imageCatalog.AddImageAsync("custom:1.0");
        var configService = new DockerExecutionWorkspaceConfigService(scope.Context, imageCatalog);
        var target = new DockerExecutionTarget(
            scope.Context,
            configService,
            lifecycle,
            imageCatalog,
            targetRunner,
            new PassThroughDockerMountIdentityVerifier());
        var (workspace, _) = CreateDockerWorkspace(scope);
        var binding = CreateBinding(workspace.WorkspaceId, "docker");
        await configService.SaveConfigAsync(
            binding.BindingId,
            new DockerExecutionWorkspaceConfig("custom:1.0", "sunder-agent-test", "/bin/sh"));
        var context = new AgentExecutionTargetContext(null, null, workspace, binding);

        Assert.Equal(nameof(AgentExecutionTargetReadinessStatus.Ready), (await GetDockerReadinessAsync(target, context)).Status.ToString());
        var generation = await GetDockerConfigurationGenerationAsync(target, context);
        using (var restartedLifecycle = new DockerContainerLifecycleService())
        {
            var restartedTarget = new DockerExecutionTarget(
                scope.Context,
                configService,
                restartedLifecycle,
                imageCatalog,
                targetRunner,
                new PassThroughDockerMountIdentityVerifier());
            Assert.Equal(generation, await GetDockerConfigurationGenerationAsync(restartedTarget, context));
        }
        var staleReadiness = await GetDockerReadinessAsync(target, context with
        {
            ExpectedConfigurationGeneration = generation,
        });
        Assert.Equal(nameof(AgentExecutionTargetReadinessStatus.Failed), staleReadiness.Status.ToString());
        Assert.Contains("image identity changed", staleReadiness.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(nameof(AgentExecutionTargetReadinessStatus.Ready), (await GetDockerReadinessAsync(target, context)).Status.ToString());

        Assert.Equal(2, runCalls.Count);
        Assert.Contains(firstImageId, runCalls[0]);
        Assert.Contains(secondImageId, runCalls[1]);
        Assert.DoesNotContain("custom:1.0", runCalls[0]);
        Assert.DoesNotContain("custom:1.0", runCalls[1]);
        Assert.Contains(targetRunner.Calls, args => args.SequenceEqual(["rm", "-f", "sunder-agent-test"]));
    }

    [UnixFact("Docker mount-root replacement requires Unix file identities.")]
    public async Task DockerExecutionTarget_RemovesContainerWhenMountRootChangesDuringCreate()
    {
        using var scope = TestScope.Create();
        using var lifecycle = new DockerContainerLifecycleService();
        var imageRunner = new FakeDockerCliRunner(scope.Context, (_, _, _, _, _) =>
            Task.FromResult(new DockerCliRunResult(0, string.Empty, TimedOut: false, WasTruncated: false)));
        var imageCatalog = new DockerImageCatalogService(scope.Context, imageRunner);
        await imageCatalog.AddImageAsync("custom:1.0");
        var configService = new DockerExecutionWorkspaceConfigService(scope.Context, imageCatalog);
        var (workspace, hostPath) = CreateDockerWorkspace(scope);
        var parked = hostPath + "-parked";
        var targetRunner = new FakeDockerCliRunner(scope.Context, (args, _, _, _, _) =>
        {
            if (args.Count > 0 && args[0] == "context")
            {
                return Task.FromResult(new DockerCliRunResult(0, "unix:///var/run/docker.sock", TimedOut: false, WasTruncated: false));
            }
            if (args.Count > 0 && args[0] == "info")
            {
                return Task.FromResult(new DockerCliRunResult(0, "test-daemon", TimedOut: false, WasTruncated: false));
            }
            if (args.Count > 1 && args[0] == "image" && args[1] == "inspect")
            {
                return Task.FromResult(new DockerCliRunResult(0, "sha256:" + new string('a', 64), TimedOut: false, WasTruncated: false));
            }
            if (args.Count > 0 && args[0] == "inspect")
            {
                return Task.FromResult(new DockerCliRunResult(1, string.Empty, TimedOut: false, WasTruncated: false));
            }
            if (args.Count > 0 && args[0] == "run")
            {
                Directory.Move(hostPath, parked);
                Directory.CreateDirectory(hostPath);
            }
            return Task.FromResult(new DockerCliRunResult(0, string.Empty, TimedOut: false, WasTruncated: false));
        });
        var target = new DockerExecutionTarget(scope.Context, configService, lifecycle, imageCatalog, targetRunner);
        var binding = CreateBinding(workspace.WorkspaceId, "docker");
        await configService.SaveConfigAsync(
            binding.BindingId,
            new DockerExecutionWorkspaceConfig("custom:1.0", "sunder-agent-test", "/bin/sh"));

        var readiness = await GetDockerReadinessAsync(
            target,
            new AgentExecutionTargetContext(null, null, workspace, binding));

        Assert.Equal(nameof(AgentExecutionTargetReadinessStatus.Failed), readiness.Status.ToString());
        Assert.Contains("retained-root identity challenge", readiness.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(targetRunner.Calls, args => args.SequenceEqual(["rm", "-f", "sunder-agent-test"]));
    }

    [Fact]
    public async Task DockerContainerLifecycleService_StopsContainerAfterIdleTimeout()
    {
        using var lifecycle = new DockerContainerLifecycleService(TimeSpan.FromMilliseconds(25));
        var stopCount = 0;

        using (await lifecycle.AcquireAsync(
                   "container-key",
                   _ => Task.FromResult("container-name"),
                   (_, _) =>
                   {
                       Interlocked.Increment(ref stopCount);
                       return Task.CompletedTask;
                   }))
        {
            await Task.Delay(75);
            Assert.Equal(0, Volatile.Read(ref stopCount));
        }

        await WaitUntilAsync(() => Volatile.Read(ref stopCount) == 1);
    }

    [Fact]
    public async Task DockerContainerLifecycleService_SerializesContainerAndStructuredOperations()
    {
        using var lifecycle = new DockerContainerLifecycleService(TimeSpan.FromMinutes(1));
        var containerLease = await lifecycle.AcquireAsync(
            "container-key",
            _ => Task.FromResult("container-name"),
            (_, _) => Task.CompletedTask);
        var structuredTask = lifecycle.AcquireOperationAsync("container-key");

        await Task.Delay(25);
        Assert.False(structuredTask.IsCompleted);
        containerLease.Dispose();
        using (await structuredTask)
        {
        }

        var structuredLease = await lifecycle.AcquireOperationAsync("container-key");
        var ensureCount = 0;
        var containerTask = lifecycle.AcquireAsync(
            "container-key",
            _ =>
            {
                Interlocked.Increment(ref ensureCount);
                return Task.FromResult("container-name");
            },
            (_, _) => Task.CompletedTask);
        await Task.Delay(25);
        Assert.False(containerTask.IsCompleted);
        Assert.Equal(0, Volatile.Read(ref ensureCount));
        structuredLease.Dispose();
        using (await containerTask)
        {
        }
    }

    [Fact]
    public async Task DockerContainerLifecycleService_Dispose_DoesNotStopContainerSynchronously()
    {
        var lifecycle = new DockerContainerLifecycleService(TimeSpan.FromMinutes(1));
        var stopCount = 0;

        using (await lifecycle.AcquireAsync(
                   "container-key",
                   _ => Task.FromResult("container-name"),
                   (_, _) =>
                   {
                       Interlocked.Increment(ref stopCount);
                       return Task.CompletedTask;
                   }))
        {
        }

        lifecycle.Dispose();

        Assert.Equal(0, Volatile.Read(ref stopCount));
    }

    [Fact]
    public async Task DockerContainerLifecycleService_DisposeAsync_StopsKnownContainer()
    {
        var lifecycle = new DockerContainerLifecycleService(TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(1));
        var stopCount = 0;

        using (await lifecycle.AcquireAsync(
                   "container-key",
                   _ => Task.FromResult("container-name"),
                   (_, _) =>
                   {
                       Interlocked.Increment(ref stopCount);
                       return Task.CompletedTask;
                   }))
        {
        }

        await lifecycle.DisposeAsync();

        Assert.Equal(1, Volatile.Read(ref stopCount));
    }

    private static async Task<(AgentResolvedResource Resource, AgentExecutionTargetContext Context)>
        ApproveDockerResourceAsync(
            DockerExecutionTarget target,
            AgentExecutionTargetContext context,
            string path,
            string actionId)
    {
        var planningContext = context with
        {
            ResourceOperation = CreateResourceOperation(actionId),
        };
        var resource = ToAgentResource(await ResolveDockerFileAsync(target, planningContext, path));
        return (resource, planningContext with
        {
            ApprovedResourceReferences = [resource.CanonicalReference],
            ApprovedResourceClaims = resource.ResourceClaim is null ? [] : [resource.ResourceClaim],
        });
    }

    private static DockerWorkerModels.AgentExecutionTargetContext ToDockerExecutionContext(
        AgentExecutionTargetContext context)
        => new()
        {
            SessionId = context.SessionId,
            ProfileId = context.ProfileId,
            Workspace = ToDockerWorkspace(context.Workspace),
            Binding = new()
            {
                BindingId = context.Binding.BindingId,
                WorkspaceId = context.Binding.WorkspaceId,
                ExtensionPointId = context.Binding.ExtensionPointId,
                ContributionId = context.Binding.ContributionId,
                Role = context.Binding.Role,
                IsEnabled = context.Binding.IsEnabled,
                SortOrder = context.Binding.SortOrder,
                CreatedAtUtc = DockerTime(context.Binding.CreatedAtUtc),
                UpdatedAtUtc = DockerTime(context.Binding.UpdatedAtUtc),
            },
            AllowOutsideConfiguredScope = context.AllowOutsideConfiguredScope,
            ApprovedResourceReferences = context.ApprovedResourceReferences,
            ApprovedResourceClaims = context.ApprovedResourceClaims.Select(claim => new DockerWorkerModels.AgentResourceClaim
            {
                Version = claim.Version,
                NamespaceId = claim.NamespaceId,
                LogicalPath = claim.LogicalPath,
                ConfiguredRoot = claim.ConfiguredRoot,
                RootIdentityChainFingerprint = claim.RootIdentityChainFingerprint,
                TargetExists = claim.TargetExists,
                TargetKind = claim.TargetKind,
                TargetIdentity = claim.TargetIdentity,
                TargetIsAuthorityRoot = claim.TargetIsAuthorityRoot,
                CaseSensitivePath = claim.CaseSensitivePath,
                ActionId = claim.ActionId,
                WorkspaceId = claim.WorkspaceId,
                WorkspaceGeneration = claim.WorkspaceGeneration,
                BindingId = claim.BindingId,
                BindingGeneration = claim.BindingGeneration,
                ToolCallId = claim.ToolCallId,
                ResourceIndex = claim.ResourceIndex,
                ToolOwnerPackageId = claim.ToolOwnerPackageId,
                ExecutionTargetOwnerPackageId = claim.ExecutionTargetOwnerPackageId,
            }).ToArray(),
            ApprovedResourceCapabilities = context.ApprovedResourceCapabilities,
            ResourceOperation = context.ResourceOperation is { } operation
                ? new DockerWorkerModels.AgentResourceOperationContext
                {
                    RunId = operation.RunId,
                    RunRevision = operation.RunRevision,
                    ToolCallId = operation.ToolCallId,
                    ActionId = operation.ActionId,
                    ResourceIndex = operation.ResourceIndex,
                    WorkspaceGeneration = operation.WorkspaceGeneration,
                    BindingGeneration = operation.BindingGeneration,
                    ToolOwnerPackageId = operation.ToolOwnerPackageId,
                    ExecutionTargetOwnerPackageId = operation.ExecutionTargetOwnerPackageId,
                    AuthorityActivationId = operation.AuthorityActivationId,
                    AuthorityUseCount = operation.AuthorityUseCount,
                    CanIssueOutsideAuthority = operation.CanIssueOutsideAuthority,
                }
                : null,
            CapturePostMutationResource = context.CapturePostMutationResource,
            ExpectedConfigurationGeneration = context.ExpectedConfigurationGeneration,
        };

    private static ValueTask<DockerWorkerModels.AgentFileReadResult> ReadDockerFileAsync(
        DockerExecutionTarget target,
        AgentExecutionTargetContext context,
        AgentFileReadRequest request)
        => target.ReadFileAsync(new()
        {
            Context = ToDockerExecutionContext(context),
            Request = new() { Path = request.Path, Offset = request.Offset, Limit = request.Limit },
        });

    private static ValueTask<DockerWorkerModels.AgentFileMutationResult> WriteDockerFileAsync(
        DockerExecutionTarget target,
        AgentExecutionTargetContext context,
        AgentFileWriteRequest request)
        => target.WriteFileAsync(new()
        {
            Context = ToDockerExecutionContext(context),
            Request = new()
            {
                Path = request.Path,
                Content = request.Content,
                Overwrite = request.Overwrite,
                ExpectedContentHash = request.ExpectedContentHash,
            },
        });

    private static ValueTask<DockerWorkerModels.AgentFileMutationResult> DeleteDockerFileAsync(
        DockerExecutionTarget target,
        AgentExecutionTargetContext context,
        AgentFileDeleteRequest request)
        => target.DeleteFileAsync(new()
        {
            Context = ToDockerExecutionContext(context),
            Request = new()
            {
                Path = request.Path,
                Recursive = request.Recursive,
                ExpectedContentHash = request.ExpectedContentHash,
            },
        });

    private static ValueTask<DockerWorkerModels.AgentExecutionScopeDescriptor> GetDockerExecutionScopeAsync(
        DockerExecutionTarget target,
        AgentExecutionTargetContext context)
        => target.ExecutionScopeAsync(ToDockerExecutionContext(context));

    private static ValueTask<DockerWorkerModels.AgentExecutionTargetReadiness> GetDockerReadinessAsync(
        DockerExecutionTarget target,
        AgentExecutionTargetContext context)
        => target.ReadinessAsync(ToDockerExecutionContext(context));

    private static ValueTask<string?> GetDockerConfigurationGenerationAsync(
        DockerExecutionTarget target,
        AgentExecutionTargetContext context)
        => target.ConfigurationGenerationAsync(ToDockerExecutionContext(context));

    private static ValueTask<DockerWorkerModels.AgentResolvedResource> ResolveDockerFileAsync(
        DockerExecutionTarget target,
        AgentExecutionTargetContext context,
        string path)
        => target.ResolveFileAsync(new()
        {
            Context = ToDockerExecutionContext(context),
            Path = path,
        });

    private static AgentResolvedResource ToAgentResource(DockerWorkerModels.AgentResolvedResource resource)
        => new(
            resource.ResourceKind,
            resource.DisplayName,
            resource.CanonicalReference,
            resource.PermissionBoundaryId,
            resource.Exists)
        {
            ScopeClassificationBasis = Enum.Parse<AgentPermissionScopeClassificationBasis>(resource.ScopeClassificationBasis.ToString()),
            ResourceClaim = resource.ResourceClaim is { } claim ? ToAgentResourceClaim(claim) : null,
            AuthorityReferences = resource.AuthorityReferences,
            DeleteCanonicalReference = resource.DeleteCanonicalReference,
            DeleteResourceClaim = resource.DeleteResourceClaim is { } deleteClaim ? ToAgentResourceClaim(deleteClaim) : null,
            DeleteAuthorityReferences = resource.DeleteAuthorityReferences,
            DeletePermissionBoundaryId = resource.DeletePermissionBoundaryId,
            IsScopedInstructionDocument = resource.IsScopedInstructionDocument,
        };

    private static AgentResourceClaim ToAgentResourceClaim(DockerWorkerModels.AgentResourceClaim claim)
        => new(
            (int)claim.Version,
            claim.NamespaceId,
            claim.LogicalPath,
            claim.ConfiguredRoot,
            claim.RootIdentityChainFingerprint,
            claim.TargetExists,
            claim.TargetKind,
            claim.TargetIdentity,
            claim.TargetIsAuthorityRoot,
            claim.CaseSensitivePath,
            claim.ActionId,
            claim.WorkspaceId,
            claim.WorkspaceGeneration,
            claim.BindingId,
            claim.BindingGeneration,
            claim.ToolCallId,
            (int)claim.ResourceIndex,
            claim.ToolOwnerPackageId,
            claim.ExecutionTargetOwnerPackageId);

    private static ValueTask<DockerWorkerModels.AgentShellCommandResult> ExecuteDockerShellAsync(
        DockerExecutionTarget target,
        AgentExecutionTargetContext context,
        AgentShellCommandRequest request)
        => target.ExecuteShellAsync(new()
        {
            Context = ToDockerExecutionContext(context),
            Request = new()
            {
                Command = request.Command,
                WorkingDirectory = request.WorkingDirectory,
                TimeoutSeconds = request.TimeoutSeconds,
            },
        });

    private static ValueTask<DockerWorkerModels.AgentScopedInstructionDiscoveryResult> DiscoverDockerInstructionsAsync(
        DockerExecutionTarget target,
        AgentExecutionTargetContext context,
        AgentScopedInstructionDiscoveryRequest request)
        => target.DiscoverInstructionsAsync(new()
        {
            Context = ToDockerExecutionContext(context),
            Request = new()
            {
                Probes = request.Probes.Select(probe => new DockerWorkerModels.AgentScopedInstructionProbe
                {
                    Path = probe.Path,
                    IsDirectory = probe.IsDirectory,
                    FollowFinalSymbolicLink = probe.FollowFinalSymbolicLink,
                }).ToArray(),
            },
        });

    private static string ConfigureFakeDockerCli(TestScope scope)
    {
        var dockerPath = Path.Combine(scope.RootPath, "bin", OperatingSystem.IsWindows() ? "docker.exe" : "docker");
        Directory.CreateDirectory(Path.GetDirectoryName(dockerPath)!);
        File.WriteAllText(dockerPath, string.Empty);
        ((TestSettings)scope.Context.Settings).Seed(DockerCli.ExecutablePathConfigurationKey, dockerPath);
        return dockerPath;
    }
}
