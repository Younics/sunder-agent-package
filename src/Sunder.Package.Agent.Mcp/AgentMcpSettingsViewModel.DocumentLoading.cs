using Sunder.Package.Agent.Mcp.Services;
using Sunder.Package.Agent.Shared.Presentation;

namespace Sunder.Package.Agent.Mcp;

public sealed partial class AgentMcpSettingsViewModel
{
    private Task LoadSelectedServerAsync(
        ConfiguredMcpServerRecord server,
        CancellationToken cancellationToken)
    {
        var ticket = _listDetail.BeginDetailLoad(cancellationToken);
        var editRevision = _editorRevision;
        return LoadSelectedServerCoreAsync(server, ticket, editRevision);
    }

    private async Task LoadSelectedServerCoreAsync(
        ConfiguredMcpServerRecord server,
        AdaptiveDetailTicket<string> ticket,
        long editRevision)
    {
        try
        {
            var text = await _gateway.LoadDocumentAsync(
                    server.ServerId,
                    ticket.Request.CancellationToken)
                .WaitAsync(ticket.Request.CancellationToken)
                .ConfigureAwait(false)
                ?? McpConfigurationDocument.CreateLocalTemplate();
            await _uiDispatcher.InvokeAsync(() =>
            {
                if (!_listDetail.IsCurrentDetail(ticket))
                {
                    return;
                }

                if (_editorRevision == editRevision)
                {
                    ApplyEditorDocument(server.Name, text, server.ServerId);
                }

                _listDetail.TrySetDetailReady(ticket);
                RefreshConnectionStatus(server);
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ticket.Request.CancellationToken.IsCancellationRequested)
        {
            await _uiDispatcher.InvokeAsync(() =>
                _listDetail.TryCancelDetailLoad(ticket)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await _uiDispatcher.InvokeAsync(() =>
            {
                if (_listDetail.TrySetDetailError(ticket, ex))
                {
                    _operations.PresentStatus(ex.Message, McpStatusKind.Error);
                }
            }).ConfigureAwait(false);
        }
    }
}
