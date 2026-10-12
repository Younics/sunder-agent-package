extern alias McpApp;
extern alias McpWorker;

using System.Collections.Concurrent;
using System.Reflection;
using Avalonia.Headless.XUnit;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using McpWorker::Sunder.Package.Agent.Mcp;
using McpWorker::Sunder.Package.Agent.Mcp.Runtime;
using McpWorker::Sunder.Package.Agent.Mcp.Services;
using Sunder.Package.Agent.Tests;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Logging;
using Xunit;
using AgentMcpSettingsViewModel = McpApp::Sunder.Package.Agent.Mcp.AgentMcpSettingsViewModel;
using McpStatusKind = McpApp::Sunder.Package.Agent.Mcp.McpStatusKind;

namespace Sunder.Package.Agent.Presentation.Tests;

public sealed class McpSettingsRaceTests
{
    [AvaloniaFact]
    public async Task InitializationFailure_IsObservedAndPresented()
    {
        var context = new ControlledMcpPackageContext();
        context.State.ListKeysFailure = new InvalidOperationException("Injected MCP catalog failure.");
        var catalog = new McpServerCatalogService(context);
        await using var manager = new McpClientConnectionManager(NullLoggerFactory.Instance);
        await using var runtime = CreateRuntime(context, catalog, manager);
        using var viewModel = new AgentMcpSettingsViewModel(runtime.Gateway);

        var first = viewModel.InitializeAsync();
        var second = viewModel.InitializeAsync();
        await Task.WhenAll(first, second);

        AdaptiveEditorLifecycleContract.SharedInitialization(Adapt(viewModel), first, second);
        Assert.Same(first, second);
        Assert.Equal(McpStatusKind.Error, viewModel.StatusKind);
        Assert.Contains("Injected MCP catalog failure", viewModel.StatusText, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task QuickSelection_DisablesSaveUntilSelectedDocumentCompletes()
    {
        var context = new ControlledMcpPackageContext();
        var catalog = new McpServerCatalogService(context);
        await catalog.SaveServerAsync(Server("alpha"), Empty, Empty, TestContext.Current.CancellationToken);
        await catalog.SaveServerAsync(Server("beta"), Empty, Empty, TestContext.Current.CancellationToken);
        await using var manager = new McpClientConnectionManager(NullLoggerFactory.Instance);
        await using var runtime = CreateRuntime(context, catalog, manager);
        using var viewModel = new AgentMcpSettingsViewModel(runtime.Gateway);
        await GetTask(viewModel, "Initialization");
        var read = context.State.BlockRead(ServerStorageKey("beta"));

        viewModel.SelectedServer = viewModel.Servers.Single(server => server.ServerId == "beta");
        var load = GetTask(viewModel, "CurrentDocumentLoad");

        AdaptiveEditorLifecycleContract.HydrationBlocksMutation(Adapt(viewModel));
        Assert.True(viewModel.IsDocumentLoading);
        Assert.False(viewModel.IsDocumentReady);
        Assert.False(viewModel.SaveCommand.CanExecute(null));
        var writesBeforeBlockedSave = context.State.WriteCount;
        await viewModel.SaveCommand.ExecuteAsync(null);
        Assert.Equal(writesBeforeBlockedSave, context.State.WriteCount);
        await read.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        read.Release.SetResult();
        await load;

        Assert.False(viewModel.IsDocumentLoading);
        Assert.True(viewModel.IsDocumentReady);
        Assert.True(viewModel.SaveCommand.CanExecute(null));
        Assert.Equal("beta", viewModel.Name);
    }

    [AvaloniaFact]
    public async Task RapidSelection_StaleDocumentCannotOverwriteCurrentSelection()
    {
        var context = new ControlledMcpPackageContext();
        var catalog = new McpServerCatalogService(context);
        await catalog.SaveServerAsync(Server("alpha"), Empty, Empty, TestContext.Current.CancellationToken);
        await catalog.SaveServerAsync(Server("beta"), Empty, Empty, TestContext.Current.CancellationToken);
        await using var manager = new McpClientConnectionManager(NullLoggerFactory.Instance);
        McpFixturePhaseLog.Mark("RapidSelection:before-CreateRuntime");
        await using var runtime = CreateRuntime(context, catalog, manager);
        McpFixturePhaseLog.Mark("RapidSelection:after-CreateRuntime");
        McpFixturePhaseLog.Mark("RapidSelection:before-view-model-construction");
        using var viewModel = new AgentMcpSettingsViewModel(runtime.Gateway);
        McpFixturePhaseLog.Mark("RapidSelection:after-view-model-construction");
        await AwaitPhaseAsync(GetTask(viewModel, "Initialization"), "initialization");
        McpFixturePhaseLog.Mark("RapidSelection:after-initialization");
        var read = context.State.BlockRead(ServerStorageKey("beta"));

        viewModel.SelectedServer = viewModel.Servers.Single(server => server.ServerId == "beta");
        var staleLoad = GetTask(viewModel, "CurrentDocumentLoad");
        await read.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        viewModel.SelectedServer = viewModel.Servers.Single(server => server.ServerId == "alpha");
        await AwaitPhaseAsync(GetTask(viewModel, "CurrentDocumentLoad"), "replacement document load");
        McpFixturePhaseLog.Mark("RapidSelection:after-replacement-document-load");
        read.Release.SetResult();
        await AwaitPhaseAsync(staleLoad, "retired document load");
        McpFixturePhaseLog.Mark("RapidSelection:after-retired-document-load");

        AdaptiveEditorLifecycleContract.CurrentSelectionWins(
            Adapt(viewModel),
            "alpha",
            "beta");
        Assert.Equal("alpha", viewModel.SelectedServer?.ServerId);
        Assert.Equal("alpha", viewModel.Name);
        McpFixturePhaseLog.Mark("RapidSelection:assertions-complete");
    }

    [AvaloniaFact]
    public async Task RapidSelection_StaleConnectionStatusCannotOverwriteCurrentServer()
    {
        var context = new ControlledMcpPackageContext();
        var catalog = new McpServerCatalogService(context);
        await catalog.SaveServerAsync(
            Server("alpha") with { IsEnabled = false },
            Empty,
            Empty,
            TestContext.Current.CancellationToken);
        await catalog.SaveServerAsync(
            Server("beta") with { IsEnabled = false, OAuthEnabled = true },
            Empty,
            Empty,
            TestContext.Current.CancellationToken);
        await catalog.SaveServerAsync(
            Server("gamma"),
            Empty,
            Empty,
            TestContext.Current.CancellationToken);
        await using var manager = new McpClientConnectionManager(NullLoggerFactory.Instance);
        await using var oauth = new McpOAuthService(context);
        await using var runtime = CreateRuntime(context, catalog, manager, oauth);
        using var viewModel = new AgentMcpSettingsViewModel(runtime.Gateway);
        await GetTask(viewModel, "Initialization");
        var blockedSecret = context.SecretsStore.BlockNextRead();

        viewModel.SelectedServer = viewModel.Servers.Single(server => server.ServerId == "beta");
        var staleStatus = GetTask(viewModel, "CurrentConnectionStatusLoad");
        await blockedSecret.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        viewModel.SelectedServer = viewModel.Servers.Single(server => server.ServerId == "gamma");
        await GetTask(viewModel, "CurrentConnectionStatusLoad");

        blockedSecret.Release.TrySetResult();
        await staleStatus;

        Assert.Equal("gamma", viewModel.SelectedServer?.ServerId);
        Assert.Equal(McpApp::Sunder.Package.Agent.Mcp.Services.McpConnectionStatusKind.Idle, viewModel.ConnectionStatusKind);
        Assert.Equal("Configured but not connected yet.", viewModel.ConnectionStatusText);
    }

    [AvaloniaFact]
    public async Task EditDuringSave_IsNotOverwrittenByCatalogReload()
    {
        var context = new ControlledMcpPackageContext();
        var catalog = new McpServerCatalogService(context);
        await catalog.SaveServerAsync(Server("alpha"), Empty, Empty, TestContext.Current.CancellationToken);
        await using var manager = new McpClientConnectionManager(NullLoggerFactory.Instance);
        await using var runtime = CreateRuntime(context, catalog, manager);
        using var viewModel = new AgentMcpSettingsViewModel(runtime.Gateway);
        await GetTask(viewModel, "Initialization");
        var write = context.State.BlockNextWrite();
        var newerEdit = """
            {
              "type": "remote",
              "url": "https://newer.example/mcp"
            }
            """;

        var save = viewModel.SaveCommand.ExecuteAsync(null);
        await write.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        viewModel.EditorText = newerEdit;
        write.Release.SetResult();
        await save;

        AdaptiveEditorLifecycleContract.EditDuringSaveRemainsDirty(
            Adapt(
                viewModel,
                isDirty: () => viewModel.StatusText.Contains(
                    "remain unsaved",
                    StringComparison.OrdinalIgnoreCase)),
            newerEdit);
        Assert.Equal(newerEdit, viewModel.EditorText);
        Assert.Contains("remain unsaved", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [AvaloniaFact]
    public async Task EditedAutomaticSelection_SurvivesCompactResizeAndLateSaveCompletion()
    {
        var context = new ControlledMcpPackageContext();
        var catalog = new McpServerCatalogService(context);
        await catalog.SaveServerAsync(Server("alpha"), Empty, Empty, TestContext.Current.CancellationToken);
        await using var manager = new McpClientConnectionManager(NullLoggerFactory.Instance);
        await using var runtime = CreateRuntime(context, catalog, manager);
        using var viewModel = new AgentMcpSettingsViewModel(runtime.Gateway);
        await GetTask(viewModel, "Initialization");
        var selected = Assert.IsType<McpApp::Sunder.Package.Agent.Mcp.ConfiguredMcpServerRecord>(viewModel.SelectedServer);
        viewModel.EditorText = """
            {
              "type": "remote",
              "url": "https://edited.example/mcp"
            }
            """;

        viewModel.IsCompactLayout = true;

        Assert.Same(selected, viewModel.SelectedServer);
        Assert.True(viewModel.IsEditorActive);
        var write = context.State.BlockNextWrite();
        var save = viewModel.SaveCommand.ExecuteAsync(null);
        await write.Started.Task.WaitAsync(TestContext.Current.CancellationToken);

        viewModel.IsCompactLayout = false;
        write.Release.TrySetResult();
        await save.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal("alpha", viewModel.SelectedServer?.ServerId);
        Assert.True(viewModel.ShowEditorPane);
    }

    [AvaloniaFact]
    public async Task BackRetiresNonCooperativeDocumentLoadAndCreateDoesNotWaitForIt()
    {
        var context = new ControlledMcpPackageContext();
        var catalog = new McpServerCatalogService(context);
        await catalog.SaveServerAsync(Server("alpha"), Empty, Empty, TestContext.Current.CancellationToken);
        await catalog.SaveServerAsync(Server("beta"), Empty, Empty, TestContext.Current.CancellationToken);
        await using var manager = new McpClientConnectionManager(NullLoggerFactory.Instance);
        await using var runtime = CreateRuntime(context, catalog, manager);
        using var viewModel = new AgentMcpSettingsViewModel(runtime.Gateway);
        await GetTask(viewModel, "Initialization");
        var read = context.State.BlockRead(ServerStorageKey("beta"));
        viewModel.SelectedServer = viewModel.Servers.Single(server => server.ServerId == "beta");
        var load = GetTask(viewModel, "CurrentDocumentLoad");
        await read.Started.Task.WaitAsync(TestContext.Current.CancellationToken);

        await AdaptiveEditorLifecycleContract.RetiredWorkCannotPublishAsync(
            Adapt(viewModel),
            () =>
            {
                viewModel.BackToServerListCommand.Execute(null);
                viewModel.CreateServerCommand.Execute(null);
            },
            () => read.Release.TrySetResult(),
            load.WaitAsync(TimeSpan.FromSeconds(2)));

        Assert.True(viewModel.IsDocumentReady);
        Assert.False(viewModel.IsDocumentLoading);
        Assert.True(viewModel.IsEditorActive || !viewModel.IsCompactLayout);
        Assert.Equal("mcp_server", viewModel.Name);
    }

    [AvaloniaFact]
    public async Task Disposal_CancelsDocumentLoadAndSuppressesLateStateCallbacks()
    {
        var context = new ControlledMcpPackageContext();
        var catalog = new McpServerCatalogService(context);
        await catalog.SaveServerAsync(Server("alpha"), Empty, Empty, TestContext.Current.CancellationToken);
        await catalog.SaveServerAsync(Server("beta"), Empty, Empty, TestContext.Current.CancellationToken);
        await using var manager = new McpClientConnectionManager(NullLoggerFactory.Instance);
        await using var runtime = CreateRuntime(context, catalog, manager);
        var viewModel = new AgentMcpSettingsViewModel(runtime.Gateway);
        await GetTask(viewModel, "Initialization");
        var read = context.State.BlockRead(ServerStorageKey("beta"));
        viewModel.SelectedServer = viewModel.Servers.Single(server => server.ServerId == "beta");
        var load = GetTask(viewModel, "CurrentDocumentLoad");
        await read.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        var notifications = 0;
        viewModel.PropertyChanged += (_, _) => Interlocked.Increment(ref notifications);

        await AdaptiveEditorLifecycleContract.RetiredWorkCannotPublishAsync(
            Adapt(viewModel, publicationCount: () => Volatile.Read(ref notifications)),
            viewModel.Dispose,
            read.Release.SetResult,
            load);

    }

    private static readonly IReadOnlyDictionary<string, string> Empty =
        new Dictionary<string, string>();

    private static string ServerStorageKey(string serverId)
        => Sunder.Sdk.Storage.PackageStorageKeyFactory.Create(
            "mcp.catalog.server",
            1,
            serverId.ToUpperInvariant());

    private static ConfiguredMcpServerRecord Server(string id) => new()
    {
        ServerId = id,
        Name = id,
        DisplayName = id,
        IsEnabled = true,
        TransportType = ConfiguredMcpTransportType.HttpSse,
        EndpointUrl = $"https://{id}.example/mcp",
        CreatedAtUtc = DateTimeOffset.UtcNow,
        UpdatedAtUtc = DateTimeOffset.UtcNow,
    };

    private static McpManagementGatewayFixture CreateRuntime(
        IPackageContext context,
        McpServerCatalogService catalog,
        McpClientConnectionManager manager,
        McpOAuthService? oauth = null)
        => McpManagementGatewayFixture.Create(context, catalog, manager, oauth);

    private static IAdaptiveEditorLifecycleAdapter Adapt(
        AgentMcpSettingsViewModel viewModel,
        Func<bool>? isDirty = null,
        Func<int>? publicationCount = null)
        => new DelegateAdaptiveEditorLifecycleAdapter(
            "MCP",
            () => new AdaptiveEditorSnapshot(
                IsHydrating: !viewModel.IsDisposed && viewModel.IsDocumentLoading,
                CanMutate: !viewModel.IsDisposed && viewModel.SaveCommand.CanExecute(null),
                IsBusy: !viewModel.IsDisposed && viewModel.IsBusy,
                IsDirty: isDirty?.Invoke() ?? false,
                viewModel.SelectedServer?.ServerId,
                viewModel.EditorText,
                publicationCount?.Invoke() ?? 0,
                viewModel.IsRuntimeAvailable
                    ? AdaptiveEditorRuntimeState.Available
                    : AdaptiveEditorRuntimeState.Unavailable),
            viewModel);

    private static Task GetTask(object instance, string propertyName)
        => Assert.IsAssignableFrom<Task>(instance.GetType()
            .GetProperty(propertyName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(instance));

    private static async Task AwaitPhaseAsync(Task task, string phase)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (TimeoutException exception)
        {
            throw new TimeoutException($"MCP race fixture timed out during {phase}.", exception);
        }
    }

    private sealed class ControlledMcpPackageContext : IPackageContext
    {
        private readonly ControlledStorage _storage;

        public ControlledMcpPackageContext()
        {
            State = new ControlledKeyValueStore();
            SecretsStore = new ControlledSecrets();
            _storage = new ControlledStorage(State);
        }

        public ControlledKeyValueStore State { get; }
        public ControlledSecrets SecretsStore { get; }
        public string PackageId => "test.mcp.presentation";
        public string Version { get; } = "1.0.0";
        public string ContentRootPath => AppContext.BaseDirectory;
        public IPackageStorageContext Storage => _storage;
        public IPackageSettings Settings { get; } = new EmptySettings();
        public IPackageSecrets Secrets => SecretsStore;
        public IPackageLogging Logging => NullPackageLogging.Instance;
    }

    private sealed class ControlledStorage(IPackageKeyValueStore state) : IPackageStorageContext
    {
        public IPackageFileStore Files { get; } = new EmptyFiles();
        public IPackageKeyValueStore State => state;
        public IPackageRoleLocalWorkspace RoleLocalWorkspace { get; } = new EmptyWorkspace();
    }

    private sealed class ControlledKeyValueStore : IPackageKeyValueStore
    {
        private readonly ConcurrentDictionary<string, string> _values = new(StringComparer.Ordinal);
        private ReadBlock? _readBlock;
        private WriteBlock? _writeBlock;

        public int WriteCount { get; private set; }

        public Exception? ListKeysFailure { get; set; }

        public ReadBlock BlockRead(string key)
        {
            TestPackageStorageGuards.Key(key);
            Assert.True(_values.ContainsKey(key), $"Cannot block missing storage key '{key}'.");
            var block = new ReadBlock(key);
            _readBlock = block;
            return block;
        }

        public WriteBlock BlockNextWrite()
        {
            var block = new WriteBlock();
            _writeBlock = block;
            return block;
        }

        public async Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default)
        {
            TestPackageStorageGuards.Key(key);
            var block = _readBlock;
            if (block is not null && string.Equals(block.Key, key, StringComparison.Ordinal))
            {
                block.Started.TrySetResult();
                await block.Release.Task.ConfigureAwait(false);
                Interlocked.CompareExchange(ref _readBlock, null, block);
            }

            return _values.GetValueOrDefault(key);
        }

        public async Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            TestPackageStorageGuards.Key(key);
            TestPackageStorageGuards.Value(value);
            var block = Interlocked.Exchange(ref _writeBlock, null);
            if (block is not null)
            {
                block.Started.TrySetResult();
                await block.Release.Task.ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            _values[key] = value;
            WriteCount++;
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

        public Task<IReadOnlyList<string>> ListKeysAsync(
            string? prefix = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TestPackageStorageGuards.Prefix(prefix);
            return ListKeysFailure is { } failure
                ? Task.FromException<IReadOnlyList<string>>(failure)
                : Task.FromResult<IReadOnlyList<string>>(_values.Keys
                    .Where(key => prefix is null || key.StartsWith(prefix, StringComparison.Ordinal))
                    .Order(StringComparer.Ordinal)
                    .ToArray());
        }
    }

    private sealed class ReadBlock(string key)
    {
        public string Key { get; } = key;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class WriteBlock
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class EmptyFiles : IPackageFileStore
    {
        public Task<byte[]?> ReadAsync(string relativePath, CancellationToken cancellationToken = default) { TestPackageStorageGuards.RelativePath(relativePath); return Task.FromResult<byte[]?>(null); }
        public Task WriteAsync(string relativePath, ReadOnlyMemory<byte> contents, CancellationToken cancellationToken = default) { TestPackageStorageGuards.RelativePath(relativePath); TestPackageStorageGuards.FileLength(contents.Length); return Task.CompletedTask; }
        public Task DeleteAsync(string relativePath, CancellationToken cancellationToken = default) { TestPackageStorageGuards.RelativePath(relativePath); return Task.CompletedTask; }
    }

    private sealed class EmptySettings : IPackageSettings
    {
        public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task<string?> GetStoredValueAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteValueAsync(string key, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class EmptySecrets : IPackageSecrets
    {
        public Task<string?> GetSecretAsync(string key, CancellationToken cancellationToken = default) { TestPackageStorageGuards.Key(key); return Task.FromResult<string?>(null); }
        public Task SetSecretAsync(string key, string value, CancellationToken cancellationToken = default) { TestPackageStorageGuards.Key(key); TestPackageStorageGuards.Value(value); return Task.CompletedTask; }
        public Task DeleteSecretAsync(string key, CancellationToken cancellationToken = default) { TestPackageStorageGuards.Key(key); return Task.CompletedTask; }
    }

    public sealed class ControlledSecrets : IPackageSecrets
    {
        private SecretReadBlock? _nextRead;

        public SecretReadBlock BlockNextRead()
        {
            var block = new SecretReadBlock();
            Assert.Null(Interlocked.Exchange(ref _nextRead, block));
            return block;
        }

        public async Task<string?> GetSecretAsync(
            string key,
            CancellationToken cancellationToken = default)
        {
            TestPackageStorageGuards.Key(key);
            var block = Interlocked.Exchange(ref _nextRead, null);
            if (block is not null)
            {
                block.Started.TrySetResult();
                await block.Release.Task;
            }
            return null;
        }

        public Task SetSecretAsync(
            string key,
            string value,
            CancellationToken cancellationToken = default)
        {
            TestPackageStorageGuards.Key(key);
            TestPackageStorageGuards.Value(value);
            return Task.CompletedTask;
        }

        public Task DeleteSecretAsync(string key, CancellationToken cancellationToken = default)
        {
            TestPackageStorageGuards.Key(key);
            return Task.CompletedTask;
        }
    }

    public sealed class SecretReadBlock
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class EmptyWorkspace : IPackageRoleLocalWorkspace
    {
        public string WorkspaceRootPath => Path.GetTempPath();
        public string GetLocalPath(string relativePath) => Path.Combine(WorkspaceRootPath, relativePath);
    }
}
