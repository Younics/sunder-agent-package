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

        var resource = await target.Service.ResolveFileAsync(new() { Context = context, Path = args.Path }, cancellationToken);
        string? expectedHash = null;
        string? previous = null;
        if (resource.Exists)
        {
            var snapshot = await target.Service.ReadFileAsync(new() { Context = context, Request = new() { Path = args.Path, Offset = null, Limit = null } }, cancellationToken);
            if (snapshot.IsError) return FileToolResult.ReadError(request.ToolId, snapshot);
            if (snapshot.IsDirectory || snapshot.WasTruncated)
                return FileToolResult.Error(request.ToolId, "Overwriting requires a complete regular text-file snapshot. No mutation was attempted.", "write-incomplete-snapshot");
            previous = snapshot.Content;
            expectedHash = FileOperation.ComputeContentHash(previous);
        }
        var result = await WriteAsync(target, context, args.Path, args.Content, overwrite: resource.Exists, cancellationToken, expectedHash);
        return ToToolResult(request.ToolId, target, result) with
        {
            PresentationPayloadJson = result.IsError ? null : FileDiffPresentation.BuildPayload([
                previous is null ? FileDiffPresentation.AddedFile(args.Path, args.Content)
                    : FileDiffPresentation.ReplacedFile(args.Path, previous, args.Content)]),
        };
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

        if (current.WasTruncated)
            return FileToolResult.Error(request.ToolId, "The full edit target could not be read. No mutation was attempted.", "edit-incomplete-snapshot");
        FileTextEditPlan plan;
        try { plan = FileTextEditPlanner.Edit(current.Content, args.OldString, args.NewString, args.ReplaceAll); }
        catch (InvalidOperationException exception)
        {
            return FileToolResult.Error(request.ToolId, exception.Message, "edit-match-invalid");
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
            plan.Content,
            overwrite: true,
            cancellationToken,
            FileOperation.ComputeContentHash(current.Content));
        var outcome = ToToolResult(request.ToolId, target, result);
        if (result.IsError) return outcome;
        var diff = FileDiffPresentation.EditedFile(args.Path, plan);
        var summary = $"Edited {args.Path}: {plan.Replacements.Count} replacement(s), +{diff.AddedLineCount}/-{diff.DeletedLineCount} lines.";
        return outcome with
        {
            Summary = summary,
            Content = summary,
            PresentationPayloadJson = FileDiffPresentation.BuildPayload([diff]),
        };
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

}
