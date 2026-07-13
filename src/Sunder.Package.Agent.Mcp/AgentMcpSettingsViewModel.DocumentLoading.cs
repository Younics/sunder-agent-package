using Sunder.Package.Agent.Mcp.Services;

namespace Sunder.Package.Agent.Mcp;

public sealed partial class AgentMcpSettingsViewModel
{
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
            var text = await _gateway.LoadDocumentAsync(server.ServerId, loadCancellation.Token)
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
}
