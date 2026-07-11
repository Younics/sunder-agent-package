using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Tools.Files;

internal static class FileReadHandler
{
    public static async Task<AgentToolResult> ExecuteAsync(
        IAgentExecutionTarget target,
        AgentExecutionTargetContext context,
        AgentToolRequest request,
        CancellationToken cancellationToken)
    {
        if (!FileToolArguments.TryParseRead(request.ArgumentsJson, out var args, out var error))
        {
            return FileToolResult.Error(request.ToolId, error!, "files-arguments-invalid");
        }

        var result = await target.ReadFileAsync(
            context,
            new AgentFileReadRequest(args.Path, args.EffectiveOffset, args.EffectiveLimit),
            cancellationToken);
        if (result.IsError)
        {
            return FileToolResult.ReadError(request.ToolId, result);
        }

        if (result.IsDirectory)
        {
            return new AgentToolResult(
                request.ToolId,
                $"Read {result.Path}",
                Content: result.Content,
                WasTruncated: result.WasTruncated,
                BackendId: FileToolResult.BackendId(target));
        }

        var legacyTruncated = false;
        var content = target is IAgentRangedFileExecutionTarget
            ? NumberLines(result.Content, args.EffectiveOffset)
            : SliceAndNumberLegacyResult(result.Content, args.EffectiveOffset, args.EffectiveLimit, out legacyTruncated);
        return new AgentToolResult(
            request.ToolId,
            $"Read {result.Path}",
            Content: content,
            WasTruncated: result.WasTruncated || (target is not IAgentRangedFileExecutionTarget && legacyTruncated),
            BackendId: FileToolResult.BackendId(target));
    }

    private static string SliceAndNumberLegacyResult(string content, int offset, int limit, out bool wasTruncated)
    {
        var lines = SplitLines(content);
        wasTruncated = offset - 1 + limit < lines.Length;
        return string.Join(
            Environment.NewLine,
            lines.Skip(offset - 1).Take(limit).Select((line, index) => $"{offset + index}: {line}"));
    }

    private static string NumberLines(string content, int offset)
        => content.Length == 0
            ? string.Empty
            : string.Join(
                Environment.NewLine,
                SplitLines(content).Select((line, index) => $"{offset + index}: {line}"));

    private static string[] SplitLines(string content)
        => content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
}
