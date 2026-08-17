using Sunder.Package.Agent.Shared.Presentation;

namespace Sunder.Package.Agent.Memory.Semantic.PackageViews;

public sealed partial class MemoryInspectorViewModel
{
    private static readonly PresentationOperation MemoryRefreshOperation = new(
        "semantic-memory.refresh",
        "Semantic Memory could not refresh. Its current state was preserved.");
    private static readonly PresentationOperation MemoryReindexOperation = new(
        "semantic-memory.reindex",
        "The semantic index operation could not be completed. Memory state was refreshed.");
    private static readonly PresentationOperation MemorySaveOperation = new(
        "semantic-memory.save",
        "The memory could not be saved. Memory state was refreshed.");
    private static readonly PresentationOperation MemoryPinOperation = new(
        "semantic-memory.pin",
        "The memory pin could not be changed. Memory state was refreshed.");
    private static readonly PresentationOperation MemoryContestOperation = new(
        "semantic-memory.contest",
        "The memory could not be contested. Memory state was refreshed.");
    private static readonly PresentationOperation MemoryForgetOperation = new(
        "semantic-memory.forget",
        "The memory could not be forgotten. Memory state was refreshed.");
    private static readonly PresentationOperation MemorySupersedeOperation = new(
        "semantic-memory.supersede",
        "The memory could not be superseded. Memory state was refreshed.");
    private static readonly PresentationOperation MemoryCorrectionOperation = new(
        "semantic-memory.correct",
        "The memory correction could not be created. Memory state was refreshed.");

    private Task ReconcileMemoryAsync(CancellationToken cancellationToken)
        => StartSessionRefresh(
            SelectedMemory?.MemoryId,
            cancellationToken);
}
