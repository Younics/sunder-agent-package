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
        var fileCount = operations.Select(operation => operation.Path)
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
        var hunks = new List<FilePatchHunk>();
        while (index < lines.Count && !lines[index].StartsWith("*** ", StringComparison.Ordinal))
        {
            if (!lines[index].StartsWith("@@", StringComparison.Ordinal))
            {
                index++;
                continue;
            }

            index++;
            var oldLines = new List<string>();
            var newLines = new List<string>();
            while (index < lines.Count
                   && !lines[index].StartsWith("@@", StringComparison.Ordinal)
                   && !lines[index].StartsWith("*** ", StringComparison.Ordinal))
            {
                ParseHunkLine(lines[index], oldLines, newLines);
                index++;
            }

            hunks.Add(new FilePatchHunk(string.Join('\n', oldLines), string.Join('\n', newLines)));
        }

        if (hunks.Count == 0)
        {
            throw new InvalidOperationException($"Update File must contain at least one hunk: {path}");
        }

        return new FilePatchOperation(FilePatchOperationKind.Update, path, null, hunks);
    }

    private static void ParseHunkLine(string line, ICollection<string> oldLines, ICollection<string> newLines)
    {
        if (line.StartsWith("\\", StringComparison.Ordinal))
        {
            return;
        }

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
