using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using System.Text;

namespace Sunder.Package.Agent.Tools.Files;

internal static class FileReadHandler
{
    private const int MaximumPageBytes = 50 * 1024;
    private const int MaximumLineCharacters = 2000;
    public static async Task<FileReadToolResult> ExecuteAsync(
        FileExecutionTarget target,
        AgentExecutionTargetContext context,
        AgentToolRequest request,
        CancellationToken cancellationToken)
    {
        if (!FileToolArguments.TryParseRead(request.ArgumentsJson, out var args, out var error))
        {
            return new FileReadToolResult(
                FileToolResult.Error(request.ToolId, error!, "files-arguments-invalid"),
                IsDirectory: false);
        }

        var result = await target.Service.ReadFileAsync(new()
        { Context = context, Request = new() { Path = args.Path, Offset = args.EffectiveOffset, Limit = args.EffectiveLimit } }, cancellationToken);
        if (result.IsError)
        {
            return new FileReadToolResult(FileToolResult.ReadError(request.ToolId, result), IsDirectory: false);
        }

        var supportsRangedRead = FilesExecutionFacets.SupportsFacet(
            target,
            AgentExecutionFacetIds.RangedFileRead);
        var page = FormatPage(result, args, supportsRangedRead);
        if (page.RangeOutsideFile)
            return new FileReadToolResult(FileToolResult.Error(request.ToolId,
                $"Line offset {args.EffectiveOffset} is outside the file.", AgentFileReadErrorCodes.RangeOutsideFile), result.IsDirectory);
        return new FileReadToolResult(
            FileToolResult.Success(request.ToolId, $"Read {result.Path}", page.Content, FileToolResult.BackendId(target), page.WasTruncated),
            IsDirectory: result.IsDirectory);
    }

    // Bound model-visible output even for older or nonconforming execution targets.
    // This is a display page only, never a mutation snapshot.
    private static (string Content, bool WasTruncated, bool RangeOutsideFile) FormatPage(AgentFileReadResult result, FileReadArgs args, bool ranged)
    {
        using var reader = new StringReader(result.Content);
        if (!ranged)
        {
            for (var index = 1; index < args.EffectiveOffset; index++)
                if (reader.ReadLine() is null)
                    return result.WasTruncated
                        ? ("[Read incomplete; the legacy target did not return the requested range. Narrow the range or use grep; do not infer omitted content.]", true, false)
                        : (string.Empty, false, true);
            if (args.EffectiveOffset > 1 && reader.Peek() < 0 && !result.WasTruncated)
                return (string.Empty, false, true);
        }
        var content = new StringBuilder();
        var count = 0;
        var bytes = 0;
        var shortened = false;
        var bounded = false;
        while (count < args.EffectiveLimit)
        {
            var line = reader.ReadLine();
            // Ranged payloads join selected lines without a final separator.
            // Metadata distinguishes a final empty selected line from no line.
            if (line is null)
            {
                if (!ranged || result.EndLine is null || result.EndLine < (long)args.EffectiveOffset + count) break;
                line = string.Empty;
            }
            if (line.Length > MaximumLineCharacters)
            {
                var length = MaximumLineCharacters;
                if (char.IsHighSurrogate(line[length - 1])) length--;
                line = line[..length] + " [line shortened; use grep]";
                shortened = true;
            }
            var rendered = result.IsDirectory ? line : $"{(long)args.EffectiveOffset + count}: {line}";
            var lineBytes = Encoding.UTF8.GetByteCount(rendered) + Environment.NewLine.Length;
            if (bytes + lineBytes > MaximumPageBytes - 512) { bounded = true; break; }
            if (count > 0) content.AppendLine();
            content.Append(rendered);
            bytes += lineBytes;
            count++;
        }
        var more = bounded || reader.Peek() >= 0;
        var end = (long)args.EffectiveOffset + count - 1;
        var incomplete = result.WasTruncated || more || shortened;
        if (incomplete)
        {
            if (content.Length > 0) content.AppendLine();
            var noun = result.IsDirectory ? "entries" : "lines";
            content.Append($"[Read incomplete; shown {count} {noun} starting at {args.EffectiveOffset}");
            if (result.TotalLines is { } total) content.Append($" of {total}");
            content.Append(". ");
            if (count > 0 && (more || result.TotalLines > end || result.TotalLines is null && count >= args.EffectiveLimit))
                content.Append($"Continue with offset={end + 1} and limit={args.EffectiveLimit}. ");
            content.Append("Output is bounded to 50 KiB; shortened lines are not complete text. Narrow the range or use grep; do not infer omitted content.]");
        }
        return (content.ToString(), incomplete, false);
    }
}

internal sealed record FileReadToolResult(AgentToolResult Result, bool IsDirectory);
