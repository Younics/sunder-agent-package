using System.Collections.Concurrent;
using System.Reflection;
using Avalonia.Headless.XUnit;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sunder.Package.Agent.Mcp;
using Sunder.Package.Agent.Mcp.Services;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Logging;
using Xunit;

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
        using var viewModel = CreateViewModel(catalog, manager);

        var first = viewModel.InitializeAsync();
        var second = viewModel.InitializeAsync();
        await Task.WhenAll(first, second);

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
        using var viewModel = CreateViewModel(catalog, manager);
        await GetTask(viewModel, "Initialization");
        var read = context.State.BlockRead("mcp.servers.beta");

        viewModel.SelectedServer = viewModel.Servers.Single(server => server.ServerId == "beta");
        var load = GetTask(viewModel, "CurrentDocumentLoad");

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
        using var viewModel = CreateViewModel(catalog, manager);
        await GetTask(viewModel, "Initialization");
        var read = context.State.BlockRead("mcp.servers.beta");

        viewModel.SelectedServer = viewModel.Servers.Single(server => server.ServerId == "beta");
        var staleLoad = GetTask(viewModel, "CurrentDocumentLoad");
        await read.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        viewModel.SelectedServer = viewModel.Servers.Single(server => server.ServerId == "alpha");
        await GetTask(viewModel, "CurrentDocumentLoad");
        read.Release.SetResult();
        await staleLoad;

        Assert.Equal("alpha", viewModel.SelectedServer?.ServerId);
        Assert.Equal("alpha", viewModel.Name);
    }

    [AvaloniaFact]
    public async Task EditDuringSave_IsNotOverwrittenByCatalogReload()
    {
        var context = new ControlledMcpPackageContext();
        var catalog = new McpServerCatalogService(context);
        await catalog.SaveServerAsync(Server("alpha"), Empty, Empty, TestContext.Current.CancellationToken);
        await using var manager = new McpClientConnectionManager(NullLoggerFactory.Instance);
        using var viewModel = CreateViewModel(catalog, manager);
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

        Assert.Equal(newerEdit, viewModel.EditorText);
        Assert.Contains("remain unsaved", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [AvaloniaFact]
    public async Task Disposal_CancelsDocumentLoadAndSuppressesLateStateCallbacks()
    {
        var context = new ControlledMcpPackageContext();
        var catalog = new McpServerCatalogService(context);
        await catalog.SaveServerAsync(Server("alpha"), Empty, Empty, TestContext.Current.CancellationToken);
        await catalog.SaveServerAsync(Server("beta"), Empty, Empty, TestContext.Current.CancellationToken);
        await using var manager = new McpClientConnectionManager(NullLoggerFactory.Instance);
        var viewModel = CreateViewModel(catalog, manager);
        await GetTask(viewModel, "Initialization");
        var read = context.State.BlockRead("mcp.servers.beta");
        viewModel.SelectedServer = viewModel.Servers.Single(server => server.ServerId == "beta");
        var load = GetTask(viewModel, "CurrentDocumentLoad");
        await read.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        var notifications = 0;
        viewModel.PropertyChanged += (_, _) => notifications++;

        viewModel.Dispose();
        var notificationsAtDisposal = notifications;
        read.Release.SetResult();
        await load;

        Assert.Equal(notificationsAtDisposal, notifications);
    }

    private static readonly IReadOnlyDictionary<string, string> Empty =
        new Dictionary<string, string>();

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

    private static AgentMcpSettingsViewModel CreateViewModel(
        McpServerCatalogService catalog,
        McpClientConnectionManager manager)
        => new(
            new McpSettingsEditorService(catalog),
            new McpConfigurationCoordinator(new McpEcosystemConfigurationImporter(catalog), syncService: null),
            new McpServerConnectionService(catalog, manager, oauthService: null),
            new McpOAuthCoordinator(oauthService: null, manager));

    private static Task GetTask(object instance, string propertyName)
        => Assert.IsAssignableFrom<Task>(instance.GetType()
            .GetProperty(propertyName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(instance));

    private sealed class ControlledMcpPackageContext : IPackageContext
    {
        private readonly ControlledStorage _storage;

        public ControlledMcpPackageContext()
        {
            State = new ControlledKeyValueStore();
            _storage = new ControlledStorage(State);
        }

        public ControlledKeyValueStore State { get; }
        public string PackageId => "test.mcp.presentation";
        public Version Version { get; } = new(1, 0, 0);
        public string InstallPath => AppContext.BaseDirectory;
        public IPackageStorageContext Storage => _storage;
        public IPackageConfiguration Configuration { get; } = new EmptyConfiguration();
        public IPackageSecrets Secrets { get; } = new EmptySecrets();
        public ILoggerFactory LoggerFactory => NullLoggerFactory.Instance;
        public IPackageLogging Logging => NullPackageLogging.Instance;
    }

    private sealed class ControlledStorage(IPackageKeyValueStore state) : IPackageStorageContext
    {
        public string DataRootPath => Path.GetTempPath();
        public string CacheRootPath => Path.GetTempPath();
        public string LogsRootPath => Path.GetTempPath();
        public IPackageFileStore Files { get; } = new EmptyFiles();
        public IPackageKeyValueStore State => state;
    }

    private sealed class ControlledKeyValueStore : IPackageKeyValueStore
    {
        private readonly ConcurrentDictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
        private ReadBlock? _readBlock;
        private WriteBlock? _writeBlock;

        public int WriteCount { get; private set; }

        public Exception? ListKeysFailure { get; set; }

        public ReadBlock BlockRead(string key)
        {
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

        public string? GetValue(string key) => _values.GetValueOrDefault(key);

        public async Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default)
        {
            var block = _readBlock;
            if (block is not null && string.Equals(block.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                block.Started.TrySetResult();
                await block.Release.Task.ConfigureAwait(false);
                Interlocked.CompareExchange(ref _readBlock, null, block);
            }

            return GetValue(key);
        }

        public async Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default)
        {
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
            => Task.FromResult(_values.ContainsKey(key));

        public Task DeleteValueAsync(string key, CancellationToken cancellationToken = default)
        {
            _values.TryRemove(key, out _);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> ListKeysAsync(
            string? prefix = null,
            CancellationToken cancellationToken = default)
            => ListKeysFailure is { } failure
                ? Task.FromException<IReadOnlyList<string>>(failure)
                : Task.FromResult<IReadOnlyList<string>>(_values.Keys
                    .Where(key => prefix is null || key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    .ToArray());
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
        public string RootPath => Path.GetTempPath();
        public string GetPath(string relativePath) => Path.Combine(RootPath, relativePath);
    }

    private sealed class EmptyConfiguration : IPackageConfiguration
    {
        public string? GetValue(string key) => null;
    }

    private sealed class EmptySecrets : IPackageSecrets
    {
        public string? GetSecret(string key) => null;
        public void SetSecret(string key, string value) { }
        public void DeleteSecret(string key) { }
    }
}
