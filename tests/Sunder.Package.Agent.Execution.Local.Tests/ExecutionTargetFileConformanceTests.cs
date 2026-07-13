using System.Reflection;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Execution.Docker;
using Sunder.Package.Agent.Execution.Local;
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

    private readonly string _root = Path.Combine(Path.GetTempPath(), "sunder-execution-conformance", Guid.NewGuid().ToString("N"));

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

        AssertReadError(result, AgentFileReadErrorCodes.OutsideConfiguredScope);
    }

    [Theory]
    [MemberData(nameof(RangedTargets))]
    public async Task MutationTargets_ReturnStructuredScopeFailures(string targetKind)
    {
        var fixture = CreateFixture(targetKind);

        var write = await fixture.WriteAsync(new AgentFileWriteRequest("../outside.txt", "unsafe"));
        var delete = await fixture.DeleteAsync(new AgentFileDeleteRequest("../outside.txt"));

        AssertMutationError(write, AgentFileReadErrorCodes.OutsideConfiguredScope);
        AssertMutationError(delete, AgentFileReadErrorCodes.OutsideConfiguredScope);
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
    public async Task DockerHelper_RejectsSymlinkEscapeForReadWriteDeleteAndRangedRead()
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

        AssertReadError(read, AgentFileReadErrorCodes.OutsideConfiguredScope);
        AssertReadError(ranged, AgentFileReadErrorCodes.OutsideConfiguredScope);
        Assert.True(write.IsError);
        Assert.Equal(AgentFileReadErrorCodes.OutsideConfiguredScope, write.ErrorCode);
        Assert.True(delete.IsError);
        Assert.Equal(AgentFileReadErrorCodes.OutsideConfiguredScope, delete.ErrorCode);
        Assert.False(File.Exists(Path.Combine(outside, "new.txt")));
        Assert.True(File.Exists(Path.Combine(outside, "secret.txt")));
    }

    [Fact]
    public async Task DockerHelper_AllowsInternalSymlinkAndDeletesLinkInsteadOfTarget()
    {
        var fixture = CreateDockerFixture();
        var target = Path.Combine(fixture.Root, "target");
        Directory.CreateDirectory(target);
        CreateDirectorySymlinkOrSkip(Path.Combine(fixture.Root, "link"), target);

        var write = await fixture.WriteAsync(new AgentFileWriteRequest("link/file.txt", "inside"));
        var read = await fixture.ReadAsync(new AgentFileReadRequest("link/file.txt", 1, 1));
        var delete = await fixture.DeleteAsync(new AgentFileDeleteRequest("link/file.txt"));

        Assert.False(write.IsError, write.Summary);
        Assert.Equal("inside", read.Content);
        Assert.False(delete.IsError, delete.Summary);
        Assert.False(File.Exists(Path.Combine(target, "file.txt")));
    }

    [Fact]
    public async Task DockerHelper_ValidatesThenDeletesSafeFileSymlinkWithoutDeletingTarget()
    {
        var fixture = CreateDockerFixture();
        var target = Path.Combine(fixture.Root, "target.txt");
        var link = Path.Combine(fixture.Root, "link.txt");
        await File.WriteAllTextAsync(target, "keep");
        CreateFileSymlinkOrSkip(link, target);

        var delete = await fixture.DeleteAsync(new AgentFileDeleteRequest("link.txt"));

        Assert.False(delete.IsError, delete.Summary);
        Assert.False(File.Exists(link));
        Assert.True(File.Exists(target));
        Assert.Equal("keep", await File.ReadAllTextAsync(target));
    }

    [Fact]
    public async Task DockerHelper_RejectsDanglingSymlinkAsAmbiguous()
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
    public async Task DockerHelper_ReturnsStructuredErrorForNonFileNode()
    {
        if (OperatingSystem.IsWindows())
        {
            throw SkipException.ForSkip("A POSIX container filesystem is required.");
        }

        var root = Path.Combine(Path.GetTempPath(), "snr-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        try
        {
            var socketPath = Path.Combine(root, "service.sock");
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.Bind(new UnixDomainSocketEndPoint(socketPath));
            var config = CreateDockerConfig(root);

            var result = await new DockerFileSystemExecutor(new ShellDockerCommandExecutor()).ReadFileAsync(
                config,
                "container",
                new AgentFileReadRequest("service.sock"),
                allowOutsideConfiguredScope: false,
                CancellationToken.None);

            AssertReadError(result, AgentFileReadErrorCodes.NotAFile);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DockerHelper_PassesHostilePathsOnlyAsShellArguments()
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
        Assert.DoesNotContain(hostileName, DockerFileOperationScript.Content, StringComparison.Ordinal);
        Assert.Contains(Path.Combine(fixture.Root, hostileName), fixture.Executor.LastArguments!);
    }

    [Fact]
    public async Task DockerHelper_FailsClosedWhenCanonicalizationIsUnavailable()
    {
        var fixture = CreateDockerFixture(new ShellDockerCommandExecutor { PathEnvironment = "/sunder-no-tools" });
        await fixture.WriteTextAsync("file.txt", "content");

        var result = await fixture.ReadAsync(new AgentFileReadRequest("file.txt"));

        AssertReadError(result, AgentFileReadErrorCodes.PathCanonicalizationFailed);
    }

    [Fact]
    public async Task DockerLexicalScopeComparison_IsCaseSensitive()
    {
        var root = Path.Combine(_root, "CaseSensitiveRoot");
        Directory.CreateDirectory(root);
        var config = CreateDockerConfig(root);
        var executor = new StubDockerCommandExecutor(_ => throw new XunitException("Out-of-scope paths must be rejected before execution."));

        var result = await new DockerFileSystemExecutor(executor).ReadFileAsync(
            config,
            "container",
            new AgentFileReadRequest(root.ToLowerInvariant() + "/file.txt"),
            allowOutsideConfiguredScope: false,
            CancellationToken.None);

        AssertReadError(result, AgentFileReadErrorCodes.OutsideConfiguredScope);
    }

    [Fact]
    public async Task DockerPhysicalPolicy_StillRejectsOutsidePathAfterPermissionOverride()
    {
        var workspace = Path.Combine(_root, "workspace");
        var outside = Path.Combine(_root, "outside-override.txt");
        Directory.CreateDirectory(workspace);
        await File.WriteAllTextAsync(outside, "outside");
        var config = CreateDockerConfig(workspace);

        var result = await new DockerFileSystemExecutor(new ShellDockerCommandExecutor()).ReadFileAsync(
            config,
            "container",
            new AgentFileReadRequest(outside),
            allowOutsideConfiguredScope: true,
            CancellationToken.None);

        AssertReadError(result, AgentFileReadErrorCodes.OutsideConfiguredScope);
    }

    [Fact]
    public async Task DockerRead_ReportsTimeoutCancellationAndOutputTruncation()
    {
        var timeoutConfig = CreateDockerConfig(Path.Combine(_root, "timeout"));
        Directory.CreateDirectory(timeoutConfig.Mounts[0].ContainerPath);
        var timedOutExecutor = new StubDockerCommandExecutor(_ => new DockerCliRunResult(124, "timed out", TimedOut: true, WasTruncated: false));
        var timedOut = await new DockerFileSystemExecutor(timedOutExecutor).ReadFileAsync(
            timeoutConfig,
            "container",
            new AgentFileReadRequest("file.txt", 1, 1),
            allowOutsideConfiguredScope: false,
            CancellationToken.None);
        AssertReadError(timedOut, AgentFileReadErrorCodes.TimedOut);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await new DockerFileSystemExecutor(timedOutExecutor).ReadFileAsync(
                timeoutConfig,
                "container",
                new AgentFileReadRequest("file.txt", 1, 1),
                allowOutsideConfiguredScope: false,
                cancellation.Token));

        var truncatedFixture = CreateDockerFixture(new ShellDockerCommandExecutor { MaxOutputLength = 100 });
        await truncatedFixture.WriteTextAsync("large.txt", new string('x', 1000));
        var truncated = await truncatedFixture.ReadAsync(new AgentFileReadRequest("large.txt"));
        Assert.False(truncated.IsError, truncated.ErrorMessage);
        Assert.True(truncated.WasTruncated);
        Assert.True(truncated.Content.Length < 1000);
        Assert.Equal(1, truncated.TotalLines);
    }

    [Fact]
    public async Task DockerHelper_FailsAtPermissionBoundaryWithoutCreatingTarget()
    {
        if (OperatingSystem.IsWindows())
        {
            throw SkipException.ForSkip("POSIX permission modes are required.");
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
    public async Task DockerExistenceQuery_UsesContainerFilesystemAndAllowsPatchAdd()
    {
        var fixture = CreateDockerFixture();
        var config = CreateDockerConfig(fixture.Root);
        var fileSystem = new DockerFileSystemExecutor(fixture.Executor);

        var before = await fileSystem.ResolveFileResourceAsync(
            config,
            "container",
            "added.txt",
            allowOutsideConfiguredScope: false,
            CancellationToken.None);
        var write = await fixture.WriteAsync(new AgentFileWriteRequest("added.txt", "added", Overwrite: false));
        var after = await fileSystem.ResolveFileResourceAsync(
            config,
            "container",
            "added.txt",
            allowOutsideConfiguredScope: false,
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

    private DockerFileFixture CreateDockerFixture(ShellDockerCommandExecutor? executor = null)
    {
        var root = Path.Combine(_root, "docker-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var config = CreateDockerConfig(root);
        executor ??= new ShellDockerCommandExecutor();
        var fileSystem = new DockerFileSystemExecutor(executor);
        return new DockerFileFixture(
            root,
            request => fileSystem.ReadFileAsync(config, "container", request, allowOutsideConfiguredScope: false, CancellationToken.None),
            request => fileSystem.WriteFileAsync(config, "container", request, allowOutsideConfiguredScope: false, CancellationToken.None),
            request => fileSystem.DeleteFileAsync(config, "container", request, allowOutsideConfiguredScope: false, CancellationToken.None),
            executor);
    }

    private static DockerExecutionRuntimeConfig CreateDockerConfig(string root)
        => new("image", "container", "/bin/sh", [], [new DockerExecutionMount(root, root)], root);

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

    private sealed class DockerFileFixture(
        string root,
        Func<AgentFileReadRequest, ValueTask<AgentFileReadResult>> read,
        Func<AgentFileWriteRequest, ValueTask<AgentFileMutationResult>> write,
        Func<AgentFileDeleteRequest, ValueTask<AgentFileMutationResult>> delete,
        ShellDockerCommandExecutor executor)
        : FileFixture(root, read, write, delete)
    {
        public ShellDockerCommandExecutor Executor { get; } = executor;
    }
}
