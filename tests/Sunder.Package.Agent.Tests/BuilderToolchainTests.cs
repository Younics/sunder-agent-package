extern alias BuilderWorker;

using System.Collections.Concurrent;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BuilderWorker::Sunder.Package.Agent.Builder;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class BuilderToolchainTests
{
    [Theory]
    [InlineData(BuilderProjectKind.Dotnet)]
    [InlineData(BuilderProjectKind.Node)]
    public async Task SanitizedWorkerChildPathKeepsSelectedSdkFirstAndCanResolveOperatingSystemShell(BuilderProjectKind kind)
    {
        using var fixture = new Fixture();
        using var service = fixture.Service();
        var systemRoot = Environment.GetEnvironmentVariable("SystemRoot") ?? Environment.GetEnvironmentVariable("WINDIR");
        var installation = new BuilderToolchainInstallation { Kind = kind, Version = kind == BuilderProjectKind.Dotnet ? "10.0.100" : "24.18.1",
            RootPath = fixture.PathOf("selected"), ExecutablePath = fixture.PathOf("selected/bin/" + (kind == BuilderProjectKind.Dotnet ? "dotnet" : "node")) };
        var variables = new Dictionary<string, string?>
        {
            ["SystemRoot"] = systemRoot, ["PATH"] = null, ["NODE_OPTIONS"] = "--require untrusted-hook",
            ["DOTNET_ROOT"] = fixture.PathOf("wrong-sdk"), ["UNRELATED_HOST_SECRET"] = "must-not-be-copied",
        };
        var environment = service.CreateEnvironment(installation, key => variables.GetValueOrDefault(key), variables.Keys);
        var paths = environment["PATH"]!.Split(Path.PathSeparator);
        Assert.Equal(Path.GetDirectoryName(installation.ExecutablePath), paths[0]);
        Assert.All(paths, path => Assert.True(Path.IsPathFullyQualified(path)));
        var shell = OperatingSystem.IsWindows() ? "cmd.exe" : "sh";
        Assert.Contains(paths, path => System.IO.File.Exists(Path.Combine(path, shell)));
        Assert.Null(environment["NODE_OPTIONS"]);
        Assert.DoesNotContain("UNRELATED_HOST_SECRET", environment.Keys);
        Assert.Null(variables["PATH"]); // No parent/global environment mutation.
        var absolute = fixture.PathOf("explicit-command-tools");
        variables["PATH"] = string.Join(Path.PathSeparator, ".", "relative-tools", absolute, absolute);
        var withAmbient = service.CreateEnvironment(installation, key => variables.GetValueOrDefault(key), variables.Keys)["PATH"]!
            .Split(Path.PathSeparator);
        Assert.Equal(paths[0], withAmbient[0]);
        Assert.Equal(1, withAmbient.Count(path => path == absolute));
        Assert.All(withAmbient, path => Assert.True(Path.IsPathFullyQualified(path)));
        Assert.Contains(withAmbient, path => System.IO.File.Exists(Path.Combine(path, shell)));

        // Execute from the constructed environment, not the test runner's PATH.
        // This protects .NET Exec and npm's shell lookup in real sanitized Workers.
        var start = new System.Diagnostics.ProcessStartInfo(OperatingSystem.IsWindows()
            ? Path.Combine(systemRoot!, "System32", "cmd.exe") : "/bin/sh")
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment.Clear();
        if (OperatingSystem.IsWindows()) start.Environment["SystemRoot"] = systemRoot;
        foreach (var (key, value) in environment) if (value is not null) start.Environment[key] = value;
        foreach (var argument in OperatingSystem.IsWindows()
            ? new[] { "/d", "/c", "cmd /d /c echo builder-shell-ready" }
            : new[] { "-c", "exec sh -c 'printf builder-shell-ready'" }) start.ArgumentList.Add(argument);
        using var child = System.Diagnostics.Process.Start(start)!;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            var output = await child.StandardOutput.ReadToEndAsync(deadline.Token);
            var error = await child.StandardError.ReadToEndAsync(deadline.Token);
            await child.WaitForExitAsync(deadline.Token);
            Assert.True(child.ExitCode == 0, error);
            Assert.Equal("builder-shell-ready", output.Trim());
        }
        finally { if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); } }
    }

    [Fact]
    public async Task OfficialCatalog_SelectsSdkArchivesAndStableLtsWithBundledNpm()
    {
        var platform = new BuilderToolchainPlatform("linux-x64", "linux-x64");
        var transport = new MetadataTransport();
        object File(string version) => new { name = "dotnet-sdk-linux-x64.tar.gz", rid = "linux-x64",
            url = $"https://builds.dotnet.microsoft.com/dotnet/Sdk/{version}/dotnet-sdk-{version}-linux-x64.tar.gz", hash = new string('a', 128) };
        transport.Add(BuilderToolchainPolicy.DotnetMetadataUri, new Dictionary<string, object>
        {
            ["channel-version"] = "10.0", ["release-type"] = "lts", ["support-phase"] = "active",
            ["releases"] = new[] { new { runtime = new { version = "10.0.999" }, sdks = new[]
            {
                new { version = "10.0.113", files = new[] { File("10.0.113") } },
                new { version = "10.0.402", files = new[] { File("10.0.402") } },
                new { version = "10.0.500-preview.1", files = Array.Empty<object>() },
            } } },
        });
        object Node(string version, object lts, string npm = "11.19.1") => new { version = "v" + version, lts, npm, files = new[] { "linux-x64" } };
        transport.Add(BuilderToolchainPolicy.NodeMetadataUri, new[]
        {
            Node("26.12.0", "future"), Node("24.18.0", "Krypton"), Node("24.22.0", "Krypton"),
            Node("24.23.0", false), Node("24.24.0", "Krypton", ""), Node("24.18.1", "Krypton"),
        });
        var catalog = new BuilderOfficialToolchainCatalog(transport, platform);
        var dotnet = await catalog.GetReleasesAsync(BuilderProjectKind.Dotnet, default);
        Assert.Equal(new[] { "10.0.402", "10.0.113" }, dotnet.Select(item => item.Version));
        Assert.All(dotnet, item => Assert.Equal("SHA512", item.HashAlgorithm));
        var node = await catalog.GetReleasesAsync(BuilderProjectKind.Node, default);
        Assert.Equal(new[] { "24.22.0", "24.18.1" }, node.Select(item => item.Version));
        transport.Bytes["https://nodejs.org/dist/v24.22.0/SHASUMS256.txt"] = Encoding.UTF8.GetBytes(
            new string('b', 64) + "  node-v24.22.0-linux-x64.tar.gz\n" + new string('c', 64) + "  node-v24.22.0.tar.gz\n");
        var archive = await catalog.ResolveArchiveAsync(node[0], default);
        Assert.Equal(new string('b', 64), archive.Checksum);
        Assert.Equal("node-v24.22.0-linux-x64", archive.ArchiveRoot);
    }

    [Theory]
    [InlineData("http://nodejs.org/dist/index.json")]
    [InlineData("https://token@nodejs.org/dist/index.json")]
    [InlineData("https://nodejs.org.evil.test/dist/index.json")]
    [InlineData("https://nodejs.org/dist/index.json?token=secret")]
    [InlineData("https://nodejs.org/dist/v24.22.0/node-v24.22.0.tar.gz")]
    [InlineData("https://nodejs.org/dist/%69ndex.json")]
    [InlineData("https://nodejs.org:444/dist/index.json")]
    [InlineData("https://builds.dotnet.microsoft.com/dotnet/Runtime/10.0.13/dotnet-runtime-10.0.13-linux-x64.tar.gz")]
    public void DownloadPolicyRejectsCredentialsOtherOriginsAndRuntimeOnlyArchives(string uri)
        => Assert.Throws<InvalidDataException>(() => BuilderToolchainHttpTransport.ValidateUri(new Uri(uri)));

    [Theory]
    [InlineData("../escape")]
    [InlineData("/absolute")]
    [InlineData("C:/escape")]
    [InlineData("sub\\escape")]
    [InlineData("sub/../escape")]
    [InlineData("sub/CON.txt")]
    [InlineData("sub/name. ")]
    public async Task ExtractionRejectsTraversalAndPortablePathAliases(string name)
    {
        using var fixture = new Fixture();
        var archive = fixture.PathOf("bad.zip");
        await System.IO.File.WriteAllBytesAsync(archive, Zip((name, "payload")));
        await Assert.ThrowsAsync<InvalidDataException>(() => BuilderToolchainArchive.ExtractAsync(archive, fixture.PathOf("extract"), null));
        Assert.False(System.IO.File.Exists(fixture.PathOf("escape")));
    }

    [Fact]
    public async Task ExtractionRejectsImplicitDirectoryCaseCollision()
    {
        using var fixture = new Fixture();
        var archive = fixture.PathOf("bad.zip");
        await System.IO.File.WriteAllBytesAsync(archive, Zip(("Upper/one", "one"), ("upper/two", "two")));
        await Assert.ThrowsAsync<InvalidDataException>(() => BuilderToolchainArchive.ExtractAsync(archive, fixture.PathOf("extract"), null));
    }

    [Theory]
    [InlineData("../../escape")]
    [InlineData("/tmp/escape")]
    [InlineData("npm")]
    public async Task ExtractionRejectsEscapingAndCyclicLinks(string target)
    {
        using var fixture = new Fixture();
        var archive = fixture.PathOf("bad.tar.gz");
        await System.IO.File.WriteAllBytesAsync(archive, Tar([("bin/node", "node")], [("bin/npm", target)]));
        await Assert.ThrowsAsync<InvalidDataException>(() => BuilderToolchainArchive.ExtractAsync(archive, fixture.PathOf("extract"), null));
    }

    [Fact]
    public async Task FullNodeInstallKeepsNpmLinksAndPinsAncestorVersionSideBySide()
    {
        using var fixture = new Fixture();
        fixture.AddNode("24.18.1", "11.16.0");
        fixture.AddNode("24.22.0", "11.19.1");
        var project = fixture.Project("repo/child");
        await System.IO.File.WriteAllTextAsync(fixture.PathOf("repo/.nvmrc"), "v24.18.1\n");
        await System.IO.File.WriteAllTextAsync(Path.Combine(project, "package.json"), "{\"engines\":{\"node\":\">=24.18.1 <25\",\"npm\":\"^11\"}}");
        using var service = fixture.Service();
        var pinned = await service.EnsureAsync(BuilderProjectKind.Node, project);
        var latest = await service.EnsureAsync(BuilderProjectKind.Node);
        Assert.Equal("24.18.1", pinned.Version);
        Assert.Equal("24.22.0", latest.Version);
        Assert.NotEqual(pinned.RootPath, latest.RootPath);
        Assert.True(latest.IsHealthy);
        Assert.Equal("11.19.1", latest.NpmVersion);
        Assert.Equal(2, (await service.ListInstalledAsync(BuilderProjectKind.Node)).Count);
        if (!OperatingSystem.IsWindows())
        {
            var npmLink = new FileInfo(Path.Combine(latest.RootPath, "bin", "npm"));
            Assert.NotNull(npmLink.LinkTarget);
            Assert.Equal(latest.NpmCliPath, npmLink.ResolveLinkTarget(true)!.FullName);
        }
        await using var selected = await service.AcquireLeaseAsync(latest);
        var request = selected.CreateProcess(["run", "build"], project, npm: true);
        Assert.Equal(latest.ExecutablePath, request.ExecutablePath);
        Assert.Equal(new[] { latest.NpmCliPath!, "run", "build" }, request.Arguments);
        Assert.StartsWith(Path.GetDirectoryName(latest.ExecutablePath)!, request.Environment["PATH"]);
        Assert.StartsWith(fixture.ManagedRoot, request.Environment["NPM_CONFIG_CACHE"]);
        Assert.Null(request.Environment["NODE_OPTIONS"]);
    }

    [Fact]
    public async Task ExistingGlobalJsonUsesActualProjectSelectedSdkAndNewCreatesLatest()
    {
        using var fixture = new Fixture();
        fixture.AddDotnet("10.0.100");
        fixture.AddDotnet("10.0.402");
        var project = fixture.Project("repo/child");
        var global = fixture.PathOf("repo/global.json");
        const string json = "{\"sdk\":{\"version\":\"10.0.100\",\"rollForward\":\"disable\"}}";
        await System.IO.File.WriteAllTextAsync(global, json);
        var external = fixture.ExternalDotnet("external", "10.0.100", "10.0.402");
        fixture.Process.ProjectDirectory = project;
        fixture.Process.ExternalSelectedVersion[external] = "10.0.402";
        using var service = fixture.Service(_ => [external]);
        var selected = await service.EnsureAsync(BuilderProjectKind.Dotnet, project);
        Assert.True(selected.IsManaged);
        Assert.Equal("10.0.100", selected.Version);
        Assert.Equal(json, await System.IO.File.ReadAllTextAsync(global));
        var latest = await service.EnsureAsync(BuilderProjectKind.Dotnet);
        Assert.Equal("10.0.402", latest.Version);
        Assert.Contains(fixture.Process.Requests, request => request.ExecutablePath == external && request.Arguments.SequenceEqual(["--list-sdks"]));
        Assert.Contains(fixture.Process.Requests, request => request.ExecutablePath == external && request.WorkingDirectory == project && request.Arguments.SequenceEqual(["--version"]));
    }

    [Theory]
    [InlineData("disable", "10.0.100")]
    [InlineData("patch", "10.0.100")]
    [InlineData("latestPatch", "10.0.113")]
    [InlineData("feature", "10.0.113")]
    [InlineData("latestFeature", "10.0.402")]
    public async Task SdkCatalogSelectionRespectsFeatureBandAndRollForward(string policy, string expected)
    {
        using var fixture = new Fixture();
        foreach (var version in new[] { "10.0.100", "10.0.113", "10.0.402" }) fixture.AddDotnet(version);
        var project = fixture.Project("project");
        await System.IO.File.WriteAllTextAsync(Path.Combine(project, "global.json"),
            JsonSerializer.Serialize(new { sdk = new { version = "10.0.100", rollForward = policy } }));
        fixture.Process.ProjectDirectory = project;
        foreach (var release in fixture.Releases)
        {
            var executable = Path.Combine(fixture.ManagedRoot, "installed", "dotnet-sdk", fixture.Platform.DotnetRid, release.Version,
                OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
            fixture.Process.ExternalSelectedVersion[executable] = release.Version;
        }
        using var service = fixture.Service();
        Assert.Equal(expected, (await service.EnsureAsync(BuilderProjectKind.Dotnet, project)).Version);
    }

    [Fact]
    public async Task InheritedAsdfPinSurvivesCloserConfigurationForAnotherTool()
    {
        using var fixture = new Fixture();
        fixture.AddNode("24.18.1", "11.16.0");
        fixture.AddNode("24.22.0", "11.19.1");
        var project = fixture.Project("repo/project");
        await System.IO.File.WriteAllTextAsync(fixture.PathOf("repo/.tool-versions"), "nodejs 24.18.1\n");
        await System.IO.File.WriteAllTextAsync(Path.Combine(project, ".tool-versions"), "python 3.14.0\n");
        using var service = fixture.Service();
        Assert.Equal("24.18.1", (await service.EnsureAsync(BuilderProjectKind.Node, project)).Version);
    }

    [Fact]
    public async Task UnsupportedPinsFailBeforeCatalogOrProcessWork()
    {
        using var fixture = new Fixture();
        var project = fixture.Project("project");
        await System.IO.File.WriteAllTextAsync(Path.Combine(project, "global.json"), "{\"sdk\":{\"version\":\"9.0.100\"}}");
        using var service = fixture.Service();
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.EnsureAsync(BuilderProjectKind.Dotnet, project));
        await System.IO.File.WriteAllTextAsync(Path.Combine(project, ".nvmrc"), "node\n");
        await Assert.ThrowsAsync<InvalidDataException>(() => service.EnsureAsync(BuilderProjectKind.Node, project));
        Assert.Equal(0, fixture.CatalogCalls);
        Assert.Empty(fixture.Process.Requests);
    }

    [Theory]
    [InlineData(">=24.18.1 <25", "24.22.0")]
    [InlineData("~24.18.1", "24.18.1")]
    [InlineData("24.18.x", "24.18.1")]
    [InlineData("24.18.1 - 24.20", "24.18.1")]
    [InlineData("^22 || ^24.22.0", "24.22.0")]
    [InlineData("<=24.18.1", "24.18.1")]
    public async Task NodeEngineRangesAreHonored(string range, string expected)
    {
        using var fixture = new Fixture();
        fixture.AddNode("24.18.1", "11.16.0");
        fixture.AddNode("24.22.0", "11.19.1");
        var project = fixture.Project("project");
        await System.IO.File.WriteAllTextAsync(Path.Combine(project, "package.json"), JsonSerializer.Serialize(new { engines = new { node = range } }));
        using var service = fixture.Service();
        Assert.Equal(expected, (await service.EnsureAsync(BuilderProjectKind.Node, project)).Version);
    }

    [Fact]
    public async Task ConflictingPinsFailWithoutDownloadAndNpmPinSelectsBundledVersion()
    {
        using var fixture = new Fixture();
        fixture.AddNode("24.18.1", "11.16.0");
        fixture.AddNode("24.22.0", "11.19.1");
        var project = fixture.Project("project");
        await System.IO.File.WriteAllTextAsync(Path.Combine(project, ".node-version"), "24.18.1");
        await System.IO.File.WriteAllTextAsync(Path.Combine(project, "package.json"), "{\"engines\":{\"node\":\">=24.22.0\"}}");
        using var service = fixture.Service();
        await Assert.ThrowsAsync<BuilderToolchainException>(() => service.EnsureAsync(BuilderProjectKind.Node, project));
        Assert.Equal(0, fixture.Downloads);
        System.IO.File.Delete(Path.Combine(project, ".node-version"));
        await System.IO.File.WriteAllTextAsync(Path.Combine(project, "package.json"), "{\"packageManager\":\"npm@11.16.0\"}");
        Assert.Equal("24.18.1", (await service.EnsureAsync(BuilderProjectKind.Node, project)).Version);
    }

    [Fact]
    public async Task ChecksumFailureAndCancellationNeverCreateReadyInstallationsAndAreRetryable()
    {
        using var fixture = new Fixture();
        fixture.AddNode("24.22.0", "11.19.1");
        using var service = fixture.Service();
        fixture.CorruptDownload = true;
        var error = await Assert.ThrowsAsync<BuilderToolchainException>(() => service.EnsureAsync(BuilderProjectKind.Node));
        Assert.Equal("checksum", error.Phase);
        Assert.Empty(fixture.Process.Requests);
        Assert.Empty(await service.ListInstalledAsync(BuilderProjectKind.Node));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(fixture.ManagedRoot, "staging")));
        fixture.CorruptDownload = false;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.OnDownload = async token => { started.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); };
        using var cancellation = new CancellationTokenSource();
        var pending = service.EnsureAsync(BuilderProjectKind.Node, cancellationToken: cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Empty(await service.ListInstalledAsync(BuilderProjectKind.Node));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(fixture.ManagedRoot, "staging")));
        Assert.Equal("cancelled", (await service.GetStatusAsync(BuilderProjectKind.Node, false)).LastOperation!.Outcome);
        fixture.OnDownload = null;
        Assert.True((await service.EnsureAsync(BuilderProjectKind.Node)).IsHealthy);
    }

    [Fact]
    public async Task ConcurrentServicesInstallOnceAndLeaseBlocksRepairAndRemoval()
    {
        using var fixture = new Fixture();
        fixture.AddNode("24.22.0", "11.19.1");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.OnDownload = async token => { started.TrySetResult(); await finish.Task.WaitAsync(token); };
        using var first = fixture.Service();
        using var second = fixture.Service();
        var one = first.EnsureAsync(BuilderProjectKind.Node);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var two = second.EnsureAsync(BuilderProjectKind.Node);
        finish.SetResult();
        var installations = await Task.WhenAll(one, two);
        Assert.Equal(1, fixture.Downloads);
        Assert.Equal(installations[0].RootPath, installations[1].RootPath);
        await Assert.ThrowsAsync<InvalidOperationException>(() => first.AcquireLeaseAsync(installations[0] with { IsManaged = false }));
        using (var lease = await first.AcquireLeaseAsync(installations[0]))
        {
            await Assert.ThrowsAsync<BuilderToolchainException>(() => second.RemoveAsync(installations[1]));
            await Assert.ThrowsAsync<BuilderToolchainException>(() => second.RepairAsync(installations[1]));
            Assert.True(System.IO.File.Exists(lease.Installation.NpmCliPath));
        }
        await second.RemoveAsync(installations[1]);
        Assert.Empty(await first.ListInstalledAsync(BuilderProjectKind.Node));
    }

    [Fact]
    public async Task CancellingConcurrentWaiterDoesNotCancelTheInstallingCaller()
    {
        using var fixture = new Fixture();
        fixture.AddNode("24.22.0", "11.19.1");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.OnDownload = async token => { started.TrySetResult(); await finish.Task.WaitAsync(token); };
        using var first = fixture.Service();
        using var second = fixture.Service();
        var installing = first.EnsureAsync(BuilderProjectKind.Node);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource();
        var waiting = second.EnsureAsync(BuilderProjectKind.Node, cancellationToken: cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.False(installing.IsCompleted);
        finish.SetResult();
        Assert.True((await installing).IsHealthy);
        Assert.Equal(1, fixture.Downloads);
    }

    [Fact]
    public async Task FailedRepairPreservesOldInstallAndSuccessfulRepairRestoresMissingNpm()
    {
        using var fixture = new Fixture();
        fixture.AddNode("24.22.0", "11.19.1");
        using var service = fixture.Service();
        var installed = await service.EnsureAsync(BuilderProjectKind.Node);
        fixture.Process.Fail = true;
        var error = await Assert.ThrowsAsync<BuilderToolchainException>(() => service.RepairAsync(installed));
        Assert.Equal("verification", error.Phase);
        Assert.True(System.IO.File.Exists(installed.NpmCliPath));
        fixture.Process.Fail = false;
        System.IO.File.Delete(installed.NpmCliPath!);
        Assert.False(Assert.Single(await service.ListInstalledAsync(BuilderProjectKind.Node)).IsHealthy);
        Assert.True((await service.RepairAsync(installed)).IsHealthy);
        Assert.True(System.IO.File.Exists(installed.NpmCliPath));
    }

    [Fact]
    public async Task CorruptReceiptIsUnhealthyAndRepairableRatherThanReadyOrFatal()
    {
        using var fixture = new Fixture();
        fixture.AddNode("24.22.0", "11.19.1");
        using var service = fixture.Service();
        var installation = await service.EnsureAsync(BuilderProjectKind.Node);
        await System.IO.File.WriteAllTextAsync(Path.Combine(installation.RootPath, "builder-toolchain-receipt.json"), "{\"Schema\":1,\"Release\":null}");
        var unhealthy = Assert.Single(await service.ListInstalledAsync(BuilderProjectKind.Node));
        Assert.False(unhealthy.IsHealthy);
        Assert.NotNull(unhealthy.HealthError);
        await Assert.ThrowsAsync<BuilderToolchainException>(() => service.AcquireLeaseAsync(unhealthy));
        Assert.True((await service.RepairAsync(unhealthy)).IsHealthy);
    }

    [Fact]
    public async Task PersistedOfflineInstallRemainsUsableWithUnknownUpdateState()
    {
        using var fixture = new Fixture();
        fixture.AddNode("24.22.0", "11.19.1");
        using (var initial = fixture.Service()) await initial.EnsureAsync(BuilderProjectKind.Node);
        fixture.Offline = true;
        using var restarted = fixture.Service();
        var status = await restarted.GetStatusAsync(BuilderProjectKind.Node, true);
        Assert.True(Assert.Single(status.Installed).IsHealthy);
        Assert.Equal("24.22.0", status.RecommendedVersion);
        Assert.NotNull(status.LastSuccessfulCheckAtUtc);
        Assert.NotNull(status.LastCheckedAtUtc);
        Assert.NotNull(status.CatalogError);
        Assert.Null(status.UpdateAvailable);
        await using var selected = await restarted.EnsureSelectedAsync(BuilderProjectKind.Node);
        Assert.Equal("24.22.0", selected.Installation.Version);
        Assert.Equal(1, fixture.Downloads);
    }

    [Fact]
    public async Task ExternalToolchainsAndForgedOwnedPathsAreNeverModified()
    {
        using var fixture = new Fixture();
        var external = fixture.ExternalDotnet("external", "10.0.100");
        using var service = fixture.Service(_ => [external]);
        var installation = Assert.Single(await service.ListInstalledAsync(BuilderProjectKind.Dotnet));
        Assert.False(installation.IsManaged);
        Assert.True(installation.IsHealthy);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RemoveAsync(installation));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RepairAsync(installation));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RemoveAsync(installation with { IsManaged = true }));
        Assert.True(System.IO.File.Exists(external));
        Assert.Equal(0, fixture.CatalogCalls);
    }

    [Fact]
    public async Task NodeVersionManagerShimResolvesToActualNodeAndVerifiedNpm()
    {
        using var fixture = new Fixture();
        var bin = fixture.Project("external/bin");
        var executable = Path.Combine(bin, OperatingSystem.IsWindows() ? "node.exe" : "node");
        var npm = Path.Combine(fixture.Project("external/lib/node_modules/npm/bin"), "npm-cli.js");
        var shim = Path.Combine(fixture.Project("shim"), OperatingSystem.IsWindows() ? "node.exe" : "node");
        await System.IO.File.WriteAllTextAsync(executable, "24.22.0");
        await System.IO.File.WriteAllTextAsync(shim, "24.22.0");
        await System.IO.File.WriteAllTextAsync(npm, "11.19.1");
        fixture.Process.ResolvedExecutable[shim] = executable;
        using var service = fixture.Service(_ => [shim]);
        var installed = Assert.Single(await service.ListInstalledAsync(BuilderProjectKind.Node));
        Assert.Equal(executable, installed.ExecutablePath);
        Assert.Equal(npm, installed.NpmCliPath);
        Assert.Equal("11.19.1", installed.NpmVersion);
        Assert.True(installed.IsHealthy);
        Assert.False(installed.IsManaged);
    }

    [UnixFact("The local process cancellation fixture requires /bin/sh.")]
    public async Task LocalProcessCancellationStopsProcessAndBoundedOutputKeepsFailureTail()
    {
        using var fixture = new Fixture();
        var runner = new BuilderLocalProcessRunner();
        var output = await runner.RunAsync(new("/bin/sh", ["-c", "printf '%8192s' x; printf terminal-error >&2; exit 3"], fixture.Root)
        { MaximumCapturedCharacters = 128 });
        Assert.Equal(3, output.ExitCode);
        Assert.EndsWith("x", output.StandardOutput);
        Assert.Contains("truncated", output.StandardOutput);
        Assert.Equal("terminal-error", output.StandardError);
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = runner.RunAsync(new("/bin/sh", ["-c", "printf ready; sleep 30 & wait"], fixture.Root),
            new InlineProgress(value => { if (value.Contains("ready", StringComparison.Ordinal)) started.TrySetResult(); }), cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    private sealed class InlineProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }

    private sealed class MetadataTransport : IBuilderToolchainTransport
    {
        internal Dictionary<string, byte[]> Bytes { get; } = [];
        internal void Add(Uri uri, object value) => Bytes[uri.ToString()] = JsonSerializer.SerializeToUtf8Bytes(value);
        public async Task CopyToAsync(Uri uri, Stream destination, long maximumBytes, IProgress<string>? progress, CancellationToken cancellationToken)
            => await destination.WriteAsync(Bytes[uri.ToString()], cancellationToken);
    }

    private sealed class Fixture : IBuilderToolchainCatalog, IBuilderToolchainTransport, IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "sunder-builder-toolchains", Guid.NewGuid().ToString("N"));
        internal string ManagedRoot => PathOf("private-toolchains");
        internal FakeProcess Process { get; } = new();
        internal BuilderToolchainPlatform Platform { get; } = BuilderToolchainPlatform.Current;
        internal List<BuilderToolchainRelease> Releases { get; } = [];
        private readonly Dictionary<string, byte[]> archives = [];
        internal int Downloads;
        internal int CatalogCalls;
        internal bool Offline;
        internal bool CorruptDownload;
        internal Func<CancellationToken, Task>? OnDownload;
        internal Fixture() => Directory.CreateDirectory(Root);
        internal string PathOf(string path) => Path.Combine(Root, path);
        internal string Project(string path) { var full = PathOf(path); Directory.CreateDirectory(full); return full; }
        internal BuilderToolchainService Service(Func<BuilderProjectKind, IReadOnlyList<string>>? candidates = null)
            => new(ManagedRoot, this, this, Process, candidates ?? (_ => []), platform: Platform);

        internal void AddNode(string version, string npm)
        {
            var platform = Platform.NodePlatform;
            var prefix = $"node-v{version}-{platform}";
            var files = OperatingSystem.IsWindows()
                ? new[] { ($"{prefix}/node.exe", version), ($"{prefix}/node_modules/npm/bin/npm-cli.js", npm) }
                : new[] { ($"{prefix}/bin/node", version), ($"{prefix}/lib/node_modules/npm/bin/npm-cli.js", npm) };
            var bytes = OperatingSystem.IsWindows() ? Zip(files) : Tar(files, [($"{prefix}/bin/npm", "../lib/node_modules/npm/bin/npm-cli.js")]);
            var suffix = OperatingSystem.IsWindows() ? ".zip" : ".tar.gz";
            var release = new BuilderToolchainRelease(BuilderProjectKind.Node, version, platform,
                new Uri($"https://nodejs.org/dist/v{version}/{prefix}{suffix}"), "SHA256", Convert.ToHexString(SHA256.HashData(bytes)), prefix, npm);
            Releases.Add(release);
            archives.Add(release.DownloadUri.ToString(), bytes);
        }

        internal void AddDotnet(string version)
        {
            var platform = Platform.DotnetRid;
            var files = new[] { (OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet", version), ($"sdk/{version}/dotnet.dll", "sdk") };
            var bytes = OperatingSystem.IsWindows() ? Zip(files) : Tar(files, []);
            var suffix = OperatingSystem.IsWindows() ? ".zip" : ".tar.gz";
            var release = new BuilderToolchainRelease(BuilderProjectKind.Dotnet, version, platform,
                new Uri($"https://builds.dotnet.microsoft.com/dotnet/Sdk/{version}/dotnet-sdk-{version}-{platform}{suffix}"),
                "SHA512", Convert.ToHexString(SHA512.HashData(bytes)), null);
            Releases.Add(release);
            archives.Add(release.DownloadUri.ToString(), bytes);
        }

        internal string ExternalDotnet(string directory, params string[] versions)
        {
            var root = Project(directory);
            var executable = Path.Combine(root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
            System.IO.File.WriteAllText(executable, versions[^1]);
            foreach (var version in versions) Directory.CreateDirectory(Path.Combine(root, "sdk", version));
            return executable;
        }

        public Task<IReadOnlyList<BuilderToolchainRelease>> GetReleasesAsync(BuilderProjectKind kind, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref CatalogCalls);
            cancellationToken.ThrowIfCancellationRequested();
            if (Offline) throw new HttpRequestException("Fixture offline.");
            return Task.FromResult<IReadOnlyList<BuilderToolchainRelease>>(Releases.Where(item => item.Kind == kind).ToArray());
        }

        public Task<BuilderToolchainRelease> ResolveArchiveAsync(BuilderToolchainRelease release, CancellationToken cancellationToken)
            => Task.FromResult(release);

        public async Task CopyToAsync(Uri uri, Stream destination, long maximumBytes, IProgress<string>? progress, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Downloads);
            var bytes = archives[uri.ToString()].ToArray();
            if (CorruptDownload) bytes[0] ^= 0x7F;
            await destination.WriteAsync(bytes.AsMemory(0, 1), cancellationToken);
            if (OnDownload is not null) await OnDownload(cancellationToken);
            await destination.WriteAsync(bytes.AsMemory(1), cancellationToken);
        }

        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
    }

    private sealed class FakeProcess : IBuilderLocalProcessRunner
    {
        internal ConcurrentQueue<BuilderLocalProcessRequest> Requests { get; } = new();
        internal bool Fail;
        internal string? ProjectDirectory;
        internal Dictionary<string, string> ExternalSelectedVersion { get; } = [];
        internal Dictionary<string, string> ResolvedExecutable { get; } = [];

        public Task<BuilderLocalProcessResult> RunAsync(BuilderLocalProcessRequest request, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Enqueue(request);
            if (Fail) return Task.FromResult(new BuilderLocalProcessResult(1, "", "Fixture native dependency missing."));
            var executable = request.ExecutablePath;
            var dotnet = Path.GetFileNameWithoutExtension(executable) == "dotnet";
            var output = System.IO.File.ReadAllText(executable);
            if (request.Arguments.SequenceEqual(["--list-sdks"]))
            {
                var sdkPath = Path.Combine(Path.GetDirectoryName(executable)!, "sdk");
                output = string.Join('\n', Directory.EnumerateDirectories(sdkPath).Select(path => $"{Path.GetFileName(path)} [{sdkPath}]"));
            }
            else if (request.Arguments.SequenceEqual(["--print", "process.execPath"])) output = ResolvedExecutable.GetValueOrDefault(executable, executable);
            else if (request.Arguments.Count == 2) output = System.IO.File.ReadAllText(request.Arguments[0]);
            else if (dotnet)
            {
                if (request.WorkingDirectory == ProjectDirectory && ExternalSelectedVersion.TryGetValue(executable, out var selected)) output = selected;
                else
                {
                    for (var directory = new DirectoryInfo(request.WorkingDirectory); directory is not null; directory = directory.Parent)
                    {
                        var path = Path.Combine(directory.FullName, "global.json");
                        if (!System.IO.File.Exists(path)) continue;
                        using var json = JsonDocument.Parse(System.IO.File.ReadAllText(path));
                        output = json.RootElement.GetProperty("sdk").GetProperty("version").GetString()!;
                        if (!Directory.Exists(Path.Combine(Path.GetDirectoryName(executable)!, "sdk", output)))
                            return Task.FromResult(new BuilderLocalProcessResult(1, "", "SDK not found."));
                        break;
                    }
                }
            }
            else output = "v" + output;
            return Task.FromResult(new BuilderLocalProcessResult(0, output + "\n", ""));
        }
    }

    private static byte[] Zip(params (string Path, string Content)[] files)
    {
        using var memory = new MemoryStream();
        using (var writer = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (path, content) in files)
            {
                using var output = new StreamWriter(writer.CreateEntry(path).Open());
                output.Write(content);
            }
        return memory.ToArray();
    }

    private static byte[] Tar((string Path, string Content)[] files, (string Path, string Target)[] links)
    {
        using var memory = new MemoryStream();
        using (var gzip = new GZipStream(memory, CompressionMode.Compress, leaveOpen: true))
        using (var writer = new TarWriter(gzip, leaveOpen: true))
        {
            foreach (var (path, content) in files)
            {
                using var source = new MemoryStream(Encoding.UTF8.GetBytes(content));
                writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, path)
                {
                    DataStream = source,
                    Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                });
            }
            foreach (var (path, target) in links) writer.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, path) { LinkName = target });
        }
        return memory.ToArray();
    }
}
