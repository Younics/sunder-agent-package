using System.Collections.Concurrent;
using System.Text.Json;
using Sunder.Package.Agent.Execution.Docker;
using Sunder.Package.Agent.Tests;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Logging;
using Xunit;

namespace Sunder.Package.Agent.Execution.Local.Tests;

public sealed class DockerCatalogConsistencyTests : IDisposable
{
    private static readonly JsonSerializerOptions CatalogJsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "sunder-docker-catalog-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ListImagesAsync_IsPureAndPerformsNoWrites()
    {
        var context = new CountingPackageContext(_root);
        var catalog = new DockerImageCatalogService(context);

        Assert.Empty(await catalog.ListImagesAsync());
        Assert.Empty(await catalog.ListImagesAsync());

        Assert.Equal(0, context.State.WriteCount);
        Assert.Empty(await context.State.ListKeysAsync());
    }

    [Fact]
    public async Task AddAndDelete_EachCommitExactlyOnceAndReturnComputedSnapshot()
    {
        var context = new CountingPackageContext(_root);
        var catalog = new DockerImageCatalogService(context);

        var added = await catalog.AddImageAndListAsync("example/image:1.2.3");

        Assert.Equal(1, context.State.WriteCount);
        Assert.Equal(1, added.Snapshot.Revision);
        Assert.Equal("example/image:1.2.3", Assert.Single(added.Snapshot.Images).ImageReference);

        var deleted = await catalog.DeleteImageAndListAsync("example/image:1.2.3");

        Assert.Equal(2, context.State.WriteCount);
        Assert.Equal(2, deleted.Revision);
        Assert.Empty(deleted.Images);
    }

    [Fact]
    public async Task ConcurrentCatalogInstances_SerializeMutationsWithoutLostUpdate()
    {
        var context = new CountingPackageContext(_root);
        var first = new DockerImageCatalogService(context);
        var second = new DockerImageCatalogService(context);

        await Task.WhenAll(
            first.AddImageAsync("first/image:1.0"),
            second.AddImageAsync("second/image:2.0"));

        var snapshot = await first.GetSnapshotAsync();
        Assert.Equal(2, snapshot.Revision);
        Assert.Equal(
            ["first/image:1.0", "second/image:2.0"],
            snapshot.Images.Select(image => image.ImageReference).ToArray());
        Assert.Equal(2, context.State.WriteCount);
    }

    [Fact]
    public async Task DeleteDuringRefresh_DoesNotResurrectImageFromStaleCompletion()
    {
        var context = new CountingPackageContext(_root);
        var runner = new BlockingDockerCliRunner(context);
        var first = new DockerImageCatalogService(context, runner);
        var second = new DockerImageCatalogService(context, runner);
        await first.AddImageAsync("example/image:1.2.3");

        var refresh = first.RefreshImageAsync("example/image:1.2.3");
        await runner.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await second.DeleteImageAsync("example/image:1.2.3");
        runner.Release.TrySetResult();
        await refresh;

        var snapshot = await first.GetSnapshotAsync();
        Assert.Equal(2, snapshot.Revision);
        Assert.Empty(snapshot.Images);
        Assert.Equal(2, context.State.WriteCount);
    }

    [Fact]
    public async Task NewerSameImageRefresh_PreventsOlderCompletionFromOverwritingStatus()
    {
        var context = new CountingPackageContext(_root);
        var runner = new SequencedDockerCliRunner(context, 2);
        var first = new DockerImageCatalogService(context, runner);
        var second = new DockerImageCatalogService(context, runner);
        await first.AddImageAsync("example/image:1.2.3");

        var older = first.RefreshImageAsync("example/image:1.2.3");
        await runner.Calls[0].Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var newer = second.RefreshImageAsync("example/image:1.2.3");
        await runner.Calls[1].Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        runner.Calls[1].Response.TrySetResult(new DockerCliRunResult(
            0,
            string.Empty,
            TimedOut: false,
            WasTruncated: false));
        await newer;
        runner.Calls[0].Response.TrySetResult(new DockerCliRunResult(
            1,
            "older failure",
            TimedOut: false,
            WasTruncated: false));
        await older;

        var snapshot = await first.GetSnapshotAsync();
        var image = Assert.Single(snapshot.Images);
        Assert.Equal(DockerImageStatus.Ready, image.Status);
        Assert.Equal("Image is ready.", image.LastMessage);
        Assert.Equal(2, snapshot.Revision);
        Assert.Equal(2, context.State.WriteCount);
    }

