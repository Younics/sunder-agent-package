using System.Reflection;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Execution.Docker;
using Sunder.Package.Agent.Execution.Local;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.Tests;
using Sunder.Package.Agent.Tools.Files;
using Sunder.Sdk.Abstractions;
using Xunit;
using Xunit.Sdk;

namespace Sunder.Package.Agent.Execution.Local.Tests;

public sealed class ExecutionTargetFileConformanceTests : IDisposable
{
    public static IEnumerable<object[]> RangedTargets()
    {
        yield return ["local"];
        yield return ["docker"];
    }

    private readonly string _root = Path.Combine(
        OperatingSystem.IsMacOS() ? "/private" + Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar) : Path.GetTempPath(),
        "sunder-execution-conformance",
        Guid.NewGuid().ToString("N"));

    [Theory]
    [MemberData(nameof(RangedTargets))]
    public async Task RangedTargets_ReturnSelectedLinesAndTotalMetadata(string targetKind)
    {
        var fixture = CreateFixture(targetKind);
        await fixture.WriteTextAsync("range.txt", "one\ntwo\nthree\nfour");

        var result = await fixture.ReadAsync(new AgentFileReadRequest("range.txt", Offset: 2, Limit: 2));

        Assert.False(result.IsError, result.ErrorMessage);
        Assert.Equal("two\nthree", result.Content);
        Assert.Equal(2, result.StartLine);
        Assert.Equal(3, result.EndLine);
        Assert.Equal(4, result.TotalLines);
        Assert.True(result.WasTruncated);
    }

    [Theory]
    [MemberData(nameof(RangedTargets))]
    public async Task RangedTargets_ReturnStructuredMissingBinaryAndRangeErrors(string targetKind)
    {
        var fixture = CreateFixture(targetKind);
        await fixture.WriteBytesAsync("binary.bin", [1, 0, 2]);
        await fixture.WriteTextAsync("short.txt", "one\ntwo");

        var missing = await fixture.ReadAsync(new AgentFileReadRequest("missing.txt", 1, 10));
        var binary = await fixture.ReadAsync(new AgentFileReadRequest("binary.bin", 1, 10));
        var invalidRange = await fixture.ReadAsync(new AgentFileReadRequest("short.txt", 0, 10));
        var outsideRange = await fixture.ReadAsync(new AgentFileReadRequest("short.txt", 3, 1));

        AssertReadError(missing, AgentFileReadErrorCodes.FileNotFound);
        AssertReadError(binary, AgentFileReadErrorCodes.BinaryFile);
        AssertReadError(invalidRange, AgentFileReadErrorCodes.InvalidRange);
        AssertReadError(outsideRange, AgentFileReadErrorCodes.RangeOutsideFile);
        Assert.Equal(2, outsideRange.TotalLines);
    }

    [Theory]
    [MemberData(nameof(RangedTargets))]
    public async Task RangedTargets_RejectLexicalTraversal(string targetKind)
    {
        var fixture = CreateFixture(targetKind);

        var result = await fixture.ReadAsync(new AgentFileReadRequest("../outside.txt", 1, 1));

        AssertReadError(
            result,
            targetKind == "docker"
                ? DockerPathResolver.StructuredBindRequiredErrorCode
                : AgentFileReadErrorCodes.OutsideConfiguredScope);
    }

    [Theory]
    [MemberData(nameof(RangedTargets))]
    public async Task MutationTargets_ReturnStructuredScopeFailures(string targetKind)
    {
        var fixture = CreateFixture(targetKind);

        var write = await fixture.WriteAsync(new AgentFileWriteRequest("../outside.txt", "unsafe"));
        var delete = await fixture.DeleteAsync(new AgentFileDeleteRequest("../outside.txt"));

        var expectedCode = targetKind == "docker"
            ? DockerPathResolver.StructuredBindRequiredErrorCode
            : AgentFileReadErrorCodes.OutsideConfiguredScope;
        AssertMutationError(write, expectedCode);
        AssertMutationError(delete, expectedCode);
    }

    [Fact]
    public async Task DockerStructuredOperations_RejectContainerPrivatePathsEvenWithLegacyApprovalContext()
    {
        var fixture = CreateDockerFixture();
        const string outside = "/container-private/file.txt";
        var fileSystem = new DockerFileSystemExecutor();
        var config = CreateDockerConfig(fixture.Root);
        var resolve = Assert.Throws<DockerStructuredBindRequiredException>(() =>
            fileSystem.ResolveFileResource(
                config,
                outside,
                "namespace",
                CancellationToken.None));
        var write = await fileSystem.WriteFileAsync(
            config,
            new AgentFileWriteRequest(outside, "blocked"),
            CancellationToken.None);
        var read = await fileSystem.ReadFileAsync(
            config,
            new AgentFileReadRequest(outside),
            CancellationToken.None);

        Assert.Equal(DockerPathResolver.StructuredBindRequiredErrorCode, resolve.ErrorCode);
        AssertMutationError(write, DockerPathResolver.StructuredBindRequiredErrorCode);
        AssertReadError(read, DockerPathResolver.StructuredBindRequiredErrorCode);
        Assert.Throws<DockerStructuredBindRequiredException>(() => DockerPathResolver.MapToHostPath(config, outside));
    }

    [Fact]
    public async Task LocalHelper_RequiresExactApprovedCanonicalReferenceForOutsideAccess()
    {
        var workspace = Path.Combine(_root, "local-approved-workspace");
        var outside = Path.Combine(_root, "local-approved-outside", "file.txt");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(Path.GetDirectoryName(outside)!);
        var config = new LocalExecutionRuntimeConfig([workspace], workspace);
        using var writeApproval = LocalResourceAuthorityTestContext.Approve(config, outside, "files.mutate");

        var unbound = await LocalFileSystemExecutor.WriteFileAsync(
            config,
            new AgentFileWriteRequest(outside, "unbound"),
            allowOutsideConfiguredScope: true,
            CancellationToken.None);
        var write = await LocalFileSystemExecutor.WriteFileAsync(
            config,
            new AgentFileWriteRequest(outside, "approved"),
            allowOutsideConfiguredScope: true,
            CancellationToken.None,
            approvedResourceReferences: writeApproval.Context.ApprovedResourceReferences,
            authorizationContext: writeApproval.Context,
            resourceReferences: writeApproval.ResourceReferences);
        using var readApproval = LocalResourceAuthorityTestContext.Approve(config, outside, "files.read");
        var read = await LocalFileSystemExecutor.ReadFileAsync(
            config,
            new AgentFileReadRequest(outside),
            allowOutsideConfiguredScope: true,
            CancellationToken.None,
            approvedResourceReferences: readApproval.Context.ApprovedResourceReferences,
            authorizationContext: readApproval.Context,
            resourceReferences: readApproval.ResourceReferences);

        AssertMutationError(unbound, AgentFileReadErrorCodes.OutsideConfiguredScope);
        Assert.False(write.IsError, write.Summary);
        Assert.False(read.IsError, read.ErrorMessage);
        Assert.Equal("approved", read.Content);
    }

    [Fact]
    public void DockerCanonicalResourceReferenceV3_IsOpaqueAndSingleRedemption()
    {
        var root = Path.Combine(_root, "resource-reference");
        Directory.CreateDirectory(root);
        var config = CreateDockerConfig(root);
        var path = DockerPathResolver.ResolveHostBinding(config, "/workspace/path\nwith-newline");
        var mountRoot = HostSecurePathEngine.OpenRoot(path.Mount.HostPath);
        var mountIdentityChains = DockerMountRootIdentityChains.Capture(
            [new DockerVerifiedMount(path.Mount, mountRoot)]);
        var resourceAuthority = HostSecurePathEngine.CaptureFromRoot(mountRoot, path.HostPath);
        var resourceBinding = resourceAuthority.Binding;
        var reference = DockerResourceReference.Create("namespace", path, resourceAuthority, mountIdentityChains);

        Assert.StartsWith("docker-resource-v3:", reference, StringComparison.Ordinal);
        Assert.DoesNotContain(Path.GetTempPath(), reference, StringComparison.Ordinal);
        Assert.True(DockerResourceReference.TryRedeem(reference, "namespace", path, mountIdentityChains, out var parsed));
        using (parsed)
        {
            _ = parsed!.ValidateMountGeneration(path, mountIdentityChains);
            using var authority = parsed!.TakeResourceAuthority();
            Assert.Equal(resourceBinding, authority.Binding);
        }
        Assert.False(DockerResourceReference.TryRedeem(reference, "namespace", path, mountIdentityChains, out _));
        var wrongMountRoot = HostSecurePathEngine.OpenRoot(path.Mount.HostPath);
        var wrongMountIdentityChains = DockerMountRootIdentityChains.Capture(
            [new DockerVerifiedMount(path.Mount, wrongMountRoot)]);
        var wrongNamespaceReference = DockerResourceReference.Create(
            "namespace",
            path,
            HostSecurePathEngine.CaptureFromRoot(wrongMountRoot, path.HostPath),
            wrongMountIdentityChains);
        Assert.False(DockerResourceReference.TryRedeem(
            wrongNamespaceReference,
            "other-namespace",
            path,
            wrongMountIdentityChains,
            out _));
        Assert.False(DockerResourceReference.TryRedeem(
            "docker-resource-v1:legacy",
            "namespace",
            path,
            mountIdentityChains,
            out _));
        Assert.False(DockerResourceReference.TryRedeem(
            "docker-resource-v2:legacy",
            "namespace",
            path,
            mountIdentityChains,
            out _));
    }

    [Theory]
    [InlineData("-delete")]
    [InlineData("line\nbreak")]
    [InlineData("quote'name")]
    [InlineData("-dash-name")]
    [InlineData("backslash\\dir")]
    public async Task DockerStructuredSearch_PreservesContainerPath(string pathName)
    {
        var hostRoot = Path.Combine(_root, "workspace-" + Guid.NewGuid().ToString("N"));
        var searchRoot = Path.Combine(hostRoot, pathName);
        Directory.CreateDirectory(searchRoot);
        await File.WriteAllTextAsync(Path.Combine(searchRoot, "match.txt"), "match");
        var result = await new DockerFileSystemExecutor().SearchAsync(
            CreateDockerConfig(hostRoot),
            new AgentFileSearchRequest(
                "/workspace/" + pathName,
                AgentFileSearchKind.Glob,
                "**/*.txt"),
            CancellationToken.None);

        var match = Assert.Single(result.Matches);
        Assert.False(result.IsError, result.ErrorMessage);
        Assert.Equal($"/workspace/{pathName}/match.txt", match.Path);
    }

    [Theory]
    [MemberData(nameof(RangedTargets))]
    public async Task MutationTargets_RejectStaleExpectedContent(string targetKind)
    {
        var fixture = CreateFixture(targetKind);
        await fixture.WriteTextAsync("conditional.txt", "newer");
        var staleHash = ContentHash("older");

        var write = await fixture.WriteAsync(new AgentFileWriteRequest("conditional.txt", "replacement")
        {
            ExpectedContentHash = staleHash,
        });
        var delete = await fixture.DeleteAsync(new AgentFileDeleteRequest("conditional.txt")
        {
            ExpectedContentHash = staleHash,
        });

        AssertMutationError(write, "file-content-changed");
        AssertMutationError(delete, "file-content-changed");
        Assert.Equal("newer", await File.ReadAllTextAsync(Path.Combine(fixture.Root, "conditional.txt")));
    }

    [Fact]
    public async Task DockerStructuredOperations_RejectSymlinkEscapeForReadWriteDeleteAndRangedRead()
    {
        var fixture = CreateDockerFixture();
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        await File.WriteAllTextAsync(Path.Combine(outside, "secret.txt"), "secret");
        CreateDirectorySymlinkOrSkip(Path.Combine(fixture.Root, "escape"), outside);

        var read = await fixture.ReadAsync(new AgentFileReadRequest("escape/secret.txt"));
        var ranged = await fixture.ReadAsync(new AgentFileReadRequest("escape/secret.txt", 1, 1));
        var write = await fixture.WriteAsync(new AgentFileWriteRequest("escape/new.txt", "bad"));
        var delete = await fixture.DeleteAsync(new AgentFileDeleteRequest("escape/secret.txt"));

        AssertReadError(read, AgentFileReadErrorCodes.PathCanonicalizationFailed);
        AssertReadError(ranged, AgentFileReadErrorCodes.PathCanonicalizationFailed);
        AssertMutationError(write, AgentFileReadErrorCodes.PathCanonicalizationFailed);
        AssertMutationError(delete, AgentFileReadErrorCodes.PathCanonicalizationFailed);
        Assert.False(File.Exists(Path.Combine(outside, "new.txt")));
        Assert.True(File.Exists(Path.Combine(outside, "secret.txt")));
    }

    [Fact]
    public async Task DockerStructuredOperations_RejectInternalSymlinks()
    {
        var fixture = CreateDockerFixture();
        var target = Path.Combine(fixture.Root, "target");
        Directory.CreateDirectory(target);
        CreateDirectorySymlinkOrSkip(Path.Combine(fixture.Root, "link"), target);

        var write = await fixture.WriteAsync(new AgentFileWriteRequest("link/file.txt", "inside"));
        var read = await fixture.ReadAsync(new AgentFileReadRequest("link/file.txt", 1, 1));
        var delete = await fixture.DeleteAsync(new AgentFileDeleteRequest("link/file.txt"));

        AssertMutationError(write, AgentFileReadErrorCodes.PathCanonicalizationFailed);
        AssertReadError(read, AgentFileReadErrorCodes.PathCanonicalizationFailed);
        AssertMutationError(delete, AgentFileReadErrorCodes.PathCanonicalizationFailed);
        Assert.False(File.Exists(Path.Combine(target, "file.txt")));
    }

    [Theory]
    [MemberData(nameof(RangedTargets))]
    public async Task MutationTargets_DoNotFollowFileSymlinkDuringDelete(string targetKind)
    {
        var fixture = CreateFixture(targetKind);
        var target = Path.Combine(fixture.Root, "target.txt");
        var link = Path.Combine(fixture.Root, "link.txt");
        await File.WriteAllTextAsync(target, "keep");
        CreateFileSymlinkOrSkip(link, target);

        var delete = await fixture.DeleteAsync(new AgentFileDeleteRequest("link.txt")
        {
            ExpectedContentHash = ContentHash("keep"),
        });

        AssertMutationError(delete, AgentFileReadErrorCodes.PathCanonicalizationFailed);
        Assert.True(File.Exists(link));
        Assert.True(File.Exists(target));
        Assert.Equal("keep", await File.ReadAllTextAsync(target));
    }

    [Fact]
    public void LocalResourceResolution_RejectsLinkEntry()
    {
        var fixture = CreateLocalFixture();
        var outsideTarget = Path.Combine(_root, "outside-target.txt");
        var link = Path.Combine(fixture.Root, "link.txt");
        File.WriteAllText(outsideTarget, "outside");
        CreateFileSymlinkOrSkip(link, outsideTarget);
        var config = new LocalExecutionRuntimeConfig([fixture.Root], fixture.Root);

        Assert.Throws<LocalSecurePathException>(() => LocalResourceResolver.ResolveFileResource(
            config,
            "link.txt",
            allowOutsideConfiguredScope: true));
    }

    [Theory]
    [InlineData("local")]
    [InlineData("docker")]
    public async Task CompoundPatch_RejectsCanonicalAgentsSymlinkAliasForEveryFileTarget(string targetKind)
    {
        var root = Path.Combine(_root, "canonical-agents", targetKind);
        Directory.CreateDirectory(root);
        var agentsPath = Path.Combine(root, "AGENTS.md");
        var aliasPath = Path.Combine(root, "policy-alias.md");
        await File.WriteAllTextAsync(agentsPath, "old policy");
        CreateFileSymlinkOrSkip(aliasPath, agentsPath);
        var target = targetKind == "local"
            ? new ResolvingExecutionTarget(
                targetKind,
                path => ValueTask.FromResult(LocalResourceResolver.ResolveFileResource(
                    new LocalExecutionRuntimeConfig([root], root),
                    path,
                    allowOutsideConfiguredScope: true)))
            : CreateDockerResolvingTarget(root);
        using var packageScope = RegressionTestPackageScope.Create();
        using var catalog = new RegressionTestExtensionCatalog();
        catalog.AddProvider(AgentRpcServices.ExecutionTargets, target);
        var source = new FilesToolSource(packageScope.Context);
        var now = DateTimeOffset.UtcNow;
        var workspace = new AgentWorkspaceRecord("workspace", "Workspace", null, now, now);
        var binding = new AgentWorkspaceBindingRecord(
            "binding",
            workspace.WorkspaceId,
            AgentRpcContractIds.ExecutionTarget,
            target.Descriptor.TargetId,
            "primary-execution-target",
            true,
            0,
            now,
            now);
        const string otherPath = "other.txt";
        var patch = $"*** Begin Patch\n*** Update File: policy-alias.md\n@@\n-old policy\n+new policy\n*** Add File: {otherPath}\n+changed\n*** End Patch";

        var result = await source.ExecuteAsync(
            new AgentToolExecutionContext(null, Workspace: workspace, ExecutionBinding: binding)
            {
                ExecutionTargetReference = catalog.GetRequiredReference(AgentRpcServices.ExecutionTargets),
            },
            new AgentToolRequest("apply_patch", JsonSerializer.Serialize(new { patchText = patch })));

        Assert.True(result.IsError);
        Assert.Equal("files-agents-patch-classification-failed", result.ErrorCode);
        Assert.Equal("old policy", await File.ReadAllTextAsync(agentsPath));
        Assert.False(File.Exists(Path.Combine(root, "other.txt")));
    }

    [Fact]
    public async Task DockerStructuredOperations_RejectDanglingSymlinkAsAmbiguous()
    {
        var fixture = CreateDockerFixture();
        CreateFileSymlinkOrSkip(Path.Combine(fixture.Root, "dangling.txt"), Path.Combine(fixture.Root, "missing-target.txt"));

        var read = await fixture.ReadAsync(new AgentFileReadRequest("dangling.txt"));
        var write = await fixture.WriteAsync(new AgentFileWriteRequest("dangling.txt", "content"));

        AssertReadError(read, AgentFileReadErrorCodes.PathCanonicalizationFailed);
        Assert.True(write.IsError);
        Assert.Equal(AgentFileReadErrorCodes.PathCanonicalizationFailed, write.ErrorCode);
    }

    [Fact]
    public async Task DockerStructuredOperations_ReturnStructuredErrorForNonFileNode()
    {
        if (OperatingSystem.IsWindows())
        {
            throw SkipException.ForSkip("A POSIX host filesystem is required.");
        }

        var root = Path.Combine(Path.GetTempPath(), "snr-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        try
        {
            var socketPath = Path.Combine(root, "service.sock");
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.Bind(new UnixDomainSocketEndPoint(socketPath));
            var config = CreateDockerConfig(root);

            var result = await new DockerFileSystemExecutor().ReadFileAsync(
                config,
                new AgentFileReadRequest("service.sock"),
                CancellationToken.None);

            AssertReadError(result, AgentFileReadErrorCodes.PathCanonicalizationFailed);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DockerStructuredOperations_TreatHostileNamesAsLiteralPaths()
    {
        var fixture = CreateDockerFixture();
        const string hostileName = "quote' ; touch SUNDER_PWNED ; $(touch SUNDER_SUBSTITUTED).txt";

        var write = await fixture.WriteAsync(new AgentFileWriteRequest(hostileName, "safe"));
        var read = await fixture.ReadAsync(new AgentFileReadRequest(hostileName, 1, 1));

        Assert.False(write.IsError, write.Summary);
        Assert.Equal("safe", read.Content);
        Assert.True(File.Exists(Path.Combine(fixture.Root, hostileName)));
        Assert.False(File.Exists(Path.Combine(fixture.Root, "SUNDER_PWNED")));
        Assert.False(File.Exists(Path.Combine(fixture.Root, "SUNDER_SUBSTITUTED")));
    }

    [Fact]
    public async Task DockerStructuredOperations_PreservePosixComponentWhitespaceAndBackslashes()
    {
        if (OperatingSystem.IsWindows())
        {
            throw SkipException.ForSkip("Backslashes are Windows separators and are rejected by Docker host mapping.");
        }

        var fixture = CreateDockerFixture();
        const string requestedPath = "  folder  / file\\name ";

        var write = await fixture.WriteAsync(new AgentFileWriteRequest(requestedPath, "exact"));
        var read = await fixture.ReadAsync(new AgentFileReadRequest(requestedPath));

        Assert.False(write.IsError, write.Summary);
        Assert.False(read.IsError, read.ErrorMessage);
        Assert.Equal("exact", read.Content);
        Assert.True(File.Exists(Path.Combine(fixture.Root, "  folder  ", " file\\name ")));
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "folder")));
    }

    [Fact]
    public async Task DockerStructuredDelete_RejectsConfiguredMountRootAndNestedMountAncestor()
    {
        var outer = Path.Combine(_root, "delete-outer");
        var nested = Path.Combine(_root, "delete-nested");
        Directory.CreateDirectory(Path.Combine(outer, "parent"));
        Directory.CreateDirectory(nested);
        await File.WriteAllTextAsync(Path.Combine(outer, "parent", "keep.txt"), "keep");
        var config = new DockerExecutionRuntimeConfig(
            "image",
            "container",
            "/bin/sh",
            [],
            [
                new DockerExecutionMount(outer, "/workspace"),
                new DockerExecutionMount(nested, "/workspace/parent/nested"),
            ],
            "/workspace");
        var executor = new DockerFileSystemExecutor();

        var rootDelete = await executor.DeleteFileAsync(
            config,
            new AgentFileDeleteRequest("/workspace", Recursive: true),
            CancellationToken.None);
        var ancestorDelete = await executor.DeleteFileAsync(
            config,
            new AgentFileDeleteRequest("/workspace/parent", Recursive: true),
            CancellationToken.None);

        Assert.Equal(DockerPathResolver.StructuredRootDeleteErrorCode, rootDelete.ErrorCode);
        Assert.Equal(DockerPathResolver.StructuredRootDeleteErrorCode, ancestorDelete.ErrorCode);
        Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(outer, "parent", "keep.txt")));
    }

    [Fact]
    public async Task DockerStructuredOperations_RejectSymlinkedHostBindRoot()
    {
        var hostRoot = Path.Combine(_root, "docker-real-root");
        var linkedRoot = Path.Combine(_root, "docker-linked-root");
        Directory.CreateDirectory(hostRoot);
        CreateDirectorySymlinkOrSkip(linkedRoot, hostRoot);
        var fileSystem = new DockerFileSystemExecutor();
        var config = CreateDockerConfig(linkedRoot);

        var result = await fileSystem.WriteFileAsync(
            config,
            new AgentFileWriteRequest("file.txt", "content"),
            CancellationToken.None);

        AssertMutationError(result, AgentFileReadErrorCodes.PathCanonicalizationFailed);
        Assert.False(File.Exists(Path.Combine(hostRoot, "file.txt")));
    }

    [Fact]
    public async Task DockerLexicalScopeComparison_IsCaseSensitive()
    {
        var root = Path.Combine(_root, "CaseSensitiveRoot");
        Directory.CreateDirectory(root);
        var config = CreateDockerConfig(root);
        var result = await new DockerFileSystemExecutor().ReadFileAsync(
            config,
            new AgentFileReadRequest("/WORKSPACE/file.txt"),
            CancellationToken.None);

        AssertReadError(result, DockerPathResolver.StructuredBindRequiredErrorCode);
    }

    [Fact]
    public async Task DockerStructuredOperations_RejectOutsideConfiguredBind()
    {
        var workspace = Path.Combine(_root, "workspace");
        Directory.CreateDirectory(workspace);
        var config = CreateDockerConfig(workspace);
        var fileSystem = new DockerFileSystemExecutor();

        var read = await fileSystem.ReadFileAsync(
            config,
            new AgentFileReadRequest("/outside-override.txt"),
            CancellationToken.None);

        AssertReadError(read, DockerPathResolver.StructuredBindRequiredErrorCode);
    }

    [Theory]
    [InlineData("remote", "unix:///var/run/docker.sock", "docker.endpoint.context-unsupported")]
    [InlineData("default", "tcp://127.0.0.1:2375", "docker.endpoint.remote-unsupported")]
    [InlineData("default", "ssh://docker@example.test", "docker.endpoint.remote-unsupported")]
    public void DockerEndpointPolicy_RejectsRemoteContextsAndEndpoints(
        string context,
        string endpoint,
        string errorCode)
    {
        var exception = Assert.Throws<DockerExecutionDomainException>(() =>
            DockerLocalEndpointPolicy.GetIdentity(context, endpoint));

        Assert.Equal(errorCode, exception.Code);
        Assert.Contains("local", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("default", "unix:///var/run/docker.sock")]
    [InlineData("desktop-linux", "unix:///Users/test/.docker/run/docker.sock")]
    [InlineData("default", "npipe:////./pipe/docker_engine")]
    public void DockerEndpointPolicy_AcceptsLocalEndpoints(string context, string endpoint)
    {
        var identity = DockerLocalEndpointPolicy.GetIdentity(context, endpoint);

        Assert.Equal(64, identity.Length);
        Assert.All(identity, character => Assert.True(Uri.IsHexDigit(character)));
    }

    [Fact]
    public async Task DockerRead_PropagatesCancellationAndEnforcesHostReadLimit()
    {
        var fixture = CreateDockerFixture();
        var config = CreateDockerConfig(fixture.Root);
        await fixture.WriteTextAsync("large.txt", new string('x', AgentPayloadLimits.MaxLocalFullFileReadBytes + 1));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new DockerFileSystemExecutor().ReadFileAsync(
                config,
                new AgentFileReadRequest("large.txt"),
                cancellation.Token).AsTask());

        var bounded = await fixture.ReadAsync(new AgentFileReadRequest("large.txt"));
        AssertReadError(bounded, AgentFileReadErrorCodes.TooLarge);
    }

    [Fact]
    public async Task DockerStructuredOperations_FailAtPermissionBoundaryWithoutCreatingTarget()
    {
        if (OperatingSystem.IsWindows())
        {
            throw SkipException.ForSkip("POSIX permission modes are required.");
        }
        if (string.Equals(Environment.UserName, "root", StringComparison.Ordinal))
        {
            return;
        }

        var fixture = CreateDockerFixture();
        var boundary = Path.Combine(fixture.Root, "boundary");
        Directory.CreateDirectory(boundary);
        File.SetUnixFileMode(boundary, UnixFileMode.None);
        try
        {
            var result = await fixture.WriteAsync(new AgentFileWriteRequest("boundary/new/file.txt", "content"));

            Assert.True(result.IsError);
            Assert.False(File.Exists(Path.Combine(boundary, "new", "file.txt")));
        }
        finally
        {
            File.SetUnixFileMode(boundary, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public async Task DockerExistenceQuery_UsesHostBindAndAllowsPatchAdd()
    {
        var fixture = CreateDockerFixture();
        var config = CreateDockerConfig(fixture.Root);
        var fileSystem = new DockerFileSystemExecutor();

        var before = fileSystem.ResolveFileResource(
            config,
            "added.txt",
            "namespace",
            CancellationToken.None);
        var write = await fixture.WriteAsync(new AgentFileWriteRequest("added.txt", "added", Overwrite: false));
        var after = fileSystem.ResolveFileResource(
            config,
            "added.txt",
            "namespace",
            CancellationToken.None);

        Assert.False(before.Exists);
        Assert.False(write.IsError, write.Summary);
        Assert.True(after.Exists);
        Assert.Equal("added", await File.ReadAllTextAsync(Path.Combine(fixture.Root, "added.txt")));
    }

    [Fact]
    public async Task DockerConditionalMutation_RejectsStaleContentWithoutDaemon()
    {
        var fixture = CreateDockerFixture();
        await fixture.WriteTextAsync("conditional.txt", "newer");
        var staleHash = ContentHash("older");

        var write = await fixture.WriteAsync(new AgentFileWriteRequest("conditional.txt", "replacement")
        {
            ExpectedContentHash = staleHash,
        });
        var delete = await fixture.DeleteAsync(new AgentFileDeleteRequest("conditional.txt")
        {
            ExpectedContentHash = staleHash,
        });

        Assert.Equal("file-content-changed", write.ErrorCode);
        Assert.Equal("file-content-changed", delete.ErrorCode);
        Assert.Equal("newer", await File.ReadAllTextAsync(Path.Combine(fixture.Root, "conditional.txt")));
    }

    [Fact]
    public void EveryRangedExecutionTarget_IsCoveredByConformanceData()
    {
        var targetAssemblies = new[] { typeof(LocalExecutionTarget).Assembly, typeof(DockerExecutionTarget).Assembly };
        var implementations = targetAssemblies
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => !type.IsAbstract && typeof(IAgentRangedFileExecutionTarget).IsAssignableFrom(type))
            .Select(type => type.FullName!)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        var coveredKinds = RangedTargets().Select(data => Assert.IsType<string>(data[0])).OrderBy(value => value, StringComparer.Ordinal).ToArray();

        Assert.Equal(
            [
                typeof(DockerExecutionTarget).FullName!,
                typeof(LocalExecutionTarget).FullName!,
            ],
            implementations);
        Assert.Equal(["docker", "local"], coveredKinds);
    }

    [Fact]
    public void ReadResult_PreservesExistingPositionalConstructorAndDeconstruction()
    {
        var result = new AgentFileReadResult("path", "content", IsDirectory: true, WasTruncated: true);

        var (path, content, isDirectory, wasTruncated) = result;

        Assert.Equal("path", path);
        Assert.Equal("content", content);
        Assert.True(isDirectory);
        Assert.True(wasTruncated);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private FileFixture CreateFixture(string targetKind)
        => targetKind switch
        {
            "local" => CreateLocalFixture(),
            "docker" => CreateDockerFixture(),
            _ => throw new ArgumentOutOfRangeException(nameof(targetKind)),
        };

    private FileFixture CreateLocalFixture()
    {
        var root = Path.Combine(_root, "local-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var config = new LocalExecutionRuntimeConfig([root], root);
        return new FileFixture(
            root,
            request => LocalFileSystemExecutor.ReadFileAsync(config, request, allowOutsideConfiguredScope: false, CancellationToken.None),
            request => LocalFileSystemExecutor.WriteFileAsync(config, request, allowOutsideConfiguredScope: false, CancellationToken.None),
            request => LocalFileSystemExecutor.DeleteFileAsync(config, request, allowOutsideConfiguredScope: false));
    }

    private DockerFileFixture CreateDockerFixture()
    {
        var root = Path.Combine(_root, "docker-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var config = CreateDockerConfig(root);
        var fileSystem = new DockerFileSystemExecutor();
        return new DockerFileFixture(
            root,
            request => fileSystem.ReadFileAsync(config, request, CancellationToken.None),
            request => fileSystem.WriteFileAsync(config, request, CancellationToken.None),
            request => fileSystem.DeleteFileAsync(config, request, CancellationToken.None));
    }

    private static DockerExecutionRuntimeConfig CreateDockerConfig(string root)
        => new("image", "container", "/bin/sh", [], [new DockerExecutionMount(root, "/workspace")], "/workspace");

    private static ResolvingExecutionTarget CreateDockerResolvingTarget(string root)
    {
        var fileSystem = new DockerFileSystemExecutor();
        var config = CreateDockerConfig(root);
        return new ResolvingExecutionTarget(
            "docker",
            path => ValueTask.FromResult(fileSystem.ResolveFileResource(
                config,
                path,
                "namespace",
                CancellationToken.None)));
    }

    private static string ContentHash(string content)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    private static void AssertReadError(AgentFileReadResult result, string expectedCode)
    {
        Assert.True(result.IsError);
        Assert.Equal(expectedCode, result.ErrorCode);
        Assert.Empty(result.Content);
        Assert.False(string.IsNullOrWhiteSpace(result.ErrorMessage));
    }

    private static void AssertMutationError(AgentFileMutationResult result, string expectedCode)
    {
        Assert.True(result.IsError);
        Assert.Equal(expectedCode, result.ErrorCode);
        Assert.False(string.IsNullOrWhiteSpace(result.Summary));
    }

    private static void CreateDirectorySymlinkOrSkip(string linkPath, string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException or NotSupportedException)
        {
            throw SkipException.ForSkip($"Symbolic link creation is unavailable: {ex.Message}");
        }
    }

    private static void CreateFileSymlinkOrSkip(string linkPath, string targetPath)
    {
        try
        {
            File.CreateSymbolicLink(linkPath, targetPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException or NotSupportedException)
        {
            throw SkipException.ForSkip($"Symbolic link creation is unavailable: {ex.Message}");
        }
    }

    private class FileFixture(
        string root,
        Func<AgentFileReadRequest, ValueTask<AgentFileReadResult>> read,
        Func<AgentFileWriteRequest, ValueTask<AgentFileMutationResult>> write,
        Func<AgentFileDeleteRequest, ValueTask<AgentFileMutationResult>> delete)
    {
        public string Root { get; } = root;

        public ValueTask<AgentFileReadResult> ReadAsync(AgentFileReadRequest request) => read(request);

        public ValueTask<AgentFileMutationResult> WriteAsync(AgentFileWriteRequest request) => write(request);

        public ValueTask<AgentFileMutationResult> DeleteAsync(AgentFileDeleteRequest request) => delete(request);

        public Task WriteTextAsync(string relativePath, string content)
        {
            var path = Path.Combine(Root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            return File.WriteAllTextAsync(path, content);
        }

        public Task WriteBytesAsync(string relativePath, byte[] content)
        {
            var path = Path.Combine(Root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            return File.WriteAllBytesAsync(path, content);
        }
    }

    private sealed class ResolvingExecutionTarget(
        string targetKind,
        Func<string, ValueTask<AgentResolvedResource>> resolve) : IAgentExecutionTarget
    {
        public AgentExecutionTargetDescriptor Descriptor { get; } = new(
            targetKind,
            targetKind,
            targetKind,
            null,
            SupportsShell: false,
            SupportsFiles: true);

        public ValueTask<AgentExecutionTargetReadiness> GetReadinessAsync(
            AgentExecutionTargetContext context,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentExecutionTargetReadiness(
                targetKind,
                targetKind,
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
            => resolve(path);

        public ValueTask<AgentShellCommandResult> ExecuteShellAsync(
            AgentExecutionTargetContext context,
            AgentShellCommandRequest request,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<AgentFileReadResult> ReadFileAsync(
            AgentExecutionTargetContext context,
            AgentFileReadRequest request,
            CancellationToken cancellationToken = default)
            => throw new XunitException("A rejected compound policy patch must not read files.");

        public ValueTask<AgentFileMutationResult> WriteFileAsync(
            AgentExecutionTargetContext context,
            AgentFileWriteRequest request,
            CancellationToken cancellationToken = default)
            => throw new XunitException("A rejected compound policy patch must not write files.");

        public ValueTask<AgentFileMutationResult> DeleteFileAsync(
            AgentExecutionTargetContext context,
            AgentFileDeleteRequest request,
            CancellationToken cancellationToken = default)
            => throw new XunitException("A rejected compound policy patch must not delete files.");
    }

    private sealed class DockerFileFixture(
        string root,
        Func<AgentFileReadRequest, ValueTask<AgentFileReadResult>> read,
        Func<AgentFileWriteRequest, ValueTask<AgentFileMutationResult>> write,
        Func<AgentFileDeleteRequest, ValueTask<AgentFileMutationResult>> delete)
        : FileFixture(root, read, write, delete)
    { }

    private sealed class RecordingDockerCliRunner(
        IPackageContext packageContext,
        Func<IReadOnlyList<string>, DockerCliRunResult> run) : DockerCliRunner(packageContext)
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];

        protected override Task<string> ResolveEndpointAsync(CancellationToken cancellationToken)
            => Task.FromResult("unix:///var/run/docker.sock");

        protected override Task<DockerCliRunResult> RunCoreAsync(
            IReadOnlyList<string> args,
            int timeoutSeconds,
            CancellationToken cancellationToken,
            string? standardInput = null,
            IProgress<string>? progress = null)
        {
            var logicalArgs = args.Count >= 2 && args[0] == "--host"
                ? args.Skip(2).ToArray()
                : args.ToArray();
            Calls.Add(logicalArgs);
            return Task.FromResult(run(logicalArgs));
        }
    }
}
