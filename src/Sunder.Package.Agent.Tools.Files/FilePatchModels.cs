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
    string? OriginalContent,
    string? NextContent,
    string? ExpectedContentHash,
    FileDiffPayloadFile PresentationFile);

internal sealed record FileDiffPresentationPayload(string Schema, IReadOnlyList<FileDiffPayloadFile> Files);

internal sealed record FileDiffPayloadFile(
    string Path,
    string Operation,
    int AddedLineCount,
    int DeletedLineCount,
    IReadOnlyList<FileDiffPayloadLine> Lines);

internal sealed record FileDiffPayloadLine(string Kind, int? LineNumber, string Text);

internal sealed record FilePatchCompensationResult(int RestoredCount, IReadOnlyList<string> Failures)
{
    public bool FullyRestored => Failures.Count == 0;
}
