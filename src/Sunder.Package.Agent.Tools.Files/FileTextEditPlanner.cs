using System.Text;

namespace Sunder.Package.Agent.Tools.Files;

internal sealed record FileTextReplacement(int StartLine, string OldText, string NewText);
internal sealed record FileTextEditPlan(string Content, IReadOnlyList<FileTextReplacement> Replacements);

// Pure planning only. Authorization, exact target identity, CAS and publication
// remain owned by the execution target. Never mutate a second, normalized copy.
internal static class FileTextEditPlanner
{
    private const int MaximumPlannedBytes = 4 * 1024 * 1024;
    internal static FileTextEditPlan Edit(string source, string oldText, string newText, bool replaceAll = false)
        => Replace(source, oldText, newText, replaceAll, wholeLines: false, startAt: 0, endOfFile: false).Plan;

    internal static FileTextEditPlan Patch(string source, IReadOnlyList<FilePatchHunk> hunks, bool allowUnchanged = false)
    {
        CheckSize(source);
        var next = source;
        var replacements = new List<FileTextReplacement>();
        var cursor = 0;
        foreach (var hunk in hunks)
        {
            if (hunk.Context is { Length: > 0 } context)
            {
                var view = View.Create(next);
                var anchors = Find(view.Text, Normalize(context), wholeLines: true, cursor, endOfFile: false);
                if (anchors.Count != 1) throw new InvalidOperationException("Patch context must identify exactly one line after the previous hunk.");
                cursor = anchors[0] + Normalize(context).Length;
                if (cursor < view.Text.Length && view.Text[cursor] == '\n') cursor++;
            }
            if (hunk.IsInsertion)
            {
                if (!hunk.EndOfFile && hunk.Context is null)
                    throw new InvalidOperationException("An insertion-only hunk requires an @@ context anchor or *** End of File.");
                var view = View.Create(next);
                var offset = hunk.EndOfFile ? view.Text.Length : cursor;
                var sourceOffset = view.Offsets[offset];
                var ending = DominantEnding(next);
                var text = Normalize(hunk.NewText).Replace("\n", ending, StringComparison.Ordinal);
                if (sourceOffset > view.Offsets[0] && next[sourceOffset - 1] is not ('\r' or '\n')) text = ending + text;
                if (!hunk.RemoveFinalNewline && (hunk.AddFinalNewline || sourceOffset < next.Length || next.EndsWith('\n'))) text += ending;
                next = next[..sourceOffset] + text + next[sourceOffset..];
                CheckSize(next);
                replacements.Add(new FileTextReplacement(LineAt(view.Text, offset), string.Empty, text));
                cursor = offset + Normalize(text).Length;
                continue;
            }
            var result = Replace(next, hunk.OldText, hunk.NewText, replaceAll: false,
                wholeLines: true, cursor, hunk.EndOfFile, hunk.IsDeletion,
                hunk.RemoveFinalNewline ? false : hunk.AddFinalNewline ? true : null, allowUnchanged);
            next = result.Plan.Content;
            cursor = result.NextOffset;
            replacements.AddRange(result.Plan.Replacements);
        }
        if (next == source && !allowUnchanged) throw new InvalidOperationException("The patch makes no changes.");
        CheckSize(next);
        return new FileTextEditPlan(next, replacements);
    }

    private static (FileTextEditPlan Plan, int NextOffset) Replace(
        string source, string oldText, string newText, bool replaceAll, bool wholeLines, int startAt, bool endOfFile, bool deleteLine = false,
        bool? finalNewline = null, bool allowUnchanged = false)
    {
        CheckSize(source);
        CheckSize(newText);
        if (oldText == newText && !deleteLine && finalNewline is null && !allowUnchanged) throw new InvalidOperationException("The replacement makes no changes.");
        var endingsOnly = !wholeLines && oldText != newText && oldText.Replace("\r", string.Empty, StringComparison.Ordinal) == newText.Replace("\r", string.Empty, StringComparison.Ordinal);
        var view = endingsOnly ? View.Identity(source) : View.Create(source);
        var search = endingsOnly ? oldText : Normalize(oldText);
        var replacement = endingsOnly ? newText : Normalize(newText);
        var matches = Find(view.Text, search, wholeLines, startAt, endOfFile, nonOverlapping: replaceAll);
        if (matches.Count == 0) throw new InvalidOperationException("The expected text was not found. Read the current file and include exact surrounding context.");
        if (!replaceAll && matches.Count != 1)
            throw new InvalidOperationException($"The expected text matches {matches.Count} locations (lines {string.Join(", ", matches.Take(5).Select(index => LineAt(view.Text, index)))}). Include more context, or use replaceAll only when every occurrence should change.");
        var changes = new List<FileTextReplacement>(matches.Count);
        var result = new StringBuilder(source.Length);
        var previousEnd = 0;
        var nextOffset = 0;
        var previousViewOffset = 0;
        var lineNumber = 1;
        var fallbackEnding = DominantEnding(source);
        foreach (var index in matches)
        {
            var start = view.Offsets[index];
            var end = view.Offsets[index + search.Length];
            var text = endingsOnly ? replacement : PreserveEndings(source[start..end], replacement, fallbackEnding);
            if (finalNewline is not null && !deleteLine)
            {
                if (end < source.Length && source[end] == '\r') end++;
                if (end < source.Length && source[end] == '\n') end++;
                if (finalNewline == true) text += fallbackEnding;
            }
            if (deleteLine)
            {
                if (end < source.Length && source[end] == '\r') end++;
                if (end < source.Length && source[end] == '\n') end++;
                else if (end == source.Length && start > view.Offsets[0] && source[start - 1] == '\n')
                {
                    start--;
                    if (start > view.Offsets[0] && source[start - 1] == '\r') start--;
                }
                text = string.Empty;
            }
            if (start < previousEnd) throw new InvalidOperationException("Patch edits overlap.");
            result.Append(source.AsSpan(previousEnd, start - previousEnd));
            result.Append(text);
            if (result.Length > MaximumPlannedBytes) throw new InvalidOperationException("The planned file exceeds the complete-snapshot mutation limit (4 MiB). No changes were applied.");
            lineNumber += view.Text.AsSpan(previousViewOffset, index - previousViewOffset).Count('\n');
            changes.Add(new FileTextReplacement(lineNumber, source[start..end], text));
            previousViewOffset = index;
            nextOffset += NormalizedLength(source.AsSpan(previousEnd, start - previousEnd)) + NormalizedLength(text);
            previousEnd = end;
        }
        result.Append(source.AsSpan(previousEnd));
        var content = result.ToString();
        if (content == source && !allowUnchanged) throw new InvalidOperationException("The replacement makes no changes.");
        CheckSize(content);
        return (new FileTextEditPlan(content, changes), Math.Max(0, nextOffset - (content.StartsWith('\uFEFF') ? 1 : 0)));
    }

