using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Sunder.Package.Agent.Shared.PackageViews;
using Sunder.Package.Agent.Shared.Presentation;

namespace Sunder.Package.Agent.PackageViews;

public partial class AgentChatView
{
    private readonly HashSet<Control> _pendingToolRefreshHeaders = [];
    private bool _drainingToolRefreshes;

    private void OnToolDetailRefreshRequested(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not Control source) return;
        var scope = source.GetVisualAncestors().OfType<TranscriptRowPresenter>().FirstOrDefault();
        var header = scope?.GetVisualDescendants().OfType<Button>().FirstOrDefault(button =>
            button.Classes.Contains("tool-step-header") && ReferenceEquals(button.DataContext, source.DataContext));
        if (header is null) return;
        _pendingToolRefreshHeaders.Add(header);
        QueueToolDetailRefreshes();
    }

    private void QueueToolDetailRefreshes()
    {
        if (_disposed || _drainingToolRefreshes || _toolExpansionCancellation is not null || _pendingToolRefreshHeaders.Count == 0) return;
        _drainingToolRefreshes = true;
        _operations.Submit(new PresentationOperation("agent-chat.refresh-tool-details", "The tool details could not be refreshed."), async _ =>
        {
            try
            {
                while (!_disposed && _toolExpansionCancellation is null && _pendingToolRefreshHeaders.Count > 0)
                {
                    var header = _pendingToolRefreshHeaders.First();
                    _pendingToolRefreshHeaders.Remove(header);
                    if (header.IsAttachedToVisualTree() && header.DataContext is AgentToolInvocationRowViewModel row
                        && (row.IsExpanded || row.IsDetailLoadFailed)) await OnToolStepHeaderClickAsync(header, refreshing: true);
                }
            }
            finally { _drainingToolRefreshes = false; }
        });
    }

    private void OnActivityGroupHeaderClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: AgentActivityGroupRowViewModel group } header) return;
        e.Handled = true;
        header.Focus();
        var scope = header.GetVisualAncestors().OfType<TranscriptRowPresenter>().FirstOrDefault();
        _transcriptBehavior.MutateViewport(() => group.IsExpanded = !group.IsExpanded,
            TranscriptViewportMutationKind.ToolExpansion, group.AnchorKey, scope, isExpanding: !group.IsExpanded);
    }

    private void OnToolStepHeaderClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        _operations.Submit(
            new PresentationOperation(
                "agent-chat.expand-tool-details",
                "The tool details could not be opened."),
            _ => OnToolStepHeaderClickAsync(sender));
    }

    private async Task OnToolStepHeaderClickAsync(object? sender, bool refreshing = false)
    {
        if (sender is not Control header
            || header.DataContext is not AgentToolInvocationRowViewModel toolRow)
        {
            return;
        }

        var scope = header.GetVisualAncestors()
            .OfType<TranscriptRowPresenter>()
            .FirstOrDefault();
        if (scope is null
            || scope.GetVisualDescendants()
                .OfType<TranscriptToolDetailHost>()
                .FirstOrDefault() is not { } detailHost)
        {
            return;
        }

        if (toolRow.IsExpanded && !refreshing)
        {
            CancelPendingToolExpansion();
            header.Focus();
            if (!TranscriptToolExpansionPreparation.IsCurrent(
                header,
                toolRow,
                scope,
                detailHost,
                toolRow.AnchorKey,
                () => !_disposed && toolRow.IsExpanded))
            {
                return;
            }
            _transcriptBehavior.MutateViewport(
                () =>
                {
                    detailHost.BeginCollapseCompensation();
                    detailHost.Clear(
                        notifyOwner: false,
                        preserveCollapseCompensator: true);
                    toolRow.CollapseDetails();
                    ViewModel?.SetTranscriptRowExpanded(toolRow, false);
                },
                TranscriptViewportMutationKind.ToolExpansion,
                toolRow.AnchorKey,
                scope,
                isExpanding: false);
            return;
        }

        if (toolRow.IsPreparing && !refreshing)
        {
            CancelPendingToolExpansion();
            toolRow.CollapseDetails();
            return;
        }

        CancelPendingToolExpansion();
        var operationCancellation = new CancellationTokenSource();
        var operationToken = operationCancellation.Token;
        _toolExpansionCancellation = operationCancellation;
        var request = toolRow.BeginExpansion();
        if (request is null)
        {
            ReleaseToolExpansionOperation(operationCancellation);
            return;
        }
        var viewportMutationGeneration = _transcriptBehavior.BeginViewportMutationPreparation(
            TranscriptViewportMutationKind.ToolExpansion,
            toolRow.AnchorKey,
            scope,
            isExpanding: true);
        if (viewportMutationGeneration == 0)
        {
            AbandonExpansion();
            ReleaseToolExpansionOperation(operationCancellation);
            return;
        }
        var viewportMutationCommitted = false;

        try
        {
            var detail = await toolRow.LoadDetailsAsync(request, operationToken);
            if (!IsCurrentToolExpansion(header, toolRow, scope, detailHost, request))
            {
                if (toolRow.IsCurrentExpansion(request))
                {
                    AbandonExpansion();
                }
                return;
            }
            if (!toolRow.TryMaterializeDetails(request, detail, out var details)
                || details is null)
            {
                toolRow.FailExpansion(request, new InvalidOperationException("Tool details are no longer available."));
                return;
            }

            var template = Resources["AgentToolDetailsTemplate"] as IDataTemplate
                           ?? throw new InvalidOperationException("The Agent tool detail template is unavailable.");
            var prepared = await ToolDetailPreparationPortal.PrepareAsync(
                details,
                template,
                ResolveDetailWidth(detailHost, header),
                operationToken,
                currentWidth: () => ResolveDetailWidth(detailHost, header));
            if (!IsCurrentToolExpansion(header, toolRow, scope, detailHost, request)
                || !ReferenceEquals(prepared.Content, details))
            {
                if (toolRow.IsCurrentExpansion(request))
                {
                    AbandonExpansion();
                }
                return;
            }

            if (refreshing)
            {
                // Loading retained details can span live rows or manual scrolling.
                // Publish against the current viewport, never the pre-read anchor.
                _transcriptBehavior.CancelViewportMutationPreparation(viewportMutationGeneration);
                viewportMutationGeneration = _transcriptBehavior.BeginViewportMutationPreparation(
                    TranscriptViewportMutationKind.ToolExpansion, toolRow.AnchorKey, scope, isExpanding: true);
            }
            viewportMutationCommitted = _transcriptBehavior.CommitViewportMutationPreparation(
                viewportMutationGeneration,
                () =>
                {
                    if (!IsCurrentToolExpansion(header, toolRow, scope, detailHost, request)
                        || !ToolDetailPreparationPortal.Commit(prepared, detailHost, toolRow)
                        || !toolRow.CommitExpansion(request))
                    {
                        detailHost.Clear(notifyOwner: false);
                        toolRow.CollapseDetails();
                        return;
                    }
                    ViewModel?.SetTranscriptRowExpanded(toolRow, true);
                });
            if (!viewportMutationCommitted && toolRow.IsCurrentExpansion(request))
            {
                AbandonExpansion();
            }
        }
        catch (OperationCanceledException) when (operationToken.IsCancellationRequested
                                                  || request.CancellationToken.IsCancellationRequested)
        {
            _transcriptBehavior.CancelViewportMutationPreparation(viewportMutationGeneration);
            if (toolRow.IsCurrentExpansion(request))
            {
                AbandonExpansion();
            }
        }
        catch (Exception exception) when (
            PresentationExceptionPolicy.IsExpectedOperationFailure(exception))
        {
            _transcriptBehavior.CancelViewportMutationPreparation(viewportMutationGeneration);
            if (IsCurrentToolExpansion(header, toolRow, scope, detailHost, request))
            {
                toolRow.FailExpansion(request, exception);
            }
            else if (toolRow.IsCurrentExpansion(request))
            {
                AbandonExpansion();
            }
            throw;
        }
        finally
        {
            if (!viewportMutationCommitted)
            {
                _transcriptBehavior.CancelViewportMutationPreparation(viewportMutationGeneration);
            }
            ReleaseToolExpansionOperation(operationCancellation);
        }

        void AbandonExpansion()
        {
            if (refreshing) toolRow.FailExpansion(request, new InvalidOperationException("Refresh was interrupted; retry details."));
            else toolRow.CollapseDetails();
        }
    }

    private bool IsCurrentToolExpansion(
        Control header,
        AgentToolInvocationRowViewModel row,
        TranscriptRowPresenter scope,
        TranscriptToolDetailHost detailHost,
        TranscriptToolExpansionRequest request)
        => TranscriptToolExpansionPreparation.IsCurrent(
            header,
            row,
            scope,
            detailHost,
            request.AnchorKey,
            () => !_disposed
                  && row.SessionId == request.SessionId
                  && row.DetailRevision == request.Revision
                  && row.IsCurrentExpansion(request));

    private static double ResolveDetailWidth(Control detailHost, Control header)
    {
        var width = detailHost.Bounds.Width;
        if (!double.IsFinite(width) || width <= 1)
        {
            width = header.Bounds.Width;
        }
        return Math.Max(1, width);
    }

    private void CancelPendingToolExpansion()
    {
        var cancellation = Interlocked.Exchange(ref _toolExpansionCancellation, null);
        cancellation?.Cancel();
        cancellation?.Dispose();
        ToolDetailPreparationPortal.CancelPreparation();
    }

    private void ReleaseToolExpansionOperation(CancellationTokenSource operation)
    {
        if (ReferenceEquals(Interlocked.CompareExchange(
                ref _toolExpansionCancellation,
                null,
                operation), operation))
        {
            ToolDetailPreparationPortal.CancelPreparation();
            operation.Dispose();
            QueueToolDetailRefreshes();
        }
    }
}