    [Fact]
    public async Task FailedReadinessReservation_DoesNotSupersedeBlockedSuccessfulPull()
    {
        var context = new CountingPackageContext(_root);
        var runner = new SequencedDockerCliRunner(context, 3);
        var first = new DockerImageCatalogService(context, runner);
        var second = new DockerImageCatalogService(context, runner);
        await first.AddImageAsync("example/image:1.2.3");

        var pull = first.PullImageAsync("example/image:1.2.3");
        await runner.Calls[0].Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var readiness = second.GetReadinessAsync("example/image:1.2.3");
        await runner.Calls[1].Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        runner.Calls[1].Response.TrySetResult(new DockerCliRunResult(
            1,
            "readiness unavailable",
            TimedOut: false,
            WasTruncated: false));
        Assert.False((await readiness).IsReady);

        runner.Calls[0].Response.TrySetResult(new DockerCliRunResult(
            0,
            string.Empty,
            TimedOut: false,
            WasTruncated: false));
        await runner.Calls[2].Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        runner.Calls[2].Response.TrySetResult(new DockerCliRunResult(
            0,
            string.Empty,
            TimedOut: false,
            WasTruncated: false));
        var pullResult = await pull;

        Assert.True(pullResult.Success);
        var image = Assert.Single(await first.ListImagesAsync());
        Assert.Equal(DockerImageStatus.Ready, image.Status);
        Assert.NotNull(image.LastPulledAtUtc);
        Assert.Equal("Image is ready.", image.LastMessage);
    }

    [Fact]
    public async Task OlderBlockedReadiness_CannotOverwriteNewerSuccessfulPull()
    {
        var context = new CountingPackageContext(_root);
        var runner = new SequencedDockerCliRunner(context, 3);
        var first = new DockerImageCatalogService(context, runner);
        var second = new DockerImageCatalogService(context, runner);
        await first.AddImageAsync("example/image:1.2.3");

        var readiness = first.GetReadinessAsync("example/image:1.2.3");
        await runner.Calls[0].Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var pull = second.PullImageAsync("example/image:1.2.3");
        await runner.Calls[1].Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        runner.Calls[1].Response.TrySetResult(new DockerCliRunResult(
            0,
            string.Empty,
            TimedOut: false,
            WasTruncated: false));
        await runner.Calls[2].Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        runner.Calls[2].Response.TrySetResult(new DockerCliRunResult(
            0,
            string.Empty,
            TimedOut: false,
            WasTruncated: false));
        var pullResult = await pull;
        Assert.True(pullResult.Success);

        runner.Calls[0].Response.TrySetResult(new DockerCliRunResult(
            0,
            string.Empty,
            TimedOut: false,
            WasTruncated: false));
        var readinessResult = await readiness;

        Assert.True(readinessResult.IsReady);
        var image = Assert.Single(await first.ListImagesAsync());
        Assert.Equal(DockerImageStatus.Ready, image.Status);
        Assert.Equal(pullResult.Image.LastPulledAtUtc, image.LastPulledAtUtc);
        Assert.NotNull(image.LastPulledAtUtc);
    }

