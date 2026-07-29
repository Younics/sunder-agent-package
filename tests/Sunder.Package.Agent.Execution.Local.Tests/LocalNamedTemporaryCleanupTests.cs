using System.Runtime.Versioning;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Execution.Local;
using Xunit;

namespace Sunder.Package.Agent.Execution.Local.Tests;

public sealed class LocalNamedTemporaryCleanupTests : IDisposable
{
    private readonly string _root = Path.Combine(
        OperatingSystem.IsMacOS() ? "/private" + Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar) : Path.GetTempPath(),
        "sunder-named-temp-cleanup",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task DirectNamedTemporaryCancellation_SanitizesQuarantinedIdentity()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        var workspace = CreateWorkspace();
        const string canary = "DIRECT-NAMED-CANCELLATION-CANARY";
        using var cancellation = new CancellationTokenSource();
        var hook = new CheckpointHook(
            LocalSecureFileSystemCheckpoint.BeforePublish,
            cancellation.Cancel);
        using var forceNamedCreation = OperatingSystem.IsLinux()
            ? LocalUnixNative.OverrideAnonymousTemporaryFileOpenError(95)
            : null;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await WriteAsync(workspace, "direct-cancel.txt", CreatePayload(canary), cancellation.Token, hook));

        Assert.True(hook.Invoked);
        Assert.False(File.Exists(Path.Combine(workspace, "direct-cancel.txt")));
        await AssertSanitizedReservedEntriesAsync(workspace, canary);
    }

    [Fact]
    public async Task LinuxAnonymousToNamedFallbackCancellation_SanitizesQuarantinedIdentity()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var workspace = CreateWorkspace();
        const string canary = "ANONYMOUS-NAMED-CANCELLATION-CANARY";
        using var cancellation = new CancellationTokenSource();
        var hook = new CheckpointHook(
            LocalSecureFileSystemCheckpoint.BeforeNamedTemporaryPublish,
            () =>
            {
                cancellation.Cancel();
                throw new OperationCanceledException(cancellation.Token);
            });
        using var forceNamedFallback = ForceAnonymousToNamedFallback();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await WriteAsync(workspace, "fallback-cancel.txt", CreatePayload(canary), cancellation.Token, hook));

        Assert.True(hook.Invoked);
        Assert.False(File.Exists(Path.Combine(workspace, "fallback-cancel.txt")));
        await AssertSanitizedReservedEntriesAsync(workspace, canary);
    }

    [Fact]
    public async Task LinuxAnonymousToNamedFallbackCopyFailure_SanitizesPartialCopy()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var workspace = CreateWorkspace();
        const string canary = "ANONYMOUS-NAMED-COPY-FAILURE-CANARY";
        var hook = new CheckpointHook(
            LocalSecureFileSystemCheckpoint.AfterNamedTemporaryCopyChunk,
            () => throw new LocalSecurePathException("Injected named temporary copy failure."));
        using var forceNamedFallback = ForceAnonymousToNamedFallback();

        var result = await WriteAsync(
            workspace,
            "fallback-copy-failure.txt",
            CreatePayload(canary),
            CancellationToken.None,
            hook);

        Assert.True(hook.Invoked);
        Assert.True(result.IsError);
        Assert.False(File.Exists(Path.Combine(workspace, "fallback-copy-failure.txt")));
        await AssertSanitizedReservedEntriesAsync(workspace, canary);
    }

    [Fact]
    public async Task LinuxAnonymousToNamedFallbackPublicationFailure_SanitizesFullCopy()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var workspace = CreateWorkspace();
        var target = Path.Combine(workspace, "fallback-publication-failure.txt");
        const string canary = "ANONYMOUS-NAMED-PUBLICATION-FAILURE-CANARY";
        var hook = new CheckpointHook(
            LocalSecureFileSystemCheckpoint.BeforeNamedTemporaryPublish,
            () => File.WriteAllText(target, "ATTACKER"));
        using var forceNamedFallback = ForceAnonymousToNamedFallback();

        var result = await WriteAsync(
            workspace,
            Path.GetFileName(target),
            CreatePayload(canary),
            CancellationToken.None,
            hook);

        Assert.True(hook.Invoked);
        Assert.Equal(FileOperation.FileExistsErrorCode, result.ErrorCode);
        Assert.Equal("ATTACKER", await File.ReadAllTextAsync(target));
        await AssertSanitizedReservedEntriesAsync(workspace, canary);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DirectNamedTemporaryRace_SanitizesExactRenamedOrLinkedInode(bool hardLink)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        var workspace = CreateWorkspace();
        const string canary = "DIRECT-NAMED-RACE-CANARY";
        var racedPath = Path.Combine(workspace, hardLink ? "linked-copy.txt" : "renamed-copy.txt");
        var faultInjector = new NamedTemporaryRaceFaultInjector(workspace, racedPath, hardLink);
        using var forceNamedCreation = OperatingSystem.IsLinux()
            ? LocalUnixNative.OverrideAnonymousTemporaryFileOpenError(95)
            : null;

        var result = await WriteAsync(
            workspace,
            "raced-cleanup.txt",
            CreatePayload(canary),
            CancellationToken.None,
            hooks: null,
            faultInjector);

        Assert.True(faultInjector.Invoked);
        Assert.True(result.IsError);
        Assert.Equal(
            hardLink
                ? FileOperation.StrictMutationRecoveryRequiredErrorCode
                : AgentFileReadErrorCodes.PathCanonicalizationFailed,
            result.ErrorCode);
        Assert.False(File.Exists(Path.Combine(workspace, "raced-cleanup.txt")));
        Assert.True(File.Exists(racedPath));
        foreach (var file in Directory.EnumerateFiles(workspace))
        {
            Assert.DoesNotContain(canary, await File.ReadAllTextAsync(file), StringComparison.Ordinal);
        }
        AssertReservationReleased(workspace);
    }

    [Fact]
    public async Task MacOsSuccessfulClone_SanitizesNamedSourceQuarantine()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var workspace = CreateWorkspace();
        const string canary = "MACOS-NAMED-SOURCE-CANARY";
        var payload = CreatePayload(canary);

        var result = await WriteAsync(
            workspace,
            "macos-clone.txt",
            payload,
            CancellationToken.None,
            hooks: null);

        Assert.False(result.IsError, result.Summary);
        Assert.Equal(payload, await File.ReadAllTextAsync(Path.Combine(workspace, "macos-clone.txt")));
        await AssertSanitizedReservedEntriesAsync(workspace, canary);
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    public async Task DirectNamedTemporaryQuarantineFailure_SanitizesExactInodeAndReportsRecovery()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        var workspace = CreateWorkspace();
        const string canary = "DIRECT-NAMED-SANITIZATION-FAILURE-CANARY";
        var hook = new CheckpointHook(
            LocalSecureFileSystemCheckpoint.BeforePublish,
            () =>
            {
                var temporary = Assert.Single(
                    Directory.EnumerateFiles(workspace),
                    path => HostSecurePathEngine.IsTemporaryName(Path.GetFileName(path)));
                File.SetUnixFileMode(temporary, UnixFileMode.UserRead);
                throw new LocalSecurePathException("Injected pre-publication failure.");
            });
        using var forceNamedCreation = OperatingSystem.IsLinux()
            ? LocalUnixNative.OverrideAnonymousTemporaryFileOpenError(95)
            : null;

        var result = await WriteAsync(
            workspace,
            "sanitize-failure.txt",
            CreatePayload(canary),
            CancellationToken.None,
            hook);

        Assert.True(hook.Invoked);
        Assert.Equal(FileOperation.StrictMutationRecoveryRequiredErrorCode, result.ErrorCode);
        var reserved = Assert.Single(
            Directory.EnumerateFiles(workspace),
            path => HostSecurePathEngine.IsReservedName(Path.GetFileName(path)));
        Assert.Equal(0, new FileInfo(reserved).Length);
        Assert.DoesNotContain(canary, await File.ReadAllTextAsync(reserved), StringComparison.Ordinal);
        AssertReservationReleased(workspace);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string CreateWorkspace()
    {
        var workspace = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        return workspace;
    }

    private static ValueTask<AgentFileMutationResult> WriteAsync(
        string workspace,
        string path,
        string content,
        CancellationToken cancellationToken,
        ILocalSecureFileSystemHooks? hooks,
        ILocalFileWriteFaultInjector? faultInjector = null)
        => LocalFileSystemExecutor.WriteFileAsync(
            new LocalExecutionRuntimeConfig([workspace], workspace),
            new AgentFileWriteRequest(path, content, Overwrite: false),
            allowOutsideConfiguredScope: false,
            cancellationToken,
            faultInjector: faultInjector,
            hooks: hooks);

    private static IDisposable ForceAnonymousToNamedFallback()
        => LocalUnixNative.OverrideAnonymousPublishErrors(method =>
            method == LocalUnixAnonymousPublishMethod.EmptyPath ? 1 : 2);

    private static string CreatePayload(string canary)
        => string.Concat(Enumerable.Repeat(canary + '-', 8192));

    private static async Task AssertSanitizedReservedEntriesAsync(string workspace, string canary)
    {
        var reservedEntries = Directory.EnumerateFileSystemEntries(workspace)
            .Where(path => HostSecurePathEngine.IsReservedName(Path.GetFileName(path)))
            .ToArray();
        Assert.NotEmpty(reservedEntries);
        foreach (var entry in reservedEntries)
        {
            Assert.True(HostSecurePathEngine.IsQuarantineName(Path.GetFileName(entry)));
            Assert.True(File.Exists(entry));
            Assert.Equal(0, new FileInfo(entry).Length);
            Assert.DoesNotContain(canary, await File.ReadAllTextAsync(entry), StringComparison.Ordinal);
        }
        AssertReservationReleased(workspace);
    }

    private static void AssertReservationReleased(string workspace)
    {
        using var root = HostSecurePathEngine.OpenRoot(workspace);
        Assert.False(HostMutationCoordinator.IsEntered(root.Handle.Identity));
    }

    private sealed class CheckpointHook(
        LocalSecureFileSystemCheckpoint checkpoint,
        Action callback) : ILocalSecureFileSystemHooks
    {
        public bool Invoked { get; private set; }

        public void OnCheckpoint(
            LocalSecureFileSystemCheckpoint current,
            string parentPath,
            string? entryName)
        {
            if (Invoked || current != checkpoint)
            {
                return;
            }

            Invoked = true;
            callback();
        }
    }

    private sealed class NamedTemporaryRaceFaultInjector(
        string workspace,
        string racedPath,
        bool hardLink) : ILocalFileWriteFaultInjector
    {
        public bool Invoked { get; private set; }

        public void OnFaultPoint(LocalFileWriteFaultPoint faultPoint)
        {
            if (Invoked || faultPoint != LocalFileWriteFaultPoint.BeforeAtomicReplace)
            {
                return;
            }

            Invoked = true;
            var temporary = Assert.Single(
                Directory.EnumerateFiles(workspace),
                path => HostSecurePathEngine.IsTemporaryName(Path.GetFileName(path)));
            if (hardLink)
            {
                using var root = HostSecurePathEngine.OpenRoot(workspace);
                LocalUnixNative.Link(
                    root.Handle.Handle.DangerousGetHandle().ToInt32(),
                    Path.GetFileName(temporary),
                    Path.GetFileName(racedPath));
            }
            else
            {
                File.Move(temporary, racedPath);
            }

            throw new LocalSecurePathException("Injected failure after the named temporary entry race.");
        }
    }
}
