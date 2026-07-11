using System.Security.Cryptography;
using System.Text;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Execution.Docker;
using Sunder.Package.Agent.Execution.Local;
using Xunit;
using Xunit.Sdk;

namespace Sunder.Package.Agent.Execution.Local.Tests;

public sealed class LocalFileSystemSymlinkContainmentTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sunder-local-path-tests", Guid.NewGuid().ToString("N"));

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
        Assert.Equal(AgentFileReadErrorCodes.OutsideConfiguredScope, result.ErrorCode);
        Assert.Empty(result.Content);
    }

    [Fact]
    public void ResolveFileResource_ClassifiesDirectorySymlinkEscapeAsOutsideConfiguredScope()
    {
        var (workspace, outside) = CreateDirectories();
        CreateDirectorySymlinkOrSkip(Path.Combine(workspace, "escape"), outside);
        var config = CreateConfig(workspace);

        var resource = LocalResourceResolver.ResolveFileResource(
            config,
            Path.Combine("escape", "file.txt"),
            allowOutsideConfiguredScope: true);

        Assert.Equal(AgentPermissionBoundaryIds.OutsideConfiguredScope, resource.PermissionBoundaryId);
        Assert.Equal(Physical(Path.Combine(outside, "file.txt")), resource.CanonicalReference);
    }

    [Fact]
    public void MapToHostPath_RejectsDirectorySymlinkEscape()
    {
        var (workspace, outside) = CreateDirectories();
        CreateDirectorySymlinkOrSkip(Path.Combine(workspace, "escape"), outside);

        Assert.Throws<InvalidOperationException>(() => LocalResourceResolver.MapToHostPath(
            CreateConfig(workspace),
            Path.Combine("escape", "project")));
    }

    [Fact]
    public void MapToHostPath_ReturnsCanonicalPathForContainedDirectorySymlink()
    {
        var (workspace, _) = CreateDirectories();
        var target = Path.Combine(workspace, "target");
        Directory.CreateDirectory(target);
        CreateDirectorySymlinkOrSkip(Path.Combine(workspace, "link"), target);

        var mapping = LocalResourceResolver.MapToHostPath(CreateConfig(workspace), Path.Combine("link", "project"));

        Assert.True(mapping.IsInsideAllowedRoot);
        Assert.Equal(Physical(Path.Combine(target, "project")), mapping.HostPath);
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

        Assert.Throws<InvalidOperationException>(() => DockerPathResolver.MapToHostPath(config, "/workspace/escape/project"));
    }

    [Fact]
    public void DockerMapToHostPath_ReturnsCanonicalPathForContainedDirectorySymlink()
    {
        var (workspace, _) = CreateDirectories();
        var target = Path.Combine(workspace, "target");
        Directory.CreateDirectory(target);
        CreateDirectorySymlinkOrSkip(Path.Combine(workspace, "link"), target);

        var mapping = DockerPathResolver.MapToHostPath(CreateDockerConfig(workspace), "/workspace/link/project");

        Assert.True(mapping.IsInsideAllowedRoot);
        Assert.Equal(Physical(Path.Combine(target, "project")), mapping.HostPath);
    }

    [Fact]
    public async Task WriteFileAsync_RejectsNewFileBelowDirectorySymlinkEscape()
    {
        var (workspace, outside) = CreateDirectories();
        CreateDirectorySymlinkOrSkip(Path.Combine(workspace, "escape"), outside);
        var config = CreateConfig(workspace);
        var outsidePath = Path.Combine(outside, "new", "file.txt");

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await LocalFileSystemExecutor.WriteFileAsync(
                config,
                new AgentFileWriteRequest(Path.Combine("escape", "new", "file.txt"), "outside"),
                allowOutsideConfiguredScope: false,
                CancellationToken.None));

        Assert.False(File.Exists(outsidePath));
    }

    [Fact]
    public async Task WriteFileAsync_RejectsDanglingFileSymlinkEscape()
    {
        var (workspace, outside) = CreateDirectories();
        var outsidePath = Path.Combine(outside, "new.txt");
        CreateFileSymlinkOrSkip(Path.Combine(workspace, "escape.txt"), outsidePath);
        var config = CreateConfig(workspace);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await LocalFileSystemExecutor.WriteFileAsync(
                config,
                new AgentFileWriteRequest("escape.txt", "outside"),
                allowOutsideConfiguredScope: false,
                CancellationToken.None));

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

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await LocalFileSystemExecutor.DeleteFileAsync(
                config,
                new AgentFileDeleteRequest(Path.Combine("escape", "keep.txt")),
                allowOutsideConfiguredScope: false));

        Assert.True(File.Exists(outsidePath));
    }

    [Fact]
    public async Task FileOperations_AllowDirectorySymlinkThatStaysInsideWorkspace()
    {
        var (workspace, _) = CreateDirectories();
        var target = Path.Combine(workspace, "target");
        Directory.CreateDirectory(target);
        CreateDirectorySymlinkOrSkip(Path.Combine(workspace, "link"), target);
        var config = CreateConfig(workspace);

        await LocalFileSystemExecutor.WriteFileAsync(
            config,
            new AgentFileWriteRequest(Path.Combine("link", "file.txt"), "inside"),
            allowOutsideConfiguredScope: false,
            CancellationToken.None);
        var readResult = await LocalFileSystemExecutor.ReadFileAsync(
            config,
            new AgentFileReadRequest(Path.Combine("link", "file.txt")),
            allowOutsideConfiguredScope: false,
            CancellationToken.None);
        await LocalFileSystemExecutor.DeleteFileAsync(
            config,
            new AgentFileDeleteRequest(Path.Combine("link", "file.txt")),
            allowOutsideConfiguredScope: false);

        Assert.Equal("inside", readResult.Content);
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

        Assert.False(writeResult.IsError);
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
                injector));

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
}
