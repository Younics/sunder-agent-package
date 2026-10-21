extern alias BuilderWorker;

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using BuilderWorker::Sunder.Package.Agent.Builder;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class BuilderTemplateCacheTests
{
    [Fact]
    public async Task ProductionShapedUnicodeStorageUsesOneShortHomeForColdInstallListScaffoldAndReuse()
    {
        using var fixture = new Fixture();
        var options = fixture.Options with { CacheRootPath = fixture.PathOf("Library/Application Support/Sunder/runtime/v1/package-data/" +
            "younics.sunder.package.maker/workspace/development-toolchains/project-tools/项目工具") };
        var oldIdentity = "1.1.0-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("1.1.0\n")))[..16];
        var legacy = Path.Combine(options.CacheRootPath, "dotnet-templates", oldIdentity, "10.0.402");
        Directory.CreateDirectory(legacy);
        var oldReady = Path.Combine(legacy, BuilderTemplateCache.ReadyName);
        await File.WriteAllTextAsync(oldReady, "{\"version\":\"1.1.0\"}");
        Assert.True(Encoding.UTF8.GetByteCount(legacy) > 189);

        await fixture.ScaffoldAsync(options);
        var requests = fixture.Process.Requests.ToArray();
        Assert.Equal(new[] { "install", "list", "sunder-package" }, requests.Select(item => item.Arguments[1]));
        var home = Assert.Single(requests.Select(Home).Distinct());
        Assert.StartsWith(BuilderTemplateCache.ValidateBasePath(options) + Path.DirectorySeparatorChar, home);
        Assert.Equal(32, Path.GetFileName(home).Length);
        Assert.True(Encoding.UTF8.GetByteCount(home) <= BuilderTemplateCache.MaximumHomeUtf8Bytes);
        Assert.DoesNotContain(options.CacheRootPath, home);
        Assert.All(requests, item =>
        {
            Assert.Equal(Path.Combine(options.CacheRootPath, "nuget-packages"), item.Environment["NUGET_PACKAGES"]);
            Assert.DoesNotContain(item.Arguments, argument => argument.Contains("custom-hive", StringComparison.Ordinal));
        });
        Assert.True(File.Exists(Path.Combine(home, BuilderTemplateCache.ReadyName)));
        Assert.True(File.Exists(Path.Combine(home, BuilderTemplateCache.LockName)));
        Assert.Equal("{\"version\":\"1.1.0\"}", await File.ReadAllTextAsync(oldReady));
        await fixture.ScaffoldAsync(options);
        Assert.Equal(new[] { "install", "list", "sunder-package", "sunder-package" }, fixture.Process.Requests.Select(item => item.Arguments[1]));
        Assert.All(fixture.Process.Requests, item => Assert.Equal(home, Home(item)));
    }

    [Fact]
    public async Task FullOwnerSdkCoordinateAndLocalArchiveBytesAreIsolated()
    {
        using var fixture = new Fixture();
        var original = await HomeAsync(fixture.Options);
        Assert.Equal(original, await HomeAsync(fixture.Options with { CacheRootPath = Path.Combine(fixture.Options.CacheRootPath, "..", "tools") }));
        Assert.NotEqual(original, await HomeAsync(fixture.Options with { CacheRootPath = fixture.PathOf("another-owner") }));
        Assert.NotEqual(original, await HomeAsync(fixture.Options, "10.0.403"));
        Assert.NotEqual(original, await HomeAsync(fixture.Options with { CoordinatedPackageVersion = "1.1.1" }));
        var archive = fixture.PathOf("template.nupkg");
        await File.WriteAllTextAsync(archive, "first archive bytes");
        var local = fixture.Options with { DotnetTemplatePackagePath = archive };
        var first = await HomeAsync(local);
        Assert.NotEqual(original, first);
        await File.WriteAllTextAsync(archive, "different archive bytes");
        Assert.NotEqual(first, await HomeAsync(local));
        var copy = fixture.PathOf("copy.nupkg");
        File.Copy(archive, copy);
        Assert.Equal(await HomeAsync(local), await HomeAsync(local with { DotnetTemplatePackagePath = copy }));
        Assert.Contains(fixture.Options.CacheRootPath.Replace('\\', '/'),
            (await File.ReadAllTextAsync(Path.Combine(original, BuilderTemplateCache.OwnerName))).Replace("\\\\", "/"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Utf8BudgetRejectsLongBasesBeforeSdkSelectionOrProcessLaunch(bool unicode)
    {
        using var fixture = new Fixture();
        var prefix = BuilderTemplateCache.ValidateBasePath(fixture.Options);
        var paddingBytes = BuilderTemplateCache.MaximumHomeUtf8Bytes - Encoding.UTF8.GetByteCount(prefix) - 34;
        Assert.True(paddingBytes > 0);
        var boundary = prefix + "/" + new string('a', paddingBytes);
        Assert.Equal(BuilderTemplateCache.MaximumHomeUtf8Bytes, Encoding.UTF8.GetByteCount(boundary + "/" + new string('0', 32)));
        var exact = fixture.Options with { DotnetTemplateCacheRootPath = boundary };
        Assert.Equal(boundary, BuilderTemplateCache.ValidateBasePath(exact));
        var tooLong = unicode ? prefix + "/" + new string('界', paddingBytes / 3 + 1) : boundary + "a";
        var options = fixture.Options with { DotnetTemplateCacheRootPath = tooLong };
        if (unicode) Assert.True(tooLong.Length + 33 <= BuilderTemplateCache.MaximumHomeUtf8Bytes);
        var error = await Assert.ThrowsAsync<IOException>(() => fixture.ExecuteAsync(options));
        Assert.Contains("UTF-8 bytes", error.Message);
        Assert.Contains("DotnetTemplateCacheRootPath", error.Message);
        Assert.Empty(fixture.Process.Requests);
        Assert.Equal(0, fixture.Tools.Selections);
        Assert.False(Directory.Exists(tooLong));
        Assert.False(Directory.Exists(prefix));
    }

    [Fact]
    public async Task ConcurrentCreatorsHoldActualHomeLockThroughScaffoldAndCancelledWaiterCanRetry()
    {
        using var fixture = new Fixture();
        var entered = Signal(); var release = Signal();
        fixture.Process.Before = async (request, token) =>
        {
            if (request.Arguments[1] == "sunder-package") { entered.TrySetResult(); await release.Task.WaitAsync(token); }
        };
        var first = fixture.ScaffoldAsync(fixture.Options);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var cancelled = new CancellationTokenSource();
        var waiting = fixture.ExecuteAsync(fixture.Options, cancelled.Token);
        await Task.Delay(100);
        Assert.Equal(3, fixture.Process.Requests.Count);
        Assert.False(waiting.IsCompleted);
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        var second = fixture.ScaffoldAsync(fixture.Options);
        await Task.Delay(100);
        Assert.False(second.IsCompleted);
        release.SetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(fixture.Process.Requests, item => item.Arguments[1] == "install");
        Assert.Equal(2, fixture.Process.Requests.Count(item => item.Arguments[1] == "sunder-package"));
        Assert.Single(fixture.Process.Requests.Select(Home).Distinct());
    }

    [Fact]
    public async Task CancelledInstallLeavesNoReadyMarkerAndColdRetryReinstalls()
    {
        using var fixture = new Fixture();
        var entered = Signal();
        fixture.Process.Before = async (_, token) => { entered.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); };
        using var cancelled = new CancellationTokenSource();
        var creating = fixture.ExecuteAsync(fixture.Options, cancelled.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var home = Home(Assert.Single(fixture.Process.Requests));
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => creating);
        Assert.False(File.Exists(Path.Combine(home, BuilderTemplateCache.ReadyName)));
        fixture.Process.Before = null;
        await fixture.ScaffoldAsync(fixture.Options);
        Assert.Equal(2, fixture.Process.Requests.Count(item => item.Arguments[1] == "install"));
        Assert.True(File.Exists(Path.Combine(home, BuilderTemplateCache.ReadyName)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FailedInstallOrMissingTemplateListingNeverMarksReady(bool failInstall)
    {
        using var fixture = new Fixture();
        fixture.Process.FailInstall = failInstall;
        fixture.Process.EmptyList = !failInstall;
        if (failInstall) await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.ExecuteAsync(fixture.Options));
        else await Assert.ThrowsAsync<InvalidDataException>(() => fixture.ExecuteAsync(fixture.Options));
        var home = Home(fixture.Process.Requests.First());
        Assert.False(File.Exists(Path.Combine(home, BuilderTemplateCache.ReadyName)));
        fixture.Process.FailInstall = fixture.Process.EmptyList = false;
        await fixture.ScaffoldAsync(fixture.Options);
        Assert.Equal(2, fixture.Process.Requests.Count(item => item.Arguments[1] == "install"));
    }

    [Fact]
    public async Task FileLockContentionIsCancellableAndDoesNotRewriteTheLock()
    {
        using var fixture = new Fixture();
        var home = await HomeAsync(fixture.Options);
        var path = Path.Combine(home, BuilderTemplateCache.LockName);
        using (var otherActivation = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            using var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BuilderTemplateCache.AcquireAsync(fixture.Options, "10.0.402", cancelled.Token));
        }
        Assert.Equal(0, new FileInfo(path).Length);
        Assert.Equal(home, await HomeAsync(fixture.Options));
    }

    [Fact]
    public async Task ConcurrentColdInitializationPublishesOneOwnedReadyHome()
    {
        using var fixture = new Fixture();
        var initialized = 0;
        var homes = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            using var cache = await BuilderTemplateCache.AcquireAsync(fixture.Options, "10.0.402", default);
            if (!cache.IsReady())
            {
                Interlocked.Increment(ref initialized);
                await cache.MarkReadyAsync(default);
            }
            return cache.HomePath;
        })));
        Assert.Single(homes.Distinct());
        Assert.Equal(1, initialized);
        Assert.Empty(Directory.EnumerateFileSystemEntries(BuilderTemplateCache.ValidateBasePath(fixture.Options), ".bt-*"));
    }

    [Fact]
    public async Task FullOwnershipMarkersRejectCopiedOwnersAndCopiedBases()
    {
        using var fixture = new Fixture();
        var original = await HomeAsync(fixture.Options);
        var otherOptions = fixture.Options with { CacheRootPath = fixture.PathOf("other-tools") };
        var other = await HomeAsync(otherOptions);
        var originalOwner = await File.ReadAllBytesAsync(Path.Combine(original, BuilderTemplateCache.OwnerName));
        await File.WriteAllBytesAsync(Path.Combine(other, BuilderTemplateCache.OwnerName), originalOwner);
        var error = await Assert.ThrowsAsync<IOException>(() => BuilderTemplateCache.AcquireAsync(otherOptions, "10.0.402", default));
        Assert.Contains("full identity", error.Message);
        Assert.Equal(originalOwner, await File.ReadAllBytesAsync(Path.Combine(other, BuilderTemplateCache.OwnerName)));

        var baseCopy = fixture.PathOf("copied-base");
        PrivateDirectory(baseCopy);
        PrivateFile(Path.Combine(baseCopy, BuilderTemplateCache.OwnerName), await File.ReadAllBytesAsync(
            Path.Combine(BuilderTemplateCache.ValidateBasePath(fixture.Options), BuilderTemplateCache.OwnerName)));
        await Assert.ThrowsAsync<IOException>(() => BuilderTemplateCache.AcquireAsync(fixture.Options with { DotnetTemplateCacheRootPath = baseCopy }, "10.0.402", default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnmarkedNonemptyBaseOrHomeIsNotAdopted(bool home)
    {
        using var fixture = new Fixture();
        var path = home ? await HomeAsync(fixture.Options) : BuilderTemplateCache.ValidateBasePath(fixture.Options);
        PrivateDirectory(path);
        if (home) File.Delete(Path.Combine(path, BuilderTemplateCache.OwnerName));
        var sentinel = Path.Combine(path, "user-state");
        await File.WriteAllTextAsync(sentinel, "untouched");
        await Assert.ThrowsAsync<IOException>(() => BuilderTemplateCache.AcquireAsync(fixture.Options, "10.0.402", default));
        Assert.False(File.Exists(Path.Combine(path, BuilderTemplateCache.OwnerName)));
        Assert.Equal("untouched", await File.ReadAllTextAsync(sentinel));
    }

    [Theory]
    [InlineData(BuilderTemplateCache.OwnerName)]
    [InlineData(BuilderTemplateCache.ReadyName)]
    [InlineData(BuilderTemplateCache.LockName)]
    public async Task StateFilesAreBoundedAndNeverOverwritten(string name)
    {
        using var fixture = new Fixture();
        var home = await HomeAsync(fixture.Options);
        var path = Path.Combine(home, name);
        var bytes = new byte[64 * 1024 + 1];
        PrivateFile(path, bytes);
        await Assert.ThrowsAsync<IOException>(async () =>
        {
            using var cache = await BuilderTemplateCache.AcquireAsync(fixture.Options, "10.0.402", default);
            cache.IsReady();
        });
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
    }

    [UnixTheory("Template cache link containment uses Unix symbolic links.")]
    [InlineData("base")]
    [InlineData("ancestor")]
    [InlineData("home")]
    [InlineData(BuilderTemplateCache.OwnerName)]
    [InlineData(BuilderTemplateCache.ReadyName)]
    [InlineData(BuilderTemplateCache.LockName)]
    [InlineData(".templateengine")]
    [InlineData(".templateengine/nested")]
    public async Task LinksCannotRedirectTemplateStateIncludingAncestorsAndMarkers(string location)
    {
        using var fixture = new Fixture();
        var outside = fixture.PathOf("outside"); PrivateDirectory(outside);
        var sentinel = Path.Combine(outside, "user-state"); await File.WriteAllTextAsync(sentinel, "untouched");
        var options = fixture.Options;
        if (location is "base" or "ancestor")
        {
            Directory.CreateSymbolicLink(options.DotnetTemplateCacheRootPath!, outside);
            if (location == "ancestor") options = options with { DotnetTemplateCacheRootPath = Path.Combine(options.DotnetTemplateCacheRootPath!, "child") };
        }
        else
        {
            var home = await HomeAsync(options);
            var target = location == "home" ? home : Path.Combine(home, location);
            if (location == "home") Directory.Delete(home, true);
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Delete(target);
            }
            if (location.EndsWith(".json", StringComparison.Ordinal) || location.EndsWith(".lock", StringComparison.Ordinal)) File.CreateSymbolicLink(target, sentinel);
            else Directory.CreateSymbolicLink(target, outside);
        }
        await Assert.ThrowsAsync<IOException>(() => BuilderTemplateCache.AcquireAsync(options, "10.0.402", default));
        Assert.Equal("untouched", await File.ReadAllTextAsync(sentinel));
        Assert.Single(Directory.EnumerateFileSystemEntries(outside));
    }

    [UnixFact("Private template cache modes are Unix permissions.")]
    public async Task NewlyOwnedStateIsPrivateAndExistingPublicPermissionsAreNotChanged()
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var fixture = new Fixture();
        var home = await HomeAsync(fixture.Options);
        var root = BuilderTemplateCache.ValidateBasePath(fixture.Options);
        const UnixFileMode privateDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        Assert.Equal(privateDirectory, File.GetUnixFileMode(home));
        Assert.Equal(privateDirectory, File.GetUnixFileMode(root));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(home, BuilderTemplateCache.OwnerName)));
        var publicMode = privateDirectory | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
        File.SetUnixFileMode(home, publicMode);
        await Assert.ThrowsAsync<IOException>(() => BuilderTemplateCache.AcquireAsync(fixture.Options, "10.0.402", default));
        Assert.Equal(publicMode, File.GetUnixFileMode(home));
    }

    private static async Task<string> HomeAsync(BuilderProjectDriverOptions options, string sdk = "10.0.402")
    {
        using var cache = await BuilderTemplateCache.AcquireAsync(options, sdk, default);
        return cache.HomePath;
    }
    private static string Home(BuilderLocalProcessRequest request) => Assert.IsType<string>(request.Environment["DOTNET_CLI_HOME"]);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void PrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(path);
        else Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
    private static void PrivateFile(string path, byte[] bytes)
    {
        File.WriteAllBytes(path, bytes);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private sealed class Fixture : IDisposable
    {
        // An explicitly injected short base in every test protects the user's real template home.
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "sunder-bt-" + Guid.NewGuid().ToString("N"));
        internal BuilderProjectDriverOptions Options => new(PathOf("tools")) { CoordinatedPackageVersion = "1.1.0", DotnetTemplateCacheRootPath = PathOf("bt") };
        internal FakeProcess Process { get; } = new();
        internal FakeTools Tools { get; } = new();
        internal Fixture() => PrivateDirectory(Root);
        internal string PathOf(string path) => Path.Combine(Root, path);
        internal Task ExecuteAsync(BuilderProjectDriverOptions options, CancellationToken token = default) => new BuilderProjectDriver(Tools, Process, options)
            .ExecuteAsync(new() { Operation = "create", Kind = BuilderProjectKind.Dotnet, ProjectFolder = PathOf("project-" + Guid.NewGuid().ToString("N")),
                PackageId = "sample.template", DisplayName = "Template" }, new Reporter(), token);
        internal Task ScaffoldAsync(BuilderProjectDriverOptions options) => Assert.ThrowsAsync<ScaffoldReachedException>(() => ExecuteAsync(options));
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
    }
    private sealed class ScaffoldReachedException : Exception;
    private sealed class Reporter : IBuilderJobReporter
    {
        public void Report(string value) { }
        public void Update(BuilderJobUpdate update) { }
    }
    private sealed class FakeProcess : IBuilderLocalProcessRunner
    {
        internal ConcurrentQueue<BuilderLocalProcessRequest> Requests { get; } = new();
        internal Func<BuilderLocalProcessRequest, CancellationToken, Task>? Before;
        internal bool FailInstall;
        internal bool EmptyList;
        public async Task<BuilderLocalProcessResult> RunAsync(BuilderLocalProcessRequest request, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
        {
            Assert.Equal("new", request.Arguments[0]); // These tests never restore/build or use a system/global template home.
            Requests.Enqueue(request);
            if (Before is { } before) await before(request, cancellationToken);
            if (request.Arguments[1] == "sunder-package") throw new ScaffoldReachedException();
            if (request.Arguments[1] == "install" && FailInstall) return new(1, "", "fixture install failure");
            return new(0, EmptyList ? "" : "sunder-package", "");
        }
    }
    private sealed class FakeTools : IBuilderProjectDriverToolchains
    {
        internal int Selections;
        public Task<IBuilderProjectDriverSelection> SelectAsync(BuilderProjectKind kind, string? projectDirectory, BuilderToolchainInstallation? preferred,
            IProgress<string>? progress, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Selections);
            return Task.FromResult<IBuilderProjectDriverSelection>(new Selection());
        }
        public Task<BuilderToolchainStatus> GetStatusAsync(BuilderProjectKind kind, bool checkUpdates, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BuilderToolchainInstallation> EnsureAsync(BuilderProjectKind kind, string? projectDirectory, IProgress<string>? progress, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BuilderToolchainInstallation> RepairAsync(BuilderToolchainInstallation installation, IProgress<string>? progress, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task RemoveAsync(BuilderToolchainInstallation installation, IProgress<string>? progress, CancellationToken cancellationToken) => throw new NotSupportedException();
        private sealed class Selection : IBuilderProjectDriverSelection
        {
            public BuilderToolchainInstallation Installation { get; } = new()
            {
                Kind = BuilderProjectKind.Dotnet, Version = "10.0.402", RootPath = Path.GetTempPath(),
                ExecutablePath = Path.Combine(Path.GetTempPath(), "fixture-dotnet"), IsHealthy = true,
            };
            public BuilderLocalProcessRequest CreateProcess(IReadOnlyList<string> arguments, string workingDirectory, bool npm = false) => new(Installation.ExecutablePath, arguments, workingDirectory);
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