    [Fact]
    public async Task RefreshImagesResponse_UsesImagesAndRevisionFromSameInterleavedSnapshot()
    {
        var context = new CountingPackageContext(_root);
        var runner = new ImmediateDockerCliRunner(context);
        var catalog = new DockerImageCatalogService(context, runner);
        await catalog.AddImageAsync("example/image:1.2.3");
        catalog.ImagesChanged += () => context.State.RunAfterNextGet(json =>
        {
            var state = JsonSerializer.Deserialize<DockerImageCatalogState>(json!, CatalogJsonOptions)!;
            var interleaved = new DockerImageCatalogState(
                state.SchemaVersion,
                checked(state.Revision + 1),
                [.. state.Images, new DockerImageDefinition(
                    "interleaved/image:2.0",
                    DockerImageStatus.NotPulled,
                    null,
                    "Interleaved mutation.")]);
            context.State.ReplaceValue(
                DockerImageCatalogService.ImagesKey,
                JsonSerializer.Serialize(interleaved, CatalogJsonOptions));
        });
        var config = new DockerExecutionWorkspaceConfigService(context, catalog);
        var editor = new DockerExecutionWorkspaceEditorContributor(config, catalog);
        var handler = new DockerExecutionRuntimeOperationHandler(context, runner, catalog, editor);

        var response = await handler.HandleAsync(new DockerExecutionOperationRequest(
            DockerExecutionOperationKind.RefreshImages));

        Assert.True(response.Success);
        Assert.Equal(3, response.CatalogRevision);
        Assert.Equal(
            ["example/image:1.2.3", "interleaved/image:2.0"],
            response.Images!.Select(image => image.ImageReference).ToArray());
    }

    [Theory]
    [InlineData("{ malformed", "docker.catalog.malformed")]
    [InlineData("{\"schemaVersion\":99,\"revision\":1,\"images\":[]}", "docker.catalog.unsupported-schema")]
    public async Task MalformedOrFutureCatalog_IsPreservedAndSurfacedWithoutRepairWrite(
        string json,
        string expectedCode)
    {
        var context = new CountingPackageContext(_root);
        await context.State.SetValueAsync(DockerImageCatalogService.ImagesKey, json);
        context.State.ResetWriteCount();
        var migration = new DockerPackageStorageMigration(context);
        await migration.StartAsync();
        var catalog = new DockerImageCatalogService(context, new DockerCliRunner(context), migration);

        var exception = await Assert.ThrowsAsync<DockerExecutionDomainException>(
            () => catalog.ListImagesAsync());

        Assert.Equal(expectedCode, exception.Code);
        Assert.Equal(json, await context.State.GetValueAsync(DockerImageCatalogService.ImagesKey));
        Assert.Equal(0, context.State.WriteCount);
    }

    [Theory]
    [InlineData("{\"schemaVersion\":2,\"revision\":1,\"images\":[],\"retentionPolicy\":\"keep\"}")]
    [InlineData("{\"schemaVersion\":2,\"revision\":1,\"images\":[{\"imageReference\":\"known/image:1.0\",\"status\":0,\"lastPulledAtUtc\":null,\"lastMessage\":null,\"sourceDigest\":\"keep\"}]}")]
    public async Task CurrentCatalog_UnknownRootOrImageDataBlocksMutationWithoutDroppingMetadata(string json)
    {
        var context = new CountingPackageContext(_root);
        await context.State.SetValueAsync(DockerImageCatalogService.ImagesKey, json);
        context.State.ResetWriteCount();
        var catalog = new DockerImageCatalogService(context);

        var exception = await Assert.ThrowsAsync<DockerExecutionDomainException>(
            () => catalog.AddImageAsync("new/image:2.0"));

        Assert.Equal("docker.catalog.unknown-data", exception.Code);
        Assert.Equal(json, await context.State.GetValueAsync(DockerImageCatalogService.ImagesKey));
        Assert.Equal(0, context.State.WriteCount);
    }

    [Theory]
    [InlineData("{\"Version\":1,\"Images\":[],\"RetentionPolicy\":\"keep\"}")]
    [InlineData("{\"Version\":1,\"Images\":[{\"ImageReference\":\"known/image:1.0\",\"Status\":0,\"LastPulledAtUtc\":null,\"LastMessage\":null,\"SourceDigest\":\"keep\"}]}")]
    public async Task LegacyCatalog_UnknownRootOrImageDataBlocksMigrationWithoutDroppingMetadata(string json)
    {
        var context = new CountingPackageContext(_root);
        await context.State.SetValueAsync(DockerImageCatalogService.ImagesKey, json);
        context.State.ResetWriteCount();

        var exception = await Assert.ThrowsAsync<DockerExecutionDomainException>(
            () => new DockerPackageStorageMigration(context).EnsureAsync());

        Assert.Equal("docker.catalog.unknown-data", exception.Code);
        Assert.Equal(json, await context.State.GetValueAsync(DockerImageCatalogService.ImagesKey));
        Assert.Equal(0, context.State.WriteCount);
    }

