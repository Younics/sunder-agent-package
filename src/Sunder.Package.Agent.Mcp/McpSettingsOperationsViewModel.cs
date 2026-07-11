using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sunder.Package.Agent.Mcp.Services;
using Sunder.Package.Agent.Shared.Presentation;

namespace Sunder.Package.Agent.Mcp;

internal sealed partial class McpSettingsOperationsViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan StatusDisplayDuration = TimeSpan.FromSeconds(3);
    private readonly AgentMcpSettingsViewModel _host;
    private readonly McpSettingsEditorService _editor;
    private readonly McpConfigurationCoordinator _configuration;
    private readonly McpServerConnectionService _connections;
    private readonly McpOAuthCoordinator _oauth;
    private readonly IPresentationDispatcher _uiDispatcher;
    private readonly OperationState _operation = new();
    private readonly TimedStatusController _statusTimer;
    private CancellationTokenSource? _cancellation;
    private OperationGeneration _generation;
    private McpSettingsOperationKind _kind;
    private bool _disposed;

    public McpSettingsOperationsViewModel(
        AgentMcpSettingsViewModel host,
        McpSettingsEditorService editor,
        McpConfigurationCoordinator configuration,
        McpServerConnectionService connections,
        McpOAuthCoordinator oauth,
        IPresentationDispatcher uiDispatcher)
    {
        _host = host;
        _editor = editor;
        _configuration = configuration;
        _connections = connections;
        _oauth = oauth;
        _uiDispatcher = uiDispatcher;
        _statusTimer = new TimedStatusController(dispatcher: uiDispatcher);
        _operation.PropertyChanged += OnOperationPropertyChanged;
    }

    public bool IsBusy => _operation.IsBusy;
    public bool IsDiscovering => IsBusy && _kind == McpSettingsOperationKind.Discovery;
    public string StatusText => _operation.Message;
    public McpStatusKind StatusKind => _operation.Severity switch
    {
        OperationSeverity.Success => McpStatusKind.Success,
        OperationSeverity.Warning => McpStatusKind.Warning,
        OperationSeverity.Error => McpStatusKind.Error,
        _ => McpStatusKind.None,
    };

    public async Task InitializeAsync()
    {
        if (!TryBegin(McpSettingsOperationKind.Import, "Loading MCP servers...", false, out var operation, out var cancellation))
        {
            return;
        }

        try
        {
            await _configuration.InitializeAsync(cancellation.Token);
            await _host.ReloadServersAsync(null, cancellation.Token);
            await RunOnUiAsync(() =>
            {
                var diagnosticCount = _host.CatalogDiagnostics.Count;
                Complete(operation,
                    diagnosticCount > 0
                        ? $"Loaded MCP servers with {diagnosticCount} malformed catalog {(diagnosticCount == 1 ? "entry" : "entries")} skipped."
                        : _host.Servers.Count == 0
                            ? "No MCP servers configured yet. Paste a bare MCP server object or start from a template."
                            : string.Empty,
                    diagnosticCount > 0 || _host.Servers.Count == 0 ? OperationSeverity.Warning : OperationSeverity.None);
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            await CompleteOnUiAsync(operation).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await CompleteOnUiAsync(operation, ex.Message, OperationSeverity.Error).ConfigureAwait(false);
        }
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (!CanSave()
            || !TryBegin(McpSettingsOperationKind.Save, "Saving MCP server...", false, out var operation, out var cancellation))
        {
            return;
        }

        try
        {
            var snapshot = _host.CaptureEditorSnapshot();
            var existing = snapshot.ExistingServer;
            var parsed = _editor.Parse(
                existing?.ServerId ?? Guid.NewGuid().ToString("N"),
                snapshot.Name,
                snapshot.EditorText,
                existing);
            await _host.WithSuppressedCatalogEventsAsync(() => _editor.SaveAsync(parsed, cancellation.Token));
            await _connections.DisconnectAsync(parsed.Server.ServerId);
            await _host.ReloadServersAsync(
                parsed.Server.ServerId,
                cancellation.Token,
                loadSelectedDocument: false);
            await RunOnUiAsync(() =>
            {
                var editedDuringSave = _host.HasEditorChangedSince(snapshot.Revision);
                if (!editedDuringSave)
                {
                    _host.ApplySavedDocument(parsed, snapshot.Revision);
                }

                if (snapshot.IsCompactLayout && !editedDuringSave)
                {
                    _host.SelectedServer = null;
                    _host.IsEditorActive = false;
                    Complete(operation);
                    return;
                }

                var message = existing is null
                    ? $"Created MCP server '{parsed.Server.DisplayName}'."
                    : $"Saved MCP server '{parsed.Server.DisplayName}'.";
                if (editedDuringSave)
                {
                    message += " New edits remain unsaved.";
                }

                Complete(operation, message, OperationSeverity.Success, autoClear: !editedDuringSave);
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            await CompleteOnUiAsync(
                operation,
                "MCP server save canceled.",
                OperationSeverity.Warning,
                autoClear: true).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await CompleteOnUiAsync(operation, ex.Message, OperationSeverity.Error).ConfigureAwait(false);
        }
    }

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private async Task DeleteAsync()
    {
        if (_host.SelectedServer is not { } server
            || !TryBegin(McpSettingsOperationKind.Save, $"Deleting MCP server '{server.DisplayName}'...", false, out var operation, out var cancellation))
        {
            return;
        }

        try
        {
            await _host.WithSuppressedCatalogEventsAsync(() => _editor.DeleteAsync(server.ServerId, cancellation.Token));
            await _connections.DisconnectAsync(server.ServerId);
            await _host.ReloadServersAsync(null, cancellation.Token);
            await RunOnUiAsync(() =>
            {
                _host.IsEditorActive = false;
                Complete(operation, _host.IsCompactLayout ? string.Empty : $"Deleted MCP server '{server.DisplayName}'.", OperationSeverity.Success, autoClear: true);
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await CompleteOnUiAsync(operation, ex.Message, OperationSeverity.Error).ConfigureAwait(false);
        }
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task ImportCommonConfigurationsAsync()
        => RunImportAsync("Importing common MCP configurations...", token => _configuration.ImportCommonAsync(token));

    public Task ImportFileAsync(string filePath)
        => RunImportAsync("Importing MCP configuration file...", token => _configuration.ImportFileAsync(filePath, token));

    [RelayCommand(CanExecute = nameof(CanUseServer))]
    private Task DiscoverToolsAsync() => RunDiscoveryAsync(reconnect: false);

    [RelayCommand(CanExecute = nameof(IsDiscovering))]
    private void CancelDiscovery()
    {
        _cancellation?.Cancel();
        _operation.TryReport(_generation, "Canceling MCP discovery...", OperationSeverity.Warning);
    }

    [RelayCommand(CanExecute = nameof(CanUseServer))]
    private async Task DisconnectMcpServerAsync()
    {
        if (_host.SelectedServer is not { } server
            || !TryBegin(McpSettingsOperationKind.Connection, $"Disconnecting MCP server '{server.DisplayName}'...", false, out var operation, out _))
        {
            return;
        }

        try
        {
            await _connections.DisconnectAsync(server.ServerId);
            await RunOnUiAsync(() =>
            {
                _host.RefreshConnectionStatus(server);
                Complete(operation, $"Disconnected MCP server '{server.DisplayName}'.", OperationSeverity.Success, autoClear: true);
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await CompleteOnUiAsync(operation, ex.Message, OperationSeverity.Error).ConfigureAwait(false);
        }
    }

    [RelayCommand(CanExecute = nameof(CanUseServer))]
    private Task ReconnectMcpServerAsync() => RunDiscoveryAsync(reconnect: true);

    [RelayCommand(CanExecute = nameof(CanUseOAuth))]
    private Task AuthorizeMcpServerAsync() => RunOAuthAsync(clear: false);

    [RelayCommand(CanExecute = nameof(CanUseOAuth))]
    private Task DisconnectMcpServerOAuthAsync() => RunOAuthAsync(clear: true);

    public void NotifyContextChanged()
    {
        SaveCommand.NotifyCanExecuteChanged();
        ImportCommonConfigurationsCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
        DiscoverToolsCommand.NotifyCanExecuteChanged();
        DisconnectMcpServerCommand.NotifyCanExecuteChanged();
        ReconnectMcpServerCommand.NotifyCanExecuteChanged();
        AuthorizeMcpServerCommand.NotifyCanExecuteChanged();
        DisconnectMcpServerOAuthCommand.NotifyCanExecuteChanged();
    }

    public void PresentStatus(string message, McpStatusKind kind, bool autoClear = false)
    {
        if (IsBusy)
        {
            return;
        }

        var severity = kind switch
        {
            McpStatusKind.Success => OperationSeverity.Success,
            McpStatusKind.Warning => OperationSeverity.Warning,
            McpStatusKind.Error => OperationSeverity.Error,
            _ => OperationSeverity.None,
        };
        var operation = _operation.Begin(message, severity, canCancel: false);
        _operation.TryComplete(operation, message, severity);
        if (autoClear)
        {
            _statusTimer.Cancel();
            _ = _statusTimer.ScheduleAsync(StatusDisplayDuration, ClearStatus);
        }
    }

    public void ClearStatus() => _operation.ClearStatus();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cancellation?.Cancel();
        _operation.PropertyChanged -= OnOperationPropertyChanged;
        _operation.Dispose();
        _statusTimer.Dispose();
        _cancellation?.Dispose();
    }

    private async Task RunImportAsync(string message, Func<CancellationToken, Task<McpConfigurationImportResult>> import)
    {
        if (!TryBegin(McpSettingsOperationKind.Import, message, true, out var operation, out var cancellation))
        {
            return;
        }

        try
        {
            var result = await import(cancellation.Token);
            await _host.ReloadServersAsync(_host.SelectedServer?.ServerId, cancellation.Token);
            await RunOnUiAsync(() =>
            {
                var warnings = result.Warnings.Count == 0 ? string.Empty : $" {result.Warnings.Count} warning(s).";
                Complete(operation, $"Imported {result.ImportedCount} MCP server(s); skipped {result.SkippedCount}.{warnings}",
                    result.Warnings.Count == 0 ? OperationSeverity.Success : OperationSeverity.Warning,
                    autoClear: result.Warnings.Count == 0);
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            await CompleteOnUiAsync(
                operation,
                "MCP configuration import canceled.",
                OperationSeverity.Warning,
                autoClear: true).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await CompleteOnUiAsync(operation, ex.Message, OperationSeverity.Error).ConfigureAwait(false);
        }
    }

    private async Task RunDiscoveryAsync(bool reconnect)
    {
        if (_host.SelectedServer is not { } server
            || !TryBegin(McpSettingsOperationKind.Discovery,
                reconnect ? $"Reconnecting MCP server '{server.DisplayName}'..." : $"Discovering MCP tools from '{server.DisplayName}'...",
                true, out var operation, out var cancellation))
        {
            return;
        }

        try
        {
            var tools = await _connections.DiscoverAsync(server, reconnect, cancellation.Token);
            var presentation = await _connections.GetPresentationAsync(server, cancellation.Token);
            await RunOnUiAsync(() =>
            {
                _host.RefreshConnectionStatus(server);
                var status = presentation.Status;
                Complete(operation, status.Kind == McpConnectionStatusKind.Error
                        ? status.Message
                        : $"Discovered {tools.Count} MCP tool(s) from '{server.DisplayName}'.",
                    status.Kind == McpConnectionStatusKind.Error ? OperationSeverity.Error : OperationSeverity.Success,
                    autoClear: status.Kind != McpConnectionStatusKind.Error);
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            await RunOnUiAsync(() =>
            {
                _host.RefreshConnectionStatus(server);
                Complete(operation, $"Canceled MCP discovery for '{server.DisplayName}'.", OperationSeverity.Warning, autoClear: true);
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await RunOnUiAsync(() =>
            {
                _host.RefreshConnectionStatus(server);
                Complete(operation, ex.Message, OperationSeverity.Error);
            }).ConfigureAwait(false);
        }
    }

    private async Task RunOAuthAsync(bool clear)
    {
        if (_host.SelectedServer is not { } server
            || !TryBegin(McpSettingsOperationKind.OAuth,
                clear ? $"Removing OAuth authorization for '{server.DisplayName}'..." : $"Starting browser authorization for '{server.DisplayName}'...",
                true, out var operation, out var cancellation))
        {
            return;
        }

        try
        {
            if (clear)
            {
                await _oauth.ClearAsync(server, cancellation.Token);
            }
            else
            {
                await _oauth.AuthorizeAsync(server, cancellation.Token);
            }

            await RunOnUiAsync(() =>
            {
                _host.RefreshConnectionStatus(server);
                Complete(operation, clear
                    ? $"Removed OAuth authorization for MCP server '{server.DisplayName}'."
                    : $"Authorized MCP server '{server.DisplayName}'.", OperationSeverity.Success, autoClear: true);
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            await CompleteOnUiAsync(
                operation,
                "MCP OAuth operation canceled.",
                OperationSeverity.Warning,
                autoClear: true).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await RunOnUiAsync(() =>
            {
                _host.RefreshConnectionStatus(server);
                Complete(operation, ex.Message, OperationSeverity.Error);
            }).ConfigureAwait(false);
        }
    }

    private bool TryBegin(McpSettingsOperationKind kind, string message, bool canCancel, out OperationGeneration operation, out CancellationTokenSource cancellation)
    {
        operation = default;
        cancellation = null!;
        if (_disposed || !CanStart())
        {
            return false;
        }

        _statusTimer.Cancel();
        _kind = kind;
        operation = _operation.Begin(message, OperationSeverity.Info, canCancel);
        _generation = operation;
        cancellation = CancellationTokenSource.CreateLinkedTokenSource(operation.CancellationToken);
        _cancellation = cancellation;
        return true;
    }

    private void Complete(OperationGeneration operation, string message = "", OperationSeverity severity = OperationSeverity.None, bool autoClear = false)
    {
        if (!_operation.TryComplete(operation, message, severity))
        {
            return;
        }

        _kind = McpSettingsOperationKind.None;
        _cancellation?.Dispose();
        _cancellation = null;
        if (autoClear && severity is OperationSeverity.Success or OperationSeverity.Warning)
        {
            _ = _statusTimer.ScheduleAsync(StatusDisplayDuration, ClearStatus);
        }
    }

    private bool CanStart() => !IsBusy && !_host.IsDocumentLoading;
    private bool CanSave() => CanStart() && _host.IsDocumentReady;
    private bool CanDelete() => _host.SelectedServer is not null && CanStart();
    private bool CanUseServer() => _host.SelectedServer is not null && CanStart();
    private bool CanUseOAuth() => _host.SelectedServer?.OAuthEnabled == true && CanStart();

    private Task CompleteOnUiAsync(
        OperationGeneration operation,
        string message = "",
        OperationSeverity severity = OperationSeverity.None,
        bool autoClear = false)
        => RunOnUiAsync(() => Complete(operation, message, severity, autoClear));

    private Task RunOnUiAsync(Action action)
        => _uiDispatcher.InvokeAsync(() =>
        {
            if (!_disposed)
            {
                action();
            }
        });

    private void OnOperationPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(IsDiscovering));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusKind));
        SaveCommand.NotifyCanExecuteChanged();
        ImportCommonConfigurationsCommand.NotifyCanExecuteChanged();
        CancelDiscoveryCommand.NotifyCanExecuteChanged();
        NotifyContextChanged();
    }
}

internal enum McpSettingsOperationKind
{
    None = 0,
    Save,
    Discovery,
    Connection,
    OAuth,
    Import,
}
