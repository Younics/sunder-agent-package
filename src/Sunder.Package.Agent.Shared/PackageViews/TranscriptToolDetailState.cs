using LiveMarkdown.Avalonia;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Shared.PackageViews;

internal enum TranscriptToolExpansionState
{
    Collapsed,
    Preparing,
    Expanded,
    Refreshing,
    DetailLoadFailed,
}

internal sealed record TranscriptToolExpansionRequest(
    long Generation,
    Guid SessionId,
    object AnchorKey,
    long Revision,
    AgentTranscriptToolDetailRequest DetailRequest,
    CancellationToken CancellationToken);

internal sealed record TranscriptToolDetailData(
    AgentTranscriptToolDetailRecord Detail,
    AgentToolPresentation Presentation);

internal sealed class TranscriptToolDetailViewModel : IDisposable
{
    private readonly int _diffLineCount;
    private bool _disposed;

    internal TranscriptToolDetailViewModel(
        AgentTranscriptToolDetailRecord detail,
        AgentToolPresentation presentation,
        ToolDiffViewModel? toolDiff)
    {
        OutputText = presentation.OutputText?.Trim() ?? string.Empty;
        ErrorCodeText = detail.ErrorCode ?? string.Empty;
        BackendText = detail.BackendId ?? string.Empty;
        HasAmbiguousWarning = detail.Status == AgentToolExecutionStatus.Ambiguous;
        WasTransportTruncated = detail.WasTransportTruncated;
        ToolDiff = toolDiff;
        Revision = detail.Revision;
        DetailMarkdownBuilder = new ObservableStringBuilder();
        DetailMarkdownBuilder.Append(presentation.DetailMarkdown?.Trim() ?? string.Empty);
        _diffLineCount = toolDiff?.Files.Sum(file => file.Lines.Count) ?? 0;
        TranscriptToolDiagnostics.DetailViewModelCreated(_diffLineCount);
    }

    public ObservableStringBuilder DetailMarkdownBuilder { get; }
    public string OutputText { get; }
    public string ErrorCodeText { get; }
    public string BackendText { get; }
    public bool HasAmbiguousWarning { get; }
    public string AmbiguousWarningText => "Effects may have occurred and no retry happened. Sunder did not retry this tool call.";
    public bool WasTransportTruncated { get; }
    public string TransportTruncatedText => "Some detail fields were omitted because this result exceeds the Runtime transport limit.";
    public bool HasOutput => !string.IsNullOrWhiteSpace(OutputText);
    public bool HasErrorCode => !string.IsNullOrWhiteSpace(ErrorCodeText);
    public bool HasBackend => !string.IsNullOrWhiteSpace(BackendText);
    public bool HasMetadata => HasErrorCode || HasBackend;
    public bool HasMarkdownDetails => DetailMarkdownBuilder.Length > 0;
    public bool ShowMarkdownDetails => HasMarkdownDetails && (ToolDiff?.ShowMarkdownDetails ?? true);
    internal ToolDiffViewModel? ToolDiff { get; }
    internal long Revision { get; }
    public bool HasToolDiff => ToolDiff?.HasFiles == true;
    internal IReadOnlyList<ToolDiffFileViewModel> ToolDiffFiles => ToolDiff?.Files ?? [];
    public string ToolDiffSectionTitle => ToolDiff?.SectionTitle ?? string.Empty;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        DetailMarkdownBuilder.Clear();
        TranscriptToolDiagnostics.DetailViewModelDestroyed(_diffLineCount);
    }
}

internal sealed class TranscriptToolDetailState : IDisposable
{
    private readonly Func<AgentTranscriptToolDetailRequest, CancellationToken, Task<AgentTranscriptToolDetailRecord?>> _loadDetail;
    private readonly Func<AgentTurnItemRecord, CancellationToken, ValueTask<AgentToolPresentation>> _resolvePresentation;
    private TranscriptToolProjection _projection;
    private TranscriptToolDetailViewModel? _details;
    private TranscriptToolDetailViewModel? _pendingDetails;
    private CancellationTokenSource? _requestCancellation;
    private long _generation;
    private long _materializedRevision;
    private bool _disposed;

