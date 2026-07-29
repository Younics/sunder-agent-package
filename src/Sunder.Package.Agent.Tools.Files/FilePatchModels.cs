using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Tools.Files;

internal enum FilePatchOperationKind
{
    Add,
    Update,
    Delete,
}

internal sealed record FilePatchHunk(string OldText, string NewText);

internal sealed record FilePatchOperation(
    FilePatchOperationKind Kind,
    string Path,
    string? Content,
    IReadOnlyList<FilePatchHunk> Hunks);

internal sealed record FilePatchPlan(IReadOnlyList<PlannedFilePatchOperation> Operations);

internal sealed record PlannedFilePatchOperation(
    FilePatchOperationKind Kind,
    string Path,
    int ResourceIndex,
    string? OriginalContent,
    string? NextContent,
    string? ExpectedContentHash,
    string ResourceReference,
    FileDiffPayloadFile PresentationFile)
{
    public AgentResolvedResource? PostMutationResource { get; set; }
}

internal sealed record FileDiffPresentationPayload(string Schema, IReadOnlyList<FileDiffPayloadFile> Files);

internal sealed record FileDiffPayloadFile(
    string Path,
    string Operation,
    int AddedLineCount,
    int DeletedLineCount,
    IReadOnlyList<FileDiffPayloadLine> Lines);

internal sealed record FileDiffPayloadLine(string Kind, int? LineNumber, string Text);

internal sealed record FilePatchCompensationResult(
    IReadOnlyList<string> RestoredPaths,
    IReadOnlyList<FilePatchCompensationFailure> Failures)
{
    public int RestoredCount => RestoredPaths.Count;

    public bool FullyRestored => Failures.Count == 0;
}

internal sealed record FilePatchCompensationFailure(string Path, string Failure);

internal sealed record FilePatchCompensationProbeFailure(string Path, string Failure);
