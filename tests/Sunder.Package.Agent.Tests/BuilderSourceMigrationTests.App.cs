using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Worker;
using Sunder.Sdk.Abstractions;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class BuilderSourceMigrationTests
{
    internal static async Task VerifyAppAsync(string root, Assembly worker, Assembly assembly, IPackageContext context,
        SunderRpcContractDescriptor descriptor, SunderWorkerProviderRegistration registration,
        IAsyncDisposable workerLifetime, RegressionTestRpcState state, RegressionTestRpcClient client)
    {
        var exportPath = Path.Combine(CanonicalPackageRoles.Analysis(root), "package-export.v1.json");
        var exportBytes = File.ReadAllBytes(exportPath);
        using (var receipt = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(CanonicalPackageRoles.Analysis(root), "app-authoring-receipt.v1.json"))))
        {
            Assert.Empty(receipt.RootElement.GetProperty("providers").EnumerateArray());
            var use = Assert.Single(receipt.RootElement.GetProperty("uses").EnumerateArray());
            Assert.Equal("app", use.GetProperty("role").GetString());
            Assert.True(use.GetProperty("required").GetBoolean());
        }
        var folder = Directory.CreateDirectory(Path.Combine(context.Storage.RoleLocalWorkspace.WorkspaceRootPath, "project with spaces")).FullName;
        var entry = Path.Combine(folder, "Chosen.App.csproj");
        await File.WriteAllTextAsync(entry, """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><SunderPackageProject>true</SunderPackageProject></PropertyGroup>
            <Target Name="BeforeBuild"><Error Text="Inspection must never execute MSBuild" /></Target></Project>
            """);
        var metadata = Path.Combine(folder, "PackageMetadata.cs");
        await File.WriteAllTextAsync(metadata, "using Sunder.Sdk.Packaging;\n[assembly: SunderPackage(Id = \"sample.builder\", Name = \"Builder 漢字\")]\n");
        var sourceBytes = File.ReadAllBytes(metadata);
        var gatewayType = assembly.GetType("Sunder.Package.Agent.Builder.BuilderBackendGateway", true)!;
        var runtimeType = assembly.GetType("Sunder.Generated.Packages.Younics_Sunder_Package_Maker_Services.IBuilderRuntimeService", true)!;
        var observations = new SourceCaptureObservations(context.PackageId);
        using (var missingState = new RegressionTestRpcState())
        {
            await using var missingServices = AgentSourceArtifactMigrationTests.CreateGeneratedApp(assembly, context,
                new RegressionTestRpcClient(missingState, context.PackageId), new CompositionBackgroundProcessQueue(), observations);
            var missing = await Assert.ThrowsAsync<SunderRpcException>(() => Invoke(missingServices.GetRequiredService(gatewayType), "DiscoverAsync", folder, CancellationToken.None));
            Assert.Equal("rpc.provider-not-found", missing.Error.Code);
        }
        state.Add("unrelated.package", descriptor, new object(), registration.Handler, NoopDisposable.Instance, "builder.unrelated", "2.0.0");
        await using var app = AgentSourceArtifactMigrationTests.CreateGeneratedApp(assembly, context, client, new CompositionBackgroundProcessQueue(), observations);
        // This lower-boundary fixture supplies no positive Host cleanup evidence.
        // Production application jobs additionally require the mandatory journal.
        // Exercise the emitted constructor binding, not a manually repaired
        // gateway that could hide a missing nullable Host-service injection.
        Assert.Same(observations, app.GetRequiredService<IPackageWorkerExecutionObservations>());
        var gateway = app.GetRequiredService(gatewayType);
        var runtime = app.GetRequiredService(runtimeType);
        var discovered = Value(await Invoke(gateway, "DiscoverAsync", folder, CancellationToken.None));
        Assert.Equal(entry, Assert.Single(discovered.EnumerateArray()).GetProperty("entryPoint").GetString());
        var inspected = (await Invoke(gateway, "InspectAsync", entry, CancellationToken.None))!;
        Assert.Same(assembly, inspected.GetType().Assembly);
        Assert.Equal("Builder 漢字", Value(inspected).GetProperty("displayName").GetString());
        Assert.Equal(sourceBytes, File.ReadAllBytes(metadata));
        Assert.False(Directory.Exists(Path.Combine(folder, "obj")));

        var invalid = await Assert.ThrowsAsync<SunderRpcException>(() => Invoke(gateway, "InspectAsync", "relative.csproj", CancellationToken.None));
        Assert.Equal("agent.builder.invalid-request", invalid.Error.Code);
        var oversized = await Assert.ThrowsAsync<SunderRpcException>(() => Invoke(runtime, "StartJobAsync", new string('x', 64 * 1024), CancellationToken.None));
        Assert.Equal("agent.builder.request-too-large", oversized.Error.Code);
        var duplicate = await Assert.ThrowsAsync<SunderRpcException>(() => Invoke(runtime, "StartJobAsync", "{\"operation\":\"build\",\"operation\":\"create\",\"kind\":0}", CancellationToken.None));
        Assert.Equal("agent.builder.invalid-request", duplicate.Error.Code);
        var endpoint = Assert.Single((await client.DiscoverAsync(descriptor.ContractId)).Providers, provider => provider.PackageId == context.PackageId).Endpoint;
        var shape = await Assert.ThrowsAsync<SunderRpcException>(() => client.InvokeAsync(endpoint, "builder-runtime", "inspect", JsonSerializer.SerializeToElement(new { })).AsTask());
        Assert.Equal(SunderRpcErrorKind.Validation, shape.Error.Kind);
        AssertResponseBounds(worker);
        AssertResponseBounds(assembly);
        state.FailNextResponseContentRegistration(new IOException("private-content-store-failure"));
        var contentFailure = await Assert.ThrowsAsync<SunderRpcException>(() => Invoke(gateway, "InspectAsync", entry, CancellationToken.None));
        Assert.Equal(SunderRpcErrorKind.ProviderFaulted, contentFailure.Error.Kind);
        Assert.DoesNotContain("private-content-store-failure", contentFailure.Message, StringComparison.Ordinal);
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Invoke(gateway, "DiscoverAsync", folder, cancelled.Token));
        }

        var nodeKind = Enum.Parse(assembly.GetType("Sunder.Package.Agent.Builder.BuilderProjectKind", true)!, "Node");
        var status = Value(await Invoke(gateway, "GetToolchainsAsync", nodeKind, false, CancellationToken.None));
        Assert.Equal(1, status.GetProperty("kind").GetInt32());
        Assert.Equal(JsonValueKind.Null, status.GetProperty("lastCheckedAtUtc").ValueKind);
        var firstCapture = (await Invoke(gateway, "CaptureExecutionAsync", CancellationToken.None))!;
        var backendId = (string)firstCapture.GetType().GetProperty("BackendId")!.GetValue(firstCapture)!;
        var absent = await Assert.ThrowsAsync<SunderRpcException>(() => Invoke(runtime, "GetJobAsync", "absent-job", backendId, CancellationToken.None));
        Assert.Equal("agent.builder.job-not-found", absent.Error.Code);

        object Request(string id, string name) => JsonSerializer.SerializeToElement(new
        {
            id, backendId, operation = "save-metadata", kind = 0, entryPoint = entry, expected = Value(inspected), displayName = name, packageId = "sample.builder",
        }, BuilderJson).Deserialize(assembly.GetType("Sunder.Package.Agent.Builder.BuilderJobRequest", true)!, BuilderJson)!;
        var jobs = new CapturedSourceJobs(gateway, backendId);
        Task<string> Start(object request) => jobs.StartAsync(request);
        Task<string> WorkerRetry(object request) => WorkerRetryAsync(runtime, request);
        Task<JsonElement> Terminal(string id) => WaitForJob(jobs, id, backendId, value => value.GetProperty("state").GetString() != "running");

        // Hold the real metadata transaction lock so cancellation and activation disposal
        // exercise the generated RPC -> production driver -> cancellable filesystem path.
        using (var editLock = new FileStream(Path.Combine(folder, ".sunder-builder-metadata.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            var request = Request("cancelled-job", "Never saved");
            var job = await Start(request);
            await WaitForJob(jobs, job, backendId, value => value.GetProperty("message").GetString() == "Project source inspected.");
            // Worker deduplication is a separate private-contract assertion. The
            // production captured App path never issues a second Start attempt.
            Assert.Equal(job, await WorkerRetry(request));
            var conflict = await Assert.ThrowsAsync<SunderRpcException>(() => WorkerRetry(Request(job, "Different retry")));
            Assert.Equal("agent.builder.invalid-request", conflict.Error.Code);
            var busy = await Assert.ThrowsAsync<SunderRpcException>(() => Start(Request("overlapping-job", "Concurrent save")));
            Assert.Equal("agent.builder.busy", busy.Error.Code);
            await jobs.CancelJobAsync(job, CancellationToken.None);
            Assert.Equal("cancelled", (await Terminal(job)).GetProperty("state").GetString());
            await jobs.CancelJobAsync(job, CancellationToken.None);
        }
        Assert.Equal(sourceBytes, File.ReadAllBytes(metadata));
        var saved = await Terminal(await Start(Request("completed-job", "Saved through generated RPC")));
        Assert.Equal("succeeded", saved.GetProperty("state").GetString());
        Assert.Equal("Saved through generated RPC", saved.GetProperty("project").GetProperty("displayName").GetString());
        Assert.Contains("Saved through generated RPC", File.ReadAllText(metadata), StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(folder, "obj")));
        var stale = await Terminal(await Start(Request("stale-edit-job", "Must not overwrite")));
        Assert.Equal("failed", stale.GetProperty("state").GetString());
        Assert.Contains("changed", stale.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);

        var alternate = new object();
        state.Add(context.PackageId, descriptor, alternate, registration.Handler, NoopDisposable.Instance, "builder.alternate", "2.0.0");
        var ambiguous = await Assert.ThrowsAsync<SunderRpcException>(() => Invoke(gateway, "InspectAsync", entry, CancellationToken.None));
        Assert.Equal("rpc.provider-ambiguous", ambiguous.Error.Code);
        await state.RetireAsync(alternate);
        inspected = (await Invoke(gateway, "InspectAsync", entry, CancellationToken.None))!;
        var beforeShutdown = File.ReadAllBytes(metadata);
        using (var editLock = new FileStream(Path.Combine(folder, ".sunder-builder-metadata.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            var stopping = await Start(Request("shutdown-job", "Must drain without saving"));
            await WaitForJob(jobs, stopping, backendId, value => value.GetProperty("message").GetString() == "Project source inspected.");
            await state.RetireAsync(registration.Handler).WaitAsync(TimeSpan.FromSeconds(5));
            await workerLifetime.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            await workerLifetime.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Equal(beforeShutdown, File.ReadAllBytes(metadata));
        var retired = await Assert.ThrowsAsync<SunderRpcException>(() => client.InvokeAsync(endpoint, "builder-runtime", "get-job",
            JsonSerializer.SerializeToElement(new { jobId = "shutdown-job", expectedBackendId = backendId })).AsTask());
        Assert.Equal(SunderRpcErrorKind.StaleEndpoint, retired.Error.Kind);
        Assert.Equal(exportBytes, File.ReadAllBytes(exportPath));
    }

    private static async Task<string> WorkerRetryAsync(object runtime, object request)
        => (string)(await Invoke(runtime, "StartJobAsync", JsonSerializer.Serialize(request, request.GetType(), BuilderJson), CancellationToken.None))!;

    private sealed class CapturedSourceJobs(object gateway, string backendId)
    {
        private readonly Dictionary<string, object> _captures = new(StringComparer.Ordinal);
        internal async Task<string> StartAsync(object request)
        {
            var capture = (await Invoke(gateway, "CaptureExecutionAsync", CancellationToken.None))!;
            Assert.Equal(backendId, capture.GetType().GetProperty("BackendId")!.GetValue(capture));
            var id = Value(request).GetProperty("id").GetString()!;
            Assert.True(_captures.TryAdd(id, capture), "This fixture must not replay a captured App Start.");
            return (string)(await Invoke(capture, "StartJobAsync", request, CancellationToken.None))!;
        }
        public Task<object?> GetJobAsync(string id, string expectedBackendId, CancellationToken token)
        {
            Assert.Equal(backendId, expectedBackendId);
            return Invoke(_captures[id], "GetJobAsync", id, token);
        }
        internal Task<object?> CancelJobAsync(string id, CancellationToken token)
            => Invoke(_captures[id], "CancelJobAsync", id, token);
    }

    private sealed class SourceCaptureObservations(string packageId) : IPackageWorkerExecutionObservations
    {
        private readonly Guid _fixtureHost = Guid.NewGuid();
        public async Task<PackageWorkerExecutionReference> CaptureAsync<TService>(Sunder.Sdk.Services.ServiceProvider<TService> provider,
            CancellationToken cancellationToken = default) where TService : class
        {
            Assert.Equal(packageId, provider.PackageId);
            await provider.EnsureCurrentAsync(cancellationToken);
            return new(_fixtureHost, provider.ActivationId.ToString("N"));
        }
        public Task<PackageWorkerExecutionObservation> ObserveAsync(PackageWorkerExecutionReference reference, CancellationToken cancellationToken = default)
            => Task.FromResult(new PackageWorkerExecutionObservation(reference, PackageWorkerExecutionState.CleanupPending));
    }

    private sealed class NoopDisposable : IDisposable
    {
        public static NoopDisposable Instance { get; } = new();
        public void Dispose() { }
    }
}
