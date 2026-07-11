using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sunder.Package.Agent.Mcp.Services;
using Sunder.Package.Agent.Shared.Presentation;

namespace Sunder.Package.Agent.Mcp;

public sealed partial class AgentMcpSettingsViewModel : ObservableObject, IDisposable
{
    private readonly McpSettingsEditorService _editor;
    private readonly McpServerConnectionService _connections;
    private readonly McpSettingsOperationsViewModel _operations;
    private readonly IPresentationDispatcher _uiDispatcher;
    private readonly Task _initialization;
    private bool _suppressSelectionHandlers;
    private bool _suppressServerChangeNotifications;
    private bool _suppressEditorTracking;
    private bool _isDocumentLoading;
    private bool _isDocumentReady = true;
    private bool _reloadServersPending;
    private bool _disposed;
    private int _serverLoadVersion;
    private long _editorRevision;
    private CancellationTokenSource? _serverLoadCancellation;
    private Task _currentDocumentLoad = Task.CompletedTask;

    internal static IReadOnlyCollection<string> OwnedConfigurationKeys { get; } = [];

    public AgentMcpSettingsViewModel(
        McpSettingsEditorService editor,
        McpConfigurationCoordinator configuration,
        McpServerConnectionService connections,
        McpOAuthCoordinator oauth)
        : this(editor, configuration, connections, oauth, PresentationDispatcher.Capture())
    {
    }

    internal AgentMcpSettingsViewModel(
        McpSettingsEditorService editor,
        McpConfigurationCoordinator configuration,
        McpServerConnectionService connections,
        McpOAuthCoordinator oauth,
        IPresentationDispatcher uiDispatcher)
    {
        _editor = editor;
        _connections = connections;
        _uiDispatcher = uiDispatcher;
        _operations = new McpSettingsOperationsViewModel(
            this,
            editor,
            configuration,
            connections,
            oauth,
            uiDispatcher);
        _editor.ServersChanged += OnServersChanged;
        _connections.StatusChanged += OnConnectionStatusChanged;
        _operations.PropertyChanged += OnOperationsPropertyChanged;
        _initialization = _operations.InitializeAsync();
    }

    internal AgentMcpSettingsViewModel(
        McpServerCatalogService catalog,
        McpClientConnectionManager connectionManager,
        McpOAuthService? oauthService = null,
        McpEcosystemConfigurationImporter? configurationImporter = null,
        McpSunderConfigurationSyncService? sunderConfigurationSyncService = null)
        : this(CreateLegacyDependencies(catalog, connectionManager, oauthService, configurationImporter, sunderConfigurationSyncService))
    {
    }

    private AgentMcpSettingsViewModel(LegacyDependencies dependencies)
        : this(dependencies.Editor, dependencies.Configuration, dependencies.Connections, dependencies.OAuth)
    {
    }

    public ObservableCollection<ConfiguredMcpServerRecord> Servers { get; } = [];

    internal IReadOnlyList<McpCatalogDiagnostic> CatalogDiagnostics => _editor.Diagnostics;

    internal Task Initialization => _initialization;

    public Task InitializeAsync() => _initialization;

