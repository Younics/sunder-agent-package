using LiveMarkdown.Avalonia;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Shared.PackageViews;

internal enum TranscriptToolExpansionState
{
    Collapsed,
    Preparing,
    Expanded,
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
    private CancellationTokenSource? _requestCancellation;
    private long _generation;
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

    internal TranscriptToolExpansionState State { get; private set; }
    internal TranscriptToolDetailViewModel? Details => _details;
    internal string FailureText { get; private set; } = string.Empty;
    internal bool IsExpanded => State == TranscriptToolExpansionState.Expanded;
    internal bool IsPreparing => State == TranscriptToolExpansionState.Preparing;

    internal void UpdateProjection(TranscriptToolProjection projection)
    {
        var invalidatesDetail = projection.SessionId != _projection.SessionId
                                || !Equals(projection.AnchorKey, _projection.AnchorKey)
                                || projection.DetailRevision != _projection.DetailRevision
                                || projection.ToolExecutionId != _projection.ToolExecutionId
                                || projection.ItemId != _projection.ItemId;
        _projection = projection;
        if (invalidatesDetail)
        {
            Collapse();
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
        DestroyDetails();
        _requestCancellation = new CancellationTokenSource();
        State = TranscriptToolExpansionState.Preparing;
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
            if (detail is null || !MatchesDetail(request, detail)) return null;
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
           && State == TranscriptToolExpansionState.Preparing
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
        _details = details;
        Changed?.Invoke();
        return true;
    }

    private static bool MatchesDetail(TranscriptToolExpansionRequest request, AgentTranscriptToolDetailRecord detail)
        => detail.SessionId == request.SessionId
           && detail.Revision == request.Revision
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
        if (!IsCurrent(request) || Details is null)
        {
            return false;
        }

        State = TranscriptToolExpansionState.Expanded;
        CancelRequest(disposeOnly: true);
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
        DestroyDetails();
        FailureText = BoundFailure(exception.Message);
        State = TranscriptToolExpansionState.DetailLoadFailed;
        Changed?.Invoke();
    }

    internal void Collapse()
    {
        _generation++;
        CancelRequest();
        FailureText = string.Empty;
        var invalidated = State != TranscriptToolExpansionState.Collapsed || _details is not null;
        State = TranscriptToolExpansionState.Collapsed;
        if (invalidated)
        {
            Invalidated?.Invoke();
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
    }

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
    }
}
