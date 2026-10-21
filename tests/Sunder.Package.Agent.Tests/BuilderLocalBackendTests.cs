extern alias BuilderWorker;

using System.Collections.Concurrent;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BuilderWorker::Sunder.Package.Agent.Builder;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class BuilderLocalBackendTests
{
    [Fact]
    public void PrivateRpcSurfaceIsLocalAndFinite()
    {
        var methods = typeof(BuilderRuntimeService).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(method => method.CustomAttributes.Any(attribute => attribute.AttributeType.Name == "SunderMethodAttribute"));
        Assert.Equal(new[] { "CancelJobAsync", "DiscoverAsync", "GetBackendIdAsync", "GetJobAsync", "GetToolchainsAsync", "InspectAsync", "StartJobAsync" },
            methods.Select(method => method.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task BackendIdentityIsColdStableAndFencesEveryJobOperationOnReplacement()
    {
        await using var first = new BuilderRuntimeService(null!);
        await using var replacement = new BuilderRuntimeService(null!);
        var identity = await first.GetBackendIdAsync();
        Assert.True(Guid.TryParseExact(identity, "N", out _));
        Assert.Equal(identity, await first.GetBackendIdAsync());
        Assert.NotEqual(identity, await replacement.GetBackendIdAsync());
        // Null context deliberately proves rejection precedes constructing tools,
        // reading another backend's jobs or mutating its project/process state.
        var start = await Assert.ThrowsAsync<SunderRpcException>(() => replacement.StartJobAsync(
            JsonSerializer.Serialize(new BuilderJobRequest { Id = "exact-job", BackendId = identity,
                Operation = "ensure-sdk", Kind = BuilderProjectKind.Dotnet }, new JsonSerializerOptions(JsonSerializerDefaults.Web))).AsTask());
        var read = await Assert.ThrowsAsync<SunderRpcException>(() => replacement.GetJobAsync("exact-job", identity).AsTask());
        var cancel = await Assert.ThrowsAsync<SunderRpcException>(() => replacement.CancelJobAsync("exact-job", identity).AsTask());
        Assert.All(new[] { start, read, cancel }, error => Assert.Equal("agent.builder.backend-changed", error.Error.Code));
    }

    [Theory]
    [InlineData("{\"operation\":\"ensure-sdk\",\"operation\":\"create\",\"kind\":1}", "agent.builder.invalid-request")]
    [InlineData("{\"operation\":\"ensure-sdk\",\"kind\":1,\"unexpected\":true}", "agent.builder.invalid-request")]
    public async Task RpcRejectsAmbiguousOrUnknownJobJsonBeforeStartingBackend(string json, string expected)
    {
        await using var service = new BuilderRuntimeService(null!);
        var error = await Assert.ThrowsAsync<SunderRpcException>(() => service.StartJobAsync(json).AsTask());
        Assert.Equal(expected, error.Error.Code);
        var large = await Assert.ThrowsAsync<SunderRpcException>(() => service.StartJobAsync(new string('x', 64 * 1024)).AsTask());
        Assert.Equal("agent.builder.request-too-large", large.Error.Code);
    }

    [Fact]
    public async Task OpeningThroughRpcOnlyInspectsSourceAndNeedsNoExecutionBackend()
    {
        using var fixture = new Fixture();
        var entry = fixture.WriteDotnet("project");
        var text = await File.ReadAllTextAsync(entry);
        await File.WriteAllTextAsync(entry, text.Replace("</Project>", "<Target Name=\"BeforeBuild\"><Error Text=\"Opening must never execute this target\" /></Target></Project>", StringComparison.Ordinal));
        await using var service = new BuilderRuntimeService(null!); // any attempt to construct toolchains would fail
        await using var content = await service.InspectAsync(entry);
        await using var stream = await content.OpenReadAsync(default);
        using var json = await JsonDocument.ParseAsync(stream);
        Assert.Equal("sample.hello", json.RootElement.GetProperty("packageId").GetString());
        Assert.Equal(entry, json.RootElement.GetProperty("entryPoint").GetString());
        Assert.False(Directory.Exists(Path.Combine(Path.GetDirectoryName(entry)!, "obj")));
        await using var discovered = await service.DiscoverAsync(fixture.Root);
        await using var discoveryStream = await discovered.OpenReadAsync(default);
        using var list = await JsonDocument.ParseAsync(discoveryStream);
        Assert.Single(list.RootElement.EnumerateArray());
    }

    [Fact]
    public async Task StartRetriesAreDeduplicatedAndCancelDoesNotBlockTheCaller()
    {
        var entered = NewSignal();
        var stopped = NewSignal();
        var count = 0;
        var driver = new DelegateDriver(async (_, report, token) =>
        {
            Interlocked.Increment(ref count);
            report.Report("live output\n"); entered.SetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { stopped.SetResult(); }
        });
        await using var jobs = new BuilderJobManager(driver);
        var request = Request("ensure-sdk");
        var id = jobs.Start(request);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(id, jobs.Start(request));
        Assert.Equal(1, count);
        Assert.Throws<ArgumentException>(() => jobs.Start(request with { Operation = "repair-sdk", Installation = Installation(BuilderProjectKind.Node, Path.GetTempPath()) }));
        Assert.Equal("running", jobs.GetJob(id).State);
        Assert.Contains("live output", jobs.GetJob(id).Output);
        await jobs.CancelAsync(id);
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("cancelled", (await TerminalAsync(jobs, id)).State);
    }

    [Theory]
    [InlineData("create")]
    [InlineData("setup")]
    public async Task CancelledSetupReportsCancellationWithoutLosingSavedSource(string operation)
    {
        using var fixture = new Fixture();
        var entry = fixture.WriteDotnet("project");
        var project = await new BuilderProjectDiscovery().InspectAsync(entry);
        var entered = NewSignal();
        var driver = new DelegateDriver(async (_, report, token) =>
        {
            report.Update(new() { Project = project, Message = "Building the current-platform development package." });
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        });
        await using var jobs = new BuilderJobManager(driver);
        var request = operation == "create"
            ? Request(operation, kind: BuilderProjectKind.Dotnet) with
            { ProjectFolder = fixture.PathOf("created"), DisplayName = "Hello", PackageId = "sample.hello" }
            : Request(operation, entry, BuilderProjectKind.Dotnet);
        var id = jobs.Start(request);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await jobs.CancelAsync(id);
        var result = await TerminalAsync(jobs, id);
        Assert.Equal("cancelled", result.State);
        Assert.Contains("cancelled", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("source is saved", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("setup is incomplete", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(project, result.Project);
        Assert.Null(result.DevelopmentFolder);
    }

    [Fact]
    public async Task WatchExcludesSameProjectButAllowsIndependentFiniteWork()
    {
        using var fixture = new Fixture();
        var entered = NewSignal();
        var driver = new DelegateDriver(async (request, _, token) =>
        {
            if (request.Operation == "watch") { entered.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); }
        });
        await using var jobs = new BuilderJobManager(driver, new() { MaximumConcurrentJobs = 2 });
        var project = fixture.WriteNode("first");
        var watch = jobs.Start(Request("watch", project));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Throws<BuilderJobBusyException>(() => jobs.Start(Request("build", project)));
        var other = jobs.Start(Request("build", fixture.WriteNode("other")));
        Assert.Equal("succeeded", (await TerminalAsync(jobs, other)).State);
        await jobs.CancelAsync(watch);
        Assert.Equal("cancelled", (await TerminalAsync(jobs, watch)).State);
        Assert.Equal("succeeded", (await TerminalAsync(jobs, jobs.Start(Request("build", project)))).State);
    }

    [Fact]
    public async Task HeartbeatsRenewWatchLeaseAndOrphansStop()
    {
        var entered = NewSignal(); var stopped = NewSignal();
        var driver = new DelegateDriver(async (_, _, token) =>
        {
            entered.SetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { stopped.SetResult(); }
        });
        using var fixture = new Fixture();
        await using var jobs = new BuilderJobManager(driver, new() { OrphanTimeout = TimeSpan.FromMilliseconds(250), HeartbeatInterval = TimeSpan.FromMilliseconds(25) });
        var id = jobs.Start(Request("watch", fixture.WriteNode("project")));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        for (var index = 0; index < 8; index++) { Assert.Equal("running", jobs.GetJob(id).State); await Task.Delay(50); }
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5)); // no polling: the lease must expire
        var final = await TerminalAsync(jobs, id);
        Assert.Equal("cancelled", final.State);
        Assert.Contains("heartbeats", final.Error);
    }

    [Fact]
    public async Task FiniteDeadlineAppliesEvenWhileClientPolls()
    {
        var driver = new DelegateDriver((_, _, token) => Task.Delay(Timeout.InfiniteTimeSpan, token));
        await using var jobs = new BuilderJobManager(driver, new()
        {
            FiniteOperationTimeout = TimeSpan.FromMilliseconds(100), HeartbeatInterval = TimeSpan.FromMilliseconds(10),
            OrphanTimeout = TimeSpan.FromSeconds(2),
        });
        var final = await TerminalAsync(jobs, jobs.Start(Request("ensure-sdk")));
        Assert.Equal("cancelled", final.State);
        Assert.Contains("deadline", final.Error);
    }

    [Fact]
    public async Task DisposalWaitsForDriverCleanupBeforeReturning()
    {
        var entered = NewSignal(); var cancelled = NewSignal(); var cleanup = NewSignal(); var cleaned = false;
        var driver = new DelegateDriver(async (_, _, token) =>
        {
            entered.SetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { cancelled.SetResult(); await cleanup.Task; cleaned = true; }
        });
        var jobs = new BuilderJobManager(driver);
        jobs.Start(Request("ensure-sdk"));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var disposing = jobs.DisposeAsync().AsTask();
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(disposing.IsCompleted);
        Assert.Throws<ObjectDisposedException>(() => jobs.Start(Request("ensure-sdk")));
        cleanup.SetResult();
        await disposing.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(cleaned);
        await jobs.DisposeAsync();
    }

    [Fact]
    public async Task CompletedJobsAndOutputAreBoundedAndSecretsAcrossChunksAreRedacted()
    {
        var calls = 0;
        var driver = new DelegateDriver((_, report, _) =>
        {
            calls++;
            report.Report(new string('x', 4000)); report.Report("\ntoken="); report.Report("split-secret\nAuthorization: Bearer bearer-secret\n");
            report.Update(new() { Message = "finished password=message-secret" });
            return Task.CompletedTask;
        });
        await using var jobs = new BuilderJobManager(driver, new() { CompletedJobLimit = 2, MaximumOutputCharacters = 1024 });
        var first = Request("ensure-sdk");
        await TerminalAsync(jobs, jobs.Start(first));
        await TerminalAsync(jobs, jobs.Start(Request("ensure-sdk")));
        var lastRequest = Request("ensure-sdk");
        var last = await TerminalAsync(jobs, jobs.Start(lastRequest));
        Assert.True(last.Output.Length <= 1024);
        Assert.DoesNotContain("split-secret", last.Output); Assert.DoesNotContain("bearer-secret", last.Output);
        Assert.DoesNotContain("message-secret", last.Message); Assert.Contains("redacted", last.Output);
        Assert.Throws<BuilderJobNotFoundException>(() => jobs.GetJob(first.Id!));
        Assert.Throws<BuilderJobNotFoundException>(() => jobs.Start(first));
        Assert.Equal(last.Id, jobs.Start(lastRequest)); Assert.Equal(3, calls);
    }

    [Theory]
    [InlineData("ensure-sdk", BuilderProjectKind.Dotnet)]
    [InlineData("ensure-sdk", BuilderProjectKind.Node)]
    [InlineData("repair-sdk", BuilderProjectKind.Dotnet)]
    [InlineData("remove-sdk", BuilderProjectKind.Node)]
    [InlineData("build", BuilderProjectKind.Dotnet)]
    [InlineData("create", BuilderProjectKind.Dotnet)]
    public async Task ToolchainProgressReplacesCurrentStatusWhileCommandOutputStillAppends(string operation, BuilderProjectKind kind)
    {
        using var fixture = new Fixture();
        var first = NewSignal(); var next = NewSignal(); var second = NewSignal(); var finish = NewSignal();
        fixture.Tools.ProgressOperation = async (_, progress, token) =>
        {
            progress!.Report("Downloaded 8 MiB of 220 MiB.");
            first.TrySetResult();
            await next.Task.WaitAsync(token);
            progress.Report("Downloaded 16 MiB of 220 MiB.");
            second.TrySetResult();
            await finish.Task.WaitAsync(token);
            progress.Report("Verifying official checksum.");
        };
        var request = Request(operation, operation == "build" ? fixture.WriteDotnet("project") : null, kind);
        if (operation is "repair-sdk" or "remove-sdk") request = request with { Installation = fixture.Tools.For(kind) };
        if (operation == "create") request = request with
        {
            ProjectFolder = fixture.PathOf("created"), DisplayName = "Hello package", PackageId = "sample.hello", Installation = fixture.Tools.For(kind),
        };
        await using var jobs = fixture.Jobs();
        var id = jobs.Start(request);
        await first.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("Downloaded 8 MiB of 220 MiB.", jobs.GetJob(id).Message);
        Assert.Empty(jobs.GetJob(id).Output);
        next.TrySetResult();
        await second.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("Downloaded 16 MiB of 220 MiB.", jobs.GetJob(id).Message);
        Assert.Empty(jobs.GetJob(id).Output);
        finish.TrySetResult();
        var final = await TerminalAsync(jobs, id);
        Assert.Equal("succeeded", final.State);
        Assert.DoesNotContain("Downloaded", final.Message);
        Assert.DoesNotContain("Downloaded", final.Output);
        if (operation is "build" or "create") Assert.Contains("successful command output", final.Output);
        else Assert.Empty(final.Output);
    }

    [Fact]
    public async Task TemplateCrashHasOneShortErrorAndFullDiagnostics_RetryReusesPreparedSdkAndInputs()
    {
        using var fixture = new Fixture();
        fixture.Process.FailTemplateInstall = true;
        await using var jobs = fixture.Jobs();
        var prepared = fixture.Tools.For(BuilderProjectKind.Dotnet);
        var request = Request("create", kind: BuilderProjectKind.Dotnet) with
        {
            ProjectFolder = fixture.PathOf("created"), DisplayName = "Hello package", PackageId = "sample.hello", Installation = prepared,
        };
        var failed = await TerminalAsync(jobs, jobs.Start(request));
        Assert.Equal("failed", failed.State);
        Assert.Contains("dotnet new install", failed.Error);
        Assert.Contains("134", failed.Error);
        Assert.DoesNotContain("AsyncMutex", failed.Error);
        Assert.DoesNotContain("AsyncMutex", failed.Message);
        Assert.NotEqual(failed.Error, failed.Message);
        Assert.Contains(FakeProcess.TemplateFailure, failed.Output);
        Assert.Null(failed.Project);
        Assert.False(Directory.Exists(request.ProjectFolder));
        Assert.Equal(0, fixture.Tools.Leases);

        fixture.Process.FailTemplateInstall = false;
        var retried = await TerminalAsync(jobs, jobs.Start(request with { Id = Guid.NewGuid().ToString("N") }));
        Assert.Equal("succeeded", retried.State);
        Assert.Equal(prepared, retried.Installation);
        Assert.Equal(request.ProjectFolder, retried.Project!.ProjectFolder);
        Assert.Equal(request.DisplayName, retried.Project.DisplayName);
        Assert.Equal(request.PackageId, retried.Project.PackageId);
        Assert.Equal(0, fixture.Tools.Ensures);
        Assert.Equal(2, fixture.Tools.Selections);
        Assert.Equal(1, fixture.Process.Scaffolds);
        Assert.Single(fixture.Process.Requests.Where(item => item.Arguments[0] == "new").Select(item => item.Environment["DOTNET_CLI_HOME"]).Distinct());
    }

    [Theory]
    [InlineData(BuilderProjectKind.Dotnet)]
    [InlineData(BuilderProjectKind.Node)]
    public async Task CreatePinsPreparedSdkBeforeCommandsAndReportsRecoverableRestoreFailure(BuilderProjectKind kind)
    {
        using var fixture = new Fixture();
        fixture.Process.FailRestore = true;
        await using var jobs = fixture.Jobs();
        var destination = fixture.PathOf("new project");
        var prepared = fixture.Tools.For(kind);
        var request = Request("create", kind: kind) with { ProjectFolder = destination, DisplayName = "Hello package", PackageId = "sample.hello", Installation = prepared };
        var result = await TerminalAsync(jobs, jobs.Start(request));
        Assert.Equal("failed", result.State);
        Assert.True(result.Project is not null, result.Error); Assert.NotNull(result.DevelopmentFolder);
        Assert.Equal(destination, result.Project.ProjectFolder);
        Assert.Contains("setup is incomplete", result.Message);
        Assert.True(File.Exists(result.Project.EntryPoint)); Assert.Equal(0, fixture.Tools.Leases);
        Assert.DoesNotContain("restore-secret", result.Error);
        Assert.Empty(Directory.EnumerateDirectories(fixture.Root, ".sunder-builder-create-*"));
        if (kind == BuilderProjectKind.Dotnet)
        {
            using var pin = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(destination, "global.json")));
            Assert.Equal(prepared.Version, pin.RootElement.GetProperty("sdk").GetProperty("version").GetString());
            Assert.All(fixture.Process.Requests.Where(item => item.Arguments[0] == "new"), item => Assert.True(fixture.Process.PinSeen[item.WorkingDirectory]));
            Assert.Contains(fixture.Process.Requests, item => item.Arguments.Contains("--withAvalonia") && item.Arguments.Contains("true"));
            Assert.Contains(fixture.Process.Requests, item => item.Arguments.Contains("Sunder.Package.Templates@1.1.0"));
        }
        else
        {
            Assert.Equal(prepared.Version, (await File.ReadAllTextAsync(Path.Combine(destination, ".node-version"))).Trim());
            Assert.Contains(fixture.Process.Requests, item => item.Arguments.Contains("react-node") && item.Arguments.Contains("ts"));
        }
        fixture.Process.FailRestore = false;
        var creates = fixture.Process.Scaffolds;
        var retry = await TerminalAsync(jobs, jobs.Start(Request("setup", result.Project.EntryPoint, kind)));
        Assert.True(retry.State == "succeeded", retry.Error); Assert.Equal(creates, fixture.Process.Scaffolds);
        Assert.True(File.Exists(Path.Combine(retry.DevelopmentFolder!, "manifest", "sunder-package.json")));
    }

    [Fact]
    public async Task CreateNeverOverwritesUserFilesIncludingWritesRacingScaffolding()
    {
        using var fixture = new Fixture();
        var destination = fixture.PathOf("destination"); Directory.CreateDirectory(destination);
        await File.WriteAllTextAsync(Path.Combine(destination, "user.txt"), "keep");
        await using var jobs = fixture.Jobs();
        var request = Request("create") with { ProjectFolder = destination, DisplayName = "Hello", PackageId = "sample.hello" };
        Assert.Equal("failed", (await TerminalAsync(jobs, jobs.Start(request))).State);
        Assert.Empty(fixture.Process.Requests);
        File.Delete(Path.Combine(destination, "user.txt"));
        fixture.Process.AfterScaffold = () => File.WriteAllText(Path.Combine(destination, "user.txt"), "racing write");
        Assert.Equal("failed", (await TerminalAsync(jobs, jobs.Start(request with { Id = Guid.NewGuid().ToString("N") }))).State);
        Assert.Equal("racing write", await File.ReadAllTextAsync(Path.Combine(destination, "user.txt")));
        Assert.Single(Directory.EnumerateFileSystemEntries(destination));
    }

    [Fact]
    public async Task BuildTargetsSelectedProjectAndRejectsForeignRidManifest()
    {
        using var fixture = new Fixture();
        var entry = fixture.WriteDotnet("project with spaces", "Arbitrary.App.csproj");
        await using var jobs = fixture.Jobs();
        var valid = await TerminalAsync(jobs, jobs.Start(Request("build", entry, BuilderProjectKind.Dotnet)));
        Assert.Equal("succeeded", valid.State);
        Assert.Equal(BuilderJobPhase.BuildReady, valid.Phase);
        Assert.NotNull(valid.LastSuccessfulBuildAtUtc);
        Assert.Null(valid.LastSourceChangeAtUtc);
        var command = Assert.Single(fixture.Process.Requests, request => request.Arguments[0] == "msbuild");
        Assert.Single(fixture.Process.Requests);
        Assert.Contains("-restore", command.Arguments); // MSBuild still restores before build evaluation.
        Assert.Contains("-p:RestoreDisableParallel=true", command.Arguments);
        Assert.Equal(fixture.Tools.For(BuilderProjectKind.Dotnet).ExecutablePath, command.ExecutablePath);
        Assert.Contains(entry, command.Arguments); Assert.Equal(Path.GetDirectoryName(entry), command.WorkingDirectory);
        Assert.Contains("-p:RuntimeIdentifier=" + BuilderToolchainPlatform.Current.DotnetRid, command.Arguments);
        Assert.Equal("1", command.Environment["AVALONIA_TELEMETRY_OPTOUT"]);
        var unchanged = await TerminalAsync(jobs, jobs.Start(Request("build", entry, BuilderProjectKind.Dotnet)));
        Assert.Equal("succeeded", unchanged.State);
        Assert.Equal(valid.DevelopmentFolder, unchanged.DevelopmentFolder);
        // Every explicit Build still invokes MSBuild, including its integrated restore phase.
        Assert.Equal(2, fixture.Process.Builds);
        Assert.Equal(2, fixture.Process.Requests.Count);

        // Changed sources still pass through restore/build authority.
        await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(entry)!, "NewSource.cs"), "internal class NewSource { }");
        Assert.Equal("succeeded", (await TerminalAsync(jobs, jobs.Start(Request("build", entry, BuilderProjectKind.Dotnet)))).State);
        Assert.Equal(3, fixture.Process.Builds);
        Assert.Equal(3, fixture.Process.Requests.Count);

        // Identical sources/index cannot authorize reuse of changed payload bytes. The
        // resulting rebuild must still reject a compiler's foreign-RID output.
        await File.WriteAllTextAsync(Path.Combine(valid.DevelopmentFolder!, "payload", "runtime",
            BuilderToolchainPlatform.Current.DotnetRid, "bin", "worker.cjs"), "tampered");
        fixture.Process.InvalidRid = true;
        var invalid = await TerminalAsync(jobs, jobs.Start(Request("build", entry, BuilderProjectKind.Dotnet)));
        Assert.Equal("failed", invalid.State); Assert.Contains("current-RID", invalid.Error);
        Assert.Null(invalid.DevelopmentFolder);
        Assert.Equal(4, fixture.Process.Builds);
    }

    [Theory]
    [InlineData("shared", true)]
    [InlineData("role-shared", true)]
    [InlineData("target", true)]
    [InlineData("foreign", false)]
    [InlineData("overlap", false)]
    public async Task CanonicalOutputResolvesLogicalEntryPointsWithoutForeignRidOrLayerFallback(string layer, bool valid)
    {
        using var fixture = new Fixture(); fixture.Process.PayloadLayer = layer;
        await using var jobs = fixture.Jobs();
        var result = await TerminalAsync(jobs, jobs.Start(Request("build", fixture.WriteNode("project"))));
        Assert.True(result.State == (valid ? "succeeded" : "failed"), result.Error);
        if (valid) Assert.NotNull(result.DevelopmentFolder);
        else { Assert.Null(result.DevelopmentFolder); Assert.Contains(layer == "overlap" ? "ambiguous" : "does not resolve", result.Error); }
    }

    [Fact]
    public async Task CombinedBuildRestoreFailureKeepsLastGoodOutputAndNeverAdvertisesSuccess()
    {
        using var fixture = new Fixture();
        var entry = fixture.WriteDotnet("project");
        await using var jobs = fixture.Jobs();
        var first = await TerminalAsync(jobs, jobs.Start(Request("build", entry, BuilderProjectKind.Dotnet)));
        Assert.Equal("succeeded", first.State);
        var index = Path.Combine(first.DevelopmentFolder!, "manifest", "content-index.json");
        var before = await File.ReadAllBytesAsync(index);
        await File.AppendAllTextAsync(entry, "\n<!-- dependency evaluation changed -->\n");
        fixture.Process.FailRestore = true;
        fixture.Process.BeforeBuild = _ => throw new InvalidOperationException("Compilation must not follow a failed integrated restore.");
        var failed = await TerminalAsync(jobs, jobs.Start(Request("build", entry, BuilderProjectKind.Dotnet)));
        Assert.Equal("failed", failed.State);
        Assert.Null(failed.DevelopmentFolder);
        Assert.Contains("integrated dependency restore failed", failed.Output);
        Assert.Equal(before, await File.ReadAllBytesAsync(index));
        Assert.Equal(new[] { "msbuild", "msbuild" }, fixture.Process.Requests.Select(request => request.Arguments[0]));
        Assert.Equal(0, fixture.Tools.Leases);
    }

    [Theory]
    [InlineData(BuilderProjectKind.Dotnet, "valid")]
    [InlineData(BuilderProjectKind.Node, "valid")]
    [InlineData(BuilderProjectKind.Node, "missing")]
    [InlineData(BuilderProjectKind.Node, "invalid")]
    [InlineData(BuilderProjectKind.Node, "cold")]
    public async Task WatchOwnsExactSdkLeaseUntilItsProcessStops(BuilderProjectKind kind, string outputState)
    {
        using var fixture = new Fixture();
        var entry = kind == BuilderProjectKind.Dotnet ? fixture.WriteDotnet("project") : fixture.WriteNode("project");
        await using var jobs = fixture.Jobs();
        string? output = null;
        if (outputState != "cold")
        {
            var setup = await TerminalAsync(jobs, jobs.Start(Request("setup", entry, kind)));
            Assert.Equal("succeeded", setup.State);
            output = setup.DevelopmentFolder!;
        }
        if (outputState == "missing") Directory.Delete(output!, recursive: true);
        if (outputState == "invalid")
            await File.WriteAllTextAsync(Path.Combine(output!, "payload", "runtime", BuilderToolchainPlatform.Current.DotnetRid, "bin", "worker.cjs"), "broken");
        if (kind == BuilderProjectKind.Node && outputState == "valid")
            await File.AppendAllTextAsync(Path.Combine(Path.GetDirectoryName(entry)!, "src", "services.ts"), "\n// Changed after setup; npm watch owns the initial rebuild.\n");
        var preparing = NewSignal(); var release = NewSignal();
        var mustPrepare = kind == BuilderProjectKind.Node && outputState != "valid";
        if (mustPrepare) fixture.Process.BeforeBuild = async token => { preparing.TrySetResult(); await release.Task.WaitAsync(token); };
        var id = jobs.Start(Request("watch", entry, kind));
        if (mustPrepare)
        {
            try
            {
                await preparing.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(1, fixture.Tools.Leases);
                Assert.False(fixture.Process.WatchStarted.Task.IsCompleted);
                Assert.Null(jobs.GetJob(id).DevelopmentFolder);
                Assert.Equal(BuilderJobPhase.Building, jobs.GetJob(id).Phase);
                Assert.Null(jobs.GetJob(id).LastSuccessfulBuildAtUtc);
                Assert.DoesNotContain("Watching source changes.", jobs.GetJob(id).Message);
            }
            finally { release.TrySetResult(); }
        }
        if (kind == BuilderProjectKind.Node) await fixture.Process.WatchStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        else await WatchingAsync(jobs, id);
        Assert.Equal(1, fixture.Tools.Leases);
        var watch = fixture.Process.Requests.Last();
        Assert.Equal(fixture.Tools.For(kind).ExecutablePath, watch.ExecutablePath);
        if (kind == BuilderProjectKind.Node)
        {
            Assert.Null(watch.Timeout);
            Assert.Equal(new[] { fixture.Tools.For(kind).NpmCliPath!, "run", "sunder:dev" }, watch.Arguments);
            Assert.Single(fixture.Process.Requests, request => request.Arguments.Contains("sunder:dev"));
            Assert.Equal(outputState is "missing" or "invalid" ? 2 : 1,
                fixture.Process.Requests.Count(request => request.Arguments.Contains("sunder:dev:once")));
            Assert.Equal("worker", await File.ReadAllTextAsync(Path.Combine(jobs.GetJob(id).DevelopmentFolder!, "payload", "runtime",
                BuilderToolchainPlatform.Current.DotnetRid, "bin", "worker.cjs")));
        }
        else
        {
            // Unknown input closure must run MSBuild even for unchanged Watch startup.
            // Subsequent membership changes use the filesystem subscription, not timer retries.
            Assert.Equal(new[] { "restore", "msbuild", "msbuild" }, fixture.Process.Requests.Select(request => request.Arguments[0]));
            var sourceBytes = await File.ReadAllBytesAsync(entry);
            Assert.Null(jobs.GetJob(id).LastSourceChangeAtUtc);
            Assert.NotNull(jobs.GetJob(id).LastSuccessfulBuildAtUtc);
            var added = Path.Combine(Path.GetDirectoryName(entry)!, "AddedDuringWatch.cs");
            await File.WriteAllTextAsync(added, "internal class AddedDuringWatch { }");
            await WatchingAsync(jobs, id, () => fixture.Process.Builds >= 3);
            var observedChange = jobs.GetJob(id).LastSourceChangeAtUtc;
            Assert.NotNull(observedChange);
            Assert.True(jobs.GetJob(id).LastSuccessfulBuildAtUtc >= observedChange);
            Assert.Equal(BuilderJobPhase.Watching, jobs.GetJob(id).Phase);
            File.Move(added, Path.ChangeExtension(added, ".axaml"));
            await WatchingAsync(jobs, id, () => fixture.Process.Builds >= 4);
            File.Delete(Path.ChangeExtension(added, ".axaml"));
            await WatchingAsync(jobs, id, () => fixture.Process.Builds >= 5);
            Assert.Single(fixture.Process.Requests, request => request.Arguments[0] == "restore"); // Setup's explicit phase only.
            Assert.All(fixture.Process.Requests, request =>
            {
                Assert.Equal("1", request.Environment["MSBUILDDISABLENODEREUSE"]);
                Assert.Equal("0", request.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"]);
                Assert.Equal("1", request.Environment["AVALONIA_TELEMETRY_OPTOUT"]);
            });
            Assert.All(fixture.Process.Requests.Where(request => request.Arguments[0] == "msbuild"), request =>
            {
                Assert.Contains("--disable-build-servers", request.Arguments);
                Assert.Contains("-p:UseSharedCompilation=false", request.Arguments);
            });
            var builds = fixture.Process.Requests.Where(request => request.Arguments[0] == "msbuild").ToArray();
            Assert.DoesNotContain("-restore", builds[0].Arguments); // Setup restored with matching properties.
            Assert.All(builds.Skip(1), request => Assert.Contains("-restore", request.Arguments));
            Assert.Equal(sourceBytes, await File.ReadAllBytesAsync(entry));
        }
        await jobs.CancelAsync(id);
        Assert.Equal("cancelled", (await TerminalAsync(jobs, id)).State);
        if (kind == BuilderProjectKind.Node) Assert.True(fixture.Process.WatchStopped.Task.IsCompleted);
        Assert.Equal(0, fixture.Tools.Leases);
    }

    [Fact]
    public async Task NodeWatchProgressIsChunkSafeAndPresentationOnlyWhileMalformedDiagnosticsRemainOutput()
    {
        using var fixture = new Fixture();
        var entry = fixture.WriteNode("watch-progress");
        await using var jobs = fixture.Jobs();
        Assert.Equal("succeeded", (await TerminalAsync(jobs, jobs.Start(Request("setup", entry)))).State);
        var building = NewSignal(); var fail = NewSignal(); var failed = NewSignal(); var recover = NewSignal(); var ready = NewSignal();
        fixture.Process.WatchOperation = async (progress, token) =>
        {
            progress!.Report("sunder.package.watch:{not json}\nsunder.package.watch:{\"schemaVersion\":\"bad\",\"state\":\"ready\"}\n");
            progress.Report("sunder.package.watch:{\"schemaVersion\":1,\"state\":\"source-");
            progress.Report("changed\"}\nsunder.package.watch:{\"schemaVersion\":1,\"state\":\"building\"}\n");
            building.TrySetResult();
            await fail.Task.WaitAsync(token);
            progress.Report("sunder.package.watch:{\"schemaVersion\":1,\"state\":\"failed\"}\n");
            failed.TrySetResult();
            await recover.Task.WaitAsync(token);
            progress.Report("sunder.package.watch:{\"schemaVersion\":1,\"state\":\"ready\"}\n");
            ready.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        };
        var id = jobs.Start(Request("watch", entry));
        await building.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(BuilderJobPhase.Building, jobs.GetJob(id).Phase);
        Assert.NotNull(jobs.GetJob(id).LastSourceChangeAtUtc);
        Assert.Null(jobs.GetJob(id).LastSuccessfulBuildAtUtc);
        Assert.Contains("not json", jobs.GetJob(id).Output);
        Assert.Equal("1", fixture.Process.Requests.Last().Environment["SUNDER_BUILDER_WATCH_PROGRESS"]);
        fail.TrySetResult();
        await failed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(BuilderJobPhase.BuildFailed, jobs.GetJob(id).Phase);
        Assert.Equal("running", jobs.GetJob(id).State);
        recover.TrySetResult();
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(BuilderJobPhase.Watching, jobs.GetJob(id).Phase);
        Assert.NotNull(jobs.GetJob(id).LastSuccessfulBuildAtUtc);
        Assert.Equal("running", jobs.GetJob(id).State); // No invented job outcome or cleanup acknowledgement.
        await jobs.CancelAsync(id);
        Assert.Equal("cancelled", (await TerminalAsync(jobs, id)).State);
    }

    [Fact]
    public async Task DotnetWatchFailureRetainsLastGoodOutputAndCancellationDrainsActiveCompilerBeforeLeaseRelease()
    {
        using var fixture = new Fixture();
        var entry = fixture.WriteDotnet("project");
        await using var jobs = fixture.Jobs();
        Assert.Equal("succeeded", (await TerminalAsync(jobs, jobs.Start(Request("setup", entry, BuilderProjectKind.Dotnet)))).State);
        var watch = jobs.Start(Request("watch", entry, BuilderProjectKind.Dotnet));
        await WatchingAsync(jobs, watch);
        var output = jobs.GetJob(watch).DevelopmentFolder!;
        var before = await File.ReadAllBytesAsync(Path.Combine(output, "manifest", "content-index.json"));
        fixture.Process.FailBuild = true;
        var source = Path.Combine(Path.GetDirectoryName(entry)!, "Changed.cs");
        await File.WriteAllTextAsync(source, "invalid source");
        Assert.Equal("failed", (await TerminalAsync(jobs, watch)).State);
        Assert.Equal(before, await File.ReadAllBytesAsync(Path.Combine(output, "manifest", "content-index.json")));
        Assert.Equal(0, fixture.Tools.Leases);

        fixture.Process.FailBuild = false;
        await File.WriteAllTextAsync(source, "internal class Changed { }");
        watch = jobs.Start(Request("watch", entry, BuilderProjectKind.Dotnet));
        await WatchingAsync(jobs, watch);
        Assert.Single(fixture.Process.Requests, request => request.Arguments[0] == "restore");
        var entered = NewSignal(); var cancelled = NewSignal(); var release = NewSignal();
        fixture.Process.BeforeBuild = async token =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { cancelled.TrySetResult(); await release.Task; }
        };
        await File.WriteAllTextAsync(source, "internal class ChangedAgain { }");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await jobs.CancelAsync(watch);
        try
        {
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("running", jobs.GetJob(watch).State);
            Assert.Equal(1, fixture.Tools.Leases);
        }
        finally { release.TrySetResult(); }
        Assert.Equal("cancelled", (await TerminalAsync(jobs, watch)).State);
        Assert.Equal(0, fixture.Tools.Leases);
        // A replacement at the same SDK path/version is selected for the next real build.
        fixture.Process.BeforeBuild = null;
        fixture.Tools.InstalledAtUtc = DateTimeOffset.UtcNow;
        var buildsBeforeRepair = fixture.Process.Builds;
        watch = jobs.Start(Request("watch", entry, BuilderProjectKind.Dotnet));
        await WatchingAsync(jobs, watch);
        Assert.Equal(buildsBeforeRepair + 1, fixture.Process.Builds);
        Assert.Contains("-restore", fixture.Process.Requests.Last().Arguments);
        Assert.Equal(fixture.Tools.InstalledAtUtc, jobs.GetJob(watch).Installation!.InstalledAtUtc);
        await jobs.CancelAsync(watch);
        Assert.Equal("cancelled", (await TerminalAsync(jobs, watch)).State);
        Assert.Equal(0, fixture.Tools.Leases);
    }

    [Theory]
    [InlineData("build", false)]
    [InlineData("watch", true)]
    public async Task UnchangedSourcesCannotSkipAnOutsideGatedProjectOrNuGetTarget(string operation, bool packageTarget)
    {
        using var fixture = new Fixture();
        var entry = fixture.WriteDotnet("project");
        var gate = fixture.PathOf("outside-gate");
        var target = $"<Target Name=\"LoadProofCancellationGate\" BeforeTargets=\"BeforeBuild;PrepareForBuild;CoreCompile\" Condition=\"Exists('{gate}')\"><Message Text=\"Outside input requires this target\" /></Target>";
        if (packageTarget)
        {
            var path = fixture.PathOf("cache/nuget-packages/sunder.package.build/1.1.0/build/Sunder.Package.Build.targets");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, "<Project>" + target + "</Project>");
            fixture.TrackingPlan(entry, dependencyInputs: [path]);
        }
        else await File.WriteAllTextAsync(entry, (await File.ReadAllTextAsync(entry)).Replace("</Project>", target + "</Project>", StringComparison.Ordinal));
        await using var jobs = fixture.Jobs();
        Assert.Equal("succeeded", (await TerminalAsync(jobs, jobs.Start(Request("setup", entry, BuilderProjectKind.Dotnet)))).State);
        var before = await File.ReadAllBytesAsync(entry);
        await File.WriteAllTextAsync(gate, "request");
        Assert.Equal(before, await File.ReadAllBytesAsync(entry));
        var entered = NewSignal(); var cancelled = NewSignal(); var release = NewSignal();
        fixture.Process.BeforeBuild = async token =>
        {
            Assert.True(File.Exists(gate));
            entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { cancelled.TrySetResult(); await release.Task; }
        };
        var id = jobs.Start(Request(operation, entry, BuilderProjectKind.Dotnet));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, fixture.Process.Builds);
            await jobs.CancelAsync(id);
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, fixture.Tools.Leases);
            Assert.Equal("running", jobs.GetJob(id).State);
        }
        finally { release.TrySetResult(); }
        Assert.Equal("cancelled", (await TerminalAsync(jobs, id)).State);
        Assert.Equal(0, fixture.Tools.Leases);
    }

    [Theory]
    [InlineData("recovered")]
    [InlineData("unverifiable")]
    [InlineData("timeout")]
    public async Task GeneratedOutputLockProgressIsVisibleAndFailuresExplainSafeRecovery(string outcome)
    {
        using var fixture = new Fixture();
        var entry = fixture.WriteDotnet("project");
        var waiting = NewSignal(); var resume = NewSignal(); var acquired = NewSignal(); var finish = NewSignal();
        var diagnostic = outcome == "unverifiable"
            ? "Generated output lock '/fixture/output.sunder-output.lock' has unverifiable ownership evidence. The lock was preserved."
            : "Timed out after 300000 ms waiting for generated output lock '/fixture/output.sunder-output.lock'.";
        fixture.Process.BuildOperation = async (progress, token) =>
        {
            progress!.Report("prefix package-build.output-lock.wai");
            progress.Report("ting: checking ownership\n");
            waiting.TrySetResult();
            await resume.Task.WaitAsync(token);
            if (outcome != "recovered")
            {
                progress.Report(diagnostic + "\n");
                return new(1, "", diagnostic);
            }
            progress.Report("package-build.output-lock.acq");
            progress.Report("uired: continuing\r\n");
            acquired.TrySetResult();
            await finish.Task.WaitAsync(token);
            return null;
        };
        await using var jobs = fixture.Jobs();
        var id = jobs.Start(Request("build", entry, BuilderProjectKind.Dotnet));
        try
        {
            await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.StartsWith("Waiting for a generated-output lock.", jobs.GetJob(id).Message);
            Assert.Contains("package-build.output-lock.waiting:", jobs.GetJob(id).Output);
            resume.TrySetResult();
            if (outcome == "recovered")
            {
                await acquired.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.StartsWith("Generated-output locks acquired;", jobs.GetJob(id).Message);
            }
        }
        finally { resume.TrySetResult(); finish.TrySetResult(); }
        var terminal = await TerminalAsync(jobs, id);
        Assert.Equal(outcome == "recovered" ? "succeeded" : "failed", terminal.State);
        if (outcome != "recovered")
        {
            Assert.Contains("inspect the exact lock", terminal.Error!, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(diagnostic, terminal.Output);
        }
        Assert.Equal(0, fixture.Tools.Leases);
    }

    [Theory]
    [InlineData(BuilderProjectKind.Dotnet, "sunder-package.json")]
    [InlineData(BuilderProjectKind.Dotnet, "content-index.json")]
    [InlineData(BuilderProjectKind.Node, "sunder-package.json")]
    public async Task MalformedDevelopmentJsonIsRebuiltBeforeItIsAdvertised(BuilderProjectKind kind, string metadata)
    {
        using var fixture = new Fixture();
        var entry = kind == BuilderProjectKind.Dotnet ? fixture.WriteDotnet("project") : fixture.WriteNode("project");
        await using var jobs = fixture.Jobs();
        var setup = await TerminalAsync(jobs, jobs.Start(Request("setup", entry, kind)));
        Assert.Equal("succeeded", setup.State);
        await File.WriteAllTextAsync(Path.Combine(setup.DevelopmentFolder!, "manifest", metadata), "{");
        var entered = NewSignal(); var release = NewSignal();
        fixture.Process.BeforeBuild = async token => { entered.TrySetResult(); await release.Task.WaitAsync(token); };
        var id = jobs.Start(Request("watch", entry, kind));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Null(jobs.GetJob(id).DevelopmentFolder);
            Assert.False(fixture.Process.WatchStarted.Task.IsCompleted);
        }
        finally { release.TrySetResult(); }
        await WatchingAsync(jobs, id);
        Assert.Equal(2, fixture.Process.Builds);
        await BuilderProjectDriverOutputs.ValidateDevelopmentAsync(jobs.GetJob(id).DevelopmentFolder!, setup.Project!,
            BuilderToolchainPlatform.Current.DotnetRid, requireApp: false, CancellationToken.None);
        await jobs.CancelAsync(id);
        Assert.Equal("cancelled", (await TerminalAsync(jobs, id)).State);
        Assert.Equal(0, fixture.Tools.Leases);
    }

    [Fact]
    public async Task InputGraphWatchesExternalMembershipAndExplicitObjSourcesWithoutReadingReferenceBytes()
    {
        using var fixture = new Fixture();
        var entry = fixture.WriteDotnet("project");
        var projectRoot = Path.GetDirectoryName(entry)!;
        var inputs = fixture.PathOf("inputs"); Directory.CreateDirectory(inputs);
        var props = Path.Combine(inputs, "Inputs.props");
        await File.WriteAllTextAsync(props, """
            <Project><ItemGroup>
              <Compile Include="../inputs/sources/**/*.cs" />
              <Compile Include="obj/Authored.g.cs" />
              <AvaloniaResource Include="../inputs/resources/**/*" />
            </ItemGroup></Project>
            """);
        await File.WriteAllTextAsync(entry, (await File.ReadAllTextAsync(entry)).Replace("</Project>", "<Import Project=\"../inputs/Inputs.props\" /></Project>", StringComparison.Ordinal));
        var reference = Path.Combine(inputs, "External.dll"); await File.WriteAllTextAsync(reference, "original DLL bytes");
        var contract = Path.Combine(inputs, "external-contract.json"); await File.WriteAllTextAsync(contract, "{}");
        var authored = Path.Combine(projectRoot, "obj", "Authored.g.cs"); Directory.CreateDirectory(Path.GetDirectoryName(authored)!);
        await File.WriteAllTextAsync(authored, "internal class Original { }");
        var globalUsings = Path.Combine(projectRoot, "obj", "Debug", "net10.0", "sunder-analysis", BuilderToolchainPlatform.Current.DotnetRid,
            Path.GetFileNameWithoutExtension(entry) + ".GlobalUsings.g.cs");
        fixture.TrackingPlan(entry, ["obj/Authored.g.cs", globalUsings], [contract, props], [reference]);
        BuilderDotnetBuildInputs snapshot;
        // A notification graph must not open or hash the PE payload, even when it is locked.
        using (var lockedReference = new FileStream(reference, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            snapshot = await fixture.InputsAsync(entry);
        using var changes = new BuilderSourceChangeMonitor(projectRoot, [inputs]);
        changes.ObserveInputs(snapshot.Roots, snapshot.Files);
        await Change(reference, "replaced DLL bytes");
        await Change(contract, "{\"contractVersion\":2}");
        await Change(authored, "internal class AuthoredChange { }");
        // Both glob roots are absent at subscription time. A later sibling directory must
        // also be observed, not just the directory of the first evaluated member.
        await Change(Path.Combine(inputs, "sources", "first", "First.cs"), "internal class First { }");
        await Change(Path.Combine(inputs, "sources", "sibling", "Second.cs"), "internal class Second { }");
        var resource = Path.Combine(inputs, "resources", "nested", "icon.txt");
        await Change(resource, "new resource");
        await Change(resource, "changed resource");
        await Change(fixture.PathOf(".editorconfig"), "root=true\n[*.cs]\ndotnet_diagnostic.CS0168.severity=error\n");
        await Change(props, (await File.ReadAllTextAsync(props)) + "\n<!-- changed imported bytes -->\n");
        var revision = changes.Revision;
        File.Delete(resource);
        await Observe(revision);

        // Real restore/compiler products must not trigger a watch rebuild loop. The explicit
        // authored .g.cs above remains observable despite sharing the obj directory.
        revision = changes.Revision;
        Directory.CreateDirectory(Path.GetDirectoryName(globalUsings)!);
        await File.WriteAllTextAsync(globalUsings, "global using System;\n");
        await File.WriteAllTextAsync(Path.Combine(projectRoot, "obj", "project.assets.json"), "{}");
        // A shared reference-tree subscription observes tracked paths, not unrelated tool
        // cache writes elsewhere in that tree.
        var unrelated = Path.Combine(inputs, "unrelated-tool-cache", "trace.log");
        Directory.CreateDirectory(Path.GetDirectoryName(unrelated)!);
        await File.WriteAllTextAsync(unrelated, "unrelated tooling activity");
        await Task.Delay(500);
        Assert.Equal(revision, changes.Revision);

        async Task Change(string path, string text)
        {
            var before = changes.Revision;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, text);
            await Observe(before);
        }
        async Task Observe(long revision)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await changes.WaitForChangeAsync(revision, deadline.Token);
            snapshot = await fixture.InputsAsync(entry);
            changes.ObserveInputs(snapshot.Roots, snapshot.Files);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WatchRebuildsOnlyForPreviouslyUnobservedInputs(bool external)
    {
        using var fixture = new Fixture();
        var entry = fixture.WriteDotnet("project");
        var reference = fixture.PathOf(external ? "late-reference.dll" : "project/late-reference.dll");
        await File.WriteAllTextAsync(reference, "first reference bytes");
        await using var jobs = fixture.Jobs();
        Assert.Equal("succeeded", (await TerminalAsync(jobs, jobs.Start(Request("setup", entry, BuilderProjectKind.Dotnet)))).State);
        var entered = NewSignal(); var release = NewSignal();
        fixture.Process.BeforeBuild = async token =>
        {
            if (fixture.Process.Builds == 2)
            {
                // Simulate evaluation revealing an external input, followed by a save before
                // Builder can install its first subscription to that input.
                fixture.TrackingPlan(entry, references: [reference]);
                if (external) await File.WriteAllTextAsync(reference, "saved during first compilation", token);
            }
            else { entered.TrySetResult(); await release.Task.WaitAsync(token); }
        };
        var id = jobs.Start(Request("watch", entry, BuilderProjectKind.Dotnet));
        try
        {
            if (external)
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(3, fixture.Process.Builds);
                Assert.DoesNotContain("Watching source changes.", jobs.GetJob(id).Message);
            }
            else
            {
                await WatchingAsync(jobs, id);
                Assert.Equal(2, fixture.Process.Builds);
                Assert.False(entered.Task.IsCompleted);
            }
        }
        finally { release.TrySetResult(); }
        await WatchingAsync(jobs, id);
        await jobs.CancelAsync(id);
        Assert.Equal("cancelled", (await TerminalAsync(jobs, id)).State);
        Assert.Equal(0, fixture.Tools.Leases);
    }

    [Fact]
    public async Task WatchDoesNotAdvertiseIdleBetweenACompletedBuildAndItsAlreadyPendingSourceEdit()
    {
        using var fixture = new Fixture();
        var entry = fixture.WriteDotnet("project");
        var source = Path.Combine(Path.GetDirectoryName(entry)!, "Changed.cs");
        await File.WriteAllTextAsync(source, "internal class Original { }");
        var updates = new ConcurrentQueue<BuilderJobUpdate>();
        await using var jobs = fixture.Jobs(updates.Enqueue);
        Assert.Equal("succeeded", (await TerminalAsync(jobs, jobs.Start(Request("setup", entry, BuilderProjectKind.Dotnet)))).State);
        var id = jobs.Start(Request("watch", entry, BuilderProjectKind.Dotnet));
        await WatchingAsync(jobs, id);
        var first = NewSignal(); var releaseFirst = NewSignal();
        var second = NewSignal(); var releaseSecond = NewSignal();
        var buildsBeforeEdit = fixture.Process.Builds;
        fixture.Process.BeforeBuild = async token =>
        {
            var count = fixture.Process.Builds - buildsBeforeEdit;
            if (count == 1) { first.TrySetResult(); await releaseFirst.Task.WaitAsync(token); }
            if (count == 2) { second.TrySetResult(); await releaseSecond.Task.WaitAsync(token); }
        };
        try
        {
            await File.WriteAllTextAsync(source, "internal class FirstEdit { }");
            await first.Task.WaitAsync(TimeSpan.FromSeconds(5));
            updates.Clear();
            await File.WriteAllTextAsync(source, "internal class SavedDuringCompilation { }");
            // Deliver the real filesystem event while the finite compiler is gated.
            // Entry into the second build below independently proves it was queued.
            await Task.Delay(500);
            releaseFirst.TrySetResult();
            await second.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.DoesNotContain(updates, update => update.Message?.StartsWith("Watching source changes.", StringComparison.Ordinal) == true);
        }
        finally { releaseFirst.TrySetResult(); releaseSecond.TrySetResult(); }
        await WatchingAsync(jobs, id, () => fixture.Process.Builds >= buildsBeforeEdit + 2);
        await jobs.CancelAsync(id);
        Assert.Equal("cancelled", (await TerminalAsync(jobs, id)).State);
        Assert.Equal(0, fixture.Tools.Leases);
    }

    [Fact]
    public async Task EachBuildUsesTheCurrentSelectedChildEnvironment()
    {
        using var fixture = new Fixture();
        var entry = fixture.WriteDotnet("project");
        await using var jobs = fixture.Jobs();
        foreach (var value in new[] { "FIRST", "SECOND" })
        {
            fixture.Tools.Environment = new Dictionary<string, string?> { ["DefineConstants"] = value, ["DOTNET_STARTUP_HOOKS"] = null };
            Assert.Equal("succeeded", (await TerminalAsync(jobs, jobs.Start(Request("build", entry, BuilderProjectKind.Dotnet)))).State);
            var request = fixture.Process.Requests.Last();
            Assert.Equal(value, request.Environment["DefineConstants"]);
            Assert.Null(request.Environment["DOTNET_STARTUP_HOOKS"]);
            Assert.Contains("-restore", request.Arguments);
        }
        Assert.Equal(new[] { "msbuild", "msbuild" }, fixture.Process.Requests.Select(request => request.Arguments[0]));
        Assert.Equal(0, fixture.Tools.Leases);
    }

    [Theory]
    [InlineData(BuilderProjectKind.Dotnet)]
    [InlineData(BuilderProjectKind.Node)]
    public async Task ArchivePreservesCheckFreezeGatesAndReturnsValidatedArchive(BuilderProjectKind kind)
    {
        using var fixture = new Fixture();
        var entry = kind == BuilderProjectKind.Dotnet ? fixture.WriteDotnet("project", "Chosen.csproj") : fixture.WriteNode("project");
        await using var jobs = fixture.Jobs();
        var result = await TerminalAsync(jobs, jobs.Start(Request("archive", entry, kind)));
        Assert.True(result.State == "succeeded", result.Error); Assert.True(File.Exists(result.ArchivePath));
        if (kind == BuilderProjectKind.Dotnet)
        {
            var command = Assert.Single(fixture.Process.Requests);
            Assert.Contains(entry, command.Arguments);
            Assert.Contains("-t:CheckSunderContracts;FreezeSunderContracts;PackSunderPackage", command.Arguments);
            Assert.DoesNotContain(command.Arguments, argument => argument.Contains("AcceptSunder", StringComparison.Ordinal));
        }
        else Assert.Equal(new[] { "contracts:check", "contracts:freeze", "package" }, fixture.Process.Requests.Select(command => command.Arguments[2]));
    }

    [Fact]
    public async Task FailedPublicReviewStopsBeforeFreezeOrArchive()
    {
        using var fixture = new Fixture(); fixture.Process.FailReview = true;
        await using var jobs = fixture.Jobs();
        var final = await TerminalAsync(jobs, jobs.Start(Request("archive", fixture.WriteNode("project"))));
        Assert.Equal("failed", final.State); Assert.Null(final.ArchivePath);
        var command = Assert.Single(fixture.Process.Requests);
        Assert.Equal("contracts:check", command.Arguments[2]);
    }

    [Fact]
    public async Task MetadataSaveIsRevisionCheckedAndDoesNotStartBuildProcesses()
    {
        using var fixture = new Fixture();
        var entry = fixture.WriteDotnet("project");
        var expected = await new BuilderProjectDiscovery().InspectAsync(entry);
        await using var jobs = fixture.Jobs();
        var request = Request("save-metadata", entry, BuilderProjectKind.Dotnet) with { Expected = expected, DisplayName = "Renamed", PackageId = expected.PackageId };
        var saved = await TerminalAsync(jobs, jobs.Start(request));
        Assert.Equal("succeeded", saved.State); Assert.Equal("Renamed", saved.Project!.DisplayName);
        Assert.Empty(fixture.Process.Requests); Assert.Equal(0, fixture.Tools.Selections);
        var stale = await TerminalAsync(jobs, jobs.Start(request with { Id = Guid.NewGuid().ToString("N"), DisplayName = "Wrong" }));
        Assert.Equal("failed", stale.State); Assert.Contains("changed", stale.Error);
        Assert.Equal("Renamed", (await new BuilderProjectDiscovery().InspectAsync(entry)).DisplayName);
    }

    [Fact]
    public async Task GeneratedNodeMetadataEditReceivesSelectedExecutableAndHoldsLease()
    {
        using var fixture = new Fixture();
        var entry = fixture.WriteNode("project");
        Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(entry)!, ".sunder"));
        var expected = await new BuilderProjectDiscovery().InspectAsync(entry);
        Assert.True(expected.RequiresNodeToolingForIdEdit);
        string? executable = null;
        var driver = new BuilderProjectDriver(fixture.Tools, fixture.Process, new(fixture.PathOf("cache")), saveMetadata: (inspection, name, id, node, _) =>
        {
            Assert.Equal(1, fixture.Tools.Leases); executable = node;
            return Task.FromResult(inspection with { PackageId = id, DisplayName = name });
        });
        await using var jobs = new BuilderJobManager(driver);
        var result = await TerminalAsync(jobs, jobs.Start(Request("save-metadata", entry) with { Expected = expected, DisplayName = "Renamed", PackageId = "sample.renamed" }));
        Assert.True(result.State == "succeeded", result.Error);
        Assert.Equal(fixture.Tools.For(BuilderProjectKind.Node).ExecutablePath, executable);
        Assert.Equal(0, fixture.Tools.Leases); Assert.Empty(fixture.Process.Requests);
    }

    [Fact]
    public async Task SeparateActivationCannotBuildWhileAWatchOwnsProjectMutationLock()
    {
        using var fixture = new Fixture();
        var entry = fixture.WriteNode("project");
        await using var first = fixture.Jobs(); await using var second = fixture.Jobs();
        var id = first.Start(Request("watch", entry));
        await fixture.Process.WatchStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var blocked = await TerminalAsync(second, second.Start(Request("build", entry)));
        Assert.Equal("failed", blocked.State); Assert.Contains("Another Builder activation", blocked.Error);
        await first.CancelAsync(id); await TerminalAsync(first, id);
        Assert.Equal("succeeded", (await TerminalAsync(second, second.Start(Request("build", entry)))).State);
    }

    [Fact]
    public async Task LocalCreatorAndSdkOverridesAreScopedToOwnedCacheAndNewProject()
    {
        using var fixture = new Fixture();
        var creator = fixture.PathOf("creator.tgz"); var sdk = fixture.PathOf("sdk.tgz"); var tool = fixture.PathOf("tool.tgz");
        foreach (var path in new[] { creator, sdk, tool }) await File.WriteAllTextAsync(path, "fixture package bytes");
        var options = new BuilderProjectDriverOptions(fixture.PathOf("cache"))
        {
            CoordinatedPackageVersion = "1.1.0", NodeCreatorPackagePath = creator,
            NodePackageOverrides = new Dictionary<string, string> { ["@sunder/sdk"] = sdk, ["@sunder/package-tool"] = tool },
        };
        await using var jobs = new BuilderJobManager(new BuilderProjectDriver(fixture.Tools, fixture.Process, options));
        var destination = fixture.PathOf("created");
        var result = await TerminalAsync(jobs, jobs.Start(Request("create") with { ProjectFolder = destination, DisplayName = "Hello", PackageId = "sample.hello" }));
        Assert.True(result.State == "succeeded", result.Error);
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(destination, "package.json")));
        Assert.Equal("file:" + sdk.Replace('\\', '/'), json.RootElement.GetProperty("dependencies").GetProperty("@sunder/sdk").GetString());
        Assert.Equal("file:" + tool.Replace('\\', '/'), json.RootElement.GetProperty("devDependencies").GetProperty("@sunder/package-tool").GetString());
        Assert.Contains(fixture.Process.Requests, command => command.WorkingDirectory.StartsWith(options.CacheRootPath, StringComparison.Ordinal) && command.Arguments.Contains("--ignore-scripts"));
    }

    [UnixFact("The metadata output fixture requires /bin/sh.")]
    public async Task MetadataSubprocessStreamsButBoundsCapturedOutput()
    {
        using var fixture = new Fixture();
        var entry = fixture.WriteNode("project");
        Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(entry)!, ".sunder"));
        var expected = await new BuilderProjectDiscovery().InspectAsync(entry);
        Assert.True(expected.CanEditPackageId, expected.PackageIdReadOnlyReason);
        var cli = fixture.PathOf("metadata-fixture.sh");
        await File.WriteAllTextAsync(cli, "while IFS= read -r line; do :; done\nprintf '%65536s' x\nprintf '%65536s' y >&2\nexit 1\n");
        var reported = 0;
        var metadata = new BuilderProjectMetadataService("/bin/sh", cli, progress: new CallbackProgress(text => Interlocked.Add(ref reported, text.Length)));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => metadata.SaveAsync(expected, expected.DisplayName, "sample.renamed"));
        Assert.True(error.Message.Length < 17 * 1024); Assert.True(reported >= 128 * 1024);
        Assert.Equal(expected.PackageId, (await new BuilderProjectDiscovery().InspectAsync(entry)).PackageId);
    }

    private sealed class CallbackProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task WatchingAsync(BuilderJobManager jobs, string id, Func<bool>? condition = null)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var snapshot = jobs.GetJob(id);
            Assert.True(snapshot.State == "running", snapshot.Error);
            if (snapshot.Message.StartsWith("Watching source changes.", StringComparison.Ordinal) && (condition?.Invoke() ?? true)) return;
            await Task.Delay(20, deadline.Token);
        }
    }
    private static BuilderJobRequest Request(string operation, string? entry = null, BuilderProjectKind kind = BuilderProjectKind.Node)
        => new() { Id = Guid.NewGuid().ToString("N"), Operation = operation, Kind = kind, EntryPoint = entry };
    private static async Task<BuilderJobSnapshot> TerminalAsync(BuilderJobManager jobs, string id)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var value = jobs.GetJob(id);
            if (value.State != "running") return value;
            await Task.Delay(10, deadline.Token);
        }
    }

    private sealed class DelegateDriver(Func<BuilderJobRequest, IBuilderJobReporter, CancellationToken, Task> execute) : IBuilderJobDriver
    {
        public Task ExecuteAsync(BuilderJobRequest request, IBuilderJobReporter reporter, CancellationToken cancellationToken)
            => execute(request, reporter, cancellationToken);
    }

    private sealed class ObservingReporter(IBuilderJobReporter inner, Action<BuilderJobUpdate> observe) : IBuilderJobReporter
    {
        public void Report(string value) => inner.Report(value);
        public void Update(BuilderJobUpdate update) { inner.Update(update); observe(update); }
    }

    private static BuilderToolchainInstallation Installation(BuilderProjectKind kind, string root) => new()
    {
        Kind = kind, Version = kind == BuilderProjectKind.Dotnet ? "10.0.402" : "24.22.0", RootPath = root,
        ExecutablePath = Path.Combine(root, kind == BuilderProjectKind.Dotnet ? "dotnet" : "node"),
        NpmCliPath = kind == BuilderProjectKind.Node ? Path.Combine(root, "npm-cli.js") : null,
        NpmVersion = kind == BuilderProjectKind.Node ? "11.19.1" : null, IsManaged = true, IsHealthy = true,
    };

    private sealed class FakeTools(string root) : IBuilderProjectDriverToolchains
    {
        internal int Leases;
        internal int Selections;
        internal int Ensures;
        internal DateTimeOffset? InstalledAtUtc;
        internal IReadOnlyDictionary<string, string?> Environment = new Dictionary<string, string?>();
        internal Func<string, IProgress<string>?, CancellationToken, Task>? ProgressOperation;
        internal BuilderToolchainInstallation For(BuilderProjectKind kind) => Installation(kind, Path.Combine(root, kind.ToString())) with { InstalledAtUtc = InstalledAtUtc };
        public Task<BuilderToolchainStatus> GetStatusAsync(BuilderProjectKind kind, bool checkUpdates, CancellationToken cancellationToken)
            => Task.FromResult(new BuilderToolchainStatus(kind, [For(kind)], For(kind).Version, null, null, null));
        public async Task<BuilderToolchainInstallation> EnsureAsync(BuilderProjectKind kind, string? projectDirectory, IProgress<string>? progress, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Ensures);
            if (ProgressOperation is { } report) await report("ensure-sdk", progress, cancellationToken);
            return For(kind);
        }
        public async Task<IBuilderProjectDriverSelection> SelectAsync(BuilderProjectKind kind, string? projectDirectory, BuilderToolchainInstallation? preferred, IProgress<string>? progress, CancellationToken cancellationToken)
        {
            if (ProgressOperation is { } report) await report("select", progress, cancellationToken);
            Interlocked.Increment(ref Leases); Interlocked.Increment(ref Selections);
            return new Selection(preferred ?? For(kind), Environment, () => Interlocked.Decrement(ref Leases));
        }
        public async Task<BuilderToolchainInstallation> RepairAsync(BuilderToolchainInstallation installation, IProgress<string>? progress, CancellationToken cancellationToken)
        {
            if (ProgressOperation is { } report) await report("repair-sdk", progress, cancellationToken);
            return installation;
        }
        public Task RemoveAsync(BuilderToolchainInstallation installation, IProgress<string>? progress, CancellationToken cancellationToken)
            => ProgressOperation?.Invoke("remove-sdk", progress, cancellationToken) ?? Task.CompletedTask;
        private sealed class Selection(BuilderToolchainInstallation installation, IReadOnlyDictionary<string, string?> environment, Action release) : IBuilderProjectDriverSelection
        {
            public BuilderToolchainInstallation Installation => installation;
            public BuilderLocalProcessRequest CreateProcess(IReadOnlyList<string> arguments, string workingDirectory, bool npm = false)
                => new(installation.ExecutablePath, npm ? [installation.NpmCliPath!, .. arguments] : arguments, workingDirectory) { Environment = environment };
            public ValueTask DisposeAsync() { release(); return ValueTask.CompletedTask; }
        }
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "sunder-builder-local-backend", Guid.NewGuid().ToString("N"));
        internal FakeTools Tools { get; }
        internal FakeProcess Process { get; }
        internal Fixture() { Directory.CreateDirectory(Root); Tools = new(PathOf("tools")); Process = new(this); }
        internal string PathOf(string relative) => Path.Combine(Root, relative);
        internal BuilderJobManager Jobs(Action<BuilderJobUpdate>? observe = null)
        {
            var driver = new BuilderProjectDriver(Tools, Process,
                new(PathOf("cache")) { CoordinatedPackageVersion = "1.1.0", DotnetTemplateCacheRootPath = PathOf("templates") });
            return observe is null ? new(driver) : new(new DelegateDriver((request, reporter, token) =>
                driver.ExecuteAsync(request, new ObservingReporter(reporter, observe), token)));
        }
        internal string WriteDotnet(string folder, string filename = "Chosen.csproj", string id = "sample.hello", string name = "Hello package")
            => Dotnet(PathOf(folder), filename, id, name);
        internal async Task<BuilderDotnetBuildInputs> InputsAsync(string entry)
        {
            var project = await new BuilderProjectDiscovery().InspectAsync(entry);
            var installation = Tools.For(BuilderProjectKind.Dotnet);
            return await BuilderDotnetBuildInputs.DiscoverAsync(project, [PathOf("cache"), installation.RootPath], null, CancellationToken.None);
        }
        internal void TrackingPlan(string entry, string[]? sources = null, string[]? dependencyInputs = null, string[]? references = null)
        {
            // A notification-input fixture, not complete build/up-to-date authority.
            var path = Path.Combine(Path.GetDirectoryName(entry)!, "obj", "Debug", "net10.0", "sunder-analysis",
                BuilderToolchainPlatform.Current.DotnetRid, "sunder-rpc", "package-role-plan.v1.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(new
            {
                sourceFiles = (sources ?? []).Select(path => new { path }),
                workerDependencies = new { inputs = (dependencyInputs ?? []).Select(path => new { path }) },
                workerReferences = (references ?? []).Select(path => new { path }),
            }));
        }
        internal static string Dotnet(string folder, string filename, string id, string name)
        {
            Directory.CreateDirectory(folder);
            var entry = Path.Combine(folder, filename);
            File.WriteAllText(entry, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><SunderPackageProject>true</SunderPackageProject><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><PackageReference Include=\"Sunder.Sdk\" Version=\"1.1.0\" /></ItemGroup></Project>");
            File.WriteAllText(Path.Combine(folder, "PackageMetadata.cs"), $"using Sunder.Sdk.Packaging;\n[assembly: SunderPackage(Id = {JsonSerializer.Serialize(id)}, Name = {JsonSerializer.Serialize(name)})]\n");
            return entry;
        }
        internal string WriteNode(string folder) => Node(PathOf(folder), "sample.hello", "Hello package");
        internal static string Node(string folder, string id, string name)
        {
            Directory.CreateDirectory(Path.Combine(folder, "src"));
            File.WriteAllText(Path.Combine(folder, "sunder.package.json"), JsonSerializer.Serialize(new { schemaVersion = 3, id, name, version = "1.0.0", workerCapabilities = Array.Empty<string>(), services = new { module = "src/services.ts" } }));
            File.WriteAllText(Path.Combine(folder, "package.json"), JsonSerializer.Serialize(new
            {
                name = id.Replace('.', '-'), version = "1.0.0", engines = new { node = ">=24.18.1 <25.0.0" },
                scripts = new Dictionary<string, string> { ["sunder:dev:once"] = "sunder-package dev", ["sunder:dev"] = "sunder-package dev --watch", ["contracts:check"] = "sunder-package contracts check", ["contracts:freeze"] = "sunder-package contracts freeze", ["package"] = "sunder-package package" },
                dependencies = new Dictionary<string, string> { ["@sunder/sdk"] = "1.1.0" }, devDependencies = new Dictionary<string, string> { ["@sunder/package-tool"] = "1.1.0" },
            }));
            File.WriteAllText(Path.Combine(folder, "sunder-contracts.lock.json"), "{\"lockVersion\":1,\"packageArtifacts\":{},\"packageImports\":{},\"providerImports\":{},\"platformImports\":{}}\n");
            File.WriteAllText(Path.Combine(folder, "src", "services.ts"), "import { defineService, type ServiceContext } from \"@sunder/sdk\";\nconst packageName = " + JsonSerializer.Serialize(name) + ";\n" +
                "export const messages = defineService({ async send(message: string, context: ServiceContext): Promise<string> { return `${packageName}: ${message}`; } }, { constraints: { \"send.message\": { maxLength: 256 } } });\n");
            return Path.Combine(folder, "sunder.package.json");
        }
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }

    private sealed class FakeProcess(Fixture fixture) : IBuilderLocalProcessRunner
    {
        internal ConcurrentQueue<BuilderLocalProcessRequest> Requests { get; } = new();
        internal ConcurrentDictionary<string, bool> PinSeen { get; } = new();
        internal bool FailRestore;
        internal bool FailBuild;
        internal int Builds;
        internal Func<CancellationToken, Task>? BeforeBuild;
        internal Func<IProgress<string>?, CancellationToken, Task>? WatchOperation;
        internal Func<IProgress<string>?, CancellationToken, Task<BuilderLocalProcessResult?>>? BuildOperation;
        internal bool FailReview;
        internal bool FailTemplateInstall;
        internal const string TemplateFailure = "Unhandled exception. System.ArgumentException: The length of the name exceeds the maximum limit.\n   at Microsoft.TemplateEngine.Edge.Settings.AsyncMutex.WaitLoop(Object state)\n";
        internal bool InvalidRid;
        internal string PayloadLayer = "target";
        internal int Scaffolds;
        internal Action? AfterScaffold;
        internal TaskCompletionSource WatchStarted { get; } = NewSignal();
        internal TaskCompletionSource WatchStopped { get; } = NewSignal();

        public async Task<BuilderLocalProcessResult> RunAsync(BuilderLocalProcessRequest request, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
        {
            Requests.Enqueue(request); cancellationToken.ThrowIfCancellationRequested();
            var args = request.Arguments.ToArray();
            var npm = args[0] == fixture.Tools.For(BuilderProjectKind.Node).NpmCliPath;
            if (npm) args = args[1..];
            if (FailReview && npm && args.SequenceEqual(["run", "contracts:check"])) return new(1, "", "Public API review is required.");
            if (args[0] == "new")
            {
                PinSeen[request.WorkingDirectory] = File.Exists(Path.Combine(request.WorkingDirectory, "global.json"));
                if (args[1] == "install" && FailTemplateInstall)
                {
                    progress?.Report(TemplateFailure);
                    return new(134, "", TemplateFailure);
                }
                if (args[1] == "sunder-package")
                {
                    Fixture.Dotnet(Value(args, "--output"), Value(args, "--name") + ".csproj", Value(args, "--packageId"), Value(args, "--packageName"));
                    Scaffolds++; AfterScaffold?.Invoke();
                }
            }
            else if (args[0].Contains("create-sunder-package", StringComparison.Ordinal) && args[0].EndsWith("cli.js", StringComparison.Ordinal))
            {
                Fixture.Node(Value(args, "--output"), Value(args, "--package-id"), Value(args, "--package-name"));
                Scaffolds++; AfterScaffold?.Invoke();
            }
            else if (npm && args[0] == "install" && request.WorkingDirectory.Contains("node-creators", StringComparison.Ordinal))
            {
                foreach (var package in new[] { "create-sunder-package", "@sunder/sdk" })
                {
                    var directory = Path.Combine(request.WorkingDirectory, "node_modules", package, "dist"); Directory.CreateDirectory(directory);
                    File.WriteAllText(Path.Combine(directory, "..", "package.json"), JsonSerializer.Serialize(new { name = package, version = "1.1.0" }));
                    File.WriteAllText(Path.Combine(directory, "cli.js"), "creator");
                }
            }
            else if (args[0] is "restore" or "install" or "ci")
            {
                if (FailRestore) { progress?.Report("dependency restore failed\n"); return new(1, "", "restore failed token=restore-secret"); }
            }
            else if (args[0] == "watch" || npm && args.SequenceEqual(["run", "sunder:dev"]))
            {
                WatchStarted.SetResult();
                try
                {
                    if (WatchOperation is { } operation) await operation(progress, cancellationToken);
                    else await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                finally { WatchStopped.SetResult(); }
            }
            else if (args[0] == "msbuild" && !args.Any(argument => argument.StartsWith("-t:", StringComparison.Ordinal))
                || npm && args.SequenceEqual(["run", "sunder:dev:once"]))
            {
                if (!npm && args.Contains("-restore") && FailRestore)
                { progress?.Report("integrated dependency restore failed\n"); return new(1, "", "restore failed"); }
                Interlocked.Increment(ref Builds);
                if (BeforeBuild is { } before) await before(cancellationToken);
                if (BuildOperation is { } operation && await operation(progress, cancellationToken) is { } result) return result;
                if (FailBuild) return new(1, "", "source compilation failed");
                var output = npm ? Path.Combine(request.WorkingDirectory, "dist", "sunder-dev") : Path.Combine(Property(args, "SunderPackageComposedOutputRoot"), "sunder-dev");
                var entry = npm ? Path.Combine(request.WorkingDirectory, "sunder.package.json") : args[1];
                var project = await new BuilderProjectDiscovery().InspectAsync(entry, cancellationToken);
                await WriteOutputAsync(output, project.PackageId, InvalidRid, PayloadLayer);
            }
            else if (args[0] == "msbuild" || npm && args.SequenceEqual(["run", "package"]))
            {
                var entry = npm ? Path.Combine(request.WorkingDirectory, "sunder.package.json") : args[1];
                var project = await new BuilderProjectDiscovery().InspectAsync(entry, cancellationToken);
                var archive = npm ? Path.Combine(request.WorkingDirectory, "dist", project.PackageId + ".1.0.0.sunderpkg") : Property(args, "SunderPackageOutputPath");
                var payload = Path.Combine(request.WorkingDirectory, "obj", "archive-fixture");
                await WriteOutputAsync(payload, project.PackageId, false, PayloadLayer);
                Directory.CreateDirectory(Path.GetDirectoryName(archive)!);
                using var zip = ZipFile.Open(archive, ZipArchiveMode.Create);
                foreach (var file in Directory.EnumerateFiles(payload, "*", SearchOption.AllDirectories)) zip.CreateEntryFromFile(file, Path.GetRelativePath(payload, file).Replace('\\', '/'));
            }
            progress?.Report("successful command output\n");
            return new(0, "sunder-package successful command output\n", "");
        }

        private static string Value(string[] args, string name) => args[Array.IndexOf(args, name) + 1];
        private static string Property(string[] args, string name) => Uri.UnescapeDataString(args.Single(argument => argument.StartsWith("-p:" + name + "=", StringComparison.Ordinal))[(name.Length + 4)..]);
        private static async Task WriteOutputAsync(string root, string id, bool badRid, string layer)
        {
            var rid = badRid ? "wrong-x64" : BuilderToolchainPlatform.Current.DotnetRid;
            var prefix = layer switch { "shared" => "payload/shared", "role-shared" or "overlap" => "payload/runtime/shared",
                "foreign" => "payload/runtime/" + (rid == "win-x64" ? "osx-x64" : "win-x64"), _ => "payload/runtime/" + rid };
            Directory.CreateDirectory(Path.Combine(root, "manifest")); Directory.CreateDirectory(Path.Combine(root, prefix, "bin"));
            Directory.CreateDirectory(Path.Combine(root, "payload", "app", "shared"));
            await File.WriteAllTextAsync(Path.Combine(root, prefix, "bin", "worker.cjs"), "worker");
            if (layer == "overlap")
            {
                Directory.CreateDirectory(Path.Combine(root, "payload", "runtime", rid, "bin"));
                await File.WriteAllTextAsync(Path.Combine(root, "payload", "runtime", rid, "bin", "worker.cjs"), "worker");
            }
            await File.WriteAllTextAsync(Path.Combine(root, "payload", "app", "shared", "index.html"), "app");
            await File.WriteAllTextAsync(Path.Combine(root, "manifest", "sunder-package.json"), JsonSerializer.Serialize(new
            {
                archiveFormatVersion = 1, manifestVersion = 1, id, version = "1.0.0", targets = new[]
                {
                    new { role = "runtime", rid, kind = "worker", entryPoint = "bin/worker.cjs" },
                    new { role = "app", rid, kind = "web", entryPoint = "index.html" },
                },
            }));
            var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Where(path => Path.GetFileName(path) != "content-index.json")
                .Select(path => new { path = Path.GetRelativePath(root, path).Replace('\\', '/'), size = new FileInfo(path).Length,
                    sha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))) }).ToArray();
            await File.WriteAllTextAsync(Path.Combine(root, "manifest", "content-index.json"), JsonSerializer.Serialize(new { schemaVersion = 1, files }));
        }
    }
}
