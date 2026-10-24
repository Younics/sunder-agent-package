extern alias BuilderWorker;

using System.Collections.Concurrent;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using BuilderWorker::Sunder.Package.Agent.Builder;
using Sunder.Sdk.Abstractions;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class BuilderLocalNuGetTests
{
    [Fact]
    public async Task MissingPointerKeepsPublicDefaultsWithoutCreatingStorage()
    {
        using var fixture = new Fixture();
        Assert.Same(fixture.Options, await BuilderLocalNuGet.LoadOptionsAsync(fixture.Options));
        Assert.False(Directory.Exists(fixture.Options.CacheRootPath));
        Assert.Null(fixture.Options.DotnetTemplatePackagePath);
        Assert.Null(fixture.Options.NuGetConfigPath);
    }

    [Theory]
    [InlineData("[1.1.0,1.2.0)")]
    [InlineData("[1.1.0, 1.2.0)")]
    public async Task CoordinatedNuSpecRangesAcceptTemplateAndNuGetPackFormatting(string range)
    {
        using var fixture = new Fixture();
        fixture.CreateFeed();
        fixture.WritePackage("Sunder.Sdk.Avalonia", dependencyVersion: range);
        fixture.WritePackage("Sunder.Sdk.Stacks", dependencyVersion: range);
        var options = await BuilderLocalNuGet.LoadOptionsAsync(fixture.Options);
        Assert.NotNull(options.DotnetTemplatePackagePath);
        Assert.NotNull(options.NuGetConfigPath);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{\"schemaVersion\":2,\"feedPath\":\"/absent\"}")]
    [InlineData("{\"schemaVersion\":1,\"feedPath\":\"relative\"}")]
    [InlineData("{\"schemaVersion\":1,\"feedPath\":null}")]
    [InlineData("{\"schemaVersion\":1,\"feedPath\":\"/absent\",\"other\":true}")]
    [InlineData("{\"schemaVersion\":1,\"schemaVersion\":1,\"feedPath\":\"/absent\"}")]
    [InlineData("{\"schemaVersion\":1,\"feedPath\":\"/absent\",\"FeedPath\":\"/other\"}")]
    public async Task ClosedConfigurationRejectsInvalidAndAmbiguousDocuments(string json)
    {
        using var fixture = new Fixture();
        fixture.Pointer(json);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => BuilderLocalNuGet.LoadOptionsAsync(fixture.Options));
        Assert.Contains("local-nuget.json", error.Message);
        Assert.Contains("public Sunder fallback is disabled", error.Message);
        Assert.False(Directory.Exists(fixture.Snapshots));
    }

    [Fact]
    public async Task PointerHasABoundedRead()
    {
        using var fixture = new Fixture();
        fixture.Pointer(new string(' ', 16 * 1024 + 1));
        await Assert.ThrowsAsync<InvalidDataException>(() => BuilderLocalNuGet.LoadOptionsAsync(fixture.Options));
        Assert.False(Directory.Exists(fixture.Snapshots));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("partial")]
    [InlineData("nested")]
    public async Task IncompleteFeedFailsBeforeSdkSelectionOrProjectMutation(string failure)
    {
        using var fixture = new Fixture();
        fixture.CreateFeed();
        if (failure == "missing") Directory.Delete(fixture.Feed, recursive: true);
        else if (failure == "partial") File.Delete(fixture.Archive("Sunder.Sdk.Worker"));
        else
        {
            Directory.CreateDirectory(Path.Combine(fixture.Feed, "nested"));
            File.Move(fixture.Archive("Sunder.Sdk.Worker"), Path.Combine(fixture.Feed, "nested", "Sunder.Sdk.Worker.1.1.0.nupkg"));
        }
        await using var jobs = fixture.Jobs();
        var result = await TerminalAsync(jobs, jobs.Start(fixture.CreateRequest()));
        Assert.Equal("failed", result.State);
        Assert.Contains("local", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, fixture.Tools.Selections);
        Assert.Empty(fixture.Process.Requests);
        Assert.Empty(Directory.EnumerateDirectories(fixture.Root, ".sunder-builder-create-*"));
    }

    [Theory]
    [InlineData("wrong-id")]
    [InlineData("wrong-version")]
    [InlineData("sdk-constraint")]
    [InlineData("required-artifact")]
    [InlineData("template-version")]
    [InlineData("template-framework")]
    [InlineData("duplicate-entry")]
    [InlineData("traversal-entry")]
    public async Task InvalidPackageContentsAreRejectedBeforeRetention(string failure)
    {
        using var fixture = new Fixture();
        fixture.CreateFeed();
        fixture.WritePackage(failure.StartsWith("template-", StringComparison.Ordinal) ? "Sunder.Package.Templates" : "Sunder.Sdk.Worker", failure);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => BuilderLocalNuGet.LoadOptionsAsync(fixture.Options));
        Assert.Contains("public Sunder fallback is disabled", error.Message);
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Snapshots));
    }

    [UnixFact("Symbolic-link fixtures require Unix link permissions.")]
    public async Task LinkedPointerFeedPackageAndOwnedDestinationAreRejected()
    {
        foreach (var mode in new[] { "pointer", "feed", "archive", "cache" })
        {
            using var fixture = new Fixture();
            fixture.CreateFeed();
            if (mode == "pointer")
            {
                var path = fixture.PathOf("pointer-target.json"); File.Move(fixture.PointerPath, path);
                File.CreateSymbolicLink(fixture.PointerPath, path);
            }
            else if (mode == "archive")
            {
                var original = fixture.Archive("Sunder.Sdk"); var path = fixture.PathOf("sdk.nupkg"); File.Move(original, path);
                File.CreateSymbolicLink(original, path);
            }
            else
            {
                var path = fixture.PathOf("directory-target");
                if (mode == "feed") Directory.Move(fixture.Feed, path); else Directory.CreateDirectory(path);
                Directory.CreateSymbolicLink(mode == "feed" ? fixture.Feed : fixture.Snapshots, path);
            }
            await Assert.ThrowsAsync<InvalidDataException>(() => BuilderLocalNuGet.LoadOptionsAsync(fixture.Options));
        }
    }

    [Fact]
    public async Task RetentionIsAtomicSourceMappedAndDoesNotRewriteFeedOldCacheOrSettings()
    {
        using var fixture = new Fixture();
        fixture.CreateFeed();
        var originals = Directory.GetFiles(fixture.Feed).ToDictionary(path => Path.GetFileName(path)!, File.ReadAllBytes);
        var global = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        var settings = fixture.PathOf("NuGet.Config"); File.WriteAllText(settings, "user settings sentinel");
        var old = Path.Combine(fixture.Options.CacheRootPath, "nuget-packages", "old-cache"); Directory.CreateDirectory(old);
        File.WriteAllText(Path.Combine(old, "keep"), "old bytes");
        var selections = await Task.WhenAll(BuilderLocalNuGet.LoadOptionsAsync(fixture.Options), BuilderLocalNuGet.LoadOptionsAsync(fixture.Options));
        Assert.Equal(selections[0], selections[1]);
        var resolved = selections[0];
        Assert.EndsWith(Path.Combine("tools"), resolved.CacheRootPath);
        Assert.Equal(fixture.Options.ProjectMutationLockRootPath, resolved.ProjectMutationLockRootPath);
        Assert.NotEqual(fixture.Archive("Sunder.Package.Templates"), resolved.DotnetTemplatePackagePath);
        var snapshot = Path.GetDirectoryName(resolved.CacheRootPath)!;
        Assert.Equal(64, Path.GetFileName(snapshot).Length);
        Assert.Single(Directory.EnumerateDirectories(fixture.Snapshots));
        var xml = XDocument.Load(resolved.NuGetConfigPath!);
        var sources = xml.Root!.Element("packageSources")!;
        Assert.NotNull(sources.Element("clear"));
        Assert.Equal(new[] { "local", "nugetorg" }, sources.Elements("add").Select(item => (string?)item.Attribute("key")));
        Assert.Equal(Path.Combine(snapshot, "feed"), (string?)sources.Elements("add").First().Attribute("value"));
        Assert.Equal("https://api.nuget.org/v3/index.json", (string?)sources.Elements("add").Last().Attribute("value"));
        var mapping = xml.Root.Element("packageSourceMapping")!;
        Assert.NotNull(mapping.Element("clear"));
        Assert.Equal(new[] { "Sunder.*", "*" }, mapping.Elements("packageSource").Select(item => (string?)item.Element("package")!.Attribute("pattern")));
        Assert.NotNull(xml.Root.Element("fallbackPackageFolders")!.Element("clear"));
        foreach (var (name, bytes) in originals)
        {
            Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(fixture.Feed, name!)));
            Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(snapshot, "feed", name!)));
        }
        Assert.Equal("user settings sentinel", File.ReadAllText(settings));
        Assert.Equal("old bytes", File.ReadAllText(Path.Combine(old, "keep")));
        Assert.Equal(global, Environment.GetEnvironmentVariable("NUGET_PACKAGES"));
    }

    [Fact]
    public async Task ChangedSameVersionSdkBytesSelectNewNuGetCacheAndShortTemplateHome()
    {
        using var fixture = new Fixture();
        fixture.CreateFeed();
        var first = await BuilderLocalNuGet.LoadOptionsAsync(fixture.Options);
        var retained = File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(first.DotnetTemplatePackagePath)!, "Sunder.Sdk.1.1.0.nupkg"));
        using var home1 = await BuilderTemplateCache.AcquireAsync(first, "10.0.402", default);
        fixture.WritePackage("Sunder.Sdk", marker: "changed source bytes at the same package version");
        var second = await BuilderLocalNuGet.LoadOptionsAsync(fixture.Options);
        using var home2 = await BuilderTemplateCache.AcquireAsync(second, "10.0.402", default);
        Assert.NotEqual(first.CacheRootPath, second.CacheRootPath);
        Assert.NotEqual(home1.HomePath, home2.HomePath);
        Assert.True(Encoding.UTF8.GetByteCount(home2.HomePath) <= BuilderTemplateCache.MaximumHomeUtf8Bytes);
        Assert.Equal(retained, File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(first.DotnetTemplatePackagePath)!, "Sunder.Sdk.1.1.0.nupkg")));
    }

    [Fact]
    public async Task DifferentFeedPathIsADistinctSelectionEvenForIdenticalBytes()
    {
        using var fixture = new Fixture(); fixture.CreateFeed();
        var first = await BuilderLocalNuGet.LoadOptionsAsync(fixture.Options);
        var other = fixture.PathOf("other feed"); Directory.CreateDirectory(other);
        foreach (var file in Directory.GetFiles(fixture.Feed)) File.Copy(file, Path.Combine(other, Path.GetFileName(file)));
        fixture.Pointer(JsonSerializer.Serialize(new { schemaVersion = 1, feedPath = other }));
        var second = await BuilderLocalNuGet.LoadOptionsAsync(fixture.Options);
        Assert.NotEqual(first.CacheRootPath, second.CacheRootPath);
        Assert.Equal(File.ReadAllBytes(first.DotnetTemplatePackagePath!), File.ReadAllBytes(second.DotnetTemplatePackagePath!));
    }

    [Theory]
    [InlineData("feed/Sunder.Sdk.1.1.0.nupkg")]
    [InlineData("NuGet.Config")]
    [InlineData(".sunder-builder-local-nuget.json")]
    public async Task ChangedRetainedStateFailsClosedWithoutRepairingExistingCache(string relative)
    {
        using var fixture = new Fixture(); fixture.CreateFeed();
        var selected = await BuilderLocalNuGet.LoadOptionsAsync(fixture.Options);
        var path = Path.Combine(Path.GetDirectoryName(selected.CacheRootPath)!, relative);
        File.WriteAllText(path, "corrupted retained bytes");
        await Assert.ThrowsAsync<InvalidDataException>(() => BuilderLocalNuGet.LoadOptionsAsync(fixture.Options));
        Assert.Equal("corrupted retained bytes", File.ReadAllText(path));
        Assert.Single(Directory.EnumerateDirectories(fixture.Snapshots));
    }

    [Fact]
    public async Task CancelledResolutionPublishesNothing()
    {
        using var fixture = new Fixture(); fixture.CreateFeed();
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BuilderLocalNuGet.LoadOptionsAsync(fixture.Options, cancellation.Token));
        Assert.False(Directory.Exists(fixture.Snapshots));

        fixture.WritePackage("Sunder.Package.Build", paddingBytes: 64 * 1024 * 1024);
        using var copying = new CancellationTokenSource();
        var work = BuilderLocalNuGet.LoadOptionsAsync(fixture.Options, copying.Token);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!Directory.Exists(fixture.Snapshots) || !Directory.EnumerateDirectories(fixture.Snapshots, ".stage-*").Any(stage =>
            File.Exists(Path.Combine(stage, "feed", "Sunder.Package.Build.1.1.0.nupkg"))))
        {
            Assert.False(work.IsCompleted, "Resolution must still be copying the bounded fixture when cancelled.");
            await Task.Delay(1, deadline.Token);
        }
        copying.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work);
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Snapshots));
    }

    [Theory]
    [InlineData("ensure-sdk", BuilderProjectKind.Node)]
    [InlineData("repair-sdk", BuilderProjectKind.Dotnet)]
    [InlineData("remove-sdk", BuilderProjectKind.Dotnet)]
    public async Task SdkManagementAndNodeDoNotReadBrokenDotnetOverride(string operation, BuilderProjectKind kind)
    {
        using var fixture = new Fixture(); fixture.Pointer("broken-json");
        await using var jobs = fixture.Jobs();
        var request = new BuilderJobRequest { Id = Guid.NewGuid().ToString("N"), Operation = operation, Kind = kind,
            Installation = operation == "ensure-sdk" ? null : fixture.Tools.Installation(kind) };
        Assert.Equal("succeeded", (await TerminalAsync(jobs, jobs.Start(request))).State);
        Assert.Equal(1, fixture.Tools.ManagementCalls);
        Assert.False(Directory.Exists(fixture.Snapshots));
    }

    [Fact]
    public async Task EnsureSdkPreflightsConfiguredFeedAndDoesNotReinstallSdkItself()
    {
        using var fixture = new Fixture(); fixture.CreateFeed();
        await using var jobs = fixture.Jobs();
        var request = new BuilderJobRequest { Id = Guid.NewGuid().ToString("N"), Operation = "ensure-sdk", Kind = BuilderProjectKind.Dotnet };
        Assert.Equal("succeeded", (await TerminalAsync(jobs, jobs.Start(request))).State);
        Assert.Equal(1, fixture.Tools.ManagementCalls); Assert.Empty(fixture.Process.Requests);
        Directory.Delete(fixture.Feed, true);
        var result = await TerminalAsync(jobs, jobs.Start(request with { Id = Guid.NewGuid().ToString("N") }));
        Assert.Equal("failed", result.State); Assert.Equal(1, fixture.Tools.ManagementCalls);
    }

    [Fact]
    public async Task NodeCreationIgnoresBrokenDotnetConfiguration()
    {
        using var fixture = new Fixture(); fixture.Pointer("broken-json");
        await using var jobs = fixture.Jobs();
        var result = await TerminalAsync(jobs, jobs.Start(fixture.CreateRequest() with { Kind = BuilderProjectKind.Node }));
        Assert.DoesNotContain("local-nuget", result.Error);
        Assert.Equal(1, fixture.Tools.Selections);
        Assert.Contains("node-creators", Assert.Single(fixture.Process.Requests).WorkingDirectory);
        Assert.False(Directory.Exists(fixture.Snapshots));
    }

    [Fact]
    public async Task LocalArchiveInstallListAndScaffoldUseTheSameShortCliHome()
    {
        using var fixture = new Fixture(); fixture.CreateFeed(); fixture.Process.InstallTemplate = true;
        await using var jobs = fixture.Jobs();
        await TerminalAsync(jobs, jobs.Start(fixture.CreateRequest()));
        var commands = fixture.Process.Requests.ToArray();
        Assert.Equal(new[] { "install", "list", "sunder-package" }, commands.Select(command => command.Arguments[1]));
        Assert.StartsWith(fixture.Snapshots, commands[0].Arguments[2]);
        Assert.Equal(File.ReadAllBytes(fixture.Archive("Sunder.Package.Templates")), File.ReadAllBytes(commands[0].Arguments[2]));
        var home = Assert.Single(commands.Select(command => command.Environment["DOTNET_CLI_HOME"]).Distinct());
        Assert.True(Encoding.UTF8.GetByteCount(home!) <= BuilderTemplateCache.MaximumHomeUtf8Bytes);
        Assert.Single(commands.Select(command => command.Environment["NUGET_PACKAGES"]).Distinct());
        Assert.Single(commands.Select(command => command.Environment["RestoreConfigFile"]).Distinct());
    }

    [Fact]
    public async Task AcceptedJobsPinSelectionWhileNewJobsReloadAndExactRetriesStayDeduplicated()
    {
        using var fixture = new Fixture(); fixture.CreateFeed();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Tools.BeforeSelect = async token => { entered.TrySetResult(); await release.Task.WaitAsync(token); };
        await using var jobs = fixture.Jobs();
        var first = fixture.CreateRequest(); var id = jobs.Start(first);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var selected = Assert.Single(Directory.GetDirectories(fixture.Snapshots));
        fixture.WritePackage("Sunder.Package.Templates", marker: "replacement template bytes");
        Assert.Equal(id, jobs.Start(first));
        release.SetResult();
        await TerminalAsync(jobs, id);
        var command1 = Assert.Single(fixture.Process.Requests);
        Assert.StartsWith(selected, command1.Arguments[2]);
        Assert.Equal(Path.Combine(selected, "tools", "nuget-packages"), command1.Environment["NUGET_PACKAGES"]);
        Assert.Equal(Path.Combine(selected, "NuGet.Config"), command1.Environment["RestoreConfigFile"]);
        Assert.Equal(id, jobs.Start(first)); Assert.Single(fixture.Process.Requests);
        await TerminalAsync(jobs, jobs.Start(fixture.CreateRequest()));
        var command2 = fixture.Process.Requests.Last();
        Assert.NotEqual(command1.Arguments[2], command2.Arguments[2]);
        Assert.NotEqual(command1.Environment["DOTNET_CLI_HOME"], command2.Environment["DOTNET_CLI_HOME"]);
        File.Delete(fixture.PointerPath);
        await TerminalAsync(jobs, jobs.Start(fixture.CreateRequest()));
        Assert.Equal("Sunder.Package.Templates@1.1.0", fixture.Process.Requests.Last().Arguments[2]);
        Assert.Equal(Path.Combine(fixture.Options.CacheRootPath, "nuget-packages"), fixture.Process.Requests.Last().Environment["NUGET_PACKAGES"]);
    }

    [Theory]
    [InlineData("setup")]
    [InlineData("build")]
    [InlineData("archive")]
    public async Task ScopedConfigAndPackagesReachRestoreBuildAndArchive(string operation)
    {
        using var fixture = new Fixture(); fixture.CreateFeed();
        var project = fixture.WriteProject();
        await using var jobs = fixture.Jobs();
        await TerminalAsync(jobs, jobs.Start(new() { Id = Guid.NewGuid().ToString("N"), Operation = operation, Kind = BuilderProjectKind.Dotnet, EntryPoint = project }));
        Assert.NotEmpty(fixture.Process.Requests);
        var command = fixture.Process.Requests.Last();
        var config = command.Environment["RestoreConfigFile"]!;
        Assert.StartsWith(fixture.Snapshots, config);
        Assert.Equal(Path.Combine(Path.GetDirectoryName(config)!, "tools", "nuget-packages"), command.Environment["NUGET_PACKAGES"]);
        Assert.Equal(Path.Combine(Path.GetDirectoryName(config)!, "tools", "nuget-http"), command.Environment["NUGET_HTTP_CACHE_PATH"]);
        Assert.Equal(Path.Combine(Path.GetDirectoryName(config)!, "tools", "nuget-plugins"), command.Environment["NUGET_PLUGINS_CACHE_PATH"]);
        Assert.Equal(Path.Combine(Path.GetDirectoryName(config)!, "tools", "nuget-scratch"), command.Environment["NUGET_SCRATCH"]);
        Assert.Null(command.Environment["NUGET_FALLBACK_PACKAGES"]);
        Assert.Contains("-p:RestoreConfigFile=" + config, command.Arguments);
        if (operation == "setup")
        {
            var restore = fixture.Process.Requests.First();
            Assert.Equal("restore", restore.Arguments[0]); Assert.Contains("--configfile", restore.Arguments); Assert.Contains(config, restore.Arguments);
            Assert.DoesNotContain("-restore", command.Arguments);
        }
        else if (operation == "build")
        {
            Assert.Single(fixture.Process.Requests);
            Assert.Equal("msbuild", command.Arguments[0]);
            Assert.Contains("-restore", command.Arguments);
        }
        else Assert.Contains("-restore", command.Arguments);
    }

    [Fact]
    public async Task AcceptedFiniteBuildPinsScopedSourceAndNewJobInvalidatesSameVersionHotfix()
    {
        using var fixture = new Fixture(); fixture.CreateFeed(); fixture.Process.ProduceDevelopment = true;
        var entry = fixture.WriteProject();
        await using var jobs = fixture.Jobs();
        var setup = await TerminalAsync(jobs, jobs.Start(new() { Id = Guid.NewGuid().ToString("N"), Operation = "setup", Kind = BuilderProjectKind.Dotnet, EntryPoint = entry }));
        Assert.Equal("succeeded", setup.State);
        var selected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Tools.BeforeSelect = async token => { selected.TrySetResult(); await proceed.Task.WaitAsync(token); };
        var id = jobs.Start(new() { Id = Guid.NewGuid().ToString("N"), Operation = "build", Kind = BuilderProjectKind.Dotnet, EntryPoint = entry });
        await selected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Pointer("now-invalid");
        proceed.TrySetResult();
        Assert.Equal("succeeded", (await TerminalAsync(jobs, id)).State);
        fixture.Tools.BeforeSelect = null;
        var commands = fixture.Process.Requests.ToArray();
        var config = Assert.Single(commands.Select(command => command.Environment["RestoreConfigFile"]).Distinct())!;
        Assert.Equal(new[] { "restore", "msbuild", "msbuild" }, commands.Where(command => !command.Arguments.Any(argument => argument.StartsWith("-t:", StringComparison.Ordinal)))
            .Select(command => command.Arguments[0]));
        Assert.Single(fixture.Process.Requests, command => command.Arguments[0] == "restore");
        Assert.All(fixture.Process.Requests, command => Assert.Equal(config, command.Environment["RestoreConfigFile"]));
        Assert.Contains("-restore", commands.Last().Arguments);

        fixture.CreateFeed();
        fixture.WritePackage("Sunder.Package.Build", marker: "same-version hotfix");
        var before = fixture.Process.Requests.Count;
        id = jobs.Start(new() { Id = Guid.NewGuid().ToString("N"), Operation = "build", Kind = BuilderProjectKind.Dotnet, EntryPoint = entry });
        Assert.Equal("succeeded", (await TerminalAsync(jobs, id)).State);
        var hotfix = fixture.Process.Requests.Skip(before).ToArray();
        Assert.Single(hotfix); // One finite MSBuild invocation, with integrated restore.
        Assert.All(hotfix, command => Assert.Equal("msbuild", command.Arguments[0]));
        Assert.Contains("-restore", hotfix[0].Arguments);
        var newConfig = Assert.Single(hotfix.Select(command => command.Environment["RestoreConfigFile"]).Distinct());
        Assert.NotEqual(config, newConfig);
        Assert.NotEqual(commands[0].Environment["NUGET_PACKAGES"], hotfix[0].Environment["NUGET_PACKAGES"]);
    }

    [Fact]
    public async Task ProductionRuntimeUsesPointerAddedAndRemovedAfterBackendConstruction()
    {
        using var fixture = new Fixture();
        await using var runtime = new BuilderRuntimeService(fixture.Context());
        var backendId = await runtime.GetBackendIdAsync();
        // A nonempty create destination stops before actual SDK discovery/download. This still
        // exercises default RuntimeService construction, job acceptance and production resolution.
        var request = fixture.CreateRequest(); Directory.CreateDirectory(request.ProjectFolder!);
        File.WriteAllText(Path.Combine(request.ProjectFolder!, "user-file"), "keep");
        async Task<BuilderJobSnapshot> Start(BuilderJobRequest value)
        {
            var id = await runtime.StartJobAsync(JsonSerializer.Serialize(value with { BackendId = backendId }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (true)
            {
                await using var content = await runtime.GetJobAsync(id, backendId, deadline.Token);
                await using var stream = await content.OpenReadAsync(deadline.Token);
                var result = (await JsonSerializer.DeserializeAsync<BuilderJobSnapshot>(stream, new JsonSerializerOptions(JsonSerializerDefaults.Web), deadline.Token))!;
                if (result.State != "running") return result;
                await Task.Delay(10, deadline.Token);
            }
        }
        Assert.Contains("destination contains user files", (await Start(request)).Error);
        fixture.Pointer("{}");
        await using (var status = await runtime.GetToolchainsAsync("dotnet", checkUpdates: false))
            Assert.NotNull(status); // management status is usable even with a malformed local feed
        var invalid = request with { Id = Guid.NewGuid().ToString("N") };
        Assert.Contains("local-nuget.json", (await Start(invalid)).Error);
        fixture.CreateFeed();
        Assert.Contains("local-nuget.json", (await Start(invalid)).Error); // same job retains the original failure
        Assert.Contains("destination contains user files", (await Start(request with { Id = Guid.NewGuid().ToString("N") })).Error);
        Assert.Single(Directory.EnumerateDirectories(fixture.Snapshots));
        File.Delete(fixture.PointerPath);
        Assert.Contains("destination contains user files", (await Start(request with { Id = Guid.NewGuid().ToString("N") })).Error);
        Assert.Equal("keep", File.ReadAllText(Path.Combine(request.ProjectFolder!, "user-file")));
    }

    private static async Task<BuilderJobSnapshot> TerminalAsync(BuilderJobManager jobs, string id)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true)
        {
            var result = jobs.GetJob(id); if (result.State != "running") return result;
            await Task.Delay(10, deadline.Token);
        }
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "bln-" + Guid.NewGuid().ToString("N"));
        internal string Feed => PathOf("source feed");
        internal string PointerPath => Path.Combine(Options.CacheRootPath, "local-nuget.json");
        internal string Snapshots => Path.Combine(Options.CacheRootPath, "local-nuget");
        internal BuilderProjectDriverOptions Options { get; }
        internal Tools Tools { get; }
        internal Processes Process { get; } = new();
        internal Fixture()
        {
            Directory.CreateDirectory(Root);
            Options = new(PathOf("development-toolchains/project-tools")) { CoordinatedPackageVersion = "1.1.0", DotnetTemplateCacheRootPath = PathOf("bt") };
            Tools = new(Root);
        }
        internal string PathOf(string name) => Path.Combine(Root, name);
        internal string Archive(string id) => Path.Combine(Feed, id + ".1.1.0.nupkg");
        internal void Pointer(string json) { Directory.CreateDirectory(Options.CacheRootPath); File.WriteAllText(PointerPath, json); }
        internal void CreateFeed()
        {
            Directory.CreateDirectory(Feed);
            foreach (var id in new[] { "Sunder.Sdk", "Sunder.Sdk.Avalonia", "Sunder.Sdk.Stacks", "Sunder.Sdk.Worker", "Sunder.Package.Build", "Sunder.Package.Templates" }) WritePackage(id);
            Pointer(JsonSerializer.Serialize(new { schemaVersion = 1, feedPath = Feed }));
        }
        internal BuilderJobManager Jobs() => new(new BuilderConfiguredJobDriver(Tools, Process, Options));
        internal BuilderJobRequest CreateRequest() => new() { Id = Guid.NewGuid().ToString("N"), Operation = "create", Kind = BuilderProjectKind.Dotnet,
            ProjectFolder = PathOf("project-" + Guid.NewGuid().ToString("N")), PackageId = "sample.local", DisplayName = "Local" };
        internal string WriteProject()
        {
            var folder = PathOf("existing"); Directory.CreateDirectory(folder);
            var entry = Path.Combine(folder, "Local.csproj");
            File.WriteAllText(entry, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><SunderPackageProject>true</SunderPackageProject></PropertyGroup><ItemGroup><PackageReference Include=\"Sunder.Sdk.Worker\" Version=\"1.1.0\" /></ItemGroup></Project>");
            File.WriteAllText(Path.Combine(folder, "PackageMetadata.cs"), "using Sunder.Sdk.Packaging;\n[assembly: SunderPackage(Id = \"sample.local\", Name = \"Local\")]\n");
            return entry;
        }
        internal IPackageContext Context()
        {
            var workspace = Proxy.Create<IPackageRoleLocalWorkspace>((method, args) => method.Name switch
            {
                "get_WorkspaceRootPath" => Root, "GetLocalPath" => PathOf((string)args![0]!), _ => throw new NotSupportedException(method.Name),
            });
            var storage = Proxy.Create<IPackageStorageContext>((method, _) => method.Name == "get_RoleLocalWorkspace" ? workspace : throw new NotSupportedException(method.Name));
            return Proxy.Create<IPackageContext>((method, _) => method.Name == "get_Storage" ? storage : throw new NotSupportedException(method.Name));
        }
        internal void WritePackage(string id, string? failure = null, string marker = "original", int paddingBytes = 0, string? dependencyVersion = null)
        {
            using var output = File.Create(Archive(id)); using var zip = new ZipArchive(output, ZipArchiveMode.Create);
            void Add(string path, string text = "artifact")
            {
                using var writer = new StreamWriter(zip.CreateEntry(path).Open(), new UTF8Encoding(false)); writer.Write(text);
            }
            var sdkRange = failure == "sdk-constraint" ? "[2.0.0]" : dependencyVersion ?? (id == "Sunder.Sdk.Worker" ? "[1.1.0]" : "[1.1.0, 1.2.0)");
            var dependency = id.StartsWith("Sunder.Sdk.", StringComparison.Ordinal)
                ? $"<dependencies><group targetFramework=\"net10.0\"><dependency id=\"Sunder.Sdk\" version=\"{sdkRange}\" /></group></dependencies>" : "";
            Add(id + ".nuspec", $"<package><metadata><id>{(failure == "wrong-id" ? "Wrong.Id" : id)}</id><version>{(failure == "wrong-version" ? "1.1.1" : "1.1.0")}</version>{dependency}" +
                (id == "Sunder.Package.Templates" ? "<packageTypes><packageType name=\"Template\" /></packageTypes>" : "") + "</metadata></package>");
            if (id.StartsWith("Sunder.Sdk", StringComparison.Ordinal))
            {
                if (failure != "required-artifact") Add("lib/net10.0/" + id + ".dll");
                if (id == "Sunder.Sdk.Stacks") { Add("contracts/sunder.stack.contributor.rpc.json", "{}"); Add("buildTransitive/Sunder.Sdk.Stacks.props", "<Project />"); }
            }
            else if (id == "Sunder.Package.Build")
            {
                foreach (var name in new[] { "props", "targets", "SourceLock.targets", "Authoring.targets", "Authoring.Outer.targets", "Authoring.Dispatch.targets", "Authoring.References.targets", "Authoring.RoleInputs.targets", "Authoring.CrossTargeting.targets" }) Add("buildTransitive/Sunder.Package.Build." + name, "<Project />");
                foreach (var name in new[] { "Sunder.Package.Build", "Sunder.Package.Format", "Sunder.Sdk", "Microsoft.CodeAnalysis", "Microsoft.CodeAnalysis.CSharp", "Microsoft.Extensions.DependencyInjection.Abstractions", "Microsoft.Extensions.Logging.Abstractions" }) Add("tasks/net10.0/" + name + ".dll");
                Add("analyzers/dotnet/cs/Sunder.Package.Build.Editor.dll");
            }
            else
            {
                const string root = "content/sunder-package/"; const string project = root + "Sunder.Package.Template/";
                Add(root + ".template.config/template.json", JsonSerializer.Serialize(new { identity = "Sunder.Package", shortName = "sunder-package", symbols = new
                {
                    coordinatedPackageVersion = new { type = "generated", generator = "constant", parameters = new { value = failure == "template-version" ? "1.1.1" : "1.1.0" } },
                    coordinatedPackageVersionRange = new { type = "generated", generator = "constant", parameters = new { value = "[1.1.0,1.2.0)" } },
                } }));
                string Reference(string package) => $"<PackageReference Include=\"{package}\" Version=\"SUNDER_TEMPLATE_PACKAGE_VERSION_RANGE\" />";
                Add(project + "Sunder.Package.Template.csproj", "<Project><PropertyGroup><TargetFramework>" + (failure == "template-framework" ? "net11.0" : "net10.0") + "</TargetFramework></PropertyGroup><ItemGroup>" + Reference("Sunder.Sdk.Worker") + Reference("Sunder.Package.Build") + "</ItemGroup></Project>");
                Add(project + "Views/Avalonia.PackageReferences.props", "<Project><ItemGroup>" + Reference("Sunder.Sdk.Avalonia") + "</ItemGroup></Project>");
                Add(project + "Stacks/Stacks.PackageReferences.props", "<Project><ItemGroup>" + Reference("Sunder.Sdk.Stacks") + "</ItemGroup></Project>");
            }
            Add("marker.txt", marker);
            if (paddingBytes > 0)
            {
                using var padded = zip.CreateEntry("padding.bin", CompressionLevel.NoCompression).Open();
                var buffer = new byte[64 * 1024];
                for (var written = 0; written < paddingBytes; written += buffer.Length) padded.Write(buffer);
            }
            if (failure == "duplicate-entry") Add("MARKER.txt", "duplicate");
            if (failure == "traversal-entry") Add("../outside", "escape");
        }
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }

    public class Proxy : DispatchProxy
    {
        private Func<MethodInfo, object?[]?, object?> invoke = null!;
        internal static T Create<T>(Func<MethodInfo, object?[]?, object?> invoke) where T : class
        {
            var proxy = DispatchProxy.Create<T, Proxy>(); ((Proxy)(object)proxy).invoke = invoke; return proxy;
        }
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => invoke(targetMethod!, args);
    }

    private sealed class Tools(string root) : IBuilderProjectDriverToolchains
    {
        internal int Selections;
        internal int ManagementCalls;
        internal Func<CancellationToken, Task>? BeforeSelect;
        internal BuilderToolchainInstallation Installation(BuilderProjectKind kind) => new() { Kind = kind, Version = kind == BuilderProjectKind.Dotnet ? "10.0.402" : "24.22.0",
            RootPath = root, ExecutablePath = Path.Combine(root, kind == BuilderProjectKind.Dotnet ? "dotnet" : "node"), IsHealthy = true, IsManaged = true };
        public Task<BuilderToolchainStatus> GetStatusAsync(BuilderProjectKind kind, bool checkUpdates, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BuilderToolchainInstallation> EnsureAsync(BuilderProjectKind kind, string? projectDirectory, IProgress<string>? progress, CancellationToken cancellationToken)
        { ManagementCalls++; return Task.FromResult(Installation(kind)); }
        public Task<BuilderToolchainInstallation> RepairAsync(BuilderToolchainInstallation installation, IProgress<string>? progress, CancellationToken cancellationToken)
        { ManagementCalls++; return Task.FromResult(installation); }
        public Task RemoveAsync(BuilderToolchainInstallation installation, IProgress<string>? progress, CancellationToken cancellationToken)
        { ManagementCalls++; return Task.CompletedTask; }
        public async Task<IBuilderProjectDriverSelection> SelectAsync(BuilderProjectKind kind, string? projectDirectory, BuilderToolchainInstallation? preferred, IProgress<string>? progress, CancellationToken cancellationToken)
        { Selections++; if (BeforeSelect is not null) await BeforeSelect(cancellationToken); return new Selection(Installation(kind)); }
        private sealed class Selection(BuilderToolchainInstallation installation) : IBuilderProjectDriverSelection
        {
            public BuilderToolchainInstallation Installation => installation;
            public BuilderLocalProcessRequest CreateProcess(IReadOnlyList<string> arguments, string workingDirectory, bool npm = false) => new(installation.ExecutablePath, arguments, workingDirectory);
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class Processes : IBuilderLocalProcessRunner
    {
        internal ConcurrentQueue<BuilderLocalProcessRequest> Requests { get; } = new();
        internal bool InstallTemplate;
        internal bool ProduceDevelopment;
        public async Task<BuilderLocalProcessResult> RunAsync(BuilderLocalProcessRequest request, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
        {
            Requests.Enqueue(request);
            if (request.Arguments[0] == "restore") return new(0, "", "");
            if (InstallTemplate && request.Arguments[0] == "new" && request.Arguments[1] is "install" or "list") return new(0, "sunder-package", "");
            string Property(string name) => Uri.UnescapeDataString(request.Arguments.Single(argument => argument.StartsWith("-p:" + name + "=", StringComparison.Ordinal)).Split('=', 2)[1]);
            if (ProduceDevelopment && request.Arguments[0] == "msbuild"
                && !request.Arguments.Any(argument => argument.StartsWith("-t:", StringComparison.Ordinal)))
            {
                var root = Path.Combine(Property("SunderPackageComposedOutputRoot"), "sunder-dev");
                var rid = BuilderToolchainPlatform.Current.DotnetRid;
                Directory.CreateDirectory(Path.Combine(root, "manifest")); Directory.CreateDirectory(Path.Combine(root, "payload/runtime", rid));
                await File.WriteAllTextAsync(Path.Combine(root, "payload/runtime", rid, "worker.js"), "worker", cancellationToken);
                await File.WriteAllTextAsync(Path.Combine(root, "manifest/sunder-package.json"), JsonSerializer.Serialize(new
                {
                    archiveFormatVersion = 1, manifestVersion = 1, id = "sample.local", version = "1.0.0",
                    targets = new[] { new { role = "runtime", rid, kind = "worker", entryPoint = "worker.js" } },
                }), cancellationToken);
                var files = Directory.GetFiles(root, "*", SearchOption.AllDirectories).Where(path => Path.GetFileName(path) != "content-index.json").Select(path => new
                {
                    path = Path.GetRelativePath(root, path).Replace('\\', '/'), size = new FileInfo(path).Length, sha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))),
                });
                await File.WriteAllTextAsync(Path.Combine(root, "manifest/content-index.json"), JsonSerializer.Serialize(new { schemaVersion = 1, files }), cancellationToken);
                return new(0, "", "");
            }
            throw new InvalidOperationException("Captured process boundary; fixture does not launch build tools.");
        }
    }
}