    internal TranscriptToolDetailState(
        TranscriptToolProjection projection,
        Func<AgentTranscriptToolDetailRequest, CancellationToken, Task<AgentTranscriptToolDetailRecord?>> loadDetail,
        Func<AgentTurnItemRecord, CancellationToken, ValueTask<AgentToolPresentation>> resolvePresentation)
    {
        _projection = projection;
        _loadDetail = loadDetail;
        _resolvePresentation = resolvePresentation;
    }

    internal event Action? Changed;
    internal event Action? Invalidated;
    internal event Action? RefreshRequested;

    internal TranscriptToolExpansionState State { get; private set; }
    internal TranscriptToolDetailViewModel? Details => _details ?? _pendingDetails;
    internal string FailureText { get; private set; } = string.Empty;
    internal bool IsExpanded => State is TranscriptToolExpansionState.Expanded or TranscriptToolExpansionState.Refreshing;
    internal bool IsPreparing => State is TranscriptToolExpansionState.Preparing or TranscriptToolExpansionState.Refreshing;

    internal void UpdateProjection(TranscriptToolProjection projection)
    {
        var identityChanged = !projection.SameInvocation(_projection);
        if (!identityChanged && projection.DetailRevision < _projection.DetailRevision) return;
        var revisionChanged = projection.DetailRevision != _projection.DetailRevision;
        _projection = projection;
        if (identityChanged || !projection.HasDetails)
        {
            Collapse();
        }
        else if (revisionChanged && projection.DetailRevision > _materializedRevision)
        {
            if (_details is not null && IsExpanded)
            {
                ++_generation;
                CancelRequest();
                DestroyPendingDetails();
                State = TranscriptToolExpansionState.Expanded;
                FailureText = string.Empty;
                Changed?.Invoke();
                RefreshRequested?.Invoke();
            }
            else Collapse();
        }
    }

