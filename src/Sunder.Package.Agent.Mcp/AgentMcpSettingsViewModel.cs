using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sunder.Package.Agent.Mcp.Services;

namespace Sunder.Package.Agent.Mcp;

public sealed partial class AgentMcpSettingsViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan SuccessStatusDisplayDuration = TimeSpan.FromSeconds(3);

    private readonly McpServerCatalogService _serverCatalogService;
    private readonly McpClientConnectionManager _connectionManager;
    private CancellationTokenSource? _successStatusClearCancellation;
    private CancellationTokenSource? _discoveryCancellation;
    private bool _suppressSelectionHandlers;
    private bool _disposed;
    private int _serverLoadVersion;

    public AgentMcpSettingsViewModel(
        McpServerCatalogService serverCatalogService,
        McpClientConnectionManager connectionManager)
    {
        _serverCatalogService = serverCatalogService;
        _connectionManager = connectionManager;
        _connectionManager.StatusChanged += OnConnectionStatusChanged;
        _ = InitializeAsync();
    }

    public ObservableCollection<ConfiguredMcpServerRecord> Servers { get; } = [];

    public bool HasSelectedServer => SelectedServer is not null;

    public bool IsListActive => !IsEditorActive;

    public bool ShowWideLayout => !IsCompactLayout;

    public bool ShowCompactList => IsCompactLayout && IsListActive;

    public bool ShowCompactEditor => IsCompactLayout && IsEditorActive;

    public bool ShowListPane => ShowWideLayout || ShowCompactList;

    public bool ShowEditorPane => ShowWideLayout || ShowCompactEditor;

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
    private string _statusText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStatusSuccess))]
    [NotifyPropertyChangedFor(nameof(IsStatusWarning))]
    [NotifyPropertyChangedFor(nameof(IsStatusError))]
    private McpStatusKind _statusKind = McpStatusKind.None;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isDiscovering;

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

    public bool IsStatusSuccess => StatusKind == McpStatusKind.Success;

    public bool IsStatusWarning => StatusKind == McpStatusKind.Warning;

    public bool IsStatusError => StatusKind == McpStatusKind.Error;

    public bool IsConnectionStatusSuccess => ConnectionStatusKind == McpConnectionStatusKind.Connected;

    public bool IsConnectionStatusWarning => ConnectionStatusKind is McpConnectionStatusKind.Connecting
        or McpConnectionStatusKind.DiscoveringTools
        or McpConnectionStatusKind.Disabled
        or McpConnectionStatusKind.Disconnected;

    public bool IsConnectionStatusError => ConnectionStatusKind == McpConnectionStatusKind.Error;

    public bool HasConnectionStatusDetail => !string.IsNullOrWhiteSpace(ConnectionStatusDetail);

    private async Task InitializeAsync()
    {
        SetStatus("Loading MCP servers...", McpStatusKind.None);

        try
        {
            await ReloadServersAsync(selectServerId: null);
        }
        catch (Exception ex)
        {
            ClearEditor();
            SetStatus(ex.Message, McpStatusKind.Error);
        }
    }

    partial void OnSelectedServerChanged(ConfiguredMcpServerRecord? value)
    {
        DeleteCommand.NotifyCanExecuteChanged();
        DiscoverToolsCommand.NotifyCanExecuteChanged();
        DisconnectMcpServerCommand.NotifyCanExecuteChanged();
        ReconnectMcpServerCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(HasSelectedServer));

        if (_suppressSelectionHandlers)
        {
            return;
        }

        if (value is null)
        {
            ClearEditor();
            RefreshConnectionStatus(null);
            return;
        }

        RefreshConnectionStatus(value);
        _ = LoadSelectedServerAsync(value, ++_serverLoadVersion);
        if (IsCompactLayout)
        {
            IsEditorActive = true;
        }
    }

    [RelayCommand]
    private void CreateServer()
    {
        SelectedServer = null;
        Name = "mcp_server";
        EditorText = McpConfigurationDocument.CreateLocalTemplate();
        IsEditorActive = true;
        RefreshConnectionStatus(null);
        SetStatus("Editing a new MCP server draft. Paste a bare MCP server object or start from a template.", McpStatusKind.Warning);
    }

    [RelayCommand]
    private void BackToServerList()
    {
        if (IsCompactLayout)
        {
            SelectedServer = null;
            ClearStatus();
        }

        IsEditorActive = false;
    }

    [RelayCommand]
    private void OpenServerEditor(ConfiguredMcpServerRecord? server)
    {
        if (server is null)
        {
            return;
        }

        ActivateServer(server);
    }

    public void ActivateServer(ConfiguredMcpServerRecord server)
    {
        if (!string.Equals(SelectedServer?.ServerId, server.ServerId, StringComparison.OrdinalIgnoreCase))
        {
            SelectedServer = server;
        }

        if (IsCompactLayout)
        {
            IsEditorActive = true;
        }
    }

    [RelayCommand]
    private void LoadLocalTemplate()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            Name = "mcp_server";
        }

        EditorText = McpConfigurationDocument.CreateLocalTemplate();
        SetStatus("Loaded local MCP template.", McpStatusKind.Success, autoClear: true);
    }

    [RelayCommand]
    private void LoadRemoteTemplate()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            Name = "mcp_server";
        }

        EditorText = McpConfigurationDocument.CreateRemoteTemplate();
        SetStatus("Loaded remote MCP template.", McpStatusKind.Success, autoClear: true);
    }

    [RelayCommand]
    private void Format()
    {
        try
        {
            var normalizedName = _serverCatalogService.NormalizeServerName(Name);
            var parsed = McpConfigurationDocument.Parse(SelectedServer?.ServerId ?? Guid.NewGuid().ToString("N"), normalizedName, EditorText, SelectedServer);
            Name = normalizedName;
            EditorText = McpConfigurationDocument.BuildEditorText(parsed.Server, parsed.Headers, parsed.EnvironmentVariables);
            SetStatus("MCP configuration formatted.", McpStatusKind.Success, autoClear: true);
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, McpStatusKind.Error);
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        IsBusy = true;
        try
        {
            var normalizedName = _serverCatalogService.NormalizeServerName(Name);
            var existing = SelectedServer;
            var serverId = existing?.ServerId ?? Guid.NewGuid().ToString("N");
            var parsed = McpConfigurationDocument.Parse(serverId, normalizedName, EditorText, existing);
            await _serverCatalogService.SaveServerAsync(parsed.Server, parsed.Headers, parsed.EnvironmentVariables);
            await _connectionManager.DisconnectServerAsync(serverId);
            var shouldClearSelection = IsCompactLayout;
            await ReloadServersAsync(serverId);
            if (shouldClearSelection)
            {
                SelectedServer = null;
                IsEditorActive = false;
                ClearStatus();
            }
            else
            {
                SetStatus(
                    existing is null
                        ? $"Created MCP server '{parsed.Server.DisplayName}'."
                        : $"Saved MCP server '{parsed.Server.DisplayName}'.",
                    McpStatusKind.Success,
                    autoClear: true);
            }
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, McpStatusKind.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanDeleteServer))]
    private async Task DeleteAsync()
    {
        if (SelectedServer is null)
        {
            return;
        }

        var deletedName = SelectedServer.DisplayName;
        var serverId = SelectedServer.ServerId;
        var shouldClearSelection = IsCompactLayout;
        await _serverCatalogService.DeleteServerAsync(SelectedServer.ServerId);
        await _connectionManager.DisconnectServerAsync(serverId);
        await ReloadServersAsync(selectServerId: null);
        if (shouldClearSelection)
        {
            SelectedServer = null;
            ClearStatus();
        }
        else
        {
            SetStatus($"Deleted MCP server '{deletedName}'.", McpStatusKind.Success, autoClear: true);
        }

        IsEditorActive = false;
    }

    private bool CanDeleteServer() => SelectedServer is not null;

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

        OnPropertyChanged(nameof(ShowWideLayout));
        OnPropertyChanged(nameof(ShowCompactList));
        OnPropertyChanged(nameof(ShowCompactEditor));
        OnPropertyChanged(nameof(ShowListPane));
        OnPropertyChanged(nameof(ShowEditorPane));
    }

    partial void OnIsEditorActiveChanged(bool value)
    {
        OnPropertyChanged(nameof(IsListActive));
        OnPropertyChanged(nameof(ShowCompactList));
        OnPropertyChanged(nameof(ShowCompactEditor));
        OnPropertyChanged(nameof(ShowListPane));
        OnPropertyChanged(nameof(ShowEditorPane));
    }

    partial void OnIsDiscoveringChanged(bool value)
    {
        DiscoverToolsCommand.NotifyCanExecuteChanged();
        CancelDiscoveryCommand.NotifyCanExecuteChanged();
        DisconnectMcpServerCommand.NotifyCanExecuteChanged();
        ReconnectMcpServerCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanDiscoverTools))]
    private async Task DiscoverToolsAsync()
    {
        if (SelectedServer is { } server)
        {
            await DiscoverToolsCoreAsync(server, reconnectFirst: false);
        }
    }

    private async Task DiscoverToolsCoreAsync(ConfiguredMcpServerRecord server, bool reconnectFirst)
    {
        CancelDiscovery();
        var discoveryCancellation = new CancellationTokenSource();
        _discoveryCancellation = discoveryCancellation;
        IsDiscovering = true;
        IsBusy = true;
        SetStatus(
            reconnectFirst
                ? $"Reconnecting MCP server '{server.DisplayName}'..."
                : $"Discovering MCP tools from '{server.DisplayName}'...",
            McpStatusKind.None);

        try
        {
            if (reconnectFirst)
            {
                await _connectionManager.DisconnectServerAsync(server.ServerId);
                RefreshConnectionStatus(server);
            }

            var tools = await _connectionManager.GetToolsAsync(
                Guid.Empty,
                server,
                _serverCatalogService.GetHeaders(server),
                _serverCatalogService.GetEnvironmentVariables(server),
                McpTimeoutResolver.ResolveDiscoveryTimeoutMilliseconds(server),
                discoveryCancellation.Token);
            RefreshConnectionStatus(server);
            var status = _connectionManager.GetStatus(server);
            SetStatus(
                status.Kind == McpConnectionStatusKind.Error
                    ? status.Message
                    : $"Discovered {tools.Count} MCP tool(s) from '{server.DisplayName}'.",
                status.Kind == McpConnectionStatusKind.Error ? McpStatusKind.Error : McpStatusKind.Success,
                autoClear: status.Kind != McpConnectionStatusKind.Error);
        }
        catch (OperationCanceledException) when (discoveryCancellation.IsCancellationRequested)
        {
            RefreshConnectionStatus(server);
            SetStatus($"Canceled MCP discovery for '{server.DisplayName}'.", McpStatusKind.Warning, autoClear: true);
        }
        catch (Exception ex)
        {
            RefreshConnectionStatus(server);
            SetStatus(ex.Message, McpStatusKind.Error);
        }
        finally
        {
            if (_discoveryCancellation == discoveryCancellation)
            {
                _discoveryCancellation = null;
            }

            discoveryCancellation.Dispose();
            IsDiscovering = false;
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(IsDiscovering))]
    private void CancelDiscovery()
        => _discoveryCancellation?.Cancel();

    [RelayCommand(CanExecute = nameof(CanManageConnection))]
    private async Task DisconnectMcpServerAsync()
    {
        if (SelectedServer is null)
        {
            return;
        }

        var server = SelectedServer;
        CancelDiscovery();
        IsBusy = true;
        try
        {
            await _connectionManager.DisconnectServerAsync(server.ServerId);
            RefreshConnectionStatus(server);
            SetStatus($"Disconnected MCP server '{server.DisplayName}'.", McpStatusKind.Success, autoClear: true);
        }
        catch (Exception ex)
        {
            RefreshConnectionStatus(server);
            SetStatus(ex.Message, McpStatusKind.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanManageConnection))]
    private async Task ReconnectMcpServerAsync()
    {
        if (SelectedServer is { } server)
        {
            await DiscoverToolsCoreAsync(server, reconnectFirst: true);
        }
    }

    private bool CanDiscoverTools() => SelectedServer is not null && !IsDiscovering;

    private bool CanManageConnection() => SelectedServer is not null && !IsDiscovering;

    private async Task LoadSelectedServerAsync(ConfiguredMcpServerRecord server, int version)
    {
        IsBusy = true;
        try
        {
            var editorText = await _serverCatalogService.ExportServerJsonAsync(server.ServerId) ?? McpConfigurationDocument.CreateLocalTemplate();
            if (version != _serverLoadVersion || SelectedServer?.ServerId != server.ServerId)
            {
                return;
            }

            Name = server.Name;
            EditorText = editorText;
            RefreshConnectionStatus(server);
            SetStatus($"Editing MCP server '{server.DisplayName}'.", McpStatusKind.None);
        }
        catch (Exception ex)
        {
            if (version == _serverLoadVersion && SelectedServer?.ServerId == server.ServerId)
            {
                SetStatus(ex.Message, McpStatusKind.Error);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ReloadServersAsync(string? selectServerId)
    {
        var currentServerId = SelectedServer?.ServerId;
        var servers = await _serverCatalogService.ListServersAsync();

        _suppressSelectionHandlers = true;
        try
        {
            Servers.Clear();
            foreach (var server in servers)
            {
                Servers.Add(server);
            }

            var selectedServer = Servers.FirstOrDefault(server => server.ServerId == selectServerId);
            if (selectedServer is null && (!IsCompactLayout || selectServerId is not null))
            {
                selectedServer = Servers.FirstOrDefault(server => server.ServerId == currentServerId)
                                 ?? Servers.FirstOrDefault();
            }

            SelectedServer = selectedServer;
        }
        finally
        {
            _suppressSelectionHandlers = false;
        }

        if (SelectedServer is null)
        {
            ClearEditor();
            RefreshConnectionStatus(null);
        }
        else
        {
            await LoadSelectedServerAsync(SelectedServer, ++_serverLoadVersion);
        }

        DeleteCommand.NotifyCanExecuteChanged();
    }

    private void ClearEditor()
    {
        if (Servers.Count == 0)
        {
            Name = "mcp_server";
            EditorText = McpConfigurationDocument.CreateLocalTemplate();
            SetStatus("No MCP servers configured yet. Paste a bare MCP server object or start from a template.", McpStatusKind.Warning);
            return;
        }

        Name = string.Empty;
        EditorText = string.Empty;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CancelDiscovery();
        _connectionManager.StatusChanged -= OnConnectionStatusChanged;
        CancelSuccessStatusClear();
    }

    private void OnConnectionStatusChanged()
        => RunOnUiThread(() => RefreshConnectionStatus(SelectedServer));

    private void RefreshConnectionStatus(ConfiguredMcpServerRecord? server)
    {
        if (server is null)
        {
            ConnectionStatusKind = McpConnectionStatusKind.Idle;
            ConnectionStatusText = "No saved MCP server selected.";
            ConnectionStatusDetail = string.Empty;
            ConnectionDiagnosticsText = string.Empty;
            return;
        }

        var status = _connectionManager.GetStatus(server);
        ConnectionStatusKind = status.Kind;
        ConnectionStatusText = status.Message;
        ConnectionStatusDetail = BuildConnectionStatusDetail(status);
        ConnectionDiagnosticsText = BuildConnectionDiagnosticsText(status);
    }

    private static string BuildConnectionStatusDetail(McpConnectionStatus status)
    {
        var lines = new List<string>
        {
            $"Active connection: {(status.ActiveConnectionCount > 0 ? "Yes" : "No")}",
            $"Discovered tools: {status.ToolCount ?? 0}",
        };

        return string.Join(Environment.NewLine, lines);
    }

    private static string BuildConnectionDiagnosticsText(McpConnectionStatus status)
    {
        var lines = new List<string>
        {
            $"Status: {status.Kind}",
            $"Message: {status.Message}",
            $"Active connection: {(status.ActiveConnectionCount > 0 ? "Yes" : "No")}",
            $"Discovered tools: {status.ToolCount ?? 0}",
        };

        if (status.LastChangedAtUtc is not null)
        {
            lines.Add($"Updated: {status.LastChangedAtUtc.Value:yyyy-MM-dd HH:mm:ss} UTC");
        }

        if (status.ToolNames is { Count: > 0 })
        {
            lines.Add(string.Empty);
            lines.Add("Tools:");
            lines.AddRange(status.ToolNames.Select(toolName => "- " + toolName));
        }

        if (!string.IsNullOrWhiteSpace(status.Error))
        {
            lines.Add(string.Empty);
            lines.Add($"Error: {status.Error}");
        }

        if (status.StandardErrorTail is { Count: > 0 })
        {
            lines.Add(string.Empty);
            lines.Add("stderr:");
            lines.AddRange(status.StandardErrorTail);
        }

        return lines.Count == 0 ? "No recent MCP diagnostics." : string.Join(Environment.NewLine, lines);
    }

    private void ClearStatus()
        => SetStatus(string.Empty, McpStatusKind.None);

    private void SetStatus(string message, McpStatusKind kind, bool autoClear = false)
    {
        CancelSuccessStatusClear();
        StatusKind = string.IsNullOrWhiteSpace(message) ? McpStatusKind.None : kind;
        StatusText = message;
        if (autoClear && StatusKind == McpStatusKind.Success)
        {
            ScheduleSuccessStatusClear(message);
        }
    }

    private void ScheduleSuccessStatusClear(string message)
    {
        var cancellation = new CancellationTokenSource();
        _successStatusClearCancellation = cancellation;
        _ = ClearSuccessStatusAfterDelayAsync(message, cancellation);
    }

    private async Task ClearSuccessStatusAfterDelayAsync(string message, CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(SuccessStatusDisplayDuration, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        RunOnUiThread(() =>
        {
            if (_successStatusClearCancellation == cancellation
                && StatusKind == McpStatusKind.Success
                && string.Equals(StatusText, message, StringComparison.Ordinal))
            {
                ClearStatus();
            }
        });
    }

    private void CancelSuccessStatusClear()
    {
        var cancellation = _successStatusClearCancellation;
        if (cancellation is null)
        {
            return;
        }

        _successStatusClearCancellation = null;
        cancellation.Cancel();
        cancellation.Dispose();
    }

    private static void RunOnUiThread(Action action)
    {
        if (Avalonia.Application.Current is null || Dispatcher.UIThread.CheckAccess())
        {
            action();
            return;
        }

        Dispatcher.UIThread.Post(action, DispatcherPriority.Background);
    }
}

public enum McpStatusKind
{
    None = 0,
    Success,
    Warning,
    Error,
}
