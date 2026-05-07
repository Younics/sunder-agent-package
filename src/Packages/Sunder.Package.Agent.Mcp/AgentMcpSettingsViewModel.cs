using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sunder.Package.Agent.Mcp.Services;

namespace Sunder.Package.Agent.Mcp;

public sealed partial class AgentMcpSettingsViewModel : ObservableObject
{
    private readonly McpServerCatalogService _serverCatalogService;
    private readonly McpClientConnectionManager _connectionManager;
    private bool _suppressSelectionHandlers;
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

    [ObservableProperty]
    private ConfiguredMcpServerRecord? _selectedServer;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _editorText = string.Empty;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    private async Task InitializeAsync()
    {
        StatusText = "Loading MCP servers...";

        try
        {
            await ReloadServersAsync(selectServerId: null);
        }
        catch (Exception ex)
        {
            ClearEditor();
            StatusText = ex.Message;
        }
    }

    partial void OnSelectedServerChanged(ConfiguredMcpServerRecord? value)
    {
        DeleteCommand.NotifyCanExecuteChanged();

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
    }

    [RelayCommand]
    private void CreateServer()
    {
        SelectedServer = null;
        Name = "mcp_server";
        EditorText = McpConfigurationDocument.CreateLocalTemplate();
        StatusText = "Editing a new MCP server draft. Paste a bare MCP server object or start from a template.";
    }

    [RelayCommand]
    private void LoadLocalTemplate()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            Name = "mcp_server";
        }

        EditorText = McpConfigurationDocument.CreateLocalTemplate();
        StatusText = "Loaded local MCP template.";
    }

    [RelayCommand]
    private void LoadRemoteTemplate()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            Name = "mcp_server";
        }

        EditorText = McpConfigurationDocument.CreateRemoteTemplate();
        StatusText = "Loaded remote MCP template.";
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
            StatusText = "MCP configuration formatted.";
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
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
            await ReloadServersAsync(serverId);
            StatusText = existing is null
                ? $"Created MCP server '{parsed.Server.DisplayName}'."
                : $"Saved MCP server '{parsed.Server.DisplayName}'.";
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
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
        await _serverCatalogService.DeleteServerAsync(SelectedServer.ServerId);
        await _connectionManager.DisconnectServerAsync(serverId);
        await ReloadServersAsync(selectServerId: null);
        StatusText = $"Deleted MCP server '{deletedName}'.";
    }

    private bool CanDeleteServer() => SelectedServer is not null;

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
            StatusText = $"Editing MCP server '{server.DisplayName}'.";
        }
        catch (Exception ex)
        {
            if (version == _serverLoadVersion && SelectedServer?.ServerId == server.ServerId)
            {
                StatusText = ex.Message;
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ReloadServersAsync(string? selectServerId)
    {
        Servers.Clear();
        foreach (var server in await _serverCatalogService.ListServersAsync())
        {
            Servers.Add(server);
        }

        _suppressSelectionHandlers = true;
        try
        {
            SelectedServer = Servers.FirstOrDefault(server => server.ServerId == selectServerId)
                ?? Servers.FirstOrDefault();
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
            StatusText = "No MCP servers configured yet. Paste a bare MCP server object or start from a template.";
            return;
        }

        Name = string.Empty;
        EditorText = string.Empty;
    }
}
