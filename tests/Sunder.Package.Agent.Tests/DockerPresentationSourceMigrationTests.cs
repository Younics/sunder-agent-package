extern alias DockerWorker;

using DockerWorker::Sunder.Package.Agent.Execution.Docker;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
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
        "src/Sunder.Package.Agent.Execution.Docker/DockerExecutionPresentationService.cs",
        "src/Sunder.Package.Agent.Execution.Docker/Runtime/DockerPresentationContent.cs",
    ];

    [AgentPublicSourceMigrationFact]
    public async Task PresentationSource_UsesNaturalManagementOperationsAndBoundedContent()
    {
        using var source = PrivateManagementSourceFixture.Load("Execution.Docker", CurrentPrivateRpcDescriptors.Get("sunder.package.agent.execution.docker.presentation"));
        using var package = RegressionTestPackageScope.Create();
        var context = new TestPackageContext("sunder.package.agent.execution.docker", package.RootPath, storage: package.Context.Storage);
        var runner = new TestDockerCli(context);
        var catalog = new DockerImageCatalogService(context, runner);
        var service = new DockerExecutionPresentationService(context, runner, catalog);
        var descriptor = CurrentPrivateRpcDescriptors.Get("sunder.package.agent.execution.docker.presentation");
        using var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
        var rpc = new RegressionTestRpcClient(state, context.PackageId);
        await using var generated = new GeneratedServiceTestScope(rpc);
        var registration = generated.Register(service, descriptor.ContractId);
        Assert.Equal("docker.presentation", registration.ProviderId);
        state.Add(context.PackageId, descriptor, service, registration.Handler, Noop.Instance, registration.ProviderId, "2.0.0");
        await using var app = AgentSourceArtifactMigrationTests.CreateGeneratedApp(source.App, context, rpc);
        var client = app.GetRequiredService(source.App.GetType("Sunder.Generated.Packages.Sunder_Package_Agent_Execution_Docker_Services.IPresentationService", true)!);
        Assert.Same(source.App, client.GetType().Assembly);
        var initial = await ReadAsync(AgentRoleBoundary.Call(client, "GetSettingsAsync", CancellationToken.None)!);
        Assert.Equal("300", initial.TimeoutSeconds);
        Assert.Empty(initial.Images!);
        Assert.True((await ReadAsync(AgentRoleBoundary.Call(client, "SaveSettingsAsync", "47", null, CancellationToken.None)!)).Success);
        Assert.Equal("47", (await ReadAsync(AgentRoleBoundary.Call(client, "GetSettingsAsync", CancellationToken.None)!)).TimeoutSeconds);
        var invalid = await ReadAsync(AgentRoleBoundary.Call(client, "SaveSettingsAsync", "0", null, CancellationToken.None)!);
        Assert.False(invalid.Success);
        Assert.Equal("docker.timeout.invalid", invalid.Error!.Code);
        Assert.False(string.IsNullOrWhiteSpace(invalid.Error.CorrelationId));
        const string image = "example.com/source/image:dev";
        var added = await ReadAsync(AgentRoleBoundary.Call(client, "AddImageAsync", image, CancellationToken.None)!);
        Assert.Equal(image, Assert.Single(added.Images!).ImageReference);
        Assert.Equal(DockerImageStatus.NotPulled, added.Images![0].Status);
        var refreshed = await ReadAsync(AgentRoleBoundary.Call(client, "RefreshImageAsync", image, CancellationToken.None)!);
        Assert.Equal(DockerImageStatus.Ready, Assert.Single(refreshed.Images!).Status);
        Assert.True((await ReadAsync(AgentRoleBoundary.Call(client, "RefreshImagesAsync", CancellationToken.None)!)).Success);
        var pulled = await ReadAsync(AgentRoleBoundary.Call(client, "PullImageAsync", image, CancellationToken.None)!);
        Assert.True(pulled.Success);
        Assert.NotNull(Assert.Single(pulled.Images!).LastPulledAtUtc);
        Assert.True((await ReadAsync(AgentRoleBoundary.Call(client, "TestDockerAsync", CancellationToken.None)!)).Success);
        Assert.NotEmpty(runner.Calls);
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AgentRoleBoundary.Await(
                AgentRoleBoundary.Call(client, "DeleteImageAsync", image, cancelled.Token)!));
            Assert.Single(await catalog.ListImagesAsync());
        }
        Assert.Empty((await ReadAsync(AgentRoleBoundary.Call(client, "DeleteImageAsync", image, CancellationToken.None)!)).Images!);

        var images = Enumerable.Range(0, 2500).Select(index => new DockerImageDefinition(
            $"example.com/source/image:{index}", DockerImageStatus.Ready, DateTimeOffset.UtcNow, new string((char)120, 512))).ToArray();
        await state.RetireAsync(service);
        var catalogState = new CatalogReadStore(SerializeCatalog(images));
        var contentContext = new TestPackageContext(context.PackageId, package.RootPath,
            storage: new TestPackageStorageContext(context.Storage.Files, catalogState, context.Storage.RoleLocalWorkspace),
            settings: context.Settings, secrets: context.Secrets, callbacks: context.Callbacks, logging: context.Logging);
        var contentCatalog = new DockerImageCatalogService(contentContext, runner);
        var contentService = new DockerExecutionPresentationService(contentContext, runner, contentCatalog);
        var contentRegistration = generated.Register(contentService, descriptor.ContractId);
        state.Add(context.PackageId, descriptor, contentService, contentRegistration.Handler, Noop.Instance, contentRegistration.ProviderId, "2.0.0");
        var large = await ReadAsync(AgentRoleBoundary.Call(client, "GetSettingsAsync", CancellationToken.None)!);
        Assert.Equal(images.OrderBy(value => value.ImageReference, StringComparer.OrdinalIgnoreCase), large.Images);
        state.FailNextResponseContentRegistration(new IOException("private docker content failure"));
        var fault = await Assert.ThrowsAsync<SunderRpcException>(() => AgentRoleBoundary.Await(
            AgentRoleBoundary.Call(client, "GetSettingsAsync", CancellationToken.None)!));
        Assert.Equal(SunderRpcErrorKind.ProviderFaulted, fault.Error.Kind);
        Assert.DoesNotContain("private docker content failure", fault.Message, StringComparison.Ordinal);

        catalogState.Replace(SerializeCatalog(Enumerable.Range(0, 20000).Select(index => new DockerImageDefinition(
            $"example.com/source/oversized:{index}", DockerImageStatus.Ready, null, new string((char)120, 512)))));
        var oversized = await Assert.ThrowsAsync<SunderRpcException>(() => AgentRoleBoundary.Await(
            AgentRoleBoundary.Call(client, "GetSettingsAsync", CancellationToken.None)!));
        Assert.Equal(SunderRpcErrorKind.ProviderFaulted, oversized.Error.Kind);

        await state.RetireAsync(contentService);
        await Assert.ThrowsAsync<SunderRpcException>(() => AgentRoleBoundary.Await(
            AgentRoleBoundary.Call(client, "GetSettingsAsync", CancellationToken.None)!));

        static async ValueTask<DockerExecutionOperationResponse> ReadAsync(object pending)
        {
            var result = await AgentRoleBoundary.Await(pending);
            if (result is Sunder.Sdk.Services.SunderContent content)
            {
                await using (content)
                    return await DockerPresentationContent.ReadAsync(content, default);
            }
            return AgentRoleBoundary.As<DockerExecutionOperationResponse>(result!);
        }

        static string SerializeCatalog(IEnumerable<DockerImageDefinition> images)
            => JsonSerializer.Serialize(new DockerImageCatalogState(DockerImageCatalogService.CurrentSchemaVersion, 1, images.ToArray()),
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    private sealed class Noop : IDisposable { internal static Noop Instance { get; } = new(); public void Dispose() { } }

    // The production catalog reads historical state before projecting it into bounded content.
    // This source models that read path without allowing an oversized state write through test storage.
    private sealed class CatalogReadStore(string catalog) : IPackageKeyValueStore
    {
        private string _catalog = catalog;

        internal void Replace(string catalog) => _catalog = catalog;

        public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TestPackageStorageValidation.Key(key);
            return Task.FromResult<string?>(key == DockerImageCatalogService.ImagesKey ? _catalog : null);
        }

        public Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TestPackageStorageValidation.Key(key);
            TestPackageStorageValidation.Value(value);
            return Task.FromException(new InvalidOperationException("The controlled catalog source is read-only."));
        }

        public Task<bool> ContainsKeyAsync(string key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TestPackageStorageValidation.Key(key);
            return Task.FromResult(key == DockerImageCatalogService.ImagesKey);
        }

        public Task DeleteValueAsync(string key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TestPackageStorageValidation.Key(key);
            return Task.FromException(new InvalidOperationException("The controlled catalog source is read-only."));
        }

        public Task<IReadOnlyList<string>> ListKeysAsync(string? prefix = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TestPackageStorageValidation.Prefix(prefix);
            return Task.FromResult<IReadOnlyList<string>>(prefix is null || DockerImageCatalogService.ImagesKey.StartsWith(prefix, StringComparison.Ordinal)
                ? [DockerImageCatalogService.ImagesKey] : []);
        }
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

}
