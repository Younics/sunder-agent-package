using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Mcp;
using Sunder.Package.Agent.Mcp.Services;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Logging;
using Sunder.Sdk.Callbacks;
using Sunder.Sdk.Storage;
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
    public async Task Catalog_UsesPortablePhysicalKeysForImportedServerAndSecretIdentifiers()
    {
        var serverId = " imported:/\u65E5\u672C\u8A9E/" + new string('s', 300);
        const string headerName = "X Imported / \u00E9";
        const string environmentName = "TOKEN / \u65E5";
        var context = new TestPackageContext();
        var catalog = new McpServerCatalogService(context);
        var server = CreateServer(serverId, "portable") with
        {
            HeaderNames = [headerName],
            EnvironmentVariableNames = [environmentName],
        };

        await catalog.SaveServerAsync(
            server,
            new Dictionary<string, string> { [headerName] = "header-secret" },
            new Dictionary<string, string> { [environmentName] = "environment-secret" });

        Assert.Equal(serverId, Assert.Single(await catalog.ListServersAsync()).ServerId);
        Assert.All(await context.State.ListKeysAsync(), key => Assert.True(PackageStorageValidation.IsValidKey(key)));
        Assert.All(context.Secrets.Keys, key => Assert.True(PackageStorageValidation.IsValidKey(key)));
        Assert.True(PackageStorageValidation.IsValidKey(McpOAuthSecretKeys.TokenCache(serverId)));
        Assert.True(PackageStorageValidation.IsValidKey(McpOAuthSecretKeys.ClientRegistration(serverId)));
        Assert.True(PackageStorageValidation.IsValidKey(McpOAuthSecretKeys.ClientSecret(serverId)));
    }

    [Fact]
    public async Task PackageStorageMigration_UsesCaseEquivalentPayloadServerIdForStateAndSecrets()
    {
        const string payloadServerId = "server-one";
        const string legacyKeyServerId = "SERVER-ONE";
        var context = new TestPackageContext();
        var server = CreateServer(payloadServerId, "case-preserved");
        var payload = JsonSerializer.Serialize(server);
        var legacyStateKey = $"mcp.servers.{legacyKeyServerId}";
        var legacySecretKey = McpServerCatalogService.BuildLegacyApiKeySecretKey(payloadServerId);
        await context.State.SetValueAsync(legacyStateKey, payload);
        await context.Secrets.SetSecretAsync(legacySecretKey, "secret");

        var migration = new McpPackageStorageMigration(context);
        await migration.EnsureAsync();

        Assert.Null(await context.State.GetValueAsync(legacyStateKey));
        Assert.Equal(payload, await context.State.GetValueAsync(McpServerCatalogService.BuildServerKey(payloadServerId)));
        Assert.Null(await context.Secrets.GetSecretAsync(legacySecretKey));
        Assert.Equal(
            "secret",
            await context.Secrets.GetSecretAsync(McpServerCatalogService.BuildApiKeySecretKey(payloadServerId)));
        var catalog = new McpServerCatalogService(context, migration);
        Assert.Equal(payloadServerId, Assert.Single(await catalog.ListServersAsync()).ServerId);

        await catalog.DeleteServerAsync(legacyKeyServerId);

        Assert.Empty(await catalog.ListServersAsync());
        Assert.Null(await catalog.GetServerAsync(payloadServerId));
        Assert.Null(await context.State.GetValueAsync(McpServerCatalogService.BuildServerKey(payloadServerId)));
    }

    [Fact]
    public async Task PackageStorageMigration_RejectsCaseCollidingPayloadIdentitiesWithoutMutation()
    {
        const string firstKey = "mcp.servers.Server-One";
        const string secondKey = "mcp.servers.SERVER-ONE";
        var context = new TestPackageContext();
        var firstPayload = JsonSerializer.Serialize(CreateServer("server-one", "first"));
        var secondPayload = JsonSerializer.Serialize(CreateServer("SERVER-ONE", "second"));
        await context.State.SetValueAsync(firstKey, firstPayload);
        await context.State.SetValueAsync(secondKey, secondPayload);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new McpPackageStorageMigration(context).EnsureAsync());

        Assert.Contains("collision", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(firstPayload, await context.State.GetValueAsync(firstKey));
        Assert.Equal(secondPayload, await context.State.GetValueAsync(secondKey));
        Assert.Null(await context.State.GetValueAsync(McpServerCatalogService.BuildServerKey("server-one")));
    }

    [Fact]
    public async Task PackageStorageMigration_CleansCrashRemnantsWithoutOrphanResurrection()
    {
        const string liveServerId = "live-server";
        const string deletedServerId = "deleted-server";
        const string headerName = "X-Token";
        const string environmentName = "TOKEN";
        var context = new TestPackageContext();
        var live = CreateServer(liveServerId, "live") with
        {
            PersistenceVersion = 3,
            HeaderNames = [headerName],
            EnvironmentVariableNames = [environmentName],
            OAuthEnabled = true,
        };
        await context.State.SetValueAsync(
            McpServerCatalogService.BuildServerKey(liveServerId),
            JsonSerializer.Serialize(live));

        var exactHeader = McpServerCatalogService.BuildLegacyHeaderSecretKey(
            liveServerId,
            live.PersistenceVersion,
            headerName);
        var mixedCaseHeader = McpServerCatalogService.BuildLegacyHeaderSecretKey(
            liveServerId.ToUpperInvariant(),
            live.PersistenceVersion,
            headerName.ToLowerInvariant());
        var currentEnvironment = McpServerCatalogService.BuildEnvironmentSecretKey(
            liveServerId,
            live.PersistenceVersion,
            environmentName);
        context.Secrets.Seed(exactHeader, "current-header");
        context.Secrets.Seed(mixedCaseHeader, "conflicting-case-remnant");
        context.Secrets.Seed(currentEnvironment, "current-environment");
        context.Secrets.Seed(
            McpServerCatalogService.BuildLegacyEnvironmentSecretKey(
                liveServerId.ToUpperInvariant(),
                live.PersistenceVersion,
                environmentName.ToLowerInvariant()),
            "stale-environment");
        context.Secrets.Seed(McpOAuthSecretKeys.LegacyTokenCache(liveServerId.ToUpperInvariant()), "oauth-token");

        context.Secrets.Seed(
            McpServerCatalogService.BuildLegacyHeaderSecretKey(liveServerId, 2, headerName),
            "old-version");
        context.Secrets.Seed(
            McpServerCatalogService.BuildHeaderSecretKey(liveServerId, 2, headerName),
            "portable-old-version");
        context.Secrets.Seed(
            McpServerCatalogService.BuildApiKeySecretKey(liveServerId),
            "superseded-live-api-key");
        context.Secrets.Seed(McpServerCatalogService.BuildLegacyApiKeySecretKey(deletedServerId), "orphan-api-key");
        context.Secrets.Seed(McpServerCatalogService.BuildLegacyAuthorizationSecretKey(deletedServerId), "orphan-authorization");
        context.Secrets.Seed(McpOAuthSecretKeys.LegacyTokenCache(deletedServerId), "orphan-token-cache");
        context.Secrets.Seed(McpOAuthSecretKeys.LegacyClientRegistration(deletedServerId), "orphan-registration");
        context.Secrets.Seed(McpOAuthSecretKeys.LegacyClientSecret(deletedServerId), "orphan-client-secret");
        context.Secrets.Seed(
            McpServerCatalogService.BuildLegacyHeaderSecretKey(deletedServerId, 0, "X-Legacy"),
            "orphan-unversioned-header");
        context.Secrets.Seed(
            McpServerCatalogService.BuildLegacyHeaderSecretKey(deletedServerId, 7, "X-Orphan"),
            "orphan-header");
        context.Secrets.Seed(
            McpServerCatalogService.BuildLegacyEnvironmentSecretKey(deletedServerId, 0, "LEGACY_ENV"),
            "orphan-unversioned-environment");
        context.Secrets.Seed(
            McpServerCatalogService.BuildLegacyEnvironmentSecretKey(deletedServerId, 7, "ORPHAN_ENV"),
            "orphan-environment");
        context.Secrets.Seed(
            McpServerCatalogService.BuildApiKeySecretKey(deletedServerId),
            "portable-orphan-api-key");
        context.Secrets.Seed("unrelated.secret", "preserved");

        var migration = new McpPackageStorageMigration(context);
        await migration.EnsureAsync();
        var catalog = new McpServerCatalogService(context, migration);
        var migrated = Assert.Single(await catalog.ListServersAsync());

        Assert.Equal("current-header", (await catalog.GetHeadersAsync(migrated))[headerName]);
        Assert.Equal("current-environment", (await catalog.GetEnvironmentVariablesAsync(migrated))[environmentName]);
        Assert.Equal("oauth-token", await context.Secrets.GetSecretAsync(McpOAuthSecretKeys.TokenCache(liveServerId)));
        Assert.Equal("preserved", await context.Secrets.GetSecretAsync("unrelated.secret"));
        Assert.DoesNotContain(
            context.Secrets.Keys,
            key => key.StartsWith("mcp.servers.", StringComparison.OrdinalIgnoreCase));
        Assert.Null(await context.Secrets.GetSecretAsync(
            McpServerCatalogService.BuildHeaderSecretKey(liveServerId, 2, headerName)));
        Assert.Null(await context.Secrets.GetSecretAsync(
            McpServerCatalogService.BuildApiKeySecretKey(liveServerId)));
        Assert.Null(await context.Secrets.GetSecretAsync(
            McpServerCatalogService.BuildApiKeySecretKey(deletedServerId)));

        var recreated = CreateServer(deletedServerId, "recreated") with
        {
            IsEnabled = false,
            HeaderNames = ["X-Orphan"],
        };
        await catalog.SaveServerAsync(recreated, Empty, Empty);
        var savedRecreated = Assert.IsType<ConfiguredMcpServerRecord>(await catalog.GetServerAsync(deletedServerId));

        Assert.Empty(await catalog.GetHeadersAsync(savedRecreated));
        Assert.Null(await context.Secrets.GetSecretAsync(McpOAuthSecretKeys.ClientSecret(deletedServerId)));
    }

    [Fact]
    public async Task PackageStorageMigration_UnknownOwnedSecretShapeFailsAtomically()
    {
        var context = new TestPackageContext();
        var live = CreateServer("live-server", "live") with
        {
            PersistenceVersion = 1,
            HeaderNames = ["X-Token"],
        };
        await context.State.SetValueAsync(
            McpServerCatalogService.BuildServerKey(live.ServerId),
            JsonSerializer.Serialize(live));
        var legacyHeader = McpServerCatalogService.BuildLegacyHeaderSecretKey(
            live.ServerId,
            live.PersistenceVersion,
            "X-Token");
        context.Secrets.Seed(legacyHeader, "preserved");
        context.Secrets.Seed("mcp.servers.live-server.unknown:shape", "unknown");

        await Assert.ThrowsAsync<InvalidDataException>(
            () => new McpPackageStorageMigration(context).EnsureAsync());

        Assert.Equal("preserved", context.Secrets.GetRaw(legacyHeader));
        Assert.Equal("unknown", context.Secrets.GetRaw("mcp.servers.live-server.unknown:shape"));
        Assert.Null(context.Secrets.GetRaw(
            McpServerCatalogService.BuildHeaderSecretKey(live.ServerId, live.PersistenceVersion, "X-Token")));
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
    public async Task ConnectionPool_EvictsLeastRecentlyUsedIdleSessionConnectionAtCapacity()
    {
        var factory = new FakeConnectionFactory();
        await using var manager = new McpClientConnectionManager(
            NullLoggerFactory.Instance,
            factory,
            maxSessionScopedConnections: 2);
        var server = CreateServer("one", "one");
        var firstScope = McpConnectionScope.For(Guid.NewGuid(), "workspace");
        var secondScope = McpConnectionScope.For(Guid.NewGuid(), "workspace");

        await AcquireAndReleaseAsync(manager, server, firstScope);
        await Task.Delay(10);
        await AcquireAndReleaseAsync(manager, server, secondScope);
        await Task.Delay(10);
        await AcquireAndReleaseAsync(manager, server, firstScope);
        await Task.Delay(10);
        await AcquireAndReleaseAsync(
            manager,
            server,
            McpConnectionScope.For(Guid.NewGuid(), "workspace"));

        Assert.Equal(3, factory.ConnectCount);
        Assert.Equal(2, manager.CachedConnectionCount);
        Assert.Equal(0, factory.Connections[0].DisposeCount);
        Assert.Equal(1, factory.Connections[1].DisposeCount);
        Assert.Equal(0, factory.Connections[2].DisposeCount);
    }

    [Fact]
    public async Task ConnectionPool_RejectsNewSessionConnectionWhenEveryEntryIsLeased()
    {
        var factory = new FakeConnectionFactory();
        await using var manager = new McpClientConnectionManager(
            NullLoggerFactory.Instance,
            factory,
            maxSessionScopedConnections: 1);
        var server = CreateServer("one", "one");
        await using var lease = await manager.AcquireClientLeaseAsync(
            server,
            Empty,
            Empty,
            null,
            McpConnectionScope.For(Guid.NewGuid(), "workspace"));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.AcquireClientLeaseAsync(
                server,
                Empty,
                Empty,
                null,
                McpConnectionScope.For(Guid.NewGuid(), "workspace")));

        Assert.Contains("every connection is in use", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, factory.ConnectCount);
        Assert.Equal(1, manager.CachedConnectionCount);
    }

    [Fact]
    public async Task SessionConnectionCleaner_DrainsOnlyConnectionsForDeletedSession()
    {
        var factory = new FakeConnectionFactory();
        await using var manager = new McpClientConnectionManager(NullLoggerFactory.Instance, factory);
        var server = CreateServer("one", "one");
        var deletedSessionId = Guid.NewGuid();
        var retainedSessionId = Guid.NewGuid();
        await using var deletedSessionLease = await manager.AcquireClientLeaseAsync(
            server,
            Empty,
            Empty,
            null,
            McpConnectionScope.For(deletedSessionId, "workspace"));
        Assert.NotNull(deletedSessionLease);
        await AcquireAndReleaseAsync(
            manager,
            server,
            McpConnectionScope.For(retainedSessionId, "workspace"));
        var cleaner = new McpSessionConnectionCleaner(manager);

        var cleanup = Task.Run(() => cleaner.DeleteSessionData(deletedSessionId));
        await WaitUntilAsync(() => manager.CachedConnectionCount == 1);

        Assert.False(cleanup.IsCompleted);
        Assert.Equal(0, factory.Connections[0].DisposeCount);
        Assert.Equal(0, factory.Connections[1].DisposeCount);

        await deletedSessionLease.DisposeAsync();
        await cleanup.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, factory.Connections[0].DisposeCount);
        Assert.Equal(0, factory.Connections[1].DisposeCount);
        Assert.Equal(1, manager.CachedConnectionCount);
        Assert.Equal(McpConnectionStatusKind.Connected, manager.GetStatus(server).Kind);
    }

    [Fact]
    public async Task SessionConnectionCleaner_DisposesLateConnectionAndRejectsRecreation()
    {
        var factory = new LateConnectionFactory();
        await using var manager = new McpClientConnectionManager(NullLoggerFactory.Instance, factory);
        var server = CreateServer("one", "one");
        var sessionId = Guid.NewGuid();
        var scope = McpConnectionScope.For(sessionId, "workspace");
        var discovery = manager.GetToolsAsync(server, Empty, Empty, null, scope);
        await factory.ConnectStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var cleanup = manager.DisconnectSessionAsync(sessionId);
        await factory.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        factory.ReleaseConnection.TrySetResult();

        await cleanup.WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAsync<InvalidOperationException>(() => discovery);
        Assert.Equal(1, factory.Connection.DisposeCount);
        Assert.Equal(0, manager.CachedConnectionCount);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.GetToolsAsync(server, Empty, Empty, null, scope));
        Assert.Equal(1, factory.ConnectCount);
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
        var storageKey = McpServerCatalogService.BuildServerKey("broken");
        await context.State.SetValueAsync(storageKey, "{ not-json");

        var servers = await catalog.ListServersAsync();

        Assert.Single(servers);
        Assert.Contains(catalog.LastDiagnostics, diagnostic => diagnostic.StorageKey == storageKey);
        Assert.Equal("{ not-json", await context.State.GetValueAsync(storageKey));
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
        await WaitUntilAsync(() => viewModel.Servers.Count == 2);

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

    private sealed class LateConnectionFactory : IMcpClientConnectionFactory
    {
        public int ConnectCount { get; private set; }
        public FakeConnection Connection { get; } = new();
        public TaskCompletionSource ConnectStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseConnection { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<IMcpClientConnection> ConnectAsync(
            ConfiguredMcpServerRecord server,
            IReadOnlyDictionary<string, string> headers,
            IReadOnlyDictionary<string, string> environmentVariables,
            int? discoveryTimeoutMilliseconds,
            Action<string> standardError,
            CancellationToken cancellationToken)
        {
            ConnectCount++;
            using var registration = cancellationToken.Register(
                () => CancellationObserved.TrySetResult());
            ConnectStarted.TrySetResult();
            await ReleaseConnection.Task;
            return Connection;
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

    private sealed class TestState : IPackageKeyValueStore, IPackageStorageKeyMigrator
    {
        private readonly ConcurrentDictionary<string, string> _values = new(StringComparer.Ordinal);
        public int SetCount { get; private set; }
        public bool ThrowOnNextSet { get; set; }
        public bool BlockOnNextSet { get; set; }
        public TaskCompletionSource SetStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseSet { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TestPackageStorageGuards.Key(key);
            return Task.FromResult(_values.GetValueOrDefault(key));
        }

        public async Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TestPackageStorageGuards.Key(key);
            TestPackageStorageGuards.Value(value);
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

        public Task<bool> ContainsKeyAsync(string key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TestPackageStorageGuards.Key(key);
            return Task.FromResult(_values.ContainsKey(key));
        }

        public Task DeleteValueAsync(string key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TestPackageStorageGuards.Key(key);
            _values.TryRemove(key, out _);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> ListKeysAsync(string? prefix = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TestPackageStorageGuards.Prefix(prefix);
            return Task.FromResult<IReadOnlyList<string>>(_values.Keys
                .Where(key => prefix is null || key.StartsWith(prefix, StringComparison.Ordinal))
                .Order(StringComparer.Ordinal)
                .ToArray());
        }

        public Task MigrateKeysAsync(
            IReadOnlyList<PackageStorageKeyMigration> migrations,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = ApplyKeyMigrations(_values, migrations);
            if (result.Changed)
            {
                _values.Clear();
                foreach (var pair in result.Values)
                {
                    _values[pair.Key] = pair.Value;
                }
            }
            return Task.CompletedTask;
        }
    }

    private sealed class FaultingSecrets : IPackageSecrets, IPackageStorageKeyMigrator
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
        public int SetCount { get; private set; }
        public int? FailOnSetNumber { get; set; }
        public IReadOnlyCollection<string> Keys => _values.Keys;
        public void Seed(string key, string value) => _values[key] = value;
        public string? GetRaw(string key) => _values.GetValueOrDefault(key);
        public Task<string?> GetSecretAsync(string key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TestPackageStorageGuards.Key(key);
            return Task.FromResult(_values.GetValueOrDefault(key));
        }

        public Task SetSecretAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TestPackageStorageGuards.Key(key);
            TestPackageStorageGuards.Value(value);
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
            cancellationToken.ThrowIfCancellationRequested();
            TestPackageStorageGuards.Key(key);
            _values.Remove(key);
            return Task.CompletedTask;
        }

        public Task MigrateKeysAsync(
            IReadOnlyList<PackageStorageKeyMigration> migrations,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = ApplyKeyMigrations(_values, migrations);
            if (result.Changed)
            {
                _values.Clear();
                foreach (var pair in result.Values)
                {
                    _values[pair.Key] = pair.Value;
                }
            }
            return Task.CompletedTask;
        }
    }

    private static (Dictionary<string, string> Values, bool Changed) ApplyKeyMigrations(
        IEnumerable<KeyValuePair<string, string>> values,
        IReadOnlyList<PackageStorageKeyMigration> migrations)
    {
        var source = values.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var mutations = new List<(string Source, string? Destination, string Value, int Precedence, bool Cleanup)>();
        foreach (var pair in source)
        {
            PackageStorageKeyMigrationAction? resolved = null;
            var cleanup = false;
            foreach (var migration in migrations)
            {
                var candidate = migration.Resolve(pair.Key, pair.Value);
                if (candidate.Kind == PackageStorageKeyMigrationActionKind.NoMatch)
                {
                    continue;
                }
                if (resolved is not null)
                {
                    throw new InvalidOperationException("Legacy package key matches multiple migration rules.");
                }
                resolved = candidate;
                cleanup = migration.IsDynamicCleanup;
            }
            if (resolved is null)
            {
                if (!PackageStorageValidation.IsValidKey(pair.Key))
                {
                    throw new InvalidDataException($"Stored package key '{pair.Key}' is invalid and has no declared migration.");
                }
                continue;
            }

            mutations.Add((
                pair.Key,
                resolved.DestinationKey,
                pair.Value,
                resolved.Precedence,
                cleanup));
        }

        if (mutations.Count == 0)
        {
            return (source, false);
        }
        foreach (var mutation in mutations)
        {
            source.Remove(mutation.Source);
        }
        foreach (var group in mutations
                     .Where(mutation => mutation.Destination is not null)
                     .GroupBy(mutation => mutation.Destination!, StringComparer.Ordinal))
        {
            var rewrites = group.ToArray();
            if (rewrites.Any(rewrite => !rewrite.Cleanup))
            {
                var expected = rewrites[0].Value;
                if (rewrites.Any(rewrite => !string.Equals(rewrite.Value, expected, StringComparison.Ordinal))
                    || source.TryGetValue(group.Key, out var existing)
                    && !string.Equals(existing, expected, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Package storage key migration collision at '{group.Key}' contains nonidentical values.");
                }
                source[group.Key] = expected;
                continue;
            }

            if (source.ContainsKey(group.Key))
            {
                continue;
            }
            var precedence = rewrites.Max(rewrite => rewrite.Precedence);
            var preferred = rewrites
                .Where(rewrite => rewrite.Precedence == precedence)
                .Select(rewrite => rewrite.Value)
                .Distinct(StringComparer.Ordinal)
                .Take(2)
                .ToArray();
            if (preferred.Length == 1)
            {
                source[group.Key] = preferred[0];
            }
        }
        if (source.Any(pair => !PackageStorageValidation.IsValidKey(pair.Key)
                               || !PackageStorageValidation.IsValidValue(pair.Value)))
        {
            throw new InvalidDataException("Migrated package storage is invalid.");
        }
        return (source, true);
    }

    private sealed class EmptySettings : EmptyPackageSettings;
}
