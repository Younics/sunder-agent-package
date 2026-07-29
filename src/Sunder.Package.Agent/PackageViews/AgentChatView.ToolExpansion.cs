using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Sunder.Package.Agent.Shared.PackageViews;

namespace Sunder.Package.Agent.PackageViews;

public partial class AgentChatView
{
    private async void OnToolStepHeaderClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
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

        if (toolRow.IsExpanded)
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

        if (toolRow.IsPreparing)
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
            toolRow.CollapseDetails();
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
                    toolRow.CollapseDetails();
                }
                return;
            }
            if (!toolRow.TryMaterializeDetails(request, detail, out var details)
                || details is null)
            {
                if (detail is not null && detail.Revision != request.Revision)
                {
                    toolRow.CollapseDetails();
                }
                else
                {
                    toolRow.FailExpansion(request, new InvalidOperationException("Tool details are no longer available."));
                }
                return;
            }

            var template = Resources["AgentToolDetailsTemplate"] as IDataTemplate
                           ?? throw new InvalidOperationException("The Agent tool detail template is unavailable.");
            var prepared = await ToolDetailPreparationPortal.PrepareAsync(
                details,
                template,
                ResolveDetailWidth(detailHost, header),
                operationToken);
            if (!IsCurrentToolExpansion(header, toolRow, scope, detailHost, request)
                || !ReferenceEquals(prepared.Content, details))
            {
                if (toolRow.IsCurrentExpansion(request))
                {
                    toolRow.CollapseDetails();
                }
                return;
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
                toolRow.CollapseDetails();
            }
        }
        catch (OperationCanceledException) when (operationToken.IsCancellationRequested
                                                  || request.CancellationToken.IsCancellationRequested)
        {
            _transcriptBehavior.CancelViewportMutationPreparation(viewportMutationGeneration);
            if (toolRow.IsCurrentExpansion(request))
            {
                toolRow.CollapseDetails();
            }
        }
        catch (Exception exception)
        {
            _transcriptBehavior.CancelViewportMutationPreparation(viewportMutationGeneration);
            if (IsCurrentToolExpansion(header, toolRow, scope, detailHost, request))
            {
                toolRow.FailExpansion(request, exception);
            }
            else if (toolRow.IsCurrentExpansion(request))
            {
                toolRow.CollapseDetails();
            }
        }
        finally
        {
            if (!viewportMutationCommitted)
            {
                _transcriptBehavior.CancelViewportMutationPreparation(viewportMutationGeneration);
            }
            ReleaseToolExpansionOperation(operationCancellation);
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
        }
    }
}