    [Fact]
    public async Task SemanticMigration_PreservesFloatingReferencesAndMovesAuthoritativeSettingsIdempotently()
    {
        var context = new CountingPackageContext(_root);
        const string bindingId = "legacy-binding";
        var workspaceKey = DockerExecutionWorkspaceConfigService.BuildKey(bindingId);
        var cliPath = Path.GetFullPath(Path.Combine(_root, "docker"));
        await context.State.SetValueAsync(
            DockerImageCatalogService.ImagesKey,
            "{\"Version\":1,\"Images\":[{\"ImageReference\":\"legacy/image:latest\",\"Status\":0,\"LastPulledAtUtc\":null,\"LastMessage\":null}]}");
        await context.State.SetValueAsync(
            workspaceKey,
            "{\"ImageReference\":\"legacy/image:latest\",\"ContainerName\":\"legacy-container\",\"ShellPath\":\"/bin/sh\",\"PathEntries\":[]}");
        await context.State.SetValueAsync(DockerExecutionConfiguration.TimeoutKey, "123");
        await context.State.SetValueAsync(DockerCli.ExecutablePathConfigurationKey, cliPath);
        context.State.ResetWriteCount();

        var catalog = new DockerImageCatalogService(context);
        var image = Assert.Single(await catalog.ListImagesAsync());
        var config = await new DockerExecutionWorkspaceConfigService(context, catalog)
            .GetConfigAsync(bindingId);

        Assert.Equal(DockerImageStatus.NeedsAttention, image.Status);
        Assert.Equal("legacy/image:latest", image.ImageReference);
        Assert.True(config.ImageReferenceNeedsAttention);
        Assert.Equal("legacy/image:latest", config.ImageReference);
        Assert.Equal("123", await context.SettingsStore.GetStoredValueAsync(DockerExecutionConfiguration.TimeoutKey));
        Assert.Equal(cliPath, await context.SettingsStore.GetStoredValueAsync(DockerCli.ExecutablePathConfigurationKey));
        Assert.Null(await context.State.GetValueAsync(DockerExecutionConfiguration.TimeoutKey));
        Assert.Null(await context.State.GetValueAsync(DockerCli.ExecutablePathConfigurationKey));
        var writesAfterMigration = context.State.WriteCount;

        await new DockerPackageStorageMigration(context).EnsureAsync();

        Assert.Equal(writesAfterMigration, context.State.WriteCount);
    }

    [Fact]
    public async Task SemanticMigration_UnknownWorkspaceDataFailsClosedAndPreservesDocument()
    {
        var context = new CountingPackageContext(_root);
        var key = DockerExecutionWorkspaceConfigService.BuildKey("unknown-data");
        const string json = "{\"ImageReference\":\"known:1.0\",\"UnknownPolicy\":true}";
        await context.State.SetValueAsync(key, json);
        context.State.ResetWriteCount();

        var migration = new DockerPackageStorageMigration(context);
        await migration.StartAsync();
        var exception = await Assert.ThrowsAsync<DockerExecutionDomainException>(
            () => migration.EnsureAsync());

        Assert.Equal("docker.workspace-config.unknown-data", exception.Code);
        Assert.Equal(json, await context.State.GetValueAsync(key));
        Assert.Equal(0, context.State.WriteCount);
    }

    [Fact]
    public async Task StartAsync_InternalSemanticFailureRemainsStartupFatal()
    {
        var context = new CountingPackageContext(_root);
        context.State.ListFailure = new IOException("injected internal failure");

        var exception = await Assert.ThrowsAsync<IOException>(
            () => new DockerPackageStorageMigration(context).StartAsync());

        Assert.Equal("injected internal failure", exception.Message);
    }

