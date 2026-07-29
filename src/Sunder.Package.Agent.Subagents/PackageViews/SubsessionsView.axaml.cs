using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Sunder.Package.Agent.Shared.PackageViews;
using Sunder.Package.Agent.Shared.Presentation;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Subagents.PackageViews;

public partial class SubsessionsView : UserControl,
    IDisposable,
    IPackageViewWarmupTarget,
    IPackageViewNavigationTarget,
    IPackageViewNavigationPreparationTarget
{
    private readonly AdaptiveMasterDetail _adaptiveLayout;
    private readonly TranscriptViewBehavior _transcriptBehavior;
    private SubsessionsViewModel? _viewModel;
    private CancellationTokenSource? _navigationCancellation;
    private CancellationTokenSource? _toolExpansionCancellation;
    private int _navigationGeneration;
    private int _anchoredNavigationGeneration;
    private PreparedSubsessionHistoryNavigation? _preparedHistoryNavigation;
    private bool _disposed;

    public SubsessionsView()
    {
        InitializeComponent();
        _adaptiveLayout = new AdaptiveMasterDetail(
            this,
            SubsessionAdaptiveLayout,
            SubsessionListPane,
            SubsessionDetailPane,
            isCompact =>
            {
                TranscriptScrollContent.Classes.Set("compact", isCompact);
                if (ViewModel is { } viewModel)
                {
                    viewModel.IsCompactLayout = isCompact;
                }
            });
        _transcriptBehavior = new TranscriptViewBehavior(
            this,
            TranscriptScrollViewer,
            TranscriptItemsControl,
            JumpToLatestTranscriptButton,
            () => ViewModel?.CanLoadOlderTranscriptRows == true,
            (anchor, cancellationToken) => ViewModel?.LoadOlderTranscriptRowsAsync(anchor, cancellationToken)
                                           ?? Task.FromResult(false),
            () => ViewModel?.CanLoadNewerTranscriptRows == true,
            (anchor, cancellationToken) => ViewModel?.LoadNewerTranscriptRowsAsync(
                                                anchor,
                                                cancellationToken,
                                                resumeFollowingWhenCaughtUp: false)
                                            ?? Task.FromResult(false),
            () => ViewModel?.HasNewerTranscriptRows == true,
            () => ViewModel?.IsTranscriptFollowingLatest != false,
            () => ViewModel?.IsTranscriptLoading == true,
            () => ViewModel?.Messages.Count > 0,
            () => ViewModel?.HasSelectedSubsession == true,
            () => ViewModel?.DetachTranscriptFromLatest() == true,
            () => ViewModel?.ResumeTranscriptFollowingLatestIfCaughtUp() == true,
            isVisible => ViewModel?.SetTranscriptJumpToLatestVisible(isVisible),
            anchor => ViewModel?.SetTranscriptViewportAnchor(anchor),
            () => ViewModel?.TranscriptViewportAnchor,
            exception => ViewModel?.ReportTranscriptPagingFailure(exception),
            enumerateRealizedAnchors: EnumerateRealizedTranscriptAnchors,
            realizeAnchorVisual: RealizeTranscriptAnchor,
            realizeTailVisual: RealizeTranscriptTailSentinel,
            presentationStateChanged: isActive => ViewModel?.SetTranscriptPresentationActive(isActive),
            anchorHost: TranscriptAnchorHost);
    }

    public SubsessionsView(SubsessionsViewModel viewModel)
        : this()
    {
        _viewModel = viewModel;
        _viewModel.PropertyChanging += OnViewModelPropertyChanging;
        _viewModel.TranscriptChanging += OnTranscriptChanging;
        _viewModel.TranscriptChanged += OnTranscriptChanged;
        DataContext = viewModel;
    }

    private SubsessionsViewModel? ViewModel => _viewModel ?? DataContext as SubsessionsViewModel;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _navigationCancellation?.Cancel();
        _navigationCancellation?.Dispose();
        _navigationCancellation = null;
        CancelPendingToolExpansion();
        ToolDetailPreparationPortal.Dispose();
        _adaptiveLayout.Dispose();
        _transcriptBehavior.Dispose();
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanging -= OnViewModelPropertyChanging;
            _viewModel.TranscriptChanging -= OnTranscriptChanging;
            _viewModel.TranscriptChanged -= OnTranscriptChanged;
            _viewModel.Dispose();
        }

        DataContext = null;
        _viewModel = null;
    }

    public async ValueTask WarmupAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ViewModel is { } viewModel)
        {
            await viewModel.InitializeAsync(cancellationToken);
        }
    }

    public async ValueTask OnNavigatedToAsync(
        PackageViewNavigationContext context,
        CancellationToken cancellationToken = default)
    {
        if (await PrepareNavigationAsync(context, cancellationToken))
        {
            await OnNavigationPresentedAsync(context, cancellationToken);
        }
    }

    public async ValueTask<bool> PrepareNavigationAsync(
        PackageViewNavigationContext context,
        CancellationToken cancellationToken = default)
    {
        var current = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var previous = Interlocked.Exchange(ref _navigationCancellation, current);
        var generation = Interlocked.Increment(ref _navigationGeneration);
        previous?.Cancel();
        previous?.Dispose();
        try
        {
            return await OnNavigatedToCoreAsync(context, generation, current.Token);
        }
        finally
        {
            Interlocked.CompareExchange(ref _navigationCancellation, null, current);
            current.Dispose();
        }
    }

    public ValueTask OnNavigationPresentedAsync(
        PackageViewNavigationContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_preparedHistoryNavigation is { } prepared
            && prepared.ViewGeneration == Volatile.Read(ref _navigationGeneration)
            && ViewModel is { } viewModel)
        {
            _preparedHistoryNavigation = null;
            viewModel.AcknowledgeHistoryPresentation(
                prepared.SessionId,
                prepared.AnchorKey,
                cancellationToken);
        }
        return ValueTask.CompletedTask;
    }

    private async Task<bool> OnNavigatedToCoreAsync(
        PackageViewNavigationContext context,
        int generation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (generation != Volatile.Read(ref _navigationGeneration))
        {
            throw new OperationCanceledException("The subsession navigation was superseded.");
        }
        _preparedHistoryNavigation = null;
        var anchor = SubsessionsViewModel.TryGetNavigationAnchor(context.Parameters);
        if (anchor is not null)
        {
            Volatile.Write(ref _anchoredNavigationGeneration, generation);
            var preliminaryKey = anchor.AnchorKind == TranscriptNavigationAnchorKind.Text
                ? TranscriptRowAnchorKey.Text(anchor.TurnId)
                : new TranscriptRowAnchorKey($"tool:{anchor.TurnId:N}:{anchor.ItemId:N}");
            _transcriptBehavior.MarkInitialPlacementPending(cancellationToken, preliminaryKey);
        }
        else
        {
            Volatile.Write(ref _anchoredNavigationGeneration, 0);
        }
        if (ViewModel is { } viewModel)
        {
            try
            {
                var prepared = await viewModel.PrepareNavigationAsync(context, cancellationToken);
                if (!prepared)
                {
                    await RestoreRejectedNavigationPresentationAsync(cancellationToken);
                    return false;
                }
                cancellationToken.ThrowIfCancellationRequested();
                if (generation != Volatile.Read(ref _navigationGeneration))
                {
                    throw new OperationCanceledException("The subsession navigation was superseded.");
                }
                if (anchor is not null)
                {
                    if (viewModel.NavigationAnchorKey is { } anchorKey)
                    {
                        if (!viewModel.IsCurrentNavigationPlacement(anchor.SessionId))
                        {
                            throw new OperationCanceledException(
                                "The subsession selection superseded transcript placement.");
                        }
                        _transcriptBehavior.SetInitialPlacementAnchor(anchorKey, cancellationToken);
                        _preparedHistoryNavigation = new PreparedSubsessionHistoryNavigation(
                            generation,
                            anchor.SessionId,
                            anchorKey);
                    }
                    else
                    {
                        _transcriptBehavior.MarkInitialPlacementPending(cancellationToken);
                    }
                }
                else
                {
                    _transcriptBehavior.MarkInitialPlacementPending(cancellationToken);
                }
                _transcriptBehavior.OnTranscriptChanged();
                if (anchor is not null
                    || this.IsAttachedToVisualTree() && IsEffectivelyVisible)
                {
                    await _transcriptBehavior.WaitForInitialPresentationAsync(cancellationToken);
                }
                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (SubsessionHistoryTargetUnavailableException exception)
            {
                viewModel.ReportTranscriptPagingFailure(exception);
                await RestoreRejectedNavigationPresentationAsync(cancellationToken);
                return false;
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                viewModel.ReportTranscriptPagingFailure(exception);
                await RestoreRejectedNavigationPresentationAsync(cancellationToken);
                return false;
            }
            finally
            {
                Interlocked.CompareExchange(ref _anchoredNavigationGeneration, 0, generation);
            }
        }
        return false;
    }

    private async Task RestoreRejectedNavigationPresentationAsync(
        CancellationToken cancellationToken)
    {
        _transcriptBehavior.MarkInitialPlacementPending(cancellationToken);
        _transcriptBehavior.OnTranscriptChanged();
        await _transcriptBehavior.WaitForInitialPresentationAsync(cancellationToken);
    }

    private sealed record PreparedSubsessionHistoryNavigation(
        int ViewGeneration,
        Guid SessionId,
        object AnchorKey);

    private void OnViewModelPropertyChanging(object? sender, PropertyChangingEventArgs e)
    {
        if (string.Equals(
                e.PropertyName,
                nameof(SubsessionsViewModel.SelectedSubsession),
                StringComparison.Ordinal)
            && Volatile.Read(ref _anchoredNavigationGeneration) == 0)
        {
            _transcriptBehavior.MarkInitialPlacementPending(_navigationCancellation?.Token ?? default);
        }
    }

    private void OnSubsessionItemTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not SubsessionListItemViewModel subsession)
        {
            return;
        }

        var viewModel = ViewModel;
        if (viewModel?.SelectedSubsession?.SessionId != subsession.SessionId)
        {
            _transcriptBehavior.MarkInitialPlacementPending();
        }

        viewModel?.ActivateSubsession(subsession);
    }

    private void OnTranscriptChanging(bool isPageApplication)
    {
        // Keyed page application retains the active row; expansion currentness handles actual replacement.
        if (!isPageApplication)
        {
            CancelPendingToolExpansion();
        }
        _transcriptBehavior.OnTranscriptChanging(isPageApplication);
    }

    private void OnTranscriptChanged() => _transcriptBehavior.OnTranscriptChanged();

    private void TranscriptMarkdown_OnRendered(object? sender, EventArgs e)
        => _transcriptBehavior.OnRenderedContentChanged(sender as ITranscriptGeometrySource);

    private void JumpToLatestTranscript_OnClick(object? sender, RoutedEventArgs e)
    {
        var viewModel = ViewModel;
        if (viewModel is null)
        {
            return;
        }

        if (!viewModel.HasNewerTranscriptRows)
        {
            _transcriptBehavior.ScrollToBottom();
        }
        else if (viewModel.JumpToLatestTranscriptCommand.CanExecute(null))
        {
            _transcriptBehavior.JumpToLatest(
                () => viewModel.JumpToLatestTranscriptCommand.Execute(null));
        }
    }

    private async void OnToolStepHeaderClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not Control header
            || header.DataContext is not SubsessionToolInvocationRowViewModel toolRow)
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

            var template = Resources["SubsessionToolDetailsTemplate"] as IDataTemplate
                           ?? throw new InvalidOperationException("The subsession tool detail template is unavailable.");
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
        SubsessionToolInvocationRowViewModel row,
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
