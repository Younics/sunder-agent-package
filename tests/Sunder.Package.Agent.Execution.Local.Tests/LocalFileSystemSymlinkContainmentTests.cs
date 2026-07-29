using System.Security.Cryptography;
using System.Text;
using Sunder.Agent.Execution.Common;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Execution.Docker;
using Sunder.Package.Agent.Execution.Local;
using Xunit;
using Xunit.Sdk;

namespace Sunder.Package.Agent.Execution.Local.Tests;

public sealed class LocalFileSystemSymlinkContainmentTests : IDisposable
{
    private readonly string _root = Path.Combine(
        OperatingSystem.IsMacOS() ? "/private" + Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar) : Path.GetTempPath(),
        "sunder-local-path-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ReadFileAsync_RejectsDirectorySymlinkEscape()
    {
        var (workspace, outside) = CreateDirectories();
        var secretPath = Path.Combine(outside, "secret.txt");
        await File.WriteAllTextAsync(secretPath, "outside");
        CreateDirectorySymlinkOrSkip(Path.Combine(workspace, "escape"), outside);
        var config = CreateConfig(workspace);

        var result = await LocalFileSystemExecutor.ReadFileAsync(
            config,
            new AgentFileReadRequest(Path.Combine("escape", "secret.txt")),
            allowOutsideConfiguredScope: false,
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal(AgentFileReadErrorCodes.PathCanonicalizationFailed, result.ErrorCode);
        Assert.Empty(result.Content);
    }

    [Fact]
    public void ResolveFileResource_RejectsDirectorySymlink()
    {
        var (workspace, outside) = CreateDirectories();
        CreateDirectorySymlinkOrSkip(Path.Combine(workspace, "escape"), outside);
        var config = CreateConfig(workspace);

        Assert.Throws<LocalSecurePathException>(() => LocalResourceResolver.ResolveFileResource(
            config,
            Path.Combine("escape", "file.txt"),
            allowOutsideConfiguredScope: true));
    }

    [Fact]
    public void MapToHostPath_RejectsDirectorySymlinkEscape()
    {
        var (workspace, outside) = CreateDirectories();
        CreateDirectorySymlinkOrSkip(Path.Combine(workspace, "escape"), outside);

        Assert.Throws<LocalSecurePathException>(() => LocalResourceResolver.MapToHostPath(
            CreateConfig(workspace),
            Path.Combine("escape", "project")));
    }

    [Fact]
    public void MapToHostPath_RejectsContainedDirectorySymlink()
    {
        var (workspace, _) = CreateDirectories();
        var target = Path.Combine(workspace, "target");
        Directory.CreateDirectory(target);
        CreateDirectorySymlinkOrSkip(Path.Combine(workspace, "link"), target);

        Assert.Throws<LocalSecurePathException>(() => LocalResourceResolver.MapToHostPath(
            CreateConfig(workspace),
            Path.Combine("link", "project")));
    }

    [Fact]
    public void ResolveWorkingDirectory_RejectsDirectorySymlinkEscape()
    {
        var (workspace, outside) = CreateDirectories();
        CreateDirectorySymlinkOrSkip(Path.Combine(workspace, "escape"), outside);

        Assert.Throws<InvalidOperationException>(() => LocalPathResolver.ResolveWorkingDirectory(
            CreateConfig(workspace),
            "escape",
            allowOutsideConfiguredScope: false));
    }

    [Fact]
    public void ResolveWorkingDirectory_RejectsDefaultDirectorySymlinkEscape()
    {
        var (workspace, outside) = CreateDirectories();
        var defaultWorkingDirectory = Path.Combine(workspace, "escape");
        CreateDirectorySymlinkOrSkip(defaultWorkingDirectory, outside);
        var config = new LocalExecutionRuntimeConfig([workspace], defaultWorkingDirectory);

        Assert.Throws<InvalidOperationException>(() => LocalPathResolver.ResolveWorkingDirectory(
            config,
            requestedWorkingDirectory: null,
            allowOutsideConfiguredScope: false));
    }

    [Fact]
    public void ResolveWorkingDirectory_ReturnsCanonicalPathForContainedDirectorySymlink()
    {
        var (workspace, _) = CreateDirectories();
        var target = Path.Combine(workspace, "target");
        Directory.CreateDirectory(target);
        CreateDirectorySymlinkOrSkip(Path.Combine(workspace, "link"), target);

        var resolved = LocalPathResolver.ResolveWorkingDirectory(
            CreateConfig(workspace),
            "link",
            allowOutsideConfiguredScope: false);

        Assert.Equal(Physical(target), resolved);
    }

    [Fact]
    public void DockerMapToHostPath_RejectsDirectorySymlinkEscape()
    {
        var (workspace, outside) = CreateDirectories();
        CreateDirectorySymlinkOrSkip(Path.Combine(workspace, "escape"), outside);
        var config = CreateDockerConfig(workspace);

        Assert.Throws<LocalSecurePathException>(() => DockerPathResolver.MapToHostPath(config, "/workspace/escape/project"));
    }

    [Fact]
    public void DockerMapToHostPath_RejectsContainedDirectorySymlink()
    {
        var (workspace, _) = CreateDirectories();
        var target = Path.Combine(workspace, "target");
        Directory.CreateDirectory(target);
        CreateDirectorySymlinkOrSkip(Path.Combine(workspace, "link"), target);

        Assert.Throws<LocalSecurePathException>(() =>
            DockerPathResolver.MapToHostPath(CreateDockerConfig(workspace), "/workspace/link/project"));
    }

    [Fact]
    public async Task WriteFileAsync_RejectsNewFileBelowDirectorySymlinkEscape()
    {
        var (workspace, outside) = CreateDirectories();
        CreateDirectorySymlinkOrSkip(Path.Combine(workspace, "escape"), outside);
        var config = CreateConfig(workspace);
        var outsidePath = Path.Combine(outside, "new", "file.txt");

        var result = await LocalFileSystemExecutor.WriteFileAsync(
            config,
            new AgentFileWriteRequest(Path.Combine("escape", "new", "file.txt"), "outside"),
            allowOutsideConfiguredScope: false,
            CancellationToken.None);

        Assert.Equal(AgentFileReadErrorCodes.PathCanonicalizationFailed, result.ErrorCode);
        Assert.False(File.Exists(outsidePath));
    }

    [Fact]
    public async Task WriteFileAsync_RejectsDanglingFileSymlinkEscape()
    {
        var (workspace, outside) = CreateDirectories();
        var outsidePath = Path.Combine(outside, "new.txt");
        CreateFileSymlinkOrSkip(Path.Combine(workspace, "escape.txt"), outsidePath);
        var config = CreateConfig(workspace);

        var result = await LocalFileSystemExecutor.WriteFileAsync(
            config,
            new AgentFileWriteRequest("escape.txt", "outside"),
            allowOutsideConfiguredScope: false,
            CancellationToken.None);

        Assert.Equal(AgentFileReadErrorCodes.PathCanonicalizationFailed, result.ErrorCode);
        Assert.False(File.Exists(outsidePath));
    }

    [Fact]
    public async Task DeleteFileAsync_RejectsDirectorySymlinkEscape()
    {
        var (workspace, outside) = CreateDirectories();
        var outsidePath = Path.Combine(outside, "keep.txt");
        await File.WriteAllTextAsync(outsidePath, "keep");
        CreateDirectorySymlinkOrSkip(Path.Combine(workspace, "escape"), outside);
        var config = CreateConfig(workspace);

        var result = await LocalFileSystemExecutor.DeleteFileAsync(
            config,
            new AgentFileDeleteRequest(Path.Combine("escape", "keep.txt")),
            allowOutsideConfiguredScope: false);

        Assert.Equal(AgentFileReadErrorCodes.PathCanonicalizationFailed, result.ErrorCode);
        Assert.True(File.Exists(outsidePath));
    }

    [Fact]
    public async Task FileOperations_RejectDirectorySymlinkThatStaysInsideWorkspace()
    {
        var (workspace, _) = CreateDirectories();
        var target = Path.Combine(workspace, "target");
        Directory.CreateDirectory(target);
        CreateDirectorySymlinkOrSkip(Path.Combine(workspace, "link"), target);
        var config = CreateConfig(workspace);

        var writeResult = await LocalFileSystemExecutor.WriteFileAsync(
            config,
            new AgentFileWriteRequest(Path.Combine("link", "file.txt"), "inside"),
            allowOutsideConfiguredScope: false,
            CancellationToken.None);
        var readResult = await LocalFileSystemExecutor.ReadFileAsync(
            config,
            new AgentFileReadRequest(Path.Combine("link", "file.txt")),
            allowOutsideConfiguredScope: false,
            CancellationToken.None);
        var deleteResult = await LocalFileSystemExecutor.DeleteFileAsync(
            config,
            new AgentFileDeleteRequest(Path.Combine("link", "file.txt")),
            allowOutsideConfiguredScope: false);

        Assert.Equal(AgentFileReadErrorCodes.PathCanonicalizationFailed, writeResult.ErrorCode);
        Assert.Equal(AgentFileReadErrorCodes.PathCanonicalizationFailed, readResult.ErrorCode);
        Assert.Equal(AgentFileReadErrorCodes.PathCanonicalizationFailed, deleteResult.ErrorCode);
        Assert.False(File.Exists(Path.Combine(target, "file.txt")));
    }

    [Fact]
    public async Task FileOperations_PreserveWorkspaceRelativeNewFileBehavior()
    {
        var (workspace, _) = CreateDirectories();
        var config = CreateConfig(workspace);
        var relativePath = Path.Combine("new", "nested", "file.txt");

        var writeResult = await LocalFileSystemExecutor.WriteFileAsync(
            config,
            new AgentFileWriteRequest(relativePath, "content"),
            allowOutsideConfiguredScope: false,
            CancellationToken.None);
        var readResult = await LocalFileSystemExecutor.ReadFileAsync(
            config,
            new AgentFileReadRequest(relativePath),
            allowOutsideConfiguredScope: false,
            CancellationToken.None);

        Assert.False(writeResult.IsError, writeResult.Summary);
        Assert.False(readResult.IsError, readResult.ErrorMessage);
        Assert.Equal("content", readResult.Content);
        Assert.Equal(Physical(Path.Combine(workspace, relativePath)), readResult.Path);
    }

    [Theory]
    [InlineData(nameof(LocalFileWriteFaultPoint.TemporaryFileFlushed))]
    [InlineData(nameof(LocalFileWriteFaultPoint.BeforeAtomicReplace))]
    public async Task WriteFileAsync_FailureBeforeAtomicReplace_PreservesOriginal(string faultPointName)
    {
        var faultPoint = Enum.Parse<LocalFileWriteFaultPoint>(faultPointName);
        var (workspace, _) = CreateDirectories();
        var target = Path.Combine(workspace, "important.txt");
        await File.WriteAllTextAsync(target, "original");
        var injector = new ThrowingWriteFaultInjector(faultPoint);

        await Assert.ThrowsAsync<IOException>(async () =>
            await LocalFileSystemExecutor.WriteFileAsync(
                CreateConfig(workspace),
                new AgentFileWriteRequest("important.txt", "replacement"),
                allowOutsideConfiguredScope: false,
                CancellationToken.None,
                faultInjector: injector));

        Assert.Equal("original", await File.ReadAllTextAsync(target));
        Assert.Empty(Directory.EnumerateFiles(workspace, ".important.txt.sunder-*.tmp"));
    }

    [Fact]
    public async Task WriteAndDelete_RejectStaleExpectedContentHashAtMutationBoundary()
    {
        var (workspace, _) = CreateDirectories();
        var target = Path.Combine(workspace, "concurrent.txt");
        await File.WriteAllTextAsync(target, "newer");
        var staleHash = ContentHash("older");
        var config = CreateConfig(workspace);

        var write = await LocalFileSystemExecutor.WriteFileAsync(
            config,
            new AgentFileWriteRequest("concurrent.txt", "replacement") { ExpectedContentHash = staleHash },
            allowOutsideConfiguredScope: false,
            CancellationToken.None);
        var delete = await LocalFileSystemExecutor.DeleteFileAsync(
            config,
            new AgentFileDeleteRequest("concurrent.txt") { ExpectedContentHash = staleHash },
            allowOutsideConfiguredScope: false,
            CancellationToken.None);

        Assert.Equal("file-content-changed", write.ErrorCode);
        Assert.Equal("file-content-changed", delete.ErrorCode);
        Assert.Equal("newer", await File.ReadAllTextAsync(target));
    }

    [Fact]
    public async Task OutsideApproval_ReadsRetainedTargetAndCannotBeReplayedForWrite()
    {
        var (workspace, outside) = CreateDirectories();
        var first = Path.Combine(outside, "first.txt");
        var second = Path.Combine(outside, "second.txt");
        await File.WriteAllTextAsync(first, "approved target");
        await File.WriteAllTextAsync(second, "RETARGETED_SECRET");
        var config = CreateConfig(workspace);
        using var approved = LocalResourceAuthorityTestContext.Approve(config, first, "files.read");
        Assert.StartsWith(
            "local-resource-authority-v4:",
            Assert.Single(approved.Context.ApprovedResourceCapabilities),
            StringComparison.Ordinal);
        File.Delete(first);
        File.Move(second, first);

        var read = await LocalFileSystemExecutor.ReadFileAsync(
            config,
            new AgentFileReadRequest(first),
            allowOutsideConfiguredScope: true,
            CancellationToken.None,
            approvedResourceReferences: approved.Context.ApprovedResourceReferences,
            authorizationContext: approved.Context,
            resourceReferences: approved.ResourceReferences);
        var write = await LocalFileSystemExecutor.WriteFileAsync(
            config,
            new AgentFileWriteRequest(first, "replacement"),
            allowOutsideConfiguredScope: true,
            CancellationToken.None,
            approvedResourceReferences: approved.Context.ApprovedResourceReferences,
            authorizationContext: approved.Context,
            resourceReferences: approved.ResourceReferences);

        Assert.Null(read.ErrorCode);
        Assert.Contains("approved target", read.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("RETARGETED_SECRET", read.Content, StringComparison.Ordinal);
        Assert.Equal(LocalResourceReference.ReapprovalRequiredErrorCode, write.ErrorCode);
        Assert.Equal("RETARGETED_SECRET", await File.ReadAllTextAsync(first));
    }

    [Fact]
    public async Task OutsideApproval_RejectsReparentedTarget()
    {
        var (workspace, outside) = CreateDirectories();
        var firstDirectory = Path.Combine(outside, "first");
        var secondDirectory = Path.Combine(outside, "second");
        Directory.CreateDirectory(firstDirectory);
        Directory.CreateDirectory(secondDirectory);
        var first = Path.Combine(firstDirectory, "victim.txt");
        var second = Path.Combine(secondDirectory, "victim.txt");
        await File.WriteAllTextAsync(first, "approved target");
        await File.WriteAllTextAsync(second, "keep");
        var requestedPath = first;
        var config = CreateConfig(workspace);
        using var approved = LocalResourceAuthorityTestContext.Approve(config, requestedPath, "files.mutate");
        var parkedDirectory = Path.Combine(outside, "parked");
        Directory.Move(firstDirectory, parkedDirectory);
        Directory.Move(secondDirectory, firstDirectory);
        var result = await LocalFileSystemExecutor.DeleteFileAsync(
            config,
            new AgentFileDeleteRequest(requestedPath),
            allowOutsideConfiguredScope: true,
            CancellationToken.None,
            approvedResourceReferences: approved.Context.ApprovedResourceReferences,
            authorizationContext: approved.Context,
            resourceReferences: approved.ResourceReferences);

        Assert.Equal(LocalResourceReference.ReapprovalRequiredErrorCode, result.ErrorCode);
        Assert.True(File.Exists(Path.Combine(parkedDirectory, "victim.txt")));
        Assert.True(File.Exists(Path.Combine(firstDirectory, "victim.txt")));
    }

    [Fact]
    public async Task OutsideApproval_RevalidatesImmediatelyBeforeAtomicWrite()
    {
        var (workspace, outside) = CreateDirectories();
        var first = Path.Combine(outside, "first-write.txt");
        var second = Path.Combine(outside, "second-write.txt");
        var parked = Path.Combine(outside, "parked-write.txt");
        await File.WriteAllTextAsync(first, "first");
        await File.WriteAllTextAsync(second, "second");
        var config = CreateConfig(workspace);
        using var approved = LocalResourceAuthorityTestContext.Approve(config, first, "files.mutate");
        var hooks = new RetargetingPublishHook(first, second, parked);

        var result = await LocalFileSystemExecutor.WriteFileAsync(
            config,
            new AgentFileWriteRequest(first, "replacement"),
            allowOutsideConfiguredScope: true,
            CancellationToken.None,
            approvedResourceReferences: approved.Context.ApprovedResourceReferences,
            faultInjector: null,
            hooks: hooks,
            authorizationContext: approved.Context,
            resourceReferences: approved.ResourceReferences);

        Assert.Equal(AgentFileReadErrorCodes.PathCanonicalizationFailed, result.ErrorCode);
        Assert.Equal("first", await File.ReadAllTextAsync(parked));
        Assert.Equal("second", await File.ReadAllTextAsync(first));
        Assert.DoesNotContain(
            Directory.EnumerateFiles(outside)
                .Where(file => HostSecurePathEngine.IsQuarantineName(Path.GetFileName(file)))
                .Select(File.ReadAllText),
            content => content == "second");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private (string Workspace, string Outside) CreateDirectories()
    {
        var workspace = Path.Combine(_root, "workspace");
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(outside);
        return (workspace, outside);
    }

    private static LocalExecutionRuntimeConfig CreateConfig(string workspace)
        => new([workspace], workspace);

    private static DockerExecutionRuntimeConfig CreateDockerConfig(string workspace)
        => new("image", "container", "/bin/sh", [], [new DockerExecutionMount(workspace, "/workspace")], "/workspace");

    private static string ContentHash(string content)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    private static string Physical(string path) => LocalPathResolver.ResolvePhysicalPath(path);

    private static void CreateDirectorySymlinkOrSkip(string linkPath, string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or PlatformNotSupportedException
                                          or NotSupportedException)
        {
            throw SkipException.ForSkip($"Symbolic link creation is unavailable: {exception.Message}");
        }
    }

    private static void CreateFileSymlinkOrSkip(string linkPath, string targetPath)
    {
        try
        {
            File.CreateSymbolicLink(linkPath, targetPath);
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or PlatformNotSupportedException
                                          or NotSupportedException)
        {
            throw SkipException.ForSkip($"Symbolic link creation is unavailable: {exception.Message}");
        }
    }

    private sealed class ThrowingWriteFaultInjector(LocalFileWriteFaultPoint faultPoint) : ILocalFileWriteFaultInjector
    {
        public void OnFaultPoint(LocalFileWriteFaultPoint current)
        {
            if (current == faultPoint)
            {
                throw new IOException($"Injected failure at {current}.");
            }
        }
    }

    private sealed class RetargetingPublishHook(string path, string replacement, string parked)
        : ILocalSecureFileSystemHooks
    {
        private bool _invoked;

        public void OnCheckpoint(LocalSecureFileSystemCheckpoint checkpoint, string parentPath, string? entryName)
        {
            if (_invoked || checkpoint != LocalSecureFileSystemCheckpoint.BeforePublish)
            {
                return;
            }

            _invoked = true;
            File.Move(path, parked);
            File.Move(replacement, path);
        }
    }
}
