extern alias DockerWorker;

using DockerWorker::Sunder.Package.Agent.Execution.Docker;
using Sunder.Package.Agent.TestSupport;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Worker;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class DockerPresentationSourceMigrationTests
{
    internal static readonly string[] SourcePaths =
    [
        "src/Sunder.Package.Agent.Execution.Docker/Rpc/ManagementService.cs",
        "src/Sunder.Package.Agent.Execution.Docker/Rpc/ManagementClient.cs",
        "src/Sunder.Package.Agent.Execution.Docker/Rpc/ImageCatalogContent.cs",
    ];

    [AgentPublicSourceMigrationFact]
    public async Task PresentationSource_PreservesContractCatalogContentAndRealOperationsAcrossIsolatedApp()
    {
        using var source = PrivateManagementSourceFixture.Load("Execution.Docker", RegressionTestRpcDescriptors.Get("sunder.package.agent.execution.docker.presentation"));
        using var package = RegressionTestPackageScope.Create();
        var context = new TestPackageContext("sunder.package.agent.execution.docker", package.RootPath, storage: package.Context.Storage);
        var runner = new TestDockerCli(context);
        var catalog = new DockerImageCatalogService(context, runner);
        var operations = new Operations(new DockerExecutionPresentationRpcHandler(context, runner, catalog));
        var type = source.Worker.GetType("Sunder.Package.Agent.Execution.Docker.Rpc.ManagementService", true)!;
        var service = AgentRoleBoundary.Create(type, operations);
        var registration = Assert.IsType<SunderWorkerProviderRegistration>(type.GetMethod("CreateSunderRpcProviderRegistration")!.Invoke(service, null));
        Assert.Equal("docker.presentation", registration.ProviderId);
        using var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
        state.Add(context.PackageId, source.Descriptor, service, registration.Handler, Noop.Instance, registration.ProviderId, "2.0.0");
        var client = AgentRoleBoundary.As<IDockerExecutionPresentationClient>(AgentRoleBoundary.Create(source.App.GetType("Sunder.Package.Agent.Execution.Docker.App.DockerExecutionSourceClient", true)!,
            new RegressionTestRpcClient(state, context.PackageId)));
        var initial = await client.ExecuteAsync(new(DockerExecutionOperationKind.GetSettings));
        Assert.Equal("300", initial.TimeoutSeconds);
        Assert.Empty(initial.Images!);
        Assert.True((await client.ExecuteAsync(new(DockerExecutionOperationKind.SaveSettings, "47"))).Success);
        Assert.Equal("47", (await client.ExecuteAsync(new(DockerExecutionOperationKind.GetSettings))).TimeoutSeconds);
        var invalid = await client.ExecuteAsync(new(DockerExecutionOperationKind.SaveSettings, "0"));
        Assert.False(invalid.Success);
        Assert.Equal("docker.timeout.invalid", invalid.Error!.Code);
        Assert.False(string.IsNullOrWhiteSpace(invalid.Error.CorrelationId));
        const string image = "example.com/source/image:dev";
        var added = await client.ExecuteAsync(new(DockerExecutionOperationKind.AddImage, ImageReference: image));
        Assert.Equal(image, Assert.Single(added.Images!).ImageReference);
        Assert.Equal(DockerImageStatus.NotPulled, added.Images![0].Status);
        var refreshed = await client.ExecuteAsync(new(DockerExecutionOperationKind.RefreshImage, ImageReference: image));
        Assert.Equal(DockerImageStatus.Ready, Assert.Single(refreshed.Images!).Status);
        Assert.True((await client.ExecuteAsync(new(DockerExecutionOperationKind.RefreshImages))).Success);
        var pulled = await client.ExecuteAsync(new(DockerExecutionOperationKind.PullImage, ImageReference: image));
        Assert.True(pulled.Success);
        Assert.NotNull(Assert.Single(pulled.Images!).LastPulledAtUtc);
        Assert.True((await client.ExecuteAsync(new(DockerExecutionOperationKind.TestDocker))).Success);
        Assert.NotEmpty(runner.Calls);
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ExecuteAsync(new(DockerExecutionOperationKind.DeleteImage, ImageReference: image), cancelled.Token).AsTask());
            Assert.Single(await catalog.ListImagesAsync());
        }
        Assert.Empty((await client.ExecuteAsync(new(DockerExecutionOperationKind.DeleteImage, ImageReference: image))).Images!);

        var images = Enumerable.Range(0, 2500).Select(index => new DockerImageDefinition($"example.com/source/image:{index}",
            DockerImageStatus.Ready, DateTimeOffset.UtcNow, new string('x', 1024))).ToArray();
        operations.Response = new(Images: images, CatalogRevision: 42);
        var large = await client.ExecuteAsync(new(DockerExecutionOperationKind.GetSettings));
        Assert.Equal(images, large.Images);
        Assert.Equal(42, large.CatalogRevision);
        state.FailNextResponseContentRegistration(new IOException("private docker content failure"));
        var fault = await Assert.ThrowsAsync<SunderRpcException>(() => client.ExecuteAsync(new(DockerExecutionOperationKind.GetSettings)).AsTask());
        Assert.Equal(SunderRpcErrorKind.ProviderFaulted, fault.Error.Kind);
        Assert.DoesNotContain("private docker content failure", fault.Message, StringComparison.Ordinal);
        operations.Response = new(Images: [new(image, DockerImageStatus.Ready, null, new string('x', 8 * 1024 * 1024))]);
        var oversized = await Assert.ThrowsAsync<SunderRpcException>(() => client.ExecuteAsync(new(DockerExecutionOperationKind.GetSettings)).AsTask());
        Assert.Equal(SunderRpcErrorKind.ProviderFaulted, oversized.Error.Kind);
        await state.RetireAsync(service);
        var unavailable = await Assert.ThrowsAsync<SunderRpcException>(() => client.ExecuteAsync(new(DockerExecutionOperationKind.GetSettings)).AsTask());
        Assert.Equal("docker.presentation.unavailable", unavailable.Error.Code);
    }

    private sealed class Operations(IDockerExecutionPresentationClient inner) : IDockerExecutionPresentationClient
    {
        internal DockerExecutionOperationResponse? Response { get; set; }
        public ValueTask<DockerExecutionOperationResponse> ExecuteAsync(DockerExecutionOperationRequest request, CancellationToken cancellationToken = default)
            => Response is { } response ? ValueTask.FromResult(response) : inner.ExecuteAsync(request, cancellationToken);
    }

    private sealed class TestDockerCli(IPackageContext context) : DockerCliRunner(context)
    {
        internal List<string[]> Calls { get; } = [];
        protected override Task<string> ResolveEndpointAsync(CancellationToken cancellationToken) => Task.FromResult("unix:///var/run/docker.sock");
        protected override Task<DockerCliRunResult> RunCoreAsync(IReadOnlyList<string> args, int timeoutSeconds, CancellationToken cancellationToken,
            string? standardInput = null, IProgress<string>? progress = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add(args.ToArray());
            return Task.FromResult(new DockerCliRunResult(0, string.Empty, TimedOut: false, WasTruncated: false));
        }
    }

    private sealed class Noop : IDisposable { internal static Noop Instance { get; } = new(); public void Dispose() { } }
}
