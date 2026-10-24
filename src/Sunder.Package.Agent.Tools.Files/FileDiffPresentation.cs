using System.Text.Json;

namespace Sunder.Package.Agent.Tools.Files;

internal static class FileDiffPresentation
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static string BuildPayload(IReadOnlyList<FileDiffPayloadFile> files)
        => JsonSerializer.Serialize(new FileDiffPresentationPayload("younics.ai.agent.tools.files.diff.v1", files), JsonOptions);

    public static string? BuildEditPayload(FileEditArgs args, string currentContent)
        => BuildEditPayload(args.Path, FileTextEditPlanner.Edit(currentContent, args.OldString, args.NewString, args.ReplaceAll));

    public static string? BuildEditPayload(string path, FileTextEditPlan plan)
        => BuildPayload([EditedFile(path, plan)]);

    public static FileDiffPayloadFile EditedFile(string path, FileTextEditPlan plan)
    {
        var lines = new List<FileDiffPayloadLine>();
        foreach (var replacement in plan.Replacements)
        {
            AppendReplacementLines(lines, replacement.OldText, replacement.NewText, replacement.StartLine);
        }

        return new FileDiffPayloadFile(
                path,
                "Edit",
                lines.Count(line => line.Kind == "added"),
                lines.Count(line => line.Kind == "deleted"),
                lines);
    }

    public static string ApplyHunks(string content, IReadOnlyList<FilePatchHunk> hunks, out IReadOnlyList<FileDiffPayloadLine> diffLines, bool allowUnchanged = false)
    {
        var plan = FileTextEditPlanner.Patch(content, hunks, allowUnchanged);
        var lines = new List<FileDiffPayloadLine>();
        foreach (var replacement in plan.Replacements)
        {
            AppendReplacementLines(lines, replacement.OldText, replacement.NewText, replacement.StartLine);
        }

        diffLines = lines;
        return plan.Content;
    }

    public static FileDiffPayloadFile AddedFile(string path, string content)
    {
        var lines = SplitLines(content)
            .Select((line, index) => new FileDiffPayloadLine("added", index + 1, line))
            .ToArray();
        return new FileDiffPayloadFile(path, "Add", lines.Length, 0, lines);
    }

    public static FileDiffPayloadFile UpdatedFile(string path, IReadOnlyList<FileDiffPayloadLine> lines)
        => new(
            path,
            "Update",
            lines.Count(line => line.Kind == "added"),
            lines.Count(line => line.Kind == "deleted"),
            lines);

    public static FileDiffPayloadFile ReplacedFile(string path, string before, string after)
    {
        var lines = new List<FileDiffPayloadLine>();
        AppendReplacementLines(lines, before, after, 1);
        return UpdatedFile(path, lines);
    }

    public static FileDiffPayloadFile DeletedFile(string path)
        => new(path, "Delete", 0, 0, [new FileDiffPayloadLine("collapsed", null, "File deleted")]);

    private static void AppendReplacementLines(List<FileDiffPayloadLine> lines, string oldText, string newText, int startLine)
    {
        var oldLines = SplitLines(oldText);
        var newLines = SplitLines(newText);
        var prefixLength = 0;
        while (prefixLength < oldLines.Length
               && prefixLength < newLines.Length
               && string.Equals(oldLines[prefixLength], newLines[prefixLength], StringComparison.Ordinal))
        {
            lines.Add(new FileDiffPayloadLine("context", startLine + prefixLength, oldLines[prefixLength]));
            prefixLength++;
        }

        var suffixLength = 0;
        while (suffixLength < oldLines.Length - prefixLength
               && suffixLength < newLines.Length - prefixLength
               && string.Equals(oldLines[^(suffixLength + 1)], newLines[^(suffixLength + 1)], StringComparison.Ordinal))
        {
            suffixLength++;
        }

        var changedOldLength = oldLines.Length - prefixLength - suffixLength;
        var changedNewLength = newLines.Length - prefixLength - suffixLength;
        for (var index = 0; index < changedOldLength; index++)
        {
            var oldIndex = prefixLength + index;
            lines.Add(new FileDiffPayloadLine("deleted", startLine + oldIndex, oldLines[oldIndex]));
        }

        for (var index = 0; index < changedNewLength; index++)
        {
            lines.Add(new FileDiffPayloadLine("added", startLine + prefixLength + index, newLines[prefixLength + index]));
        }

        for (var index = 0; index < suffixLength; index++)
        {
            var oldIndex = oldLines.Length - suffixLength + index;
            lines.Add(new FileDiffPayloadLine("context", startLine + oldIndex, oldLines[oldIndex]));
        }
    }

    private static IReadOnlyList<int> FindMatchIndexes(string content, string oldString, bool replaceAll)
    {
        var matches = new List<int>();
        var startIndex = 0;
        while (startIndex <= content.Length)
        {
            var index = content.IndexOf(oldString, startIndex, StringComparison.Ordinal);
            if (index < 0)
            {
                break;
            }

            matches.Add(index);
            if (!replaceAll)
            {
                break;
            }

            startIndex = index + oldString.Length;
        }

        return matches;
    }

    private static int CountLinesBefore(string content, int index)
    {
        var count = 0;
        for (var position = 0; position < Math.Min(index, content.Length); position++)
        {
            if (content[position] == '\n')
            {
                count++;
            }
        }

        return count;
    }

    private static string[] SplitLines(string value)
        => value.Length == 0 ? [] : value.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
}
