namespace Sunder.Package.Agent.Tools.Files;

internal static class FilePatchParser
{
    internal const int MaximumOperations = 64;

    public static IReadOnlyList<FilePatchOperation> Parse(string patchText)
    {
        var lines = patchText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (lines.Length == 0 || !string.Equals(lines[0].Trim(), "*** Begin Patch", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Patch must start with '*** Begin Patch'.");
        }

        var operations = new List<FilePatchOperation>();
        var index = 1;
        while (index < lines.Length)
        {
            var line = lines[index];
            if (string.Equals(line.Trim(), "*** End Patch", StringComparison.Ordinal))
            {
                if (operations.Count == 0)
                {
                    throw new InvalidOperationException("Patch must contain at least one file operation.");
                }

                if (lines.Skip(index + 1).Any(trailing => !string.IsNullOrWhiteSpace(trailing)))
                    throw new InvalidOperationException("Unexpected content after '*** End Patch'.");
                return operations;
            }

            if (line.StartsWith("*** Add File: ", StringComparison.Ordinal))
            {
                operations.Add(ParseAdd(lines, ref index, line[14..].Trim()));
                ValidateOperationCount(operations.Count);
                continue;
            }

            if (line.StartsWith("*** Delete File: ", StringComparison.Ordinal))
            {
                var path = RequirePath(line[17..].Trim());
                operations.Add(new FilePatchOperation(FilePatchOperationKind.Delete, path, null, []));
                ValidateOperationCount(operations.Count);
                index++;
                continue;
            }

            if (line.StartsWith("*** Update File: ", StringComparison.Ordinal))
            {
                operations.Add(ParseUpdate(lines, ref index, line[17..].Trim()));
                ValidateOperationCount(operations.Count);
                continue;
            }

            throw new InvalidOperationException($"Unsupported patch line: {line}");
        }

        throw new InvalidOperationException("Patch must end with '*** End Patch'.");
    }

    public static string BuildSummary(IReadOnlyList<FilePatchOperation> operations, string verb = "Applied")
    {
        var fileCount = FilePatchPaths.Affected(operations).Select(path => path.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        return $"{verb} {FileToolResult.FormatCount(operations.Count, "patch operation")} to {FileToolResult.FormatCount(fileCount, "file")}";
    }

    private static FilePatchOperation ParseAdd(IReadOnlyList<string> lines, ref int index, string path)
    {
        path = RequirePath(path);
        index++;
        var contentLines = new List<string>();
        while (index < lines.Count && !lines[index].StartsWith("*** ", StringComparison.Ordinal))
        {
            if (!lines[index].StartsWith('+'))
            {
                throw new InvalidOperationException($"Add File lines must start with '+': {path}");
            }

            contentLines.Add(lines[index][1..]);
            index++;
        }

        return new FilePatchOperation(FilePatchOperationKind.Add, path, string.Join('\n', contentLines), []);
    }

    private static FilePatchOperation ParseUpdate(IReadOnlyList<string> lines, ref int index, string path)
    {
        path = RequirePath(path);
        index++;
        string? moveTo = null;
        if (index < lines.Count && lines[index].StartsWith("*** Move to: ", StringComparison.Ordinal))
        {
            moveTo = RequirePath(lines[index][13..].Trim());
            index++;
        }
        var hunks = new List<FilePatchHunk>();
        while (index < lines.Count && !lines[index].StartsWith("*** ", StringComparison.Ordinal))
        {
            if (!lines[index].StartsWith("@@", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Expected an @@ context marker for '{path}', got: {lines[index]}");
            }

            var context = lines[index][2..].Trim();
            index++;
            var oldLines = new List<string>();
            var newLines = new List<string>();
            var removeFinalNewline = false;
            var oldNoNewline = false;
            var previousKind = '\0';
            while (index < lines.Count
                   && !lines[index].StartsWith("@@", StringComparison.Ordinal)
                   && !lines[index].StartsWith("*** ", StringComparison.Ordinal))
            {
                if (lines[index] == "\\ No newline at end of file")
                {
                    if (previousKind is '+' or ' ') removeFinalNewline = true;
                    else if (previousKind != '-') throw new InvalidOperationException("A no-newline marker must follow a hunk line.");
                    if (previousKind is '-' or ' ') oldNoNewline = true;
                    previousKind = '\0';
                }
                else
                {
                    if (oldNoNewline && (lines[index].StartsWith('-') || lines[index].StartsWith(' '))
                        || removeFinalNewline && (lines[index].StartsWith('+') || lines[index].StartsWith(' ')))
                        throw new InvalidOperationException("A no-newline marker must terminate its side of the hunk.");
                    ParseHunkLine(lines[index], oldLines, newLines);
                    previousKind = lines[index].Length == 0 ? ' ' : lines[index][0];
                }
                index++;
            }

            if (oldLines.Count == 0 && newLines.Count == 0)
                throw new InvalidOperationException($"Update hunk is empty: {path}");
            var endOfFile = index < lines.Count && lines[index].TrimEnd() == "*** End of File";
            if (endOfFile) index++;
            hunks.Add(new FilePatchHunk(string.Join('\n', oldLines), string.Join('\n', newLines),
                context.Length == 0 ? null : context, endOfFile || removeFinalNewline || oldNoNewline,
                oldLines.Count == 0, newLines.Count == 0, removeFinalNewline, oldNoNewline && !removeFinalNewline));
        }

        if (hunks.Count == 0 && moveTo is null)
        {
            throw new InvalidOperationException($"Update File must contain at least one hunk: {path}");
        }

        return new FilePatchOperation(FilePatchOperationKind.Update, path, null, hunks) { MoveTo = moveTo };
    }

    private static void ParseHunkLine(string line, ICollection<string> oldLines, ICollection<string> newLines)
    {
        if (line.Length == 0)
        {
            oldLines.Add(string.Empty);
            newLines.Add(string.Empty);
            return;
        }

        switch (line[0])
        {
            case '-':
                oldLines.Add(line[1..]);
                break;
            case '+':
                newLines.Add(line[1..]);
                break;
            case ' ':
                oldLines.Add(line[1..]);
                newLines.Add(line[1..]);
                break;
            default:
                throw new InvalidOperationException($"Unsupported patch hunk line: {line}");
        }
    }

    private static string RequirePath(string path)
        => string.IsNullOrWhiteSpace(path)
            ? throw new InvalidOperationException("Patch file paths must not be empty.")
            : path;

    private static void ValidateOperationCount(int count)
    {
        if (count > MaximumOperations)
        {
            throw new FilePatchOperationLimitException(MaximumOperations);
        }
    }
}

internal sealed class FilePatchOperationLimitException(int maximumOperations)
    : InvalidOperationException($"Patches support at most {maximumOperations} file operations.");