    public bool HasSelectedServer => SelectedServer is not null;
    public bool HasSelectedOAuthServer => SelectedServer?.OAuthEnabled == true;
    public bool IsListActive => !IsEditorActive;
    public bool ShowWideLayout => !IsCompactLayout;
    public bool ShowCompactList => IsCompactLayout && IsListActive;
    public bool ShowCompactEditor => IsCompactLayout && IsEditorActive;
    public bool ShowListPane => ShowWideLayout || ShowCompactList;
    public bool ShowEditorPane => ShowWideLayout || ShowCompactEditor;
    public bool IsBusy => _operations.IsBusy || IsDocumentLoading;
    public bool CanStartOperation => !IsBusy;
    public bool CanNavigateServers => !IsBusy;
    public bool IsDocumentLoading => _isDocumentLoading;
    public bool IsDocumentReady => _isDocumentReady;
    public bool IsEditorReadOnly => IsDocumentLoading;
    public bool IsDiscovering => _operations.IsDiscovering;
    public string StatusText => _operations.StatusText;
    public McpStatusKind StatusKind => _operations.StatusKind;
    public bool IsStatusSuccess => StatusKind == McpStatusKind.Success;
    public bool IsStatusWarning => StatusKind == McpStatusKind.Warning;
    public bool IsStatusError => StatusKind == McpStatusKind.Error;
    public bool IsConnectionStatusSuccess => ConnectionStatusKind == McpConnectionStatusKind.Connected;
    public bool IsConnectionStatusWarning => ConnectionStatusKind is McpConnectionStatusKind.Connecting
        or McpConnectionStatusKind.DiscoveringTools or McpConnectionStatusKind.Disabled or McpConnectionStatusKind.Disconnected;
    public bool IsConnectionStatusError => ConnectionStatusKind == McpConnectionStatusKind.Error;
    public bool HasConnectionStatusDetail => !string.IsNullOrWhiteSpace(ConnectionStatusDetail);
    public IAsyncRelayCommand SaveCommand => _operations.SaveCommand;
    public IAsyncRelayCommand DeleteCommand => _operations.DeleteCommand;
    public IAsyncRelayCommand ImportCommonConfigurationsCommand => _operations.ImportCommonConfigurationsCommand;
    public IAsyncRelayCommand DiscoverToolsCommand => _operations.DiscoverToolsCommand;
    public IRelayCommand CancelDiscoveryCommand => _operations.CancelDiscoveryCommand;
    public IAsyncRelayCommand DisconnectMcpServerCommand => _operations.DisconnectMcpServerCommand;
    public IAsyncRelayCommand ReconnectMcpServerCommand => _operations.ReconnectMcpServerCommand;
    public IAsyncRelayCommand AuthorizeMcpServerCommand => _operations.AuthorizeMcpServerCommand;
    public IAsyncRelayCommand DisconnectMcpServerOAuthCommand => _operations.DisconnectMcpServerOAuthCommand;

    [ObservableProperty]
    private ConfiguredMcpServerRecord? _selectedServer;

    [ObservableProperty]
    private bool _isCompactLayout;

