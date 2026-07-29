using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sunder.Package.Agent.Mcp.Services;
using Sunder.Package.Agent.Mcp.Runtime;
using Sunder.Package.Agent.Shared.Presentation;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Mcp;

public sealed partial class AgentMcpSettingsViewModel : ObservableObject,
    IPackageViewNavigationPreparationTarget,
    IDisposable
{
    private const string ListRefreshChannel = "mcp-servers";
    private const string ConnectionStatusChannel = "mcp-status";
    private readonly IMcpManagementGateway _gateway;
    private readonly McpSettingsOperationsViewModel _operations;
    private readonly IPresentationDispatcher _uiDispatcher;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly LatestRequestCoordinator _requests = new();
    private readonly KeyedAdaptiveListDetailState<string, ConfiguredMcpServerRecord> _listDetail;
    private readonly SerializedRefreshLoop _runtimeRefresh;
    private readonly Task _initialization;
    private bool _suppressEditorTracking;
    private bool _disposed;
    private long _editorRevision;
    private long _loadedEditorRevision;
    private string? _loadedDocumentServerId;
    private bool _runtimeRefreshPending;
    private bool _suppressDocumentLoad;
    private Task _currentDocumentLoad = Task.CompletedTask;
    private Task _currentConnectionStatusLoad = Task.CompletedTask;

    internal static IReadOnlyCollection<string> OwnedConfigurationKeys { get; } = [];

    public AgentMcpSettingsViewModel(
        McpSettingsEditorService editor,
        McpConfigurationCoordinator configuration,
        McpServerConnectionService connections,
        McpOAuthCoordinator oauth)
        : this(new McpLocalManagementGateway(editor, configuration, connections, oauth), PresentationDispatcher.Capture())
    {
    }

    internal AgentMcpSettingsViewModel(
        IMcpManagementGateway gateway,
        IPresentationDispatcher uiDispatcher)
    {
        _gateway = gateway;
        _uiDispatcher = uiDispatcher;
        _listDetail = new KeyedAdaptiveListDetailState<string, ConfiguredMcpServerRecord>(
            Servers,
            static server => server.ServerId,
            keyComparer: StringComparer.OrdinalIgnoreCase);
        _listDetail.PropertyChanged += OnListDetailPropertyChanged;
        _runtimeRefresh = new SerializedRefreshLoop(
            cancellationToken => ReloadServersAsync(null, cancellationToken),
            ReportRuntimeRefreshFailure);
        _operations = new McpSettingsOperationsViewModel(
            this,
            gateway,
            uiDispatcher);
        _gateway.ServersChanged += OnServersChanged;
        _gateway.StatusChanged += OnConnectionStatusChanged;
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

    internal IReadOnlyList<McpCatalogDiagnostic> CatalogDiagnostics => _gateway.Diagnostics;

    internal Task Initialization => _initialization;

    public Task InitializeAsync() => _initialization;

    public async ValueTask<bool> PrepareNavigationAsync(
        PackageViewNavigationContext context,
        CancellationToken cancellationToken = default)
    {
        await _initialization.WaitAsync(cancellationToken);
        return true;
    }

    public ValueTask OnNavigationPresentedAsync(
        PackageViewNavigationContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    public bool HasSelectedServer => SelectedServer is not null;
    public bool HasSelectedOAuthServer => SelectedServer?.OAuthEnabled == true;
    public bool IsListActive => !IsEditorActive;
    public bool ShowWideLayout => _listDetail.Layout == AdaptiveListDetailLayout.Wide;
    public bool ShowCompactList => IsCompactLayout && IsListActive;
    public bool ShowCompactEditor => IsCompactLayout && IsEditorActive;
    public bool ShowListPane => ShowWideLayout || ShowCompactList;
    public bool ShowEditorPane => ShowWideLayout || ShowCompactEditor;
    public bool IsBusy => _operations.IsBusy || IsDocumentLoading;
    public bool CanStartOperation => !IsBusy;
    public bool CanNavigateServers => !_disposed;
    public bool IsDocumentLoading => _listDetail.DetailPhase == AdaptiveDetailPhase.Loading;
    public bool IsDocumentReady => _listDetail.IsNewDetail
        || _listDetail.DetailPhase == AdaptiveDetailPhase.Ready;
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

    public ConfiguredMcpServerRecord? SelectedServer
    {
        get => _listDetail.SelectedItem;
        set
        {
            if (value is null)
            {
                CancelForUserNavigation();
                _listDetail.ShowList();
            }
            else
            {
                ActivateServer(value);
            }
        }
    }

    public bool IsCompactLayout
    {
        get => _listDetail.Layout == AdaptiveListDetailLayout.Compact;
        set => _listDetail.SetLayout(value
            ? AdaptiveListDetailLayout.Compact
            : AdaptiveListDetailLayout.Wide);
    }

    public bool IsEditorActive => IsCompactLayout && !_listDetail.IsList;

    internal AdaptiveListDetailRoute Route => _listDetail.Route;

    internal AdaptiveListDetailLayout Layout => _listDetail.Layout;

    internal AdaptiveDetailPhase DetailPhase => _listDetail.DetailPhase;

    internal long IntentRevision => _listDetail.IntentRevision;

    internal long LayoutRevision => _listDetail.LayoutRevision;

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

    partial void OnNameChanged(string value) => TrackEditorChange();

    partial void OnEditorTextChanged(string value) => TrackEditorChange();

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void CreateServer()
    {
        CancelForUserNavigation();
        _listDetail.ShowNewDetail();
        Name = "mcp_server";
        EditorText = McpConfigurationDocument.CreateLocalTemplate();
        RefreshConnectionStatus(null);
        _operations.PresentStatus("Editing a new MCP server draft. Paste a bare MCP server object or start from a template.", McpStatusKind.Warning);
    }

    [RelayCommand(CanExecute = nameof(CanNavigateServers))]
    private void BackToServerList()
    {
        CancelForUserNavigation();
        _listDetail.ShowList();
        if (IsCompactLayout)
        {
            _operations.ClearStatus();
        }
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

        if (_listDetail.IsExistingDetail && ReferenceEquals(SelectedServer, server))
        {
            return;
        }

        CancelForUserNavigation();
        _listDetail.ShowExistingDetail(server);
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
            Name = _gateway.NormalizeName(Name);
            EditorText = _gateway.Format(SelectedServer?.ServerId ?? Guid.NewGuid().ToString("N"), Name, EditorText, SelectedServer);
            _operations.PresentStatus("MCP configuration formatted.", McpStatusKind.Success, autoClear: true);
        }
        catch (Exception ex)
        {
            _operations.PresentStatus(ex.Message, McpStatusKind.Error);
        }
    }

    public Task ImportConfigurationFileAsync(string filePath) => _operations.ImportFileAsync(filePath);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetimeCancellation.Cancel();
        _gateway.ServersChanged -= OnServersChanged;
        _gateway.StatusChanged -= OnConnectionStatusChanged;
        _operations.PropertyChanged -= OnOperationsPropertyChanged;
        _listDetail.PropertyChanged -= OnListDetailPropertyChanged;
        _runtimeRefresh.Dispose();
        _listDetail.Dispose();
        _requests.Dispose();
        _operations.Dispose();
        _lifetimeCancellation.Dispose();
    }

    internal async Task ReloadServersAsync(
        string? selectServerId,
        CancellationToken cancellationToken,
        bool loadSelectedDocument = true,
        long? expectedIntentRevision = null)
    {
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCancellation.Token);
        var request = _requests.Begin(ListRefreshChannel, linkedCancellation.Token);
        try
        {
            var servers = await _gateway.ListAsync(request.CancellationToken)
                .WaitAsync(request.CancellationToken);
            Task documentLoad = Task.CompletedTask;
            await _uiDispatcher.InvokeAsync(() =>
            {
                if (_disposed || !_requests.IsCurrent(request))
                {
                    return;
                }

                var previousSuppression = _suppressDocumentLoad;
                _suppressDocumentLoad = !loadSelectedDocument;
                try
                {
                    _listDetail.Reconcile(servers);
                    if (selectServerId is not null
                        && expectedIntentRevision is { } intentRevision
                        && _listDetail.IsNewDetail)
                    {
                        _listDetail.TryShowCreatedDetail(selectServerId, intentRevision);
                    }
                }
                finally
                {
                    _suppressDocumentLoad = previousSuppression;
                }
                documentLoad = _currentDocumentLoad;
                _operations.NotifyContextChanged();
            }).ConfigureAwait(false);
            await documentLoad.WaitAsync(request.CancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _requests.Complete(request);
        }
    }

    internal async Task WithSuppressedCatalogEventsAsync(Func<Task> action)
        => await action();

    internal void RefreshConnectionStatus(ConfiguredMcpServerRecord? server)
    {
        if (server is null
            || !_listDetail.IsExistingDetail
            || !string.Equals(
                SelectedServer?.ServerId,
                server.ServerId,
                StringComparison.OrdinalIgnoreCase))
        {
            _requests.Invalidate(ConnectionStatusChannel);
            ConnectionStatusKind = McpConnectionStatusKind.Idle;
            ConnectionStatusText = "No saved MCP server selected.";
            ConnectionStatusDetail = string.Empty;
            ConnectionDiagnosticsText = string.Empty;
            _currentConnectionStatusLoad = Task.CompletedTask;
            return;
        }

        _currentConnectionStatusLoad = RefreshConnectionStatusAsync(server);
    }

    private async Task RefreshConnectionStatusAsync(ConfiguredMcpServerRecord server)
    {
        var request = _requests.Begin(ConnectionStatusChannel, _lifetimeCancellation.Token);
        try
        {
            var presentation = await _gateway.GetPresentationAsync(server, request.CancellationToken)
                .WaitAsync(request.CancellationToken);
            await _uiDispatcher.InvokeAsync(() =>
            {
                if (_requests.IsCurrent(request)
                    && _listDetail.IsExistingDetail
                    && string.Equals(
                        SelectedServer?.ServerId,
                        server.ServerId,
                        StringComparison.OrdinalIgnoreCase))
                {
                    ConnectionStatusKind = presentation.Status.Kind;
                    ConnectionStatusText = presentation.Status.Message;
                    ConnectionStatusDetail = presentation.Detail;
                    ConnectionDiagnosticsText = presentation.Diagnostics;
                }
            });
        }
        catch (OperationCanceledException) when (request.CancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            await _uiDispatcher.InvokeAsync(() =>
            {
                if (_requests.IsCurrent(request)
                    && _listDetail.IsExistingDetail
                    && string.Equals(
                        SelectedServer?.ServerId,
                        server.ServerId,
                        StringComparison.OrdinalIgnoreCase))
                {
                    ConnectionStatusKind = McpConnectionStatusKind.Error;
                    ConnectionStatusText = ex.Message;
                    ConnectionStatusDetail = string.Empty;
                    ConnectionDiagnosticsText = string.Empty;
                }
            });
        }
        finally
        {
            _requests.Complete(request);
        }
    }

    internal McpEditorSnapshot CaptureEditorSnapshot() => new(
        SelectedServer,
        Name,
        EditorText,
        IntentRevision,
        _editorRevision,
        LayoutRevision);

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
                parsed.EnvironmentVariables),
            parsed.Server.ServerId);
    }

    internal Task CurrentDocumentLoad => _currentDocumentLoad;

    internal Task CurrentConnectionStatusLoad => _currentConnectionStatusLoad;

    internal Task RunOnUiAsync(Action action) => _uiDispatcher.InvokeAsync(() =>
    {
        if (!_disposed)
        {
            action();
        }
    });

    private void ClearEditor()
    {
        ApplyEditorDocument(
            Servers.Count == 0 ? "mcp_server" : string.Empty,
            Servers.Count == 0 ? McpConfigurationDocument.CreateLocalTemplate() : string.Empty,
            serverId: null);
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
            _listDetail.PromoteSelectionToExplicit();
            _editorRevision++;
        }
    }

    private void ApplyEditorDocument(string name, string text, string? serverId)
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

        _loadedDocumentServerId = serverId;
        _loadedEditorRevision = _editorRevision;
    }

    private void OnServersChanged() => RunOnUiThread(() =>
    {
        _runtimeRefreshPending = true;
        StartPendingRuntimeRefresh();
    });

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
        StartPendingRuntimeRefresh();
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

    internal bool IsCurrentIntent(McpEditorSnapshot snapshot)
        => !_disposed && snapshot.IntentRevision == IntentRevision;

    internal bool IsCurrentMutationLayout(McpEditorSnapshot snapshot)
        => IsCurrentIntent(snapshot) && snapshot.LayoutRevision == LayoutRevision;

    internal void ShowListAfterMutation()
        => _listDetail.ShowList();

    internal void DiscardPendingServerRefresh()
    {
        _runtimeRefresh.DiscardPending();
        _requests.Invalidate(ListRefreshChannel);
    }

    private void CancelForUserNavigation()
    {
        _operations.CancelForNavigation();
    }

    private void OnListDetailPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_listDetail.IsExistingDetail)
        {
            _currentDocumentLoad = Task.CompletedTask;
        }
        if (e.PropertyName == nameof(KeyedAdaptiveListDetailState<string, ConfiguredMcpServerRecord>.SelectedItem))
        {
            OnPropertyChanged(nameof(SelectedServer));
            OnPropertyChanged(nameof(HasSelectedServer));
            OnPropertyChanged(nameof(HasSelectedOAuthServer));
            if (_listDetail.IsExistingDetail && SelectedServer is { } selected)
            {
                RefreshConnectionStatus(selected);
                var preserveCurrentDraft = string.Equals(
                        _loadedDocumentServerId,
                        selected.ServerId,
                        StringComparison.OrdinalIgnoreCase)
                    && _loadedEditorRevision != _editorRevision;
                if (!preserveCurrentDraft)
                {
                    if (_suppressDocumentLoad)
                    {
                        var ticket = _listDetail.BeginDetailLoad(_lifetimeCancellation.Token);
                        _listDetail.TrySetDetailReady(ticket);
                        _currentDocumentLoad = Task.CompletedTask;
                    }
                    else
                    {
                        _currentDocumentLoad = LoadSelectedServerAsync(
                            selected,
                            _lifetimeCancellation.Token);
                    }
                }
            }
            else if (_listDetail.IsList)
            {
                ClearEditor();
                RefreshConnectionStatus(null);
            }
            _operations.NotifyContextChanged();
        }

        OnPropertyChanged(nameof(IsCompactLayout));
        OnPropertyChanged(nameof(IsEditorActive));
        OnPropertyChanged(nameof(IsDocumentLoading));
        OnPropertyChanged(nameof(IsDocumentReady));
        OnPropertyChanged(nameof(IsEditorReadOnly));
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(CanStartOperation));
        OnPropertyChanged(nameof(CanNavigateServers));
        NotifyLayout();
        CreateServerCommand.NotifyCanExecuteChanged();
        BackToServerListCommand.NotifyCanExecuteChanged();
        _operations.NotifyContextChanged();
    }

    private void RunOnUiThread(Action action)
    {
        _ = RunOnUiAsync(action);
    }

    private void ReportRuntimeRefreshFailure(Exception exception)
        => RunOnUiThread(() => _operations.PresentStatus(exception.Message, McpStatusKind.Error));

    private void StartPendingRuntimeRefresh()
    {
        if (_disposed || !_runtimeRefreshPending || _operations.IsBusy)
        {
            return;
        }

        _runtimeRefreshPending = false;
        _ = _runtimeRefresh.MarkDirty();
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
    long IntentRevision,
    long Revision,
    long LayoutRevision);