    [Fact]
    public async Task StartAsync_InvalidLegacySettingDoesNotAbortAndCachesTypedFailure()
    {
        var context = new CountingPackageContext(_root);
        await context.State.SetValueAsync(DockerExecutionConfiguration.TimeoutKey, "unbounded");
        context.State.ResetWriteCount();
        var migration = new DockerPackageStorageMigration(context);

        await migration.StartAsync();
        var exception = await Assert.ThrowsAsync<DockerExecutionDomainException>(
            () => migration.EnsureAsync());

        Assert.Equal("docker.migration.timeout-invalid", exception.Code);
        Assert.Equal("unbounded", await context.State.GetValueAsync(DockerExecutionConfiguration.TimeoutKey));
        Assert.Null(await context.SettingsStore.GetStoredValueAsync(DockerExecutionConfiguration.TimeoutKey));
        Assert.Equal(0, context.State.WriteCount);
    }

    [Fact]
    public async Task SemanticMigration_FutureWorkspaceDataIsPreservedForANewerPackage()
    {
        var context = new CountingPackageContext(_root);
        var key = DockerExecutionWorkspaceConfigService.BuildKey("future-data");
        const string json = "{\"schemaVersion\":99,\"futurePolicy\":true}";
        await context.State.SetValueAsync(key, json);
        context.State.ResetWriteCount();

        await new DockerPackageStorageMigration(context).EnsureAsync();

        Assert.Equal(json, await context.State.GetValueAsync(key));
        Assert.Equal(0, context.State.WriteCount);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"two\"")]
    public async Task SemanticMigration_MalformedWorkspaceSchemaIsTypedAndPreserved(
        string schemaVersion)
    {
        var context = new CountingPackageContext(_root);
        var key = DockerExecutionWorkspaceConfigService.BuildKey("malformed-schema");
        var json = $"{{\"schemaVersion\":{schemaVersion}}}";
        await context.State.SetValueAsync(key, json);
        context.State.ResetWriteCount();

        var migration = new DockerPackageStorageMigration(context);
        await migration.StartAsync();
        var exception = await Assert.ThrowsAsync<DockerExecutionDomainException>(
            () => migration.EnsureAsync());

        Assert.Equal("docker.workspace-config.malformed", exception.Code);
        Assert.Equal(json, await context.State.GetValueAsync(key));
        Assert.Equal(0, context.State.WriteCount);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class CountingPackageContext
        : IPackageContext
    {
        private readonly string _root;

        internal CountingPackageContext(string root)
        {
            _root = root;
            Directory.CreateDirectory(root);
            State = new CountingKeyValueStore();
            SettingsStore = new InMemorySettings();
            Storage = new TestStorageContext(root, State);
        }

        public string PackageId => "test.docker";

        public string Version => "1.0.0";

        public string ContentRootPath => _root;

        public IPackageStorageContext Storage { get; }

        public IPackageSettings Settings => SettingsStore;

        public InMemorySettings SettingsStore { get; }

        public IPackageSecrets Secrets { get; } = new InMemoryPackageSecrets();

        public IPackageLogging Logging => NullPackageLogging.Instance;

        internal CountingKeyValueStore State { get; }
    }

    private sealed class TestStorageContext(string root, IPackageKeyValueStore state)
        : IPackageStorageContext
    {
        public IPackageFileStore Files { get; } = new TestPackageFileStoreBase(Path.Combine(root, "files"));

        public IPackageKeyValueStore State { get; } = state;

        public IPackageRoleLocalWorkspace RoleLocalWorkspace { get; } = new TestPackageRoleLocalWorkspace(root);
    }

    private sealed class CountingKeyValueStore : IPackageKeyValueStore
    {
        private readonly ConcurrentDictionary<string, string> _values = new(StringComparer.Ordinal);
        private Action<string?>? _afterNextGet;
        private int _writeCount;

        internal int WriteCount => Volatile.Read(ref _writeCount);

        internal Exception? ListFailure { get; set; }

        internal void ResetWriteCount() => Interlocked.Exchange(ref _writeCount, 0);

        internal void RunAfterNextGet(Action<string?> callback)
            => Interlocked.Exchange(ref _afterNextGet, callback);

        internal void ReplaceValue(string key, string value)
        {
            _values[key] = value;
            Interlocked.Increment(ref _writeCount);
        }

        public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var value = _values.GetValueOrDefault(key);
            Interlocked.Exchange(ref _afterNextGet, null)?.Invoke(value);
            return Task.FromResult(value);
        }

        public Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _values[key] = value;
            Interlocked.Increment(ref _writeCount);
            return Task.CompletedTask;
        }

        public Task<bool> ContainsKeyAsync(string key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_values.ContainsKey(key));
        }

        public Task DeleteValueAsync(string key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _values.TryRemove(key, out _);
            Interlocked.Increment(ref _writeCount);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> ListKeysAsync(
            string? prefix = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ListFailure is not null)
            {
                throw ListFailure;
            }
            IReadOnlyList<string> keys = _values.Keys
                .Where(key => prefix is null || key.StartsWith(prefix, StringComparison.Ordinal))
                .Order(StringComparer.Ordinal)
                .ToArray();
            return Task.FromResult(keys);
        }
    }

    internal sealed class InMemorySettings : IPackageSettings
    {
        private readonly ConcurrentDictionary<string, string> _values = new(StringComparer.Ordinal);

        public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default)
            => GetStoredValueAsync(key, cancellationToken);

        public Task<string?> GetStoredValueAsync(string key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_values.GetValueOrDefault(key));
        }

        public Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _values[key] = value;
            return Task.CompletedTask;
        }

        public Task DeleteValueAsync(string key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _values.TryRemove(key, out _);
            return Task.CompletedTask;
        }
    }

    private sealed class BlockingDockerCliRunner(IPackageContext context) : DockerCliRunner(context)
    {
        internal TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override Task<string> ResolveEndpointAsync(CancellationToken cancellationToken)
            => Task.FromResult("unix:///var/run/docker.sock");

        protected override async Task<DockerCliRunResult> RunCoreAsync(
            IReadOnlyList<string> args,
            int timeoutSeconds,
            CancellationToken cancellationToken,
            string? standardInput = null,
            IProgress<string>? progress = null)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return new DockerCliRunResult(0, string.Empty, TimedOut: false, WasTruncated: false);
        }
    }

    private sealed class ImmediateDockerCliRunner(IPackageContext context) : DockerCliRunner(context)
    {
        protected override Task<string> ResolveEndpointAsync(CancellationToken cancellationToken)
            => Task.FromResult("unix:///var/run/docker.sock");

        protected override Task<DockerCliRunResult> RunCoreAsync(
            IReadOnlyList<string> args,
            int timeoutSeconds,
            CancellationToken cancellationToken,
            string? standardInput = null,
            IProgress<string>? progress = null)
            => Task.FromResult(new DockerCliRunResult(
                0,
                string.Empty,
                TimedOut: false,
                WasTruncated: false));
    }

    private sealed class SequencedDockerCliRunner(IPackageContext context, int callCount) : DockerCliRunner(context)
    {
        private int _callIndex;

        internal PendingDockerCall[] Calls { get; } = Enumerable.Range(0, callCount)
            .Select(_ => new PendingDockerCall())
            .ToArray();

        protected override Task<string> ResolveEndpointAsync(CancellationToken cancellationToken)
            => Task.FromResult("unix:///var/run/docker.sock");

        protected override async Task<DockerCliRunResult> RunCoreAsync(
            IReadOnlyList<string> args,
            int timeoutSeconds,
            CancellationToken cancellationToken,
            string? standardInput = null,
            IProgress<string>? progress = null)
        {
            var call = Calls[Interlocked.Increment(ref _callIndex) - 1];
            call.Started.TrySetResult();
            return await call.Response.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class PendingDockerCall
    {
        internal TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource<DockerCliRunResult> Response { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