    private static List<int> Find(string text, string search, bool wholeLines, int startAt, bool endOfFile, bool nonOverlapping = false)
    {
        var matches = new List<int>();
        var start = Math.Min(startAt, text.Length);
        while (start <= text.Length)
        {
            var index = search.Length == 0 ? text.IndexOf('\n', start) : text.IndexOf(search, start, StringComparison.Ordinal);
            if (index < 0) break;
            var end = index + search.Length;
            if ((!wholeLines || (index == 0 || text[index - 1] == '\n') && (end == text.Length || text[end] == '\n'))
                && (!endOfFile || end == text.Length || end == text.Length - 1 && text[end] == '\n'))
                matches.Add(index);
            if (matches.Count > 65536) throw new InvalidOperationException("The edit exceeds the 65,536 replacement planning bound. Narrow the edit.");
            start = index + (nonOverlapping ? Math.Max(1, search.Length) : 1);
        }
        return matches;
    }

    private static string PreserveEndings(string region, string replacement, string fallback)
    {
        var oldLines = Normalize(region).Split('\n');
        var newLines = replacement.Split('\n');
        var endings = new List<string>();
        for (var index = 0; index < region.Length; index++)
            if (region[index] == '\n') endings.Add(index > 0 && region[index - 1] == '\r' ? "\r\n" : "\n");
        var positions = oldLines.Select((line, index) => (line, index)).GroupBy(item => item.line, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(item => item.index).ToArray(), StringComparer.Ordinal);
        var last = -1;
        var builder = new StringBuilder(replacement.Length);
        for (var index = 0; index < newLines.Length; index++)
        {
            builder.Append(newLines[index]);
            if (index == newLines.Length - 1) break;
            var aligned = index;
            if (positions.TryGetValue(newLines[index], out var candidates))
            {
                var candidate = Array.BinarySearch(candidates, last + 1);
                if (candidate < 0) candidate = ~candidate;
                if (candidate < candidates.Length) { aligned = candidates[candidate]; last = aligned; }
            }
            builder.Append(aligned < endings.Count ? endings[aligned] : fallback);
        }
        return builder.ToString();
    }

    private static string DominantEnding(string text)
    {
        var crlf = 0;
        var lf = 0;
        for (var index = 0; index < text.Length; index++)
            if (text[index] == '\n') { if (index > 0 && text[index - 1] == '\r') crlf++; else lf++; }
        return crlf > lf ? "\r\n" : "\n";
    }

    private static int LineAt(string text, int offset) => 1 + text.AsSpan(0, Math.Min(offset, text.Length)).Count('\n');
    private static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static int NormalizedLength(ReadOnlySpan<char> text)
    {
        var length = text.Length;
        for (var index = 1; index < text.Length; index++) if (text[index - 1] == '\r' && text[index] == '\n') length--;
        return length;
    }

    private static void CheckSize(string text)
    {
        if (Encoding.UTF8.GetByteCount(text) > MaximumPlannedBytes)
            throw new InvalidOperationException("The planned file exceeds the complete-snapshot mutation limit (4 MiB). No changes were applied.");
    }

    private sealed record View(string Text, int[] Offsets)
    {
        internal static View Identity(string source) => new(source, Enumerable.Range(0, source.Length + 1).ToArray());
        internal static View Create(string source)
        {
            var text = new StringBuilder(source.Length);
            var offsets = new List<int>(source.Length + 1);
            for (var index = source.StartsWith('\uFEFF') ? 1 : 0; index < source.Length; index++)
            {
                offsets.Add(index);
                if (source[index] == '\r' && index + 1 < source.Length && source[index + 1] == '\n') index++;
                text.Append(source[index]);
            }
            offsets.Add(source.Length);
            return new View(text.ToString(), offsets.ToArray());
        }
    }
}
