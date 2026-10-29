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

public sealed partial class BuilderLocalBackendTests
{
    [Fact]
    public void PrivateRpcSurfaceIsLocalAndFinite()
    {
        var methods = typeof(BuilderRuntimeService).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(method => method.CustomAttributes.Any(attribute => attribute.AttributeType.Name == "SunderMethodAttribute"));
        Assert.Equal(new[] { "CancelJobAsync", "CompleteDevelopmentCommandAsync", "ConnectDevelopmentAppAsync", "DiscoverAsync", "GetBackendIdAsync", "GetJobAsync", "GetToolchainsAsync", "InspectAsync", "ReadDevelopmentCommandAsync", "StartJobAsync" },
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
        var context = new BuilderInspectionOnlyContext();
        var pin = Path.Combine(Path.GetDirectoryName(entry)!, "global.json");
        const string originalPin = "{\"sdk\":{\"version\":\"10.0.100\",\"rollForward\":\"disable\"}}";
        await File.WriteAllTextAsync(pin, originalPin);
        await using var service = new BuilderRuntimeService(context); // backend/storage access still fails immediately
        await using var content = await service.InspectAsync(entry);
        await using var stream = await content.OpenReadAsync(default);
        using var json = await JsonDocument.ParseAsync(stream);
        Assert.Equal("sample.hello", json.RootElement.GetProperty("packageId").GetString());
        Assert.Equal(entry, json.RootElement.GetProperty("entryPoint").GetString());
        Assert.True(json.RootElement.GetProperty("toolchainAssessment").GetProperty("requiresUpgrade").GetBoolean());
        Assert.False(Directory.Exists(Path.Combine(Path.GetDirectoryName(entry)!, "obj")));
        await using var discovered = await service.DiscoverAsync(fixture.Root);
        await using var discoveryStream = await discovered.OpenReadAsync(default);
        using var list = await JsonDocument.ParseAsync(discoveryStream);
        Assert.Single(list.RootElement.EnumerateArray());
        Assert.True(context.VersionReads >= 2);
        Assert.Equal(0, context.ForbiddenAccesses);
        Assert.Equal(originalPin, await File.ReadAllTextAsync(pin));
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
    [InlineData("foreign-token")]
    [InlineData("tokenless")]
    [InlineData("cleanup-failure")]
    public async Task CancellationRequestDoesNotDisguiseForeignOrCleanupFailure(string kind)
    {
        var entered = NewSignal();
        var driver = new DelegateDriver(async (_, _, token) =>
        {
            entered.SetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally
            {
                if (kind == "cleanup-failure") throw new IOException("cleanup failed");
                throw new OperationCanceledException("foreign cancellation", kind == "tokenless" ? default : new CancellationToken(true));
            }
        });
        await using var jobs = new BuilderJobManager(driver);
        var id = jobs.Start(Request("ensure-sdk"));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await jobs.CancelAsync(id);
        var terminal = await TerminalAsync(jobs, id);
        Assert.Equal("failed", terminal.State);
        Assert.Contains(kind == "cleanup-failure" ? "cleanup failed" : "foreign cancellation", terminal.Error);
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
    public async Task FiniteBuildExcludesSameProjectButAllowsIndependentWork()
    {
        using var fixture = new Fixture();
        var entered = NewSignal();
        var driver = new DelegateDriver(async (request, _, token) =>
        {
            if (request.Operation == "build" && request.ProjectFolder == Path.Combine(fixture.Root, "first")) { entered.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); }
        });
        await using var jobs = new BuilderJobManager(driver, new() { MaximumConcurrentJobs = 2 });
        var project = fixture.WriteNode("first");
        var build = jobs.Start(Request("build", project));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Throws<BuilderJobBusyException>(() => jobs.Start(Request("build", project)));
        var other = jobs.Start(Request("build", fixture.WriteNode("other")));
        Assert.Equal("succeeded", (await TerminalAsync(jobs, other)).State);
        await jobs.CancelAsync(build);
        Assert.Equal("cancelled", (await TerminalAsync(jobs, build)).State);
        Assert.Equal("succeeded", (await TerminalAsync(jobs, jobs.Start(Request("archive", project)))).State);
    }

    [Fact]
    public async Task HeartbeatsRenewFiniteJobLeaseAndOrphansStop()
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
        var id = jobs.Start(Request("build", fixture.WriteNode("project")));
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
    [InlineData(BuilderProjectKind.Dotnet)]
    [InlineData(BuilderProjectKind.Node)]
    public async Task FiniteBuildOwnsExactSdkLeaseUntilItsChildDrainsAndNeverStartsAWatcher(BuilderProjectKind kind)
    {
        using var fixture = new Fixture();
        var entry = kind == BuilderProjectKind.Dotnet ? fixture.WriteDotnet("project") : fixture.WriteNode("project");
        await using var jobs = fixture.Jobs();
        var entered = NewSignal(); var cancelled = NewSignal(); var release = NewSignal();
        fixture.Process.BeforeBuild = async token =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { cancelled.TrySetResult(); await release.Task; }
        };
        var id = jobs.Start(Request("build", entry, kind));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await jobs.CancelAsync(id);
        try
        {
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, fixture.Tools.Leases);
            Assert.Equal("running", jobs.GetJob(id).State);
            Assert.Null(jobs.GetJob(id).LastSuccessfulBuildAtUtc);
            Assert.Throws<BuilderJobBusyException>(() => jobs.Start(Request("build", entry, kind)));
        }
        finally { release.TrySetResult(); }
        Assert.Equal("cancelled", (await TerminalAsync(jobs, id)).State);
        Assert.Equal(0, fixture.Tools.Leases);
        var command = Assert.Single(fixture.Process.Requests);
        Assert.Equal(fixture.Tools.For(kind).ExecutablePath, command.ExecutablePath);
        Assert.NotNull(command.Timeout);
        Assert.DoesNotContain("sunder:dev", command.Arguments);
        Assert.DoesNotContain("watch", command.Arguments);
        if (kind == BuilderProjectKind.Dotnet)
        {
            Assert.Contains("-restore", command.Arguments);
            Assert.Contains("--disable-build-servers", command.Arguments);
            Assert.Contains("-p:UseSharedCompilation=false", command.Arguments);
            Assert.Equal("1", command.Environment["MSBUILDDISABLENODEREUSE"]);
            Assert.Equal("0", command.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"]);
            Assert.Equal("1", command.Environment["AVALONIA_TELEMETRY_OPTOUT"]);
        }
    }

    [Fact]
    public async Task RemovedWatchRequestsAreRejectedBeforeToolchainOrProcessAccess()
    {
        using var fixture = new Fixture();
        await using var jobs = fixture.Jobs();
        Assert.Throws<ArgumentException>(() => jobs.Start(Request("watch", fixture.WriteNode("project"))));
        Assert.Empty(fixture.Process.Requests);
        Assert.Equal(0, fixture.Tools.Selections);
    }

    [Theory]
    [InlineData(BuilderProjectKind.Dotnet)]
    [InlineData(BuilderProjectKind.Node)]
    public async Task FailedFiniteBuildRetainsLastGoodOutputAndRequiresExplicitRetry(BuilderProjectKind kind)
    {
        using var fixture = new Fixture();
        var entry = kind == BuilderProjectKind.Dotnet ? fixture.WriteDotnet("project") : fixture.WriteNode("project");
        await using var jobs = fixture.Jobs();
        var setup = await TerminalAsync(jobs, jobs.Start(Request("setup", entry, kind)));
        Assert.Equal("succeeded", setup.State);
        var output = setup.DevelopmentFolder!;
        var before = await File.ReadAllBytesAsync(Path.Combine(output, "manifest", "content-index.json"));
        fixture.Process.FailBuild = true;
        var failed = await TerminalAsync(jobs, jobs.Start(Request("build", entry, kind)));
        Assert.Equal("failed", failed.State);
        Assert.Equal(before, await File.ReadAllBytesAsync(Path.Combine(output, "manifest", "content-index.json")));
        Assert.Null(failed.LastSuccessfulBuildAtUtc);
        Assert.Equal(0, fixture.Tools.Leases);
        var failedBuilds = fixture.Process.Builds;
        fixture.Process.FailBuild = false;
        await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(entry)!, "Fixed.cs"), "internal class Fixed { }");
        await Task.Delay(350);
        Assert.Equal(failedBuilds, fixture.Process.Builds); // Neither elapsed time nor a save restarts work.
        fixture.Tools.InstalledAtUtc = DateTimeOffset.UtcNow;
        var retried = await TerminalAsync(jobs, jobs.Start(Request("build", entry, kind)));
        Assert.Equal("succeeded", retried.State);
        Assert.Equal(failedBuilds + 1, fixture.Process.Builds);
        Assert.Equal(fixture.Tools.InstalledAtUtc, retried.Installation!.InstalledAtUtc);
        Assert.Equal(0, fixture.Tools.Leases);
    }

    [Fact]
    public async Task FiniteBuildDoesNotSpawnSuccessorsWhenSourceChangesDuringCompilation()
    {
        using var fixture = new Fixture();
        var entry = fixture.WriteDotnet("project");
        await using var jobs = fixture.Jobs();
        var entered = NewSignal(); var drain = NewSignal();
        fixture.Process.BeforeBuild = async token =>
        {
            entered.TrySetResult();
            await drain.Task.WaitAsync(token);
        };
        var build = jobs.Start(Request("build", entry, BuilderProjectKind.Dotnet));
        var source = Path.Combine(Path.GetDirectoryName(entry)!, "Changed.cs");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            for (var i = 0; i < 4; i++)
            {
                await File.WriteAllTextAsync(source, $"internal class Latest{i} {{ }}");
                await Task.Delay(80);
            }
            Assert.Equal(1, fixture.Process.Builds);
            Assert.Equal("running", jobs.GetJob(build).State);
            Assert.Equal(1, fixture.Tools.Leases);
        }
        finally { drain.TrySetResult(); }
        Assert.Equal("succeeded", (await TerminalAsync(jobs, build)).State);
        await File.WriteAllTextAsync(source, "internal class AfterBuild { }");
        await Task.Delay(350);
        Assert.Equal(1, fixture.Process.Builds);
        Assert.Equal(0, fixture.Tools.Leases);
    }

    [Theory]
    [InlineData("build", false)]
    [InlineData("build", true)]
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
        var id = jobs.Start(Request("build", entry, kind));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Null(jobs.GetJob(id).DevelopmentFolder);
        }
        finally { release.TrySetResult(); }
        Assert.Equal("succeeded", (await TerminalAsync(jobs, id)).State);
        Assert.Equal(2, fixture.Process.Builds);
        await BuilderProjectDriverOutputs.ValidateDevelopmentAsync(jobs.GetJob(id).DevelopmentFolder!, setup.Project!,
            BuilderToolchainPlatform.Current.DotnetRid, requireApp: false, CancellationToken.None);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SeparateActivationCannotBuildUntilTheExactFiniteProcessReleasesProjectLock(bool differentFeedCache)
    {
        using var fixture = new Fixture();
        var entry = fixture.WriteNode("project");
        var options = new BuilderProjectDriverOptions(fixture.PathOf("cache"));
        await using var first = new BuilderJobManager(new BuilderProjectDriver(fixture.Tools, fixture.Process, options));
        await using var second = new BuilderJobManager(new BuilderProjectDriver(fixture.Tools, fixture.Process,
            differentFeedCache ? options with { CacheRootPath = fixture.PathOf("another-retained-feed/tools") } : options));
        var entered = NewSignal();
        fixture.Process.BeforeBuild = async token => { entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); };
        var id = first.Start(Request("build", entry));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var blocked = await TerminalAsync(second, second.Start(Request("build", entry)));
        Assert.Equal("failed", blocked.State); Assert.Contains("Another Builder activation", blocked.Error);
        await first.CancelAsync(id); await TerminalAsync(first, id);
        fixture.Process.BeforeBuild = null;
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
        internal ConcurrentQueue<BuilderLocalProcessRequest> Evaluations { get; } = new();
        internal string EvaluationFramework = "net10.0";
        internal ConcurrentDictionary<string, bool> PinSeen { get; } = new();
        internal bool FailRestore;
        internal bool FailBuild;
        internal int Builds;
        internal Func<CancellationToken, Task>? BeforeBuild;
        internal Func<IProgress<string>?, CancellationToken, Task<BuilderLocalProcessResult?>>? BuildOperation;
        internal string WorkerContents = "worker";
        internal bool FailReview;
        internal bool FailTemplateInstall;
        internal const string TemplateFailure = "Unhandled exception. System.ArgumentException: The length of the name exceeds the maximum limit.\n   at Microsoft.TemplateEngine.Edge.Settings.AsyncMutex.WaitLoop(Object state)\n";
        internal bool InvalidRid;
        internal string PayloadLayer = "target";
        internal int Scaffolds;
        internal Action? AfterScaffold;

        public async Task<BuilderLocalProcessResult> RunAsync(BuilderLocalProcessRequest request, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
        {
            if (request.Arguments.Contains("-getProperty:TargetFramework"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                Evaluations.Enqueue(request);
                return new(0, EvaluationFramework + "\n", "");
            }
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
                Assert.Fail("Package Maker must not start an automatic source watcher.");
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
                var output = npm ? Path.Combine(request.WorkingDirectory, "dist", "sunder-dev") : Path.Combine(
                    Property(args, "SunderPackageComposedOutputRoot"), "sunder-dev");
                var entry = npm ? Path.Combine(request.WorkingDirectory, "sunder.package.json") : args[1];
                var project = await new BuilderProjectDiscovery().InspectAsync(entry, cancellationToken);
                await WriteOutputAsync(output, project.PackageId, InvalidRid, PayloadLayer, WorkerContents, npm ? "node24" : EvaluationFramework);
            }
            else if (args[0] == "msbuild" || npm && args.SequenceEqual(["run", "package"]))
            {
                var entry = npm ? Path.Combine(request.WorkingDirectory, "sunder.package.json") : args[1];
                var project = await new BuilderProjectDiscovery().InspectAsync(entry, cancellationToken);
                var archive = npm ? Path.Combine(request.WorkingDirectory, "dist", project.PackageId + ".1.0.0.sunderpkg") : Property(args, "SunderPackageOutputPath");
                var payload = Path.Combine(request.WorkingDirectory, "obj", "archive-fixture");
                await WriteOutputAsync(payload, project.PackageId, false, PayloadLayer, framework: npm ? "node24" : EvaluationFramework);
                Directory.CreateDirectory(Path.GetDirectoryName(archive)!);
                using var zip = ZipFile.Open(archive, ZipArchiveMode.Create);
                foreach (var file in Directory.EnumerateFiles(payload, "*", SearchOption.AllDirectories)) zip.CreateEntryFromFile(file, Path.GetRelativePath(payload, file).Replace('\\', '/'));
            }
            progress?.Report("successful command output\n");
            return new(0, "sunder-package successful command output\n", "");
        }

        private static string Value(string[] args, string name) => args[Array.IndexOf(args, name) + 1];
        private static string Property(string[] args, string name) => Uri.UnescapeDataString(args.Single(argument => argument.StartsWith("-p:" + name + "=", StringComparison.Ordinal))[(name.Length + 4)..]);
        private static async Task WriteOutputAsync(string root, string id, bool badRid, string layer, string workerContents = "worker", string framework = "node24")
        {
            var rid = badRid ? "wrong-x64" : BuilderToolchainPlatform.Current.DotnetRid;
            var prefix = layer switch { "shared" => "payload/shared", "role-shared" or "overlap" => "payload/runtime/shared",
                "foreign" => "payload/runtime/" + (rid == "win-x64" ? "osx-x64" : "win-x64"), _ => "payload/runtime/" + rid };
            Directory.CreateDirectory(Path.Combine(root, "manifest")); Directory.CreateDirectory(Path.Combine(root, prefix, "bin"));
            Directory.CreateDirectory(Path.Combine(root, "payload", "app", "shared"));
            await File.WriteAllTextAsync(Path.Combine(root, prefix, "bin", "worker.cjs"), workerContents);
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
                    new { role = "runtime", rid, kind = "worker", entryPoint = "bin/worker.cjs", targetFramework = framework },
                    new { role = "app", rid, kind = "web", entryPoint = "index.html", targetFramework = "web" },
                },
            }));
            var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Where(path => Path.GetFileName(path) != "content-index.json")
                .Select(path => new { path = Path.GetRelativePath(root, path).Replace('\\', '/'), size = new FileInfo(path).Length,
                    sha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))) }).ToArray();
            await File.WriteAllTextAsync(Path.Combine(root, "manifest", "content-index.json"), JsonSerializer.Serialize(new { schemaVersion = 1, files }));
        }
    }
}
