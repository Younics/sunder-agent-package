using System.Text.Json;

namespace Sunder.Package.Agent.Shared.PackageViews;

public sealed class ToolDiffViewModel
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private const int MaxRenderedLinesPerFile = 500;
    private const int CollapsibleContextRunThreshold = 8;
    private const int PreservedContextLinesAtEdge = 3;

    private ToolDiffViewModel(string headerText, string sectionTitle, bool showMarkdownDetails, IReadOnlyList<ToolDiffFileViewModel> files)
    {
        HeaderText = headerText;
        SectionTitle = sectionTitle;
        ShowMarkdownDetails = showMarkdownDetails;
        Files = files;
        AddedLineCount = files.Sum(file => file.AddedLineCount);
        DeletedLineCount = files.Sum(file => file.DeletedLineCount);
    }

    public string HeaderText { get; }

    public string SectionTitle { get; }

    public bool ShowMarkdownDetails { get; }

    public IReadOnlyList<ToolDiffFileViewModel> Files { get; }

    public int AddedLineCount { get; }

    public int DeletedLineCount { get; }

    public bool HasFiles => Files.Count > 0;

    private ToolDiffViewModel WithHeader(string headerText)
        => new(headerText, SectionTitle, ShowMarkdownDetails, Files);

    public static ToolDiffViewModel? TryCreate(string toolId, string argumentsJson, string? resultSummary, string? outputText, bool isError, string? presentationPayloadJson = null)
        => toolId.ToLowerInvariant() switch
        {
            "apply_patch" => TryCreatePatchDiff(argumentsJson, resultSummary, outputText, isError, presentationPayloadJson),
            "edit" => TryCreateEditDiff(argumentsJson, resultSummary, outputText, isError, presentationPayloadJson),
            _ => null,
        };

    private static ToolDiffViewModel? TryCreateEditDiff(string argumentsJson, string? resultSummary, string? outputText, bool isError, string? presentationPayloadJson)
    {
        if (TryCreateFromPresentationPayload(presentationPayloadJson, "Diff", showMarkdownDetails: true, out var metadataDiff))
        {
            var metadataHeader = isError
                ? $"Edit failed: {NormalizeFailureMessage(resultSummary, outputText, "old text not found")}"
                : BuildPatchHeader(metadataDiff.Files, metadataDiff.AddedLineCount, metadataDiff.DeletedLineCount);
            return metadataDiff.WithHeader(metadataHeader.Trim());
        }

        if (!TryGetObject(argumentsJson, out var root)
            || !TryGetString(root, "path", out var path)
            || !TryGetString(root, "oldString", out var oldString)
            || !TryGetString(root, "newString", out var newString))
        {
            return null;
        }

        var lines = new List<ToolDiffLineViewModel>();
        var oldLines = SplitLines(oldString);
        var newLines = SplitLines(newString);
        for (var index = 0; index < oldLines.Length; index++)
        {
            lines.Add(ToolDiffLineViewModel.Deleted(index + 1, oldLines[index]));
        }

        for (var index = 0; index < newLines.Length; index++)
        {
            lines.Add(ToolDiffLineViewModel.Added(index + 1, newLines[index]));
        }

        var file = new ToolDiffFileViewModel(
            path,
            "Edit",
            newLines.Length,
            oldLines.Length,
            TrimLongDiff(lines));
        var header = isError
            ? $"Edit failed: {NormalizeFailureMessage(resultSummary, outputText, "old text not found")}"
            : $"Updated {FormatPathForHeader(path)} {FormatChangeCounts(newLines.Length, oldLines.Length)}";
        return new ToolDiffViewModel(header.Trim(), "Diff", showMarkdownDetails: true, [file]);
    }

    private static ToolDiffViewModel? TryCreatePatchDiff(string argumentsJson, string? resultSummary, string? outputText, bool isError, string? presentationPayloadJson)
    {
        if (TryCreateFromPresentationPayload(presentationPayloadJson, "Patch", showMarkdownDetails: false, out var metadataDiff))
        {
            var metadataHeader = isError
                ? $"Patch failed: {NormalizeFailureMessage(resultSummary, outputText, "patch could not be applied")}"
                : BuildPatchHeader(metadataDiff.Files, metadataDiff.AddedLineCount, metadataDiff.DeletedLineCount);
            return metadataDiff.WithHeader(metadataHeader.Trim());
        }

        if (!TryGetObject(argumentsJson, out var root) || !TryGetString(root, "patchText", out var patchText))
        {
            return null;
        }

        if (!TryParsePatch(patchText, out var files))
        {
            return null;
        }

        var renderedFiles = files.Select(file => file with { Lines = TrimLongDiff(CollapseContextLines(file.Lines)) }).ToArray();
        var added = renderedFiles.Sum(file => file.AddedLineCount);
        var deleted = renderedFiles.Sum(file => file.DeletedLineCount);
        var header = isError
            ? $"Patch failed: {NormalizeFailureMessage(resultSummary, outputText, "patch could not be applied")}"
            : BuildPatchHeader(renderedFiles, added, deleted);
        return new ToolDiffViewModel(header.Trim(), "Patch", showMarkdownDetails: false, renderedFiles);
    }

    private static string BuildPatchHeader(IReadOnlyList<ToolDiffFileViewModel> files, int added, int deleted)
    {
        if (files.Count == 1)
        {
            var file = files[0];
            return file.OperationText switch
            {
                "Add" => $"Added {FormatPathForHeader(file.Path)} {FormatChangeCounts(added, 0)}",
                "Delete" => $"Deleted {FormatPathForHeader(file.Path)} {FormatChangeCounts(0, deleted)}",
                _ => $"Updated {FormatPathForHeader(file.Path)} {FormatChangeCounts(added, deleted)}",
            };
        }

        return $"Updated {FormatCount(files.Count, "file")} {FormatChangeCounts(added, deleted)}";
    }

    private static bool TryCreateFromPresentationPayload(
        string? presentationPayloadJson,
        string sectionTitle,
        bool showMarkdownDetails,
        out ToolDiffViewModel diff)
    {
        diff = new ToolDiffViewModel(string.Empty, sectionTitle, showMarkdownDetails, []);
        if (string.IsNullOrWhiteSpace(presentationPayloadJson))
        {
            return false;
        }

        try
        {
            var payload = JsonSerializer.Deserialize<FileDiffPresentationPayload>(presentationPayloadJson, JsonOptions);
            if (payload is null
                || !string.Equals(payload.Schema, "sunder.file-diff.v1", StringComparison.OrdinalIgnoreCase)
                || payload.Files.Count == 0)
            {
                return false;
            }

            var files = payload.Files.Select(file => new ToolDiffFileViewModel(
                file.Path,
                NormalizeOperation(file.Operation),
                file.AddedLineCount,
                file.DeletedLineCount,
                TrimLongDiff(CollapseContextLines(file.Lines.Select(ToLineViewModel).ToArray())))).ToArray();
            diff = new ToolDiffViewModel(string.Empty, sectionTitle, showMarkdownDetails, files);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static ToolDiffLineViewModel ToLineViewModel(FileDiffPayloadLine line)
        => (line.Kind ?? string.Empty).ToLowerInvariant() switch
        {
            "added" => ToolDiffLineViewModel.Added(line.LineNumber ?? 0, line.Text),
            "deleted" => ToolDiffLineViewModel.Deleted(line.LineNumber ?? 0, line.Text),
            "context" => ToolDiffLineViewModel.Context(line.LineNumber ?? 0, line.LineNumber ?? 0, line.Text),
            "collapsed" => ToolDiffLineViewModel.Collapsed(line.Text),
            _ => ToolDiffLineViewModel.Context(line.LineNumber ?? 0, line.LineNumber ?? 0, line.Text),
        };

    private static string NormalizeOperation(string? operation)
        => string.Equals(operation, "Add", StringComparison.OrdinalIgnoreCase)
            ? "Add"
            : string.Equals(operation, "Delete", StringComparison.OrdinalIgnoreCase)
                ? "Delete"
                : string.Equals(operation, "Edit", StringComparison.OrdinalIgnoreCase)
                    ? "Edit"
                    : "Update";

    private static bool TryParsePatch(string patchText, out ToolDiffFileViewModel[] files)
    {
        files = [];
        var lines = patchText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (lines.Length == 0 || !string.Equals(lines[0].Trim(), "*** Begin Patch", StringComparison.Ordinal))
        {
            return false;
        }

        var parsedFiles = new List<ToolDiffFileViewModel>();
        var index = 1;
        while (index < lines.Length)
        {
            var line = lines[index];
            if (string.Equals(line.Trim(), "*** End Patch", StringComparison.Ordinal))
            {
                files = parsedFiles.ToArray();
                return true;
            }

            if (line.StartsWith("*** Add File: ", StringComparison.Ordinal))
            {
                var path = line[14..].Trim();
                index++;
                var fileLines = new List<ToolDiffLineViewModel>();
                var newLineNumber = 1;
                while (index < lines.Length && !lines[index].StartsWith("*** ", StringComparison.Ordinal))
                {
                    if (lines[index].StartsWith('+'))
                    {
                        fileLines.Add(ToolDiffLineViewModel.Added(newLineNumber++, lines[index][1..]));
                    }

                    index++;
                }

                parsedFiles.Add(new ToolDiffFileViewModel(path, "Add", newLineNumber - 1, 0, fileLines));
                continue;
            }

            if (line.StartsWith("*** Delete File: ", StringComparison.Ordinal))
            {
                var path = line[17..].Trim();
                parsedFiles.Add(new ToolDiffFileViewModel(path, "Delete", 0, 0, [ToolDiffLineViewModel.Collapsed("File deleted")]));
                index++;
                continue;
            }

            if (line.StartsWith("*** Update File: ", StringComparison.Ordinal))
            {
                var path = line[17..].Trim();
                index++;
                var fileLines = new List<ToolDiffLineViewModel>();
                var oldLineNumber = 1;
                var newLineNumber = 1;
                var added = 0;
                var deleted = 0;

                while (index < lines.Length && !lines[index].StartsWith("*** ", StringComparison.Ordinal))
                {
                    var patchLine = lines[index];
                    if (patchLine.StartsWith("@@", StringComparison.Ordinal))
                    {
                        index++;
                        continue;
                    }

                    if (patchLine.StartsWith("\\", StringComparison.Ordinal))
                    {
                        index++;
                        continue;
                    }

                    if (patchLine.Length == 0)
                    {
                        fileLines.Add(ToolDiffLineViewModel.Context(oldLineNumber++, newLineNumber++, string.Empty));
                    }
                    else if (patchLine[0] == '-')
                    {
                        fileLines.Add(ToolDiffLineViewModel.Deleted(oldLineNumber++, patchLine[1..]));
                        deleted++;
                    }
                    else if (patchLine[0] == '+')
                    {
                        fileLines.Add(ToolDiffLineViewModel.Added(newLineNumber++, patchLine[1..]));
                        added++;
                    }
                    else if (patchLine[0] == ' ')
                    {
                        fileLines.Add(ToolDiffLineViewModel.Context(oldLineNumber++, newLineNumber++, patchLine[1..]));
                    }

                    index++;
                }

                parsedFiles.Add(new ToolDiffFileViewModel(path, "Update", added, deleted, fileLines));
                continue;
            }

            return false;
        }

        return false;
    }

    private static IReadOnlyList<ToolDiffLineViewModel> CollapseContextLines(IReadOnlyList<ToolDiffLineViewModel> lines)
    {
        var collapsed = new List<ToolDiffLineViewModel>(lines.Count);
        for (var index = 0; index < lines.Count;)
        {
            if (!lines[index].IsContext)
            {
                collapsed.Add(lines[index++]);
                continue;
            }

            var start = index;
            while (index < lines.Count && lines[index].IsContext)
            {
                index++;
            }

            var length = index - start;
            if (length <= CollapsibleContextRunThreshold)
            {
                collapsed.AddRange(lines.Skip(start).Take(length));
                continue;
            }

            collapsed.AddRange(lines.Skip(start).Take(PreservedContextLinesAtEdge));
            collapsed.Add(ToolDiffLineViewModel.Collapsed($"{FormatCount(length - (PreservedContextLinesAtEdge * 2), "unmodified line")}"));
            collapsed.AddRange(lines.Skip(index - PreservedContextLinesAtEdge).Take(PreservedContextLinesAtEdge));
        }

        return collapsed;
    }

    private static IReadOnlyList<ToolDiffLineViewModel> TrimLongDiff(IReadOnlyList<ToolDiffLineViewModel> lines)
    {
        if (lines.Count <= MaxRenderedLinesPerFile)
        {
            return lines;
        }

        return lines.Take(MaxRenderedLinesPerFile)
            .Append(ToolDiffLineViewModel.Collapsed($"{FormatCount(lines.Count - MaxRenderedLinesPerFile, "more diff line")}"))
            .ToArray();
    }

    private static bool TryGetObject(string json, out JsonElement root)
    {
        root = default;
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            root = document.RootElement.Clone();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetString(JsonElement root, string propertyName, out string value)
    {
        value = string.Empty;
        if (!root.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString() ?? string.Empty;
        return true;
    }

    private static string[] SplitLines(string value)
        => value.Length == 0 ? [] : value.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

    private static string NormalizeFailureMessage(string? resultSummary, string? outputText, string fallback)
    {
        var message = FirstNonBlank(resultSummary, StripMarkdownFailure(outputText), fallback) ?? fallback;
        message = message.Trim();
        if (message.StartsWith("Patch hunk ", StringComparison.OrdinalIgnoreCase))
        {
            message = message[6..];
        }
        else if (message.Equals("oldString was not found.", StringComparison.OrdinalIgnoreCase))
        {
            message = "old text not found";
        }

        return message.EndsWith(".", StringComparison.Ordinal) ? message[..^1] : message;
    }

    private static string? StripMarkdownFailure(string? outputText)
    {
        if (string.IsNullOrWhiteSpace(outputText))
        {
            return null;
        }

        var lines = outputText.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !line.StartsWith("### ", StringComparison.Ordinal))
            .ToArray();
        return lines.FirstOrDefault();
    }

    private static string? FirstNonBlank(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static string FormatPathForHeader(string path)
    {
        var parts = path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length >= 2 ? string.Join('/', parts.TakeLast(2)) : path;
    }

    private static string FormatChangeCounts(int added, int deleted)
    {
        if (added <= 0 && deleted <= 0)
        {
            return string.Empty;
        }

        return $"(+{added} -{deleted})";
    }

    private static string FormatCount(int count, string noun)
        => count == 1 ? $"1 {noun}" : $"{count} {noun}s";
}

public sealed record ToolDiffFileViewModel(
    string Path,
    string OperationText,
    int AddedLineCount,
    int DeletedLineCount,
    IReadOnlyList<ToolDiffLineViewModel> Lines)
{
    public bool HasAddedLines => AddedLineCount > 0;

    public bool HasDeletedLines => DeletedLineCount > 0;

    public string AddedLineCountText => $"+{AddedLineCount}";

    public string DeletedLineCountText => $"-{DeletedLineCount}";
}

public sealed class ToolDiffLineViewModel
{
    private ToolDiffLineViewModel(string lineNumberText, string oldLineNumberText, string newLineNumberText, string text, ToolDiffLineKind kind)
    {
        LineNumberText = lineNumberText;
        OldLineNumberText = oldLineNumberText;
        NewLineNumberText = newLineNumberText;
        MarkerText = string.Empty;
        Text = text;
        Kind = kind;
    }

    public string LineNumberText { get; }

    public string OldLineNumberText { get; }

    public string NewLineNumberText { get; }

    public string MarkerText { get; }

    public string Text { get; }

    public ToolDiffLineKind Kind { get; }

    public bool IsAdded => Kind == ToolDiffLineKind.Added;

    public bool IsDeleted => Kind == ToolDiffLineKind.Deleted;

    public bool IsContext => Kind == ToolDiffLineKind.Context;

    public bool IsHunk => Kind == ToolDiffLineKind.Hunk;

    public bool IsCollapsed => Kind == ToolDiffLineKind.Collapsed;

    public static ToolDiffLineViewModel Added(int newLineNumber, string text)
        => new(FormatLineNumber(newLineNumber), string.Empty, FormatLineNumber(newLineNumber), text, ToolDiffLineKind.Added);

    public static ToolDiffLineViewModel Deleted(int oldLineNumber, string text)
        => new(FormatLineNumber(oldLineNumber), FormatLineNumber(oldLineNumber), string.Empty, text, ToolDiffLineKind.Deleted);

    public static ToolDiffLineViewModel Context(int oldLineNumber, int newLineNumber, string text)
        => new(FormatLineNumber(newLineNumber), FormatLineNumber(oldLineNumber), FormatLineNumber(newLineNumber), text, ToolDiffLineKind.Context);

    public static ToolDiffLineViewModel Hunk(string text)
        => new(string.Empty, string.Empty, string.Empty, text, ToolDiffLineKind.Hunk);

    public static ToolDiffLineViewModel Collapsed(string text)
        => new(string.Empty, string.Empty, string.Empty, text, ToolDiffLineKind.Collapsed);

    private static string FormatLineNumber(int lineNumber)
        => lineNumber > 0 ? lineNumber.ToString() : string.Empty;
}

public sealed record FileDiffPresentationPayload(string? Schema, IReadOnlyList<FileDiffPayloadFile> Files);

public sealed record FileDiffPayloadFile(string Path, string? Operation, int AddedLineCount, int DeletedLineCount, IReadOnlyList<FileDiffPayloadLine> Lines);

public sealed record FileDiffPayloadLine(string? Kind, int? LineNumber, string Text);

public enum ToolDiffLineKind
{
    Context,
    Added,
    Deleted,
    Hunk,
    Collapsed,
}
