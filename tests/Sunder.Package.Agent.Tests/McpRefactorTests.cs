using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Mcp;
using Sunder.Package.Agent.Mcp.Services;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Logging;
using Sunder.Sdk.Callbacks;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class McpRefactorTests
{
    [Fact]
    public async Task OAuthCallbackHandler_RejectsUnknownServerBeforeStartingProviderFlow()
    {
        var context = new TestPackageContext();
        var catalog = new McpServerCatalogService(context);
        await using var oauth = new McpOAuthService(context);
        var handler = new McpOAuthCallbackHandler(catalog, oauth, context);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => handler.StartCallbackAsync(
            new PackageCallbackStartContext(
                "session",
                new Uri("http://localhost:1455/callbacks/session"),
                McpOAuthCallbackHandler.HandlerId,
                new Dictionary<string, string>
                {
                    [McpOAuthCallbackHandler.ServerIdParameter] = "missing",
                })));

        Assert.Contains("not found", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ToolIdentity_UsesStableServerIdAndRoundTripsUnambiguousSegments()
    {
        var first = McpToolIdentity.CreateStable("server_a/1", "tool_with_prefix__segment-9");
        var renamed = McpToolIdentity.CreateStable("server_a/1", "tool_with_prefix__segment-9");
        var second = McpToolIdentity.CreateStable("server_a/10", "tool_with_prefix__segment-9");

        Assert.Equal(first, renamed);
        Assert.NotEqual(first, second);
        Assert.True(McpToolIdentity.TryParseStable(first.ToUpperInvariant(), out var serverId, out var toolName));
        Assert.Equal("server_a/1", serverId);
        Assert.Equal("tool_with_prefix__segment-9", toolName);
    }

    [Fact]
    public void LegacyToolIdentity_IsAliasOnlyAndRejectsPrefixAmbiguity()
    {
        var servers = new[]
        {
            CreateServer("one", "foo"),
            CreateServer("two", "foo_bar"),
        };

        Assert.Equal("foo_run", McpToolIdentity.CreateLegacyAlias("foo", "run"));
        Assert.False(McpToolIdentity.TryParseLegacy("foo_bar_run", servers, out _, out _));
        Assert.True(McpToolIdentity.TryParseLegacy("foo_run", servers, out var server, out var toolName));
        Assert.Equal("one", server?.ServerId);
        Assert.Equal("run", toolName);
    }

    [Fact]
    public async Task LegacyToolIdentity_CanonicalizesForPersistedCalls()
    {
        var context = new TestPackageContext();
        var catalog = new McpServerCatalogService(context);
        await catalog.SaveServerAsync(CreateServer("one", "foo"), Empty, Empty);
        await using var manager = new McpClientConnectionManager(NullLoggerFactory.Instance, new FakeConnectionFactory());
        var source = new McpToolSource(catalog, manager);

        var result = await source.ExecuteAsync(
            new AgentToolExecutionContext(Guid.NewGuid()),
            new AgentToolRequest("foo_run", "{}"));

        Assert.True(result.IsError);
        Assert.Equal(McpToolIdentity.CreateStable("one", "run"), result.ToolId);
        Assert.Equal("mcp-server-unavailable", result.ErrorCode);
    }

    [Fact]
    public async Task Catalog_RejectsDuplicateNormalizedNames()
    {
        var context = new TestPackageContext();
        var catalog = new McpServerCatalogService(context);
        await catalog.SaveServerAsync(CreateServer("one", "Foo Bar"), Empty, Empty);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            catalog.SaveServerAsync(CreateServer("two", "foo-bar"), Empty, Empty));

        Assert.Contains("already exists", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(await catalog.ListServersAsync());
    }

    [Fact]
    public async Task ConnectionPool_ReusesByServerAndDisposesOnVersionChangeAndShutdown()
    {
        var factory = new FakeConnectionFactory();
        await using var manager = new McpClientConnectionManager(NullLoggerFactory.Instance, factory);
        var server = CreateServer("one", "one") with { PersistenceVersion = 1 };

        await manager.GetToolsAsync(server, Empty, Empty, null);
        await manager.GetToolsAsync(server, Empty, Empty, null);
        Assert.Equal(1, factory.ConnectCount);

        await manager.GetToolsAsync(server with { PersistenceVersion = 2 }, Empty, Empty, null);
        Assert.Equal(2, factory.ConnectCount);
        Assert.Equal(1, factory.Connections[0].DisposeCount);

        await manager.DisposeAsync();
        Assert.Equal(1, factory.Connections[1].DisposeCount);
    }

    [Fact]
    public async Task ConnectionPool_IsolatesLiveClientsBySessionAndWorkspaceWhileSharingTools()
    {
        var factory = new FakeConnectionFactory();
        await using var manager = new McpClientConnectionManager(NullLoggerFactory.Instance, factory);
        var server = CreateServer("one", "one");
        var firstSession = Guid.NewGuid();
        var secondSession = Guid.NewGuid();

        await AcquireAndReleaseAsync(manager, server, McpConnectionScope.For(firstSession, "workspace-a"));
        await AcquireAndReleaseAsync(manager, server, McpConnectionScope.For(firstSession, "workspace-a"));
        await AcquireAndReleaseAsync(manager, server, McpConnectionScope.For(secondSession, "workspace-a"));
        await AcquireAndReleaseAsync(manager, server, McpConnectionScope.For(firstSession, "workspace-b"));

        Assert.Equal(3, factory.ConnectCount);
        Assert.NotNull(manager.GetCachedTools(server));
        Assert.Equal(3, manager.GetStatus(server).ActiveConnectionCount);
    }

    [Fact]
    public async Task ConnectionPool_DisconnectRetiresConnectionUntilInvocationLeaseDrains()
    {
        var factory = new FakeConnectionFactory();
        await using var manager = new McpClientConnectionManager(NullLoggerFactory.Instance, factory);
        var server = CreateServer("one", "one");
        await using var lease = await manager.AcquireClientLeaseAsync(server, Empty, Empty, null, McpConnectionScope.For(Guid.NewGuid(), "workspace"));
        Assert.NotNull(lease);
        Assert.Equal(1, manager.GetStatus(server).ActiveConnectionCount);

        var disconnect = manager.DisconnectServerAsync(server.ServerId);
        await WaitUntilAsync(() => manager.GetStatus(server).ActiveConnectionCount == 0);

        Assert.False(disconnect.IsCompleted);
        Assert.Equal(0, factory.Connections[0].DisposeCount);

        await lease.DisposeAsync();
        await disconnect.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, factory.Connections[0].DisposeCount);
    }

    [Fact]
    public async Task ConnectionPool_ForceReconnectInvalidatesMetadataAndCreatesNewConnection()
    {
        var factory = new FakeConnectionFactory();
        await using var manager = new McpClientConnectionManager(NullLoggerFactory.Instance, factory);
        var server = CreateServer("one", "one");
        await manager.GetToolsAsync(server, Empty, Empty, null);
        Assert.NotNull(manager.GetCachedTools(server));

        await manager.ReconnectServerAsync(server.ServerId);

        Assert.Null(manager.GetCachedTools(server));
        await manager.GetToolsAsync(server, Empty, Empty, null);
        Assert.Equal(2, factory.ConnectCount);
        Assert.Equal(1, factory.Connections[0].DisposeCount);
        Assert.Equal(0, manager.KeyedLockCount);
    }

    [Fact]
    public async Task ConnectionPool_LeakedInvocationLeaseDoesNotBlockDisconnectForever()
    {
        var factory = new FakeConnectionFactory();
        await using var manager = new McpClientConnectionManager(NullLoggerFactory.Instance, factory);
        var server = CreateServer("one", "one");
        var lease = await manager.AcquireClientLeaseAsync(server, Empty, Empty, null, McpConnectionScope.Shared);
        Assert.NotNull(lease);

        await manager.DisconnectServerAsync(server.ServerId).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, factory.Connections[0].DisposeCount);
        Assert.Equal(0, manager.KeyedLockCount);
        await lease.DisposeAsync();
    }

    [Fact]
    public async Task ConnectionPool_ReconfigurationWaitsForInvocationLeaseBeforeReplacingConnection()
    {
        var factory = new FakeConnectionFactory();
        await using var manager = new McpClientConnectionManager(NullLoggerFactory.Instance, factory);
        var server = CreateServer("one", "one") with { PersistenceVersion = 1 };
        await using var lease = await manager.AcquireClientLeaseAsync(server, Empty, Empty, null, McpConnectionScope.Shared);
        Assert.NotNull(lease);

        var reconfigure = manager.GetToolsAsync(server with { PersistenceVersion = 2 }, Empty, Empty, null);
        await WaitUntilAsync(() => manager.GetStatus(server).ActiveConnectionCount == 0);

        Assert.False(reconfigure.IsCompleted);
        Assert.Equal(0, factory.Connections[0].DisposeCount);
        Assert.Equal(1, factory.ConnectCount);

        await lease.DisposeAsync();
        await factory.SecondConnectStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await reconfigure;
        Assert.Equal(1, factory.Connections[0].DisposeCount);
        Assert.Equal(2, factory.ConnectCount);
    }

    [Fact]
    public async Task ToolDiscovery_AwaitsSharedConfigurationInitializationBarrier()
    {
        var root = Path.Combine(Path.GetTempPath(), "sunder-mcp-initialization-" + Guid.NewGuid().ToString("N"));
        try
        {
            var configRoot = Path.Combine(root, ".config", "sunder");
            Directory.CreateDirectory(configRoot);
            await File.WriteAllTextAsync(Path.Combine(configRoot, "mcp.json"), """
                { "mcp": { "barrier": { "type": "remote", "url": "https://example.com/mcp" } } }
                """);
            var state = new TestState { BlockOnNextSet = true };
            var context = new TestPackageContext(state);
            var catalog = new McpServerCatalogService(context);
            var importer = new McpEcosystemConfigurationImporter(catalog);
            var sync = new McpSunderConfigurationSyncService(importer, userProfilePath: root);
            var coordinator = new McpConfigurationCoordinator(importer, sync);
            await using var manager = new McpClientConnectionManager(NullLoggerFactory.Instance, new FakeConnectionFactory());
            var source = new McpToolSource(catalog, manager, coordinator);

            var firstDiscovery = source.ListConfiguredServersAsync().AsTask();
            var secondDiscovery = source.ListConfiguredServersAsync().AsTask();
            await state.SetStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(firstDiscovery.IsCompleted);
            Assert.False(secondDiscovery.IsCompleted);

            state.ReleaseSet.TrySetResult();
            var results = await Task.WhenAll(firstDiscovery, secondDiscovery);

            Assert.All(results, servers => Assert.Equal("barrier", Assert.Single(servers).DisplayName));
            Assert.Equal(1, state.SetCount);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ConnectionPool_PropagatesConnectionCancellation()
    {
        var factory = new FakeConnectionFactory { BlockConnect = true };
        await using var manager = new McpClientConnectionManager(NullLoggerFactory.Instance, factory);
        using var cancellation = new CancellationTokenSource();
        var pending = manager.GetToolsAsync(CreateServer("one", "one"), Empty, Empty, null, cancellation.Token);
        await factory.ConnectStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public async Task ToolQueries_DoNotSynchronizeOrMutateConfiguration()
    {
        var context = new TestPackageContext();
        var catalog = new McpServerCatalogService(context);
        await catalog.SaveServerAsync(CreateServer("one", "one"), Empty, Empty);
        await using var manager = new McpClientConnectionManager(NullLoggerFactory.Instance, new FakeConnectionFactory());
        var source = new McpToolSource(catalog, manager);
        var writesBefore = context.State.SetCount;

        var servers = await source.ListConfiguredServersAsync();

        Assert.Single(servers);
        Assert.Equal(writesBefore, context.State.SetCount);
    }

    [Fact]
    public async Task Sync_SerializesConcurrentImportsOfTheSameManagedFile()
    {
        var context = new TestPackageContext();
        var catalog = new McpServerCatalogService(context);
        var importer = new McpEcosystemConfigurationImporter(catalog);
        var sync = new McpSunderConfigurationSyncService(importer);
        var path = Path.Combine(Path.GetTempPath(), "sunder-mcp-sync-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            await File.WriteAllTextAsync(path, """
                { "mcp": { "one": { "type": "remote", "url": "https://example.com/mcp" } } }
                """);

            var results = await Task.WhenAll(sync.SyncFilesAsync([path]), sync.SyncFilesAsync([path]));

            Assert.Equal(1, results.Sum(result => result.ImportedCount));
            Assert.Single(await catalog.ListServersAsync());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Catalog_SecretFailureKeepsPriorVisibleVersionAndCredentials()
    {
        var secrets = new FaultingSecrets();
        var context = new TestPackageContext(secrets: secrets);
        var catalog = new McpServerCatalogService(context);
        var original = CreateServer("one", "one") with { HeaderNames = ["Authorization"] };
        await catalog.SaveServerAsync(original, new Dictionary<string, string> { ["Authorization"] = "old" }, Empty);
        var persisted = Assert.Single(await catalog.ListServersAsync());
        secrets.FailOnSetNumber = secrets.SetCount + 2;
        var update = persisted with { HeaderNames = ["Authorization", "X-Api-Key"], UpdatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(1) };

        await Assert.ThrowsAsync<InvalidOperationException>(() => catalog.SaveServerAsync(
            update,
            new Dictionary<string, string> { ["Authorization"] = "new", ["X-Api-Key"] = "new-key" },
            Empty));

        var after = Assert.Single(await catalog.ListServersAsync());
        Assert.Equal(persisted.PersistenceVersion, after.PersistenceVersion);
        Assert.Equal("old", (await catalog.GetHeadersAsync(after))["Authorization"]);
        Assert.DoesNotContain(secrets.Keys, key => key.Contains($".v{persisted.PersistenceVersion + 1}.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Catalog_StateFailureCompensatesStagedSecrets()
    {
        var context = new TestPackageContext();
        var catalog = new McpServerCatalogService(context);
        var original = CreateServer("one", "one") with { HeaderNames = ["Authorization"] };
        await catalog.SaveServerAsync(original, new Dictionary<string, string> { ["Authorization"] = "old" }, Empty);
        var persisted = Assert.Single(await catalog.ListServersAsync());
        context.State.ThrowOnNextSet = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => catalog.SaveServerAsync(
            persisted with { UpdatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(1) },
            new Dictionary<string, string> { ["Authorization"] = "new" },
            Empty));

        var after = Assert.Single(await catalog.ListServersAsync());
        Assert.Equal(persisted.PersistenceVersion, after.PersistenceVersion);
        Assert.Equal("old", (await catalog.GetHeadersAsync(after))["Authorization"]);
        Assert.DoesNotContain(context.Secrets.Keys, key => key.Contains($".v{persisted.PersistenceVersion + 1}.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Catalog_IsolatesMalformedEntriesAndPreservesThemWithDiagnostics()
    {
        var context = new TestPackageContext();
        var catalog = new McpServerCatalogService(context);
        await catalog.SaveServerAsync(CreateServer("one", "one"), Empty, Empty);
        await context.State.SetValueAsync("mcp.servers.broken", "{ not-json");

        var servers = await catalog.ListServersAsync();

        Assert.Single(servers);
        Assert.Contains(catalog.LastDiagnostics, diagnostic => diagnostic.StorageKey == "mcp.servers.broken");
        Assert.Equal("{ not-json", await context.State.GetValueAsync("mcp.servers.broken"));
    }

    [Fact]
    public async Task ImportCancellationPropagatesAndDoesNotCreateServers()
    {
        var context = new TestPackageContext();
        var catalog = new McpServerCatalogService(context);
        var importer = new McpEcosystemConfigurationImporter(catalog);
        var path = Path.Combine(Path.GetTempPath(), "sunder-mcp-cancel-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            await File.WriteAllTextAsync(path, """
                { "mcp": { "one": { "type": "remote", "url": "https://example.com/mcp" } } }
                """);
            using var cancellation = new CancellationTokenSource();
            await cancellation.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => importer.ImportFileAsync(path, cancellation.Token));
            Assert.Empty(await catalog.ListServersAsync());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task SettingsCommands_DisableOverlappingOperationsUntilCancellationSettles()
    {
        var context = new TestPackageContext();
        var catalog = new McpServerCatalogService(context);
        await catalog.SaveServerAsync(CreateServer("one", "one"), Empty, Empty);
        var factory = new FakeConnectionFactory { BlockConnect = true };
        await using var manager = new McpClientConnectionManager(NullLoggerFactory.Instance, factory);
        using var viewModel = new AgentMcpSettingsViewModel(catalog, manager);
        await WaitUntilAsync(() => viewModel.SelectedServer is not null);

        var discovery = viewModel.DiscoverToolsCommand.ExecuteAsync(null);
        await factory.ConnectStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => viewModel.IsDiscovering);

        Assert.False(viewModel.SaveCommand.CanExecute(null));
        Assert.False(viewModel.ImportCommonConfigurationsCommand.CanExecute(null));
        Assert.False(viewModel.DisconnectMcpServerCommand.CanExecute(null));
        viewModel.CancelDiscoveryCommand.Execute(null);
        Assert.False(viewModel.SaveCommand.CanExecute(null));
        await discovery;
        Assert.True(viewModel.SaveCommand.CanExecute(null));
    }

    [Fact]
    public async Task SettingsCatalogChange_WhileBusyReloadsAfterOperationSettles()
    {
        var context = new TestPackageContext();
        var catalog = new McpServerCatalogService(context);
        await catalog.SaveServerAsync(CreateServer("one", "one"), Empty, Empty);
        var factory = new FakeConnectionFactory { BlockConnect = true };
        await using var manager = new McpClientConnectionManager(NullLoggerFactory.Instance, factory);
        using var viewModel = new AgentMcpSettingsViewModel(catalog, manager);
        await viewModel.Initialization;

        var discovery = viewModel.DiscoverToolsCommand.ExecuteAsync(null);
        await factory.ConnectStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => viewModel.IsBusy);

        await catalog.SaveServerAsync(CreateServer("two", "two"), Empty, Empty);
        Assert.DoesNotContain(viewModel.Servers, server => server.ServerId == "two");

        viewModel.CancelDiscoveryCommand.Execute(null);
        await discovery;
        await WaitUntilAsync(() => viewModel.Servers.Any(server => server.ServerId == "two"));

        Assert.Contains(viewModel.Servers, server => server.ServerId == "two");
    }

    private static readonly IReadOnlyDictionary<string, string> Empty = new Dictionary<string, string>();

    private static ConfiguredMcpServerRecord CreateServer(string id, string name)
        => new()
        {
            ServerId = id,
            Name = name,
            DisplayName = name,
            IsEnabled = true,
            TransportType = ConfiguredMcpTransportType.HttpSse,
            EndpointUrl = "https://example.com/mcp",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var timeout = DateTime.UtcNow.AddSeconds(3);
        while (!predicate())
        {
            if (DateTime.UtcNow >= timeout)
            {
                throw new TimeoutException("Condition was not reached.");
            }

            await Task.Delay(10);
        }
    }

    private static async Task AcquireAndReleaseAsync(
        McpClientConnectionManager manager,
        ConfiguredMcpServerRecord server,
        McpConnectionScope scope)
    {
        await using var lease = await manager.AcquireClientLeaseAsync(server, Empty, Empty, null, scope);
        Assert.NotNull(lease);
    }

    private sealed class FakeConnectionFactory : IMcpClientConnectionFactory
    {
        public int ConnectCount { get; private set; }
        public bool BlockConnect { get; init; }
        public TaskCompletionSource ConnectStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondConnectStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<FakeConnection> Connections { get; } = [];

        public async Task<IMcpClientConnection> ConnectAsync(
            ConfiguredMcpServerRecord server,
            IReadOnlyDictionary<string, string> headers,
            IReadOnlyDictionary<string, string> environmentVariables,
            int? discoveryTimeoutMilliseconds,
            Action<string> standardError,
            CancellationToken cancellationToken)
        {
            ConnectCount++;
            ConnectStarted.TrySetResult();
            if (ConnectCount == 2)
            {
                SecondConnectStarted.TrySetResult();
            }
            if (BlockConnect)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            var connection = new FakeConnection();
            Connections.Add(connection);
            return connection;
        }
    }

    private sealed class FakeConnection : IMcpClientConnection
    {
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public McpClient? Client => null;
        public Task Completion => _completion.Task;
        public int DisposeCount { get; private set; }

        public Task<IReadOnlyList<McpClientTool>> ListToolsAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<McpClientTool>>([]);

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            _completion.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestPackageContext : IPackageContext
    {
        private readonly TestStorage _storage;

        public TestPackageContext(TestState? state = null, FaultingSecrets? secrets = null)
        {
            State = state ?? new TestState();
            Secrets = secrets ?? new FaultingSecrets();
            _storage = new TestStorage(State);
        }

        public TestState State { get; }
        public FaultingSecrets Secrets { get; }
        public string PackageId => "test.mcp";
        public string Version { get; } = "1.0.0";
        public string ContentRootPath => AppContext.BaseDirectory;
        public IPackageStorageContext Storage => _storage;
        public IPackageSettings Settings { get; } = new EmptySettings();
        IPackageSecrets IPackageContext.Secrets => Secrets;
        public IPackageLogging Logging { get; } = NullPackageLogging.Instance;
    }

    private sealed class TestStorage(IPackageKeyValueStore state) : IPackageStorageContext
    {
        public IPackageFileStore Files { get; } = new TestFiles();
        public IPackageKeyValueStore State { get; } = state;
        public IPackageRoleLocalWorkspace RoleLocalWorkspace { get; } = new TestPackageRoleLocalWorkspace(Path.GetTempPath());
    }

    private sealed class TestFiles : TestPackageFileStoreBase
    {
        public TestFiles() : base(Path.GetTempPath()) { }
    }

    private sealed class TestState : IPackageKeyValueStore
    {
        private readonly ConcurrentDictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
        public int SetCount { get; private set; }
        public bool ThrowOnNextSet { get; set; }
        public bool BlockOnNextSet { get; set; }
        public TaskCompletionSource SetStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseSet { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default)
            => Task.FromResult(_values.GetValueOrDefault(key));

        public async Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SetCount++;
            if (ThrowOnNextSet)
            {
                ThrowOnNextSet = false;
                throw new InvalidOperationException("Injected state failure.");
            }

            if (BlockOnNextSet)
            {
                BlockOnNextSet = false;
                SetStarted.TrySetResult();
                await ReleaseSet.Task.WaitAsync(cancellationToken);
            }

            _values[key] = value;
        }

        public Task<bool> ContainsKeyAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult(_values.ContainsKey(key));

        public Task DeleteValueAsync(string key, CancellationToken cancellationToken = default)
        {
            _values.TryRemove(key, out _);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> ListKeysAsync(string? prefix = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>(_values.Keys.Where(key => prefix is null || key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray());
    }

    private sealed class FaultingSecrets : IPackageSecrets
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
        public int SetCount { get; private set; }
        public int? FailOnSetNumber { get; set; }
        public IReadOnlyCollection<string> Keys => _values.Keys;
        public Task<string?> GetSecretAsync(string key, CancellationToken cancellationToken = default)
            => Task.FromResult(_values.GetValueOrDefault(key));

        public Task SetSecretAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            SetCount++;
            if (SetCount == FailOnSetNumber)
            {
                return Task.FromException(new InvalidOperationException("Injected secret failure."));
            }

            _values[key] = value;
            return Task.CompletedTask;
        }

        public Task DeleteSecretAsync(string key, CancellationToken cancellationToken = default)
        {
            _values.Remove(key);
            return Task.CompletedTask;
        }
    }

    private sealed class EmptySettings : EmptyPackageSettings;
}
