extern alias BuilderWorker;

using System.Collections.Concurrent;
using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using BuilderWorker::Sunder.Package.Agent.Builder;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class BuilderLocalNpmTests
{
    [Fact]
    public async Task MissingPointerKeepsPublicDefaultsWithoutCreatingStorage()
    {
        using var fixture = new Fixture();
        Assert.Same(fixture.Options, await BuilderLocalNpm.LoadOptionsAsync(fixture.Options));
        Assert.False(Directory.Exists(fixture.Options.CacheRootPath));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{\"schemaVersion\":2,\"feedPath\":\"/missing\"}")]
    [InlineData("{\"schemaVersion\":1,\"feedPath\":\"relative\"}")]
    [InlineData("{\"schemaVersion\":1,\"feedPath\":null}")]
    [InlineData("{\"schemaVersion\":1,\"feedPath\":\"/missing\",\"other\":true}")]
    [InlineData("{\"schemaVersion\":1,\"schemaVersion\":1,\"feedPath\":\"/missing\"}")]
    public async Task InvalidConfigurationFailsClosed(string json)
    {
        using var fixture = new Fixture(); fixture.Pointer(json);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => BuilderLocalNpm.LoadOptionsAsync(fixture.Options));
        Assert.Contains("local-npm.json", error.Message);
        Assert.Contains("public Sunder fallback is disabled", error.Message);
        Assert.False(Directory.Exists(fixture.Retained));
    }

    [Fact]
    public async Task OversizedPointerIsRejectedBeforeRetention()
    {
        using var fixture = new Fixture(); fixture.Pointer(new string(' ', 16 * 1024 + 1));
        await Assert.ThrowsAsync<InvalidDataException>(() => BuilderLocalNpm.LoadOptionsAsync(fixture.Options));
        Assert.False(Directory.Exists(fixture.Retained));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("wrong-name")]
    [InlineData("wrong-version")]
    [InlineData("sdk-range")]
    [InlineData("missing-cli")]
    [InlineData("duplicate")]
    [InlineData("traversal")]
    [InlineData("symlink")]
    public async Task InvalidFeedFailsBeforeToolchainSelectionOrNpm(string failure)
    {
        using var fixture = new Fixture(); fixture.Feed();
        if (failure == "missing") File.Delete(fixture.Archive("@sunder/sdk"));
        else fixture.Package("create-sunder-package", failure);
        await using var jobs = fixture.Jobs();
        var result = await TerminalAsync(jobs, jobs.Start(fixture.Request()));
        Assert.Equal("failed", result.State);
        Assert.Contains("local npm", result.Error);
        Assert.Equal(0, fixture.Tools.Selections); Assert.Empty(fixture.Process.Requests);
        Assert.False(Directory.Exists(fixture.Destination));
        if (Directory.Exists(fixture.Retained)) Assert.Empty(Directory.GetFileSystemEntries(fixture.Retained));
    }

    [UnixFact("Symbolic-link fixtures require Unix link permissions.")]
    public async Task LinkedFeedArchiveAndRetainedStorageAreRejected()
    {
        foreach (var mode in new[] { "pointer", "feed", "archive", "retained" })
        {
            using var fixture = new Fixture(); fixture.Feed();
            if (mode is "pointer" or "archive")
            {
                var path = mode == "pointer" ? fixture.PointerPath : fixture.Archive("@sunder/sdk");
                File.Move(path, fixture.PathOf("target")); File.CreateSymbolicLink(path, fixture.PathOf("target"));
            }
            else
            {
                var path = mode == "feed" ? fixture.FeedPath : fixture.Retained;
                if (Directory.Exists(path)) Directory.Move(path, fixture.PathOf("target")); else Directory.CreateDirectory(fixture.PathOf("target"));
                Directory.CreateSymbolicLink(path, fixture.PathOf("target"));
            }
            await Assert.ThrowsAsync<InvalidDataException>(() => BuilderLocalNpm.LoadOptionsAsync(fixture.Options));
        }
    }

    [Fact]
    public async Task ConcurrentSelectionsRetainAllThreeArchivesAndPreserveUnrelatedSettings()
    {
        using var fixture = new Fixture(); fixture.Feed();
        var pointer = File.ReadAllBytes(fixture.PointerPath);
        var originals = Directory.GetFiles(fixture.FeedPath).ToDictionary(path => Path.GetFileName(path)!, File.ReadAllBytes);
        var npmrc = fixture.PathOf(".npmrc"); File.WriteAllText(npmrc, "user configuration sentinel");
        var selected = await Task.WhenAll(BuilderLocalNpm.LoadOptionsAsync(fixture.Options), BuilderLocalNpm.LoadOptionsAsync(fixture.Options));
        Assert.Equal(selected[0].NodeCreatorPackagePath, selected[1].NodeCreatorPackagePath);
        Assert.Equal(selected[0].NodePackageOverrides.OrderBy(row => row.Key), selected[1].NodePackageOverrides.OrderBy(row => row.Key));
        Assert.Equal(fixture.Options.CacheRootPath, selected[0].CacheRootPath);
        Assert.Equal(fixture.Options.ProjectMutationLockRootPath, selected[0].ProjectMutationLockRootPath);
        Assert.Single(Directory.GetDirectories(fixture.Retained));
        var retained = Path.GetDirectoryName(selected[0].NodeCreatorPackagePath)!;
        foreach (var (name, bytes) in originals)
        {
            Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(retained, name!)));
            Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(fixture.FeedPath, name!)));
        }
        Assert.Equal(pointer, File.ReadAllBytes(fixture.PointerPath));
        Assert.Equal("user configuration sentinel", File.ReadAllText(npmrc));
    }

    [Fact]
    public async Task SameVersionReplacementRetainsOldSelectionAndDetectsTamperingWithoutRepair()
    {
        using var fixture = new Fixture(); fixture.Feed();
        var first = await BuilderLocalNpm.LoadOptionsAsync(fixture.Options);
        var original = File.ReadAllBytes(first.NodeCreatorPackagePath!);
        fixture.Package("create-sunder-package", marker: "same-version hotfix");
        var second = await BuilderLocalNpm.LoadOptionsAsync(fixture.Options);
        Assert.NotEqual(first.NodeCreatorPackagePath, second.NodeCreatorPackagePath);
        Assert.Equal(original, File.ReadAllBytes(first.NodeCreatorPackagePath!));
        File.WriteAllText(second.NodeCreatorPackagePath!, "corrupt retained archive");
        await Assert.ThrowsAsync<InvalidDataException>(() => BuilderLocalNpm.LoadOptionsAsync(fixture.Options));
        Assert.Equal("corrupt retained archive", File.ReadAllText(second.NodeCreatorPackagePath!));
        Assert.Equal(2, Directory.GetDirectories(fixture.Retained).Length);
    }

    [Fact]
    public async Task AcceptedJobPinsLocalSelectionWhileNextJobReadsNewArchives()
    {
        using var fixture = new Fixture(); fixture.Feed();
        File.WriteAllText(Path.Combine(fixture.Options.CacheRootPath, "local-nuget.json"), "broken unrelated configuration");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Tools.BeforeSelect = async token => { entered.TrySetResult(); await release.Task.WaitAsync(token); };
        await using var jobs = fixture.Jobs();
        var request = fixture.Request(); var id = jobs.Start(request);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var retained = Assert.Single(Directory.GetDirectories(fixture.Retained));
        fixture.Package("create-sunder-package", marker: "replacement creator");
        Assert.Equal(id, jobs.Start(request));
        release.SetResult();
        await TerminalAsync(jobs, id);
        var first = Assert.Single(fixture.Process.Inputs);
        Assert.StartsWith("file:" + retained.Replace('\\', '/') + "/", first.Creator);
        Assert.StartsWith("file:" + retained.Replace('\\', '/') + "/", first.Sdk);
        Assert.Equal(id, jobs.Start(request)); Assert.Single(fixture.Process.Requests);
        await TerminalAsync(jobs, jobs.Start(fixture.Request()));
        Assert.NotEqual(first.Creator, fixture.Process.Inputs.Last().Creator);
        Assert.False(Directory.Exists(fixture.Destination));
    }

    [Fact]
    public async Task CancelledResolutionPublishesNothing()
    {
        using var fixture = new Fixture(); fixture.Feed();
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BuilderLocalNpm.LoadOptionsAsync(fixture.Options, cancellation.Token));
        Assert.False(Directory.Exists(fixture.Retained));
    }

    private static async Task<BuilderJobSnapshot> TerminalAsync(BuilderJobManager jobs, string id)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var snapshot = jobs.GetJob(id);
            if (snapshot.State != "running") return snapshot;
            await Task.Delay(10, deadline.Token);
        }
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "sunder-local-npm-" + Guid.NewGuid().ToString("N"));
        internal BuilderProjectDriverOptions Options { get; }
        internal FakeTools Tools { get; } = new();
        internal FakeProcess Process { get; } = new();
        internal string FeedPath => PathOf("source feed");
        internal string Retained => Path.Combine(Options.CacheRootPath, "local-npm");
        internal string PointerPath => Path.Combine(Options.CacheRootPath, "local-npm.json");
        internal string Destination => PathOf("created");
        internal Fixture() { Directory.CreateDirectory(Root); Options = new(PathOf("tools")) { CoordinatedPackageVersion = "1.1.0" }; }
        internal string PathOf(string relative) => Path.Combine(Root, relative);
        internal string Archive(string id) => Path.Combine(FeedPath, id.TrimStart('@').Replace('/', '-') + "-1.1.0.tgz");
        internal void Pointer(string json) { Directory.CreateDirectory(Options.CacheRootPath); File.WriteAllText(PointerPath, json); }
        internal void Feed()
        {
            Directory.CreateDirectory(FeedPath);
            foreach (var id in new[] { "@sunder/sdk", "@sunder/package-tool", "create-sunder-package" }) Package(id);
            Pointer(JsonSerializer.Serialize(new { schemaVersion = 1, feedPath = FeedPath }));
        }
        internal void Package(string id, string? failure = null, string marker = "fixture")
        {
            using var output = File.Create(Archive(id));
            using var gzip = new GZipStream(output, CompressionMode.Compress);
            using var tar = new TarWriter(gzip);
            void Entry(string name, string text) => tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name)
                { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(text)) });
            Entry("package/package.json", JsonSerializer.Serialize(new
            {
                name = failure == "wrong-name" ? "unexpected" : id, version = failure == "wrong-version" ? "9.9.9" : "1.1.0",
                dependencies = new Dictionary<string, string> { ["@sunder/sdk"] = failure == "sdk-range" ? "*" : ">=1.1.0 <1.2.0" }, marker,
            }));
            foreach (var name in new[] { "dist/index.js", "dist/index.d.ts", "dist/generated.js", "dist/browser.js", "dist/react.js", "template/package.json", "template-react-node/package.json" }) Entry("package/" + name, "{}");
            if (failure != "missing-cli") Entry("package/dist/cli.js", "// fixture");
            if (failure == "duplicate") Entry("package/package.json", "{}");
            if (failure == "traversal") Entry("package/../outside", "unsafe");
            if (failure == "symlink") tar.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "package/link") { LinkName = "outside" });
        }
        internal BuilderJobManager Jobs() => new(new BuilderConfiguredJobDriver(Tools, Process, Options));
        internal BuilderJobRequest Request() => new() { Id = Guid.NewGuid().ToString("N"), Kind = BuilderProjectKind.Node,
            Operation = "create", ProjectFolder = Destination, DisplayName = "Example", PackageId = "example.node" };
        public void Dispose() => BuilderToolchainFiles.DeleteTree(Root);
    }

    private sealed class FakeTools : IBuilderProjectDriverToolchains
    {
        internal int Selections;
        internal Func<CancellationToken, Task>? BeforeSelect;
        public async Task<IBuilderProjectDriverSelection> SelectAsync(BuilderProjectKind kind, string? projectDirectory, BuilderToolchainInstallation? preferred, IProgress<string>? progress, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Selections);
            if (BeforeSelect is not null) await BeforeSelect(cancellationToken);
            return new Selection();
        }
        public Task<BuilderToolchainStatus> GetStatusAsync(BuilderProjectKind kind, bool checkUpdates, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BuilderToolchainInstallation> EnsureAsync(BuilderProjectKind kind, string? projectDirectory, IProgress<string>? progress, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BuilderToolchainInstallation> RepairAsync(BuilderToolchainInstallation installation, IProgress<string>? progress, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task RemoveAsync(BuilderToolchainInstallation installation, IProgress<string>? progress, CancellationToken cancellationToken) => throw new NotSupportedException();
        private sealed class Selection : IBuilderProjectDriverSelection
        {
            public BuilderToolchainInstallation Installation { get; } = new() { Kind = BuilderProjectKind.Node, Version = "24.22.1", NpmVersion = "11.19.1", RootPath = Path.GetTempPath(), ExecutablePath = Path.Combine(Path.GetTempPath(), "node"), IsHealthy = true };
            public BuilderLocalProcessRequest CreateProcess(IReadOnlyList<string> arguments, string workingDirectory, bool npm = false) => new(Installation.ExecutablePath, arguments, workingDirectory);
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class FakeProcess : IBuilderLocalProcessRunner
    {
        internal ConcurrentQueue<BuilderLocalProcessRequest> Requests { get; } = new();
        internal ConcurrentQueue<(string Creator, string Sdk)> Inputs { get; } = new();
        public Task<BuilderLocalProcessResult> RunAsync(BuilderLocalProcessRequest request, IProgress<string>? progress, CancellationToken cancellationToken)
        {
            Requests.Enqueue(request);
            using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(request.WorkingDirectory, "package.json")));
            var dependencies = json.RootElement.GetProperty("dependencies");
            Inputs.Enqueue((dependencies.GetProperty("create-sunder-package").GetString()!, dependencies.GetProperty("@sunder/sdk").GetString()!));
            return Task.FromResult(new BuilderLocalProcessResult(1, "", "stop before fixture installation"));
        }
    }
}
