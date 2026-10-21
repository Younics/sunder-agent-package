extern alias DockerWorker;

using DockerWorker::Sunder.Package.Agent.Execution.Docker;
using Sunder.Sdk.Rpc;
using AgentFileDeleteRequest = DockerWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentFileDeleteRequest;
using AgentFileReadRequest = DockerWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentFileReadRequest;
using DockerModels = DockerWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Models;
using Xunit;

namespace Sunder.Package.Agent.Execution.Local.Tests;

using DockerWorker::Sunder.Agent.Execution.Common;

public sealed class DockerStrictReauditTests : IDisposable
{
    private readonly string _root = Path.Combine(
        OperatingSystem.IsMacOS() ? "/private" + Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar) : Path.GetTempPath(),
        "sunder-docker-strict-reaudit",
        Guid.NewGuid().ToString("N"));

    [PlatformFact(TestPlatform.Unix, "The mount identity verifier requires Unix file descriptors.")]
    public async Task MountVerifier_PerformsBidirectionalNestedChallengeAndExactCleanup()
    {
        Directory.CreateDirectory(_root);
        using var root = HostSecurePathEngine.OpenRoot(_root);
        var mount = new DockerExecutionMount(_root, _root.Replace('\\', '/'));
        var config = CreateConfig([mount]);
        var shell = new ShellDockerCommandExecutor();

        await new DockerMountIdentityVerifier().VerifyAsync(
            "unused-container",
            config,
            [new DockerVerifiedMount(mount, root)],
            (args, token) => shell.RunAsync(args, 5, token),
            CancellationToken.None);

        Assert.DoesNotContain(
            Directory.EnumerateFileSystemEntries(_root),
            path => HostSecurePathEngine.IsChallengeName(Path.GetFileName(path)));
    }

    [PlatformFact(TestPlatform.Unix, "The mount identity verifier requires Unix file descriptors.")]
    public async Task MountVerifier_MismatchFailsClosedAndZerosHiddenChallenge()
    {
        Directory.CreateDirectory(_root);
        using var root = HostSecurePathEngine.OpenRoot(_root);
        var mount = new DockerExecutionMount(_root, _root.Replace('\\', '/'));
        var config = CreateConfig([mount]);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new DockerMountIdentityVerifier().VerifyAsync(
                "unused-container",
                config,
                [new DockerVerifiedMount(mount, root)],
                static (_, _) => Task.FromResult(new DockerCliRunResult(0, string.Empty, false, false)),
                CancellationToken.None));

        Assert.DoesNotContain(
            Directory.EnumerateFileSystemEntries(_root),
            path => HostSecurePathEngine.IsChallengeName(Path.GetFileName(path)));
        var retainedChallengeFiles = Directory.EnumerateFiles(
                _root,
                HostSecureMountChallenge.HostChallengeFileName,
                SearchOption.AllDirectories)
            .ToArray();
        Assert.All(retainedChallengeFiles, path => Assert.Equal(0, new FileInfo(path).Length));
    }

    [Fact]
    public async Task DockerConfiguredClaim_IsStableDataAndExecutionUsesFreshAuthority()
    {
        Directory.CreateDirectory(_root);
        var config = CreateConfig([new DockerExecutionMount(_root, "/workspace")]);
        var executor = new DockerFileSystemExecutor();
        var context = CreateResourceContext("files.mutate");
        var resolved = executor.ResolveFileResource(
            config,
            "/workspace/new.txt",
            "namespace",
            CancellationToken.None,
            context: context);
        var path = DockerPathResolver.ResolveHostBinding(config, "/workspace/new.txt");
        var claim = Assert.IsType<DockerModels.AgentResourceClaim>(resolved.ResourceClaim);

        Assert.Empty(resolved.AuthorityReferences);
        Assert.StartsWith(DockerFileSystemExecutor.ClaimNamespacePrefix, resolved.CanonicalReference, StringComparison.Ordinal);
        Assert.Equal("/workspace/new.txt", claim.LogicalPath);
        Assert.Equal("/workspace", claim.ConfiguredRoot);

        var mountRoot = HostSecurePathEngine.OpenRoot(path.Mount.HostPath);
        var authority = HostSecurePathEngine.CaptureFromRoot(
            mountRoot,
            path.HostPath,
            allowMissingSuffix: true);
        HostResourceClaim.Validate(
            claim,
            DockerFileSystemExecutor.ClaimNamespacePrefix + "namespace",
            path.ContainerPath,
            path.Mount.ContainerPath,
            authority,
            context);
        var result = await executor.WriteFileAsync(
            config,
            new DockerModels.AgentFileWriteRequest
            {
                Path = "/workspace/new.txt",
                Content = "content",
                Overwrite = false,
            },
            authority,
            CancellationToken.None);

        Assert.False(result.IsError, result.Summary);
        Assert.Equal("content", await File.ReadAllTextAsync(Path.Combine(_root, "new.txt")));
    }

    private static DockerModels.AgentExecutionTargetContext CreateResourceContext(string actionId)
    {
        var now = DateTimeOffset.UtcNow;
        var timestamp = new SunderRpcOffsetDateTime(now.ToString("O"));
        var workspace = new DockerModels.AgentWorkspaceRecord
        {
            WorkspaceId = "workspace",
            DisplayName = "Workspace",
            CreatedAtUtc = timestamp,
            UpdatedAtUtc = timestamp,
            Paths = [],
            Documents = [],
        };
        var binding = new DockerModels.AgentWorkspaceBindingRecord
        {
            BindingId = "binding",
            WorkspaceId = workspace.WorkspaceId,
            ExtensionPointId = "execution-targets",
            ContributionId = "docker",
            Role = "primary-execution-target",
            IsEnabled = true,
            SortOrder = 0,
            CreatedAtUtc = timestamp,
            UpdatedAtUtc = timestamp,
        };
        return new DockerModels.AgentExecutionTargetContext
        {
            Workspace = workspace,
            Binding = binding,
            AllowOutsideConfiguredScope = false,
            ApprovedResourceReferences = [],
            ApprovedResourceClaims = [],
            ApprovedResourceCapabilities = [],
            CapturePostMutationResource = false,
            ResourceOperation = new DockerModels.AgentResourceOperationContext
            {
                RunId = Guid.NewGuid(),
                RunRevision = 1,
                ToolCallId = "tool-call",
                ActionId = actionId,
                ResourceIndex = 0,
                WorkspaceGeneration = "workspace-generation",
                BindingGeneration = "binding-generation",
                ToolOwnerPackageId = "younics.ai.agent.tools.files",
                ExecutionTargetOwnerPackageId = "younics.ai.agent.execution.docker",
                AuthorityActivationId = Guid.NewGuid().ToString("N"),
                AuthorityUseCount = 1,
                CanIssueOutsideAuthority = false,
            },
        };
    }

    [Fact]
    public void DockerV3Lease_RejectsAReplacementMountGeneration()
    {
        Directory.CreateDirectory(_root);
        var parked = _root + "-parked";
        var config = CreateConfig([new DockerExecutionMount(_root, "/workspace")]);
        var path = DockerPathResolver.ResolveHostBinding(config, "/workspace/new.txt");
        var mountRoot = HostSecurePathEngine.OpenRoot(_root);
        var approvedChains = DockerMountRootIdentityChains.Capture(
            [new DockerVerifiedMount(path.Mount, mountRoot)]);
        var resourceAuthority = HostSecurePathEngine.CaptureFromRoot(mountRoot, path.HostPath);
        var reference = DockerResourceReference.Create(
            "namespace",
            path,
            resourceAuthority,
            approvedChains);
        Directory.Move(_root, parked);
        Directory.CreateDirectory(_root);
        try
        {
            using var replacement = HostSecurePathEngine.OpenRoot(_root);
            var replacementChains = DockerMountRootIdentityChains.Capture(
                [new DockerVerifiedMount(path.Mount, replacement)]);

            Assert.NotEqual(approvedChains[0].Identities[^1], replacement.Handle.Identity);
            Assert.False(DockerResourceReference.TryRedeem(
                reference,
                "namespace",
                path,
                replacementChains,
                out _));
        }
        finally
        {
            Directory.Delete(_root);
            Directory.Move(parked, _root);
            if (DockerResourceReference.TryRedeem(
                    reference,
                    "namespace",
                    path,
                    approvedChains,
                    out var retained))
            {
                retained!.Dispose();
            }
        }
    }

    [Fact]
    public void DockerV3Lease_RejectsChangedAncestorChainWithSameFinalMountIdentity()
    {
        Directory.CreateDirectory(_root);
        var config = CreateConfig([new DockerExecutionMount(_root, "/workspace")]);
        var path = DockerPathResolver.ResolveHostBinding(config, "/workspace/new.txt");
        var mountRoot = HostSecurePathEngine.OpenRoot(_root);
        var approvedChains = DockerMountRootIdentityChains.Capture(
            [new DockerVerifiedMount(path.Mount, mountRoot)]);
        var resourceAuthority = HostSecurePathEngine.CaptureFromRoot(mountRoot, path.HostPath);
        var reference = DockerResourceReference.Create(
            "namespace",
            path,
            resourceAuthority,
            approvedChains);
        var identities = approvedChains[0].Identities.ToArray();
        Assert.True(identities.Length >= 2);
        identities[^2] = identities[^2] with { FileId = identities[^2].FileId + 1 };
        var changedChains = new[]
        {
            approvedChains[0] with { Identities = identities },
        };

        Assert.Equal(approvedChains[0].Identities[^1], changedChains[0].Identities[^1]);
        Assert.False(DockerResourceReference.TryRedeem(
            reference,
            "namespace",
            path,
            changedChains,
            out _));
        Assert.True(DockerResourceReference.TryRedeem(
            reference,
            "namespace",
            path,
            approvedChains,
            out var retained));
        retained!.Dispose();
    }

    [Fact]
    public async Task DockerDelete_ProtectsNestedPhysicalRootAcrossSiblingAliases()
    {
        var nested = Path.Combine(_root, "nested");
        Directory.CreateDirectory(nested);
        await File.WriteAllTextAsync(Path.Combine(nested, "keep.txt"), "keep");
        var config = CreateConfig(
        [
            new DockerExecutionMount(_root, "/outer-alias"),
            new DockerExecutionMount(nested, "/sibling-alias"),
        ]);

        var result = await new DockerFileSystemExecutor().DeleteFileAsync(
            config,
            new AgentFileDeleteRequest { Path = "/outer-alias/nested", Recursive = true },
            CancellationToken.None);

        Assert.Equal(DockerPathResolver.StructuredRootDeleteErrorCode, result.ErrorCode);
        Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(nested, "keep.txt")));
    }

    [PlatformFact(TestPlatform.MacCaseInsensitive, "This verifies case-insensitive macOS mount aliases.")]
    public async Task DockerDelete_ProtectsNestedRootThroughCaseInsensitiveMacAlias()
    {
        var outer = Path.Combine(_root, "CaseRoot");
        var nested = Path.Combine(outer, "NestedRoot");
        Directory.CreateDirectory(nested);
        await File.WriteAllTextAsync(Path.Combine(nested, "keep.txt"), "keep");
        var lowerAlias = Path.Combine(_root, "caseroot", "nestedroot");
        var config = CreateConfig(
        [
            new DockerExecutionMount(outer, "/outer"),
            new DockerExecutionMount(lowerAlias, "/nested"),
        ]);
        var executor = new DockerFileSystemExecutor();
        var path = DockerPathResolver.ResolveHostBinding(config, "/outer/NestedRoot");
        var outerRoot = HostSecurePathEngine.OpenRoot(config.Mounts[0].HostPath);
        using var nestedRoot = HostSecurePathEngine.OpenRoot(config.Mounts[1].HostPath);
        var currentChains = DockerMountRootIdentityChains.Capture(
        [
            new DockerVerifiedMount(config.Mounts[0], outerRoot),
            new DockerVerifiedMount(config.Mounts[1], nestedRoot),
        ]);
        var authority = HostSecurePathEngine.CaptureFromRoot(outerRoot, path.HostPath);
        var result = await executor.DeleteFileAsync(
            config,
            new AgentFileDeleteRequest { Path = "/outer/NestedRoot", Recursive = true },
            authority,
            currentChains,
            CancellationToken.None);

        Assert.Equal(DockerPathResolver.StructuredRootDeleteErrorCode, result.ErrorCode);
        Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(nested, "keep.txt")));
    }

    [Fact]
    public async Task DockerDirectoryListing_AlwaysUsesPosixSuffix()
    {
        var child = Path.Combine(_root, "child");
        Directory.CreateDirectory(child);
        var config = CreateConfig([new DockerExecutionMount(_root, "/workspace")]);

        var result = await new DockerFileSystemExecutor().ReadFileAsync(
            config,
            new AgentFileReadRequest { Path = "/workspace" },
            CancellationToken.None);

        Assert.False(result.IsError, result.ErrorMessage);
        Assert.Contains("child/", result.Content, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
        }
    }

    private static DockerExecutionRuntimeConfig CreateConfig(IReadOnlyList<DockerExecutionMount> mounts)
        => new(
            "image",
            "container",
            "/bin/sh",
            [],
            mounts,
            mounts[0].ContainerPath);
}