    [ObservableProperty]
    private bool _isEditorActive;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _editorText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConnectionStatusSuccess))]
    [NotifyPropertyChangedFor(nameof(IsConnectionStatusWarning))]
    [NotifyPropertyChangedFor(nameof(IsConnectionStatusError))]
    private McpConnectionStatusKind _connectionStatusKind = McpConnectionStatusKind.Idle;

    [ObservableProperty]
    private string _connectionStatusText = "Configured but not connected yet.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasConnectionStatusDetail))]
    private string _connectionStatusDetail = string.Empty;

    [ObservableProperty]
    private string _connectionDiagnosticsText = string.Empty;

    partial void OnSelectedServerChanged(ConfiguredMcpServerRecord? value)
    {
        OnPropertyChanged(nameof(HasSelectedServer));
        OnPropertyChanged(nameof(HasSelectedOAuthServer));
        if (_suppressSelectionHandlers)
        {
            _operations.NotifyContextChanged();
            return;
        }

        CancelDocumentLoad(documentReady: value is null);
        if (value is null)
        {
            ClearEditor();
            RefreshConnectionStatus(null);
            _operations.NotifyContextChanged();
            return;
        }

        RefreshConnectionStatus(value);
        _currentDocumentLoad = LoadSelectedServerAsync(value, CancellationToken.None);
        if (IsCompactLayout)
        {
            IsEditorActive = true;
        }

        _operations.NotifyContextChanged();
    }

    partial void OnNameChanged(string value) => TrackEditorChange();

    partial void OnEditorTextChanged(string value) => TrackEditorChange();

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void CreateServer()
    {
        SelectedServer = null;
        Name = "mcp_server";
        EditorText = McpConfigurationDocument.CreateLocalTemplate();
        IsEditorActive = true;
        RefreshConnectionStatus(null);
        _operations.PresentStatus("Editing a new MCP server draft. Paste a bare MCP server object or start from a template.", McpStatusKind.Warning);
    }

    [RelayCommand(CanExecute = nameof(CanNavigateServers))]
    private void BackToServerList()
    {
        if (IsCompactLayout)
        {
            SelectedServer = null;
            _operations.ClearStatus();
        }

        IsEditorActive = false;
    }

    [RelayCommand]
    private void OpenServerEditor(ConfiguredMcpServerRecord? server)
    {
        if (server is not null)
        {
            ActivateServer(server);
        }
    }

    public void ActivateServer(ConfiguredMcpServerRecord server)
    {
        if (!CanNavigateServers)
        {
            return;
        }

        if (!string.Equals(SelectedServer?.ServerId, server.ServerId, StringComparison.OrdinalIgnoreCase))
        {
            SelectedServer = server;
        }

        if (IsCompactLayout)
        {
            IsEditorActive = true;
        }
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void LoadLocalTemplate() => LoadTemplate(McpConfigurationDocument.CreateLocalTemplate(), "Loaded local MCP template.");

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void LoadRemoteTemplate() => LoadTemplate(McpConfigurationDocument.CreateRemoteTemplate(), "Loaded remote MCP template.");

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void Format()
    {
        try
        {
            Name = _editor.NormalizeName(Name);
            EditorText = _editor.Format(SelectedServer?.ServerId ?? Guid.NewGuid().ToString("N"), Name, EditorText, SelectedServer);
            _operations.PresentStatus("MCP configuration formatted.", McpStatusKind.Success, autoClear: true);
        }
        catch (Exception ex)
        {
            _operations.PresentStatus(ex.Message, McpStatusKind.Error);
        }
    }

    partial void OnIsCompactLayoutChanged(bool value)
    {
        if (value && !IsEditorActive)
        {
            SelectedServer = null;
        }
        else if (!value && SelectedServer is null)
        {
            SelectedServer = Servers.FirstOrDefault();
        }

        NotifyLayout();
    }

    partial void OnIsEditorActiveChanged(bool value) => NotifyLayout();

    public Task ImportConfigurationFileAsync(string filePath) => _operations.ImportFileAsync(filePath);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _serverLoadVersion++;
        _serverLoadCancellation?.Cancel();
        _serverLoadCancellation?.Dispose();
        _serverLoadCancellation = null;
        _editor.ServersChanged -= OnServersChanged;
        _connections.StatusChanged -= OnConnectionStatusChanged;
        _operations.PropertyChanged -= OnOperationsPropertyChanged;
        _operations.Dispose();
    }

    internal async Task ReloadServersAsync(
        string? selectServerId,
        CancellationToken cancellationToken,
        bool loadSelectedDocument = true)
    {
        var servers = await _editor.ListAsync(cancellationToken);
        Task documentLoad = Task.CompletedTask;
        await _uiDispatcher.InvokeAsync(() =>
        {
            if (_disposed || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            var currentServerId = SelectedServer?.ServerId;
            _suppressSelectionHandlers = true;
            try
            {
                Servers.Clear();
                foreach (var server in servers)
                {
                    Servers.Add(server);
                }

                SelectedServer = Servers.FirstOrDefault(server => server.ServerId == selectServerId)
                    ?? ((!IsCompactLayout || selectServerId is not null)
                        ? Servers.FirstOrDefault(server => server.ServerId == currentServerId) ?? Servers.FirstOrDefault()
                        : null);
            }
            finally
            {
                _suppressSelectionHandlers = false;
            }

            if (SelectedServer is null)
            {
                CancelDocumentLoad(documentReady: true);
                ClearEditor();
                RefreshConnectionStatus(null);
            }
            else if (loadSelectedDocument)
            {
                documentLoad = LoadSelectedServerAsync(SelectedServer, cancellationToken);
                _currentDocumentLoad = documentLoad;
            }
            else
            {
                RefreshConnectionStatus(SelectedServer);
            }

            _operations.NotifyContextChanged();
        }).ConfigureAwait(false);
        await documentLoad.ConfigureAwait(false);
    }

    internal async Task WithSuppressedCatalogEventsAsync(Func<Task> action)
    {
        _suppressServerChangeNotifications = true;
        try
        {
            await action();
        }
        finally
        {
            _suppressServerChangeNotifications = false;
        }
    }

    internal void RefreshConnectionStatus(ConfiguredMcpServerRecord? server)
    {
        if (server is null)
        {
            ConnectionStatusKind = McpConnectionStatusKind.Idle;
            ConnectionStatusText = "No saved MCP server selected.";
            ConnectionStatusDetail = string.Empty;
            ConnectionDiagnosticsText = string.Empty;
            return;
        }

        var presentation = _connections.GetPresentation(server);
        ConnectionStatusKind = presentation.Status.Kind;
        ConnectionStatusText = presentation.Status.Message;
        ConnectionStatusDetail = presentation.Detail;
        ConnectionDiagnosticsText = presentation.Diagnostics;
    }

    internal McpEditorSnapshot CaptureEditorSnapshot() => new(
        SelectedServer,
        Name,
        EditorText,
        IsCompactLayout,
        _editorRevision);

    internal bool HasEditorChangedSince(long revision) => _editorRevision != revision;

    internal void ApplySavedDocument(ParsedMcpServerConfiguration parsed, long revision)
    {
        if (_disposed || HasEditorChangedSince(revision))
        {
            return;
        }

        ApplyEditorDocument(
            parsed.Server.Name,
            McpConfigurationDocument.BuildEditorText(
                parsed.Server,
                parsed.Headers,
                parsed.EnvironmentVariables));
    }

    internal Task CurrentDocumentLoad => _currentDocumentLoad;

    internal Task RunOnUiAsync(Action action) => _uiDispatcher.InvokeAsync(() =>
    {
        if (!_disposed)
        {
            action();
        }
    });

    private Task LoadSelectedServerAsync(
        ConfiguredMcpServerRecord server,
        CancellationToken cancellationToken)
    {
        _serverLoadCancellation?.Cancel();
        _serverLoadCancellation?.Dispose();
        var loadCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _serverLoadCancellation = loadCancellation;
        var version = ++_serverLoadVersion;
        var editRevision = _editorRevision;
        SetDocumentLoadState(isLoading: true, isReady: false);
        return LoadSelectedServerCoreAsync(server, version, editRevision, loadCancellation);
    }

    private async Task LoadSelectedServerCoreAsync(
        ConfiguredMcpServerRecord server,
        int version,
        long editRevision,
        CancellationTokenSource loadCancellation)
    {
        var documentApplied = false;
        try
        {
            var text = await _editor.LoadDocumentAsync(server.ServerId, loadCancellation.Token)
                .ConfigureAwait(false)
                ?? McpConfigurationDocument.CreateLocalTemplate();
            await _uiDispatcher.InvokeAsync(() =>
            {
                if (IsCurrentDocumentLoad(server.ServerId, version, loadCancellation)
                    && _editorRevision == editRevision)
                {
                    ApplyEditorDocument(server.Name, text);
                    RefreshConnectionStatus(server);
                    documentApplied = true;
                }
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (loadCancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            await _uiDispatcher.InvokeAsync(() =>
            {
                if (IsCurrentDocumentLoad(server.ServerId, version, loadCancellation))
                {
                    _operations.PresentStatus(ex.Message, McpStatusKind.Error);
                }
            }).ConfigureAwait(false);
        }
        finally
        {
            await _uiDispatcher.InvokeAsync(() =>
            {
                if (IsMatchingDocumentLoad(server.ServerId, version, loadCancellation))
                {
                    _serverLoadCancellation = null;
                    SetDocumentLoadState(isLoading: false, isReady: documentApplied);
                }
            }).ConfigureAwait(false);
            loadCancellation.Dispose();
        }
    }

    private void ClearEditor()
    {
        ApplyEditorDocument(
            Servers.Count == 0 ? "mcp_server" : string.Empty,
            Servers.Count == 0 ? McpConfigurationDocument.CreateLocalTemplate() : string.Empty);
    }

    private void LoadTemplate(string template, string status)
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            Name = "mcp_server";
        }

        EditorText = template;
        _operations.PresentStatus(status, McpStatusKind.Success, autoClear: true);
    }

    private bool CanEdit() => !IsBusy;

    private void TrackEditorChange()
    {
        if (!_suppressEditorTracking)
        {
            _editorRevision++;
        }
    }

    private void ApplyEditorDocument(string name, string text)
    {
        _suppressEditorTracking = true;
        try
        {
            Name = name;
            EditorText = text;
        }
        finally
        {
            _suppressEditorTracking = false;
        }
    }

    private bool IsCurrentDocumentLoad(
        string serverId,
        int version,
        CancellationTokenSource cancellation)
        => !_disposed
            && !cancellation.IsCancellationRequested
            && IsMatchingDocumentLoad(serverId, version, cancellation);

    private bool IsMatchingDocumentLoad(
        string serverId,
        int version,
        CancellationTokenSource cancellation)
        => !_disposed
            && version == _serverLoadVersion
            && ReferenceEquals(_serverLoadCancellation, cancellation)
            && string.Equals(SelectedServer?.ServerId, serverId, StringComparison.OrdinalIgnoreCase);

    private void CancelDocumentLoad(bool documentReady)
    {
        _serverLoadVersion++;
        _serverLoadCancellation?.Cancel();
        _serverLoadCancellation?.Dispose();
        _serverLoadCancellation = null;
        SetDocumentLoadState(isLoading: false, isReady: documentReady);
    }

    private void SetDocumentLoadState(bool isLoading, bool isReady)
    {
        if (_disposed || (_isDocumentLoading == isLoading && _isDocumentReady == isReady))
        {
            return;
        }

        _isDocumentLoading = isLoading;
        _isDocumentReady = isReady;
        OnPropertyChanged(nameof(IsDocumentLoading));
        OnPropertyChanged(nameof(IsDocumentReady));
        OnPropertyChanged(nameof(IsEditorReadOnly));
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(CanStartOperation));
        OnPropertyChanged(nameof(CanNavigateServers));
        CreateServerCommand.NotifyCanExecuteChanged();
        BackToServerListCommand.NotifyCanExecuteChanged();
        LoadLocalTemplateCommand.NotifyCanExecuteChanged();
        LoadRemoteTemplateCommand.NotifyCanExecuteChanged();
        FormatCommand.NotifyCanExecuteChanged();
        _operations.NotifyContextChanged();
        TryReloadPendingServers();
    }

    private void OnServersChanged() => RunOnUiThread(() =>
    {
        if (_suppressServerChangeNotifications)
        {
            return;
        }

        _reloadServersPending = true;
        TryReloadPendingServers();
    });

    private void TryReloadPendingServers()
    {
        if (_disposed || !_reloadServersPending || IsBusy)
        {
            return;
        }

        _reloadServersPending = false;
        _ = ReloadServersSafelyAsync(SelectedServer?.ServerId);
    }

    private async Task ReloadServersSafelyAsync(string? serverId)
    {
        try
        {
            await ReloadServersAsync(serverId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            await RunOnUiAsync(() => _operations.PresentStatus(ex.Message, McpStatusKind.Error));
        }
    }

    private void OnConnectionStatusChanged() => RunOnUiThread(() => RefreshConnectionStatus(SelectedServer));

    private void OnOperationsPropertyChanged(object? sender, PropertyChangedEventArgs e) => RunOnUiThread(() =>
    {
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(CanStartOperation));
        OnPropertyChanged(nameof(CanNavigateServers));
        OnPropertyChanged(nameof(IsDiscovering));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusKind));
        OnPropertyChanged(nameof(IsStatusSuccess));
        OnPropertyChanged(nameof(IsStatusWarning));
        OnPropertyChanged(nameof(IsStatusError));
        CreateServerCommand.NotifyCanExecuteChanged();
        BackToServerListCommand.NotifyCanExecuteChanged();
        LoadLocalTemplateCommand.NotifyCanExecuteChanged();
        LoadRemoteTemplateCommand.NotifyCanExecuteChanged();
        FormatCommand.NotifyCanExecuteChanged();
        TryReloadPendingServers();
    });

    private void NotifyLayout()
    {
        OnPropertyChanged(nameof(IsListActive));
        OnPropertyChanged(nameof(ShowWideLayout));
        OnPropertyChanged(nameof(ShowCompactList));
        OnPropertyChanged(nameof(ShowCompactEditor));
        OnPropertyChanged(nameof(ShowListPane));
        OnPropertyChanged(nameof(ShowEditorPane));
    }

    private void RunOnUiThread(Action action)
    {
        _ = RunOnUiAsync(action);
    }

    private static LegacyDependencies CreateLegacyDependencies(
        McpServerCatalogService catalog,
        McpClientConnectionManager connectionManager,
        McpOAuthService? oauthService,
        McpEcosystemConfigurationImporter? configurationImporter,
        McpSunderConfigurationSyncService? sunderConfigurationSyncService)
    {
        var importer = configurationImporter ?? new McpEcosystemConfigurationImporter(catalog);
        return new LegacyDependencies(
            new McpSettingsEditorService(catalog),
            new McpConfigurationCoordinator(importer, sunderConfigurationSyncService),
            new McpServerConnectionService(catalog, connectionManager, oauthService),
            new McpOAuthCoordinator(oauthService, connectionManager));
    }

    private sealed record LegacyDependencies(
        McpSettingsEditorService Editor,
        McpConfigurationCoordinator Configuration,
        McpServerConnectionService Connections,
        McpOAuthCoordinator OAuth);
}

public enum McpStatusKind
{
    None = 0,
    Success,
    Warning,
    Error,
}

internal sealed record McpEditorSnapshot(
    ConfiguredMcpServerRecord? ExistingServer,
    string Name,
    string EditorText,
    bool IsCompactLayout,
    long Revision);
