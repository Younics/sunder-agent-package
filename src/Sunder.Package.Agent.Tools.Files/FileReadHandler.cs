using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;

namespace Sunder.Package.Agent.Tools.Files;

internal static class FileReadHandler
{
    public static async Task<FileReadToolResult> ExecuteAsync(
        IAgentExecutionTarget target,
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

        var result = await target.ReadFileAsync(
            context,
            new AgentFileReadRequest(args.Path, args.EffectiveOffset, args.EffectiveLimit),
            cancellationToken);
        if (result.IsError)
        {
            return new FileReadToolResult(FileToolResult.ReadError(request.ToolId, result), IsDirectory: false);
        }

        if (result.IsDirectory)
        {
            return new FileReadToolResult(
                new AgentToolResult(
                    request.ToolId,
                    $"Read {result.Path}",
                    Content: result.Content,
                    WasTruncated: result.WasTruncated,
                    BackendId: FileToolResult.BackendId(target)),
                IsDirectory: true);
        }

        var legacyTruncated = false;
        var supportsRangedRead = AgentExecutionTargetRpc.SupportsFacet(
            target,
            AgentExecutionFacetIds.RangedFileRead);
        var content = supportsRangedRead
            ? NumberLines(result.Content, args.EffectiveOffset)
            : SliceAndNumberLegacyResult(result.Content, args.EffectiveOffset, args.EffectiveLimit, out legacyTruncated);
        return new FileReadToolResult(
            new AgentToolResult(
                request.ToolId,
                $"Read {result.Path}",
                Content: content,
                WasTruncated: result.WasTruncated || (!supportsRangedRead && legacyTruncated),
                BackendId: FileToolResult.BackendId(target)),
            IsDirectory: false);
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

internal sealed record FileReadToolResult(AgentToolResult Result, bool IsDirectory);
