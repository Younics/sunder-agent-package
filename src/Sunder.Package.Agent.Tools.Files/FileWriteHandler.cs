using Sunder.Agent.Execution.Common;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Tools.Files;

internal static class FileWriteHandler
{
    public static async Task<AgentToolResult> ExecuteWriteAsync(
        FileExecutionTarget target,
        AgentExecutionTargetContext context,
        AgentToolRequest request,
        CancellationToken cancellationToken)
    {
        if (!FileToolArguments.TryParseWrite(request.ArgumentsJson, out var args, out var error))
        {
            return FileToolResult.Error(request.ToolId, error!, "files-arguments-invalid");
        }

        var result = await WriteAsync(target, context, args.Path, args.Content, overwrite: true, cancellationToken);
        return ToToolResult(request.ToolId, target, result);
    }

    public static async Task<AgentToolResult> ExecuteEditAsync(
        FileExecutionTarget target,
        AgentExecutionTargetContext context,
        AgentToolRequest request,
        CancellationToken cancellationToken)
    {
        if (!FileToolArguments.TryParseEdit(request.ArgumentsJson, out var args, out var error))
        {
            return FileToolResult.Error(request.ToolId, error!, "files-arguments-invalid");
        }

        var current = await target.Service.ReadFileAsync(new() { Context = context, Request = new() { Path = args.Path, Offset = null, Limit = null } }, cancellationToken);
        if (current.IsError)
        {
            return FileToolResult.ReadError(request.ToolId, current);
        }

        if (current.IsDirectory)
        {
            return FileToolResult.Error(request.ToolId, "The edit target must be a file.", "edit-target-not-file");
        }

        var next = args.ReplaceAll
            ? current.Content.Replace(args.OldString, args.NewString, StringComparison.Ordinal)
            : ReplaceOnce(current.Content, args.OldString, args.NewString);
        if (string.Equals(current.Content, next, StringComparison.Ordinal))
        {
            return FileToolResult.Error(request.ToolId, "oldString was not found.", "edit-old-string-not-found");
        }

        var mutationResource = await target.Service.ResolveFileAsync(new() { Context = context, Path = args.Path }, cancellationToken);
        var mutationContext = context with
        {
            ApprovedResourceReferences = [mutationResource.CanonicalReference],
        };
        var result = await WriteAsync(
            target,
            mutationContext,
            args.Path,
            next,
            overwrite: true,
            cancellationToken,
            FileOperation.ComputeContentHash(current.Content));
        return ToToolResult(request.ToolId, target, result) with
        { PresentationPayloadJson = result.IsError ? null : FileDiffPresentation.BuildEditPayload(args, current.Content) };
    }

    public static ValueTask<AgentFileMutationResult> WriteAsync(
        FileExecutionTarget target,
        AgentExecutionTargetContext context,
        string path,
        string content,
        bool overwrite,
        CancellationToken cancellationToken,
        string? expectedContentHash = null)
        => target.Service.WriteFileAsync(new()
        { Context = context, Request = new() { Path = path, Content = content, Overwrite = overwrite, ExpectedContentHash = expectedContentHash } }, cancellationToken);

    private static AgentToolResult ToToolResult(string toolId, FileExecutionTarget target, AgentFileMutationResult result)
        => FileToolResult.Success(toolId, result.Summary, result.Summary, FileToolResult.BackendId(target)) with
        { IsError = result.IsError, ErrorCode = result.ErrorCode };

    private static string ReplaceOnce(string current, string oldString, string newString)
    {
        var index = current.IndexOf(oldString, StringComparison.Ordinal);
        return index < 0 ? current : current[..index] + newString + current[(index + oldString.Length)..];
    }
}
