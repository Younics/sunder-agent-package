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
    private bool _suppressSelectionHandlers;
    private bool _disposed;
    private int _serverLoadVersion;

    public AgentMcpSettingsViewModel(
        McpServerCatalogService serverCatalogService,
        McpClientConnectionManager connectionManager)
    {
        _serverCatalogService = serverCatalogService;
        _connectionManager = connectionManager;
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

    public bool IsStatusSuccess => StatusKind == McpStatusKind.Success;

    public bool IsStatusWarning => StatusKind == McpStatusKind.Warning;

    public bool IsStatusError => StatusKind == McpStatusKind.Error;

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
        OnPropertyChanged(nameof(HasSelectedServer));

        if (_suppressSelectionHandlers)
        {
            return;
        }

        if (value is null)
        {
            ClearEditor();
            return;
        }

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
        CancelSuccessStatusClear();
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
