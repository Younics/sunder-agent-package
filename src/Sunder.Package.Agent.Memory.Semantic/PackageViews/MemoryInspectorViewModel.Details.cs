using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Memory.Semantic.Services;

namespace Sunder.Package.Agent.Memory.Semantic.PackageViews;

public sealed partial class MemoryInspectorViewModel
{
    private void StartMemoryDetailsLoad(MemoryListItemViewModel? selection)
    {
        var version = ++_memoryDetailsLoadVersion;
        if (selection is null)
        {
            ApplySelectionDetails(MemorySelectionDetails.Empty);
            return;
        }

        var semanticContext = _selectedSessionSemanticContext;
        _tasks.Run(cancellationToken => LoadSelectionDetailsAsync(
            selection,
            semanticContext,
            version,
            cancellationToken));
    }

    private async Task LoadSelectionDetailsAsync(
        MemoryListItemViewModel selection,
        SemanticEmbeddingContext? semanticContext,
        int version,
        CancellationToken cancellationToken)
    {
        try
        {
            var details = await CaptureSelectionDetailsAsync(
                    selection,
                    semanticContext,
                    cancellationToken)
                .ConfigureAwait(false);
            await _uiDispatcher.InvokeAsync(() =>
            {
                if (!_disposed
                    && version == _memoryDetailsLoadVersion
                    && SelectedMemory?.MemoryId == selection.MemoryId)
                {
                    ApplySelectionDetails(details);
                }
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            await _uiDispatcher.InvokeAsync(() =>
            {
                if (!_disposed && version == _memoryDetailsLoadVersion)
                {
                    StatusText = exception.Message;
                }
            }).ConfigureAwait(false);
        }
    }

    private async Task<MemorySelectionDetails> CaptureSelectionDetailsAsync(
        MemoryListItemViewModel? selection,
        SemanticEmbeddingContext? semanticContext,
        CancellationToken cancellationToken)
    {
        if (selection is null)
        {
            return MemorySelectionDetails.Empty;
        }

        var evidenceTask = _memoryInspectorService.ListEvidenceAsync(selection.MemoryId, cancellationToken);
        var supersedingTask = _memoryInspectorService.GetSupersedingMemoryAsync(selection.MemoryId, cancellationToken);
        var supersededTask = _memoryInspectorService.ListSupersededMemoriesAsync(selection.MemoryId, cancellationToken);
        var lineageTask = _memoryInspectorService.ListCorrectionLineageAsync(selection.MemoryId, cancellationToken);
        await Task.WhenAll(evidenceTask, supersedingTask, supersededTask, lineageTask).ConfigureAwait(false);
        var supersedingMemory = await supersedingTask.ConfigureAwait(false);
        var supersededMemories = await supersededTask.ConfigureAwait(false);
        var supersedingStatusTask = supersedingMemory is null
            ? Task.FromResult<MemorySemanticIndexStatusRecord?>(null)
            : GetSemanticIndexStatusAsync(supersedingMemory, semanticContext, cancellationToken);
        var supersededItemsTask = Task.WhenAll(supersededMemories.Select(async memory =>
            new MemoryListItemViewModel(
                memory,
                await _memoryInspectorService.GetSemanticIndexStatusAsync(
                        memory,
                        semanticContext,
                        cancellationToken)
                    .ConfigureAwait(false))));
        await Task.WhenAll(supersedingStatusTask, supersededItemsTask).ConfigureAwait(false);
        return new MemorySelectionDetails(
            selection,
            (await evidenceTask.ConfigureAwait(false))
                .Select(item => new MemoryEvidenceItemViewModel(item))
                .ToArray(),
            supersedingMemory is null
                ? null
                : new MemoryListItemViewModel(
                    supersedingMemory,
                    (await supersedingStatusTask.ConfigureAwait(false))!),
            await supersededItemsTask.ConfigureAwait(false),
            (await lineageTask.ConfigureAwait(false)).Count);
    }

    private async Task<MemorySemanticIndexStatusRecord?> GetSemanticIndexStatusAsync(
        StoredMemoryRecord memory,
        SemanticEmbeddingContext? semanticContext,
        CancellationToken cancellationToken)
        => await _memoryInspectorService.GetSemanticIndexStatusAsync(
                memory,
                semanticContext,
                cancellationToken)
            .ConfigureAwait(false);
}