    internal TranscriptToolExpansionRequest? BeginExpansion()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_projection.HasDetails)
        {
            return null;
        }

        CancelRequest();
        DestroyPendingDetails();
        var refreshing = _details is not null && IsExpanded;
        if (!refreshing) DestroyDetails();
        _requestCancellation = new CancellationTokenSource();
        State = refreshing ? TranscriptToolExpansionState.Refreshing : TranscriptToolExpansionState.Preparing;
        FailureText = string.Empty;
        var request = new TranscriptToolExpansionRequest(
            ++_generation,
            _projection.SessionId,
            _projection.AnchorKey,
            _projection.DetailRevision,
#if SUNDER_SUBAGENTS_APP || SUNDER_SUBAGENTS_WORKER
            new AgentTranscriptToolDetailRequest
            {
                SessionId = _projection.SessionId, ToolExecutionId = _projection.ToolExecutionId, CallId = _projection.CallId,
                ItemId = _projection.ItemId, RunId = _projection.RunId, RunRevision = _projection.RunRevision,
            },
#else
            new AgentTranscriptToolDetailRequest(
                _projection.SessionId,
                _projection.ToolExecutionId,
                _projection.CallId,
                _projection.ItemId,
                _projection.RunId,
                _projection.RunRevision),
#endif
            _requestCancellation.Token);
        Changed?.Invoke();
        return request;
    }

    internal async Task<TranscriptToolDetailData?> LoadAsync(
        TranscriptToolExpansionRequest request,
        CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            request.CancellationToken,
            cancellationToken);
        TranscriptToolDiagnostics.DetailLoadStarted();
        try
        {
            var detail = await _loadDetail(request.DetailRequest, linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            if (detail is null || !MatchesDetailIdentity(request, detail)) return null;
            if (detail.Revision < request.Revision)
            {
                // A delayed reader can return an older snapshot. Reread once,
                // never render an older revision or call the invocation missing.
                detail = await _loadDetail(request.DetailRequest, linked.Token).ConfigureAwait(false);
                linked.Token.ThrowIfCancellationRequested();
                if (detail is null || !MatchesDetailIdentity(request, detail)) return null;
                if (detail.Revision < request.Revision)
                    throw new InvalidOperationException("Tool details changed while loading. Retry details after the transcript refreshes.");
            }
            TranscriptToolDiagnostics.ResolverCalled();
            var presentation = await _resolvePresentation(CreateItem(request, detail), linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            return new TranscriptToolDetailData(detail, presentation);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            TranscriptToolDiagnostics.DetailLoadCanceled();
            throw;
        }
        catch
        {
            TranscriptToolDiagnostics.DetailLoadFailed();
            throw;
        }
    }

    internal bool IsCurrent(TranscriptToolExpansionRequest request)
        => !_disposed
           && State is TranscriptToolExpansionState.Preparing or TranscriptToolExpansionState.Refreshing
           && request.Generation == _generation
           && request.SessionId == _projection.SessionId
           && request.Revision == _projection.DetailRevision
           && Equals(request.AnchorKey, _projection.AnchorKey)
           && !request.CancellationToken.IsCancellationRequested;

    internal bool TryMaterialize(
        TranscriptToolExpansionRequest request,
        TranscriptToolDetailData? loaded,
        out TranscriptToolDetailViewModel? details)
    {
        details = null;
        if (!IsCurrent(request) || loaded is null || !MatchesDetail(request, loaded.Detail))
        {
            return false;
        }

        var detail = loaded.Detail;
        var diff = ToolDiffViewModel.TryCreate(
            detail.ToolId,
            detail.ArgumentsJson ?? "{}",
            detail.ResultSummary,
            detail.OutputText,
            detail.IsError,
            detail.PresentationPayloadJson);
        details = new TranscriptToolDetailViewModel(detail, loaded.Presentation, diff);
        DestroyPendingDetails();
        _pendingDetails = details;
        Changed?.Invoke();
        return true;
    }

    private static bool MatchesDetail(TranscriptToolExpansionRequest request, AgentTranscriptToolDetailRecord detail)
        => detail.Revision >= request.Revision && MatchesDetailIdentity(request, detail);

    private static bool MatchesDetailIdentity(TranscriptToolExpansionRequest request, AgentTranscriptToolDetailRecord detail)
        => detail.SessionId == request.SessionId
           && (request.DetailRequest.ToolExecutionId is not { } executionId || detail.ToolExecutionId == executionId)
           && (request.DetailRequest.ToolExecutionId is not null || request.DetailRequest.ItemId is not { } itemId
               || detail.CallItemId == itemId || detail.ResultItemId == itemId)
           && (request.DetailRequest.RunId is not { } runId || detail.RunId == runId)
           && (request.DetailRequest.RunRevision is not { } runRevision || detail.RunRevision == runRevision)
           && (string.IsNullOrWhiteSpace(request.DetailRequest.CallId)
               || string.Equals(request.DetailRequest.CallId, detail.CallId, StringComparison.Ordinal));

    private static AgentTurnItemRecord CreateItem(TranscriptToolExpansionRequest request, AgentTranscriptToolDetailRecord detail)
#if SUNDER_SUBAGENTS_APP || SUNDER_SUBAGENTS_WORKER
        => new()
        {
            ItemId = detail.ResultItemId ?? detail.CallItemId ?? request.DetailRequest.ItemId ?? Guid.Empty,
            TurnId = Guid.Empty, SequenceNumber = 0, Kind = detail.ResultItemId is null ? AgentTurnItemKind.ToolCall : AgentTurnItemKind.ToolResult,
            TextContent = detail.OutputText, CallId = detail.CallId, ToolId = detail.ToolId, ArgumentsJson = detail.ArgumentsJson,
            ResultSummary = detail.ResultSummary, StructuredPayloadJson = detail.StructuredPayloadJson, SourcesJson = detail.SourcesJson,
            WasTruncated = detail.WasTruncated, IsError = detail.IsError, ErrorCode = detail.ErrorCode, BackendId = detail.BackendId,
            PresentationPayloadJson = detail.PresentationPayloadJson, ToolExecutionId = detail.ToolExecutionId, ToolExecutionStatus = detail.Status,
            ToolOwnerPackageId = detail.ToolOwnerPackageId, ToolSchemaId = detail.ToolSchemaId, ToolSchemaVersion = detail.ToolSchemaVersion,
            ToolHeaderHint = null, ToolErrorSummary = null, ToolDetailRevision = 0, ToolHasDetails = null, IsToolHeaderProjection = false,
        };
#else
        => new(
            detail.ResultItemId ?? detail.CallItemId ?? request.DetailRequest.ItemId ?? Guid.Empty,
            Guid.Empty,
            0,
            detail.ResultItemId is null ? AgentTurnItemKind.ToolCall : AgentTurnItemKind.ToolResult,
            detail.OutputText,
            detail.CallId,
            detail.ToolId,
            detail.ArgumentsJson,
            detail.ResultSummary,
            detail.StructuredPayloadJson,
            detail.SourcesJson,
            detail.WasTruncated,
            detail.IsError,
            detail.ErrorCode,
            detail.BackendId,
            detail.PresentationPayloadJson)
        {
            ToolExecutionId = detail.ToolExecutionId,
            ToolExecutionStatus = detail.Status,
            ToolOwnerPackageId = detail.ToolOwnerPackageId,
            ToolSchemaId = detail.ToolSchemaId,
            ToolSchemaVersion = detail.ToolSchemaVersion,
        };
#endif

    internal bool CommitExpanded(TranscriptToolExpansionRequest request)
    {
        if (!IsCurrent(request) || _pendingDetails is null)
        {
            return false;
        }

        var previous = _details;
        _details = _pendingDetails;
        _materializedRevision = _details.Revision;
        _pendingDetails = null;
        State = TranscriptToolExpansionState.Expanded;
        CancelRequest(disposeOnly: true);
        previous?.Dispose();
        Changed?.Invoke();
        return true;
    }

    internal void Fail(TranscriptToolExpansionRequest request, Exception exception)
    {
        if (!IsCurrent(request))
        {
            return;
        }
        CancelRequest(disposeOnly: true);
        DestroyPendingDetails();
        FailureText = _details is null ? BoundFailure(exception.Message)
            : "Showing the previous details. Refresh failed: " + BoundFailure(exception.Message);
        State = _details is null ? TranscriptToolExpansionState.DetailLoadFailed : TranscriptToolExpansionState.Expanded;
        Changed?.Invoke();
    }

    internal void Collapse()
    {
        _generation++;
        CancelRequest();
        FailureText = string.Empty;
        var invalidated = State != TranscriptToolExpansionState.Collapsed || _details is not null || _pendingDetails is not null;
        State = TranscriptToolExpansionState.Collapsed;
        if (invalidated)
        {
            Invalidated?.Invoke();
            DestroyPendingDetails();
            DestroyDetails();
            Changed?.Invoke();
        }
    }

    private void CancelRequest(bool disposeOnly = false)
    {
        var cancellation = Interlocked.Exchange(ref _requestCancellation, null);
        if (cancellation is null)
        {
            return;
        }
        if (!disposeOnly)
        {
            cancellation.Cancel();
        }
        cancellation.Dispose();
    }

    private void DestroyDetails()
    {
        Interlocked.Exchange(ref _details, null)?.Dispose();
        _materializedRevision = 0;
    }

    private void DestroyPendingDetails() => Interlocked.Exchange(ref _pendingDetails, null)?.Dispose();

    private static string BoundFailure(string? value)
    {
        var text = string.IsNullOrWhiteSpace(value)
            ? "Tool details could not be loaded."
            : value.Trim();
        return text.Length <= 240 ? text : text[..240] + "...";
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        Collapse();
        Changed = null;
        Invalidated = null;
        RefreshRequested = null;
    }
}
