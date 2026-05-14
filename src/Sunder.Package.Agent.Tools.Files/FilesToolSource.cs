using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Tools.Files;

public sealed partial class FilesToolSource(IPackageExtensionCatalog extensionCatalog)
    : IAgentToolSource, IAgentPermissionAwareToolSource, IAgentPermissionSurface, IAgentSystemPromptContributor, IAgentToolPresentationResolver
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static readonly AgentToolDescriptor[] Descriptors =
    [
        new("read", "Read File", "Read a file or directory from the selected workspace.", IsReadOnly: true, ArgumentsJsonSchema: ReadSchema, SourceKind: "workspace", SourceId: "files", SourceDisplayName: "Workspace Files", RuntimeInstructions: ReadInstructions, Priority: AgentToolPriority.Medium),
        new("write", "Write File", "Create or overwrite a file in the selected workspace.", IsReadOnly: false, ArgumentsJsonSchema: WriteSchema, SourceKind: "workspace", SourceId: "files", SourceDisplayName: "Workspace Files", RuntimeInstructions: WriteInstructions, Priority: AgentToolPriority.Medium),
        new("edit", "Edit File", "Modify an existing file in the selected workspace using exact string replacement.", IsReadOnly: false, ArgumentsJsonSchema: EditSchema, SourceKind: "workspace", SourceId: "files", SourceDisplayName: "Workspace Files", RuntimeInstructions: EditInstructions, Priority: AgentToolPriority.Medium),
        new("apply_patch", "Apply Patch", "Apply a structured patch to files in the selected workspace.", IsReadOnly: false, ArgumentsJsonSchema: ApplyPatchSchema, SourceKind: "workspace", SourceId: "files", SourceDisplayName: "Workspace Files", RuntimeInstructions: ApplyPatchInstructions, Priority: AgentToolPriority.Medium),
        new("grep", "Grep", "Search file contents in the selected workspace using regular expressions.", IsReadOnly: true, ArgumentsJsonSchema: GrepSchema, SourceKind: "workspace", SourceId: "files", SourceDisplayName: "Workspace Files", RuntimeInstructions: GrepInstructions, Priority: AgentToolPriority.Medium),
        new("glob", "Glob", "Find files in the selected workspace by glob pattern.", IsReadOnly: true, ArgumentsJsonSchema: GlobSchema, SourceKind: "workspace", SourceId: "files", SourceDisplayName: "Workspace Files", RuntimeInstructions: GlobInstructions, Priority: AgentToolPriority.Medium),
    ];

    public string SourceId => "workspace-files";

    public string DisplayName => "Workspace Files";

    public string SourceKind => "workspace";

    public string SurfaceId => "files";

    public string ContributorId => "workspace-files";

    public AgentToolPresentation? ResolveToolPresentation(AgentToolPresentationRequest request)
        => request.ToolId.ToLowerInvariant() switch
        {
            "read" => ResolveReadPresentation(request),
            "write" => ResolveWritePresentation(request),
            "edit" => ResolveEditPresentation(request),
            "glob" => ResolveGlobPresentation(request),
            "grep" => ResolveGrepPresentation(request),
            "apply_patch" => ResolveApplyPatchPresentation(request),
            _ => null,
        };

    public ValueTask<IReadOnlyList<AgentToolDescriptor>> ListToolsAsync(
        AgentToolSourceContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IReadOnlyList<AgentToolDescriptor>>(Descriptors);
    }

    public async ValueTask<AgentToolReadiness?> GetReadinessAsync(
        string toolId,
        AgentToolSourceContext context,
        CancellationToken cancellationToken = default)
    {
        if (Descriptors.All(descriptor => !string.Equals(descriptor.ToolId, toolId, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        if (context.Workspace is null)
        {
            return new AgentToolReadiness(toolId, AgentToolReadinessStatus.Failed, "File tools require a selected workspace.");
        }

        var target = ResolveTarget(context.ExecutionBinding);
        if (target is null || context.ExecutionBinding is null)
        {
            return new AgentToolReadiness(toolId, AgentToolReadinessStatus.Failed, "The selected workspace is not bound to an installed execution target.");
        }

        var readiness = await target.GetReadinessAsync(new AgentExecutionTargetContext(context.SessionId, context.Profile?.ProfileId, context.Workspace, context.ExecutionBinding), cancellationToken);
        return readiness.Status == AgentExecutionTargetReadinessStatus.Ready && target.Descriptor.SupportsFiles
            ? new AgentToolReadiness(toolId, AgentToolReadinessStatus.Ready, "Workspace file tools are ready.")
            : new AgentToolReadiness(toolId, AgentToolReadinessStatus.Failed, readiness.Message);
    }

    public async ValueTask<AgentToolResult> ExecuteAsync(
        AgentToolExecutionContext context,
        AgentToolRequest request,
        CancellationToken cancellationToken = default)
    {
        if (context.Workspace is null)
        {
            return Error(request.ToolId, "File tools require a selected workspace.", "files-workspace-required");
        }

        var target = ResolveTarget(context.ExecutionBinding);
        if (target is null || context.ExecutionBinding is null)
        {
            return Error(request.ToolId, "The selected workspace is not bound to an installed execution target.", "files-target-required");
        }

        var targetContext = new AgentExecutionTargetContext(context.SessionId, context.ProfileId, context.Workspace, context.ExecutionBinding, context.AllowOutsideConfiguredScope);
        try
        {
            return request.ToolId.ToLowerInvariant() switch
            {
                "read" => await ReadAsync(target, targetContext, request, cancellationToken),
                "write" => await WriteAsync(target, targetContext, request, cancellationToken),
                "edit" => await EditAsync(target, targetContext, request, cancellationToken),
                "apply_patch" => await ApplyPatchAsync(target, targetContext, request, cancellationToken),
                "grep" => await GrepAsync(target, targetContext, request, cancellationToken),
                "glob" => await GlobAsync(target, targetContext, request, cancellationToken),
                _ => Error(request.ToolId, $"Unknown file tool '{request.ToolId}'.", "files-tool-unknown"),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Error(request.ToolId, ex.Message, "files-execution");
        }
    }

    public async ValueTask<AgentPermissionRequest?> BuildPermissionRequestAsync(
        AgentToolExecutionContext context,
        AgentToolRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var target = ResolveTarget(context.ExecutionBinding);
        var actionId = request.ToolId.ToLowerInvariant() switch
        {
            "read" => "files.read",
            "grep" or "glob" => "files.search",
            "write" or "edit" or "apply_patch" => "files.mutate",
            _ => "files.read",
        };
        var scope = string.Equals(request.ToolId, "apply_patch", StringComparison.OrdinalIgnoreCase)
            ? await ResolvePatchPermissionScopeAsync(target, context, request.ArgumentsJson, cancellationToken)
            : await ResolvePathPermissionScopeAsync(target, context, ResolvePermissionPath(request.ToolId, request.ArgumentsJson), cancellationToken);

        return new AgentPermissionRequest(
            actionId,
            scope.BoundaryId,
            $"{request.ToolId} {scope.SummaryTarget}",
            ToolId: request.ToolId,
            Path: scope.Path,
            WorkspaceId: context.Workspace?.WorkspaceId,
            BindingId: context.ExecutionBinding?.BindingId,
            ResourceDisplayName: scope.ResourceDisplayName,
            ResourceReference: scope.ResourceReference,
            IsMutation: actionId == "files.mutate");
    }

    public IReadOnlyList<AgentPermissionActionDescriptor> ListActions()
        =>
        [
            new("files.read", "Read files", "Read files and directories.", FileBoundaries()),
            new("files.search", "Search files", "Search file names and contents.", FileBoundaries()),
            new("files.mutate", "Modify files", "Create, overwrite, or edit files.", FileBoundaries()),
        ];

    private static IReadOnlyList<AgentPermissionBoundaryDescriptor> FileBoundaries()
        =>
        [
            new(AgentPermissionBoundaryIds.ConfiguredScope, "Files inside configured workspace roots", "Paths resolved by the selected execution target inside configured roots.", AgentPermissionDecision.Allow),
            new(AgentPermissionBoundaryIds.OutsideConfiguredScope, "Files outside configured workspace roots", "Paths resolved by the selected execution target outside configured roots.", AgentPermissionDecision.Ask),
            new(AgentPermissionBoundaryIds.Unknown, "Files whose workspace scope is unknown", "Requests that could not be classified by the selected execution target.", AgentPermissionDecision.Ask),
        ];

    public async ValueTask<IReadOnlyList<AgentSystemPromptBlock>> ContributeAsync(
        AgentSystemPromptRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!HasAvailableFileTool(request.AvailableTools)
            || request.Workspace is null
            || request.ExecutionBinding is null)
        {
            return [];
        }

        var target = ResolveTarget(request.ExecutionBinding);
        if (target is not IAgentExecutionScopeProvider scopeProvider)
        {
            return [];
        }

        var scope = await scopeProvider.GetExecutionScopeAsync(
            new AgentExecutionTargetContext(request.Session.SessionId, request.Profile.ProfileId, request.Workspace, request.ExecutionBinding),
            cancellationToken);
        if (scope.AllowedRoots.Count == 0)
        {
            return [];
        }

        var content = new StringBuilder();
        content.Append("Workspace file and search tools are scoped to the selected ")
            .Append(scope.DisplayName)
            .AppendLine(" workspace.")
            .AppendLine()
            .AppendLine("Configured allowed roots:");
        foreach (var root in scope.AllowedRoots)
        {
            content.Append("- ").AppendLine(root);
        }

        if (!string.IsNullOrWhiteSpace(scope.DefaultWorkingDirectory))
        {
            content.AppendLine()
                .AppendLine("Default working directory:")
                .AppendLine(scope.DefaultWorkingDirectory);
        }

        if (!string.IsNullOrWhiteSpace(scope.PathStyleDescription))
        {
            content.AppendLine()
                .AppendLine(scope.PathStyleDescription.Trim());
        }

        content.AppendLine()
            .AppendLine("Use these exact configured roots when an absolute path is needed. Prefer relative paths from the default working directory when possible. Do not invent paths from other user profiles or machines. Paths outside the configured roots require permission.");

        return
        [
            new AgentSystemPromptBlock(
                "workspace-file-scope",
                "Workspace File Scope",
                content.ToString().Trim(),
                Priority: 90,
                Required: true,
                SourceId: SourceId)
        ];
    }

    private static bool HasAvailableFileTool(IReadOnlyList<AgentToolDescriptor> availableTools)
        => availableTools.Any(tool => string.Equals(tool.SourceId, "files", StringComparison.OrdinalIgnoreCase));

    private async Task<AgentToolResult> ReadAsync(IAgentExecutionTarget target, AgentExecutionTargetContext context, AgentToolRequest request, CancellationToken cancellationToken)
    {
        if (!TryParseReadArgs(request.ArgumentsJson, out var args, out var error))
        {
            return Error(request.ToolId, error!, "files-arguments-invalid");
        }

        var result = await target.ReadFileAsync(context, new AgentFileReadRequest(args.Path, args.Offset, args.Limit), cancellationToken);
        var wasTruncated = false;
        var content = result.IsDirectory ? result.Content : FormatReadContent(result.Content, args.Offset, args.Limit, out wasTruncated);
        return new AgentToolResult(request.ToolId, $"Read {result.Path}", Content: content, WasTruncated: result.WasTruncated || wasTruncated, BackendId: BackendId(target));
    }

    private async Task<AgentToolResult> WriteAsync(IAgentExecutionTarget target, AgentExecutionTargetContext context, AgentToolRequest request, CancellationToken cancellationToken)
    {
        if (!TryParseWriteArgs(request.ArgumentsJson, out var args, out var error))
        {
            return Error(request.ToolId, error!, "files-arguments-invalid");
        }

        var result = await target.WriteFileAsync(context, new AgentFileWriteRequest(args.Path, args.Content, Overwrite: true), cancellationToken);
        return new AgentToolResult(request.ToolId, result.Summary, Content: result.Summary, IsError: result.IsError, ErrorCode: result.ErrorCode, BackendId: BackendId(target));
    }

    private async Task<AgentToolResult> EditAsync(IAgentExecutionTarget target, AgentExecutionTargetContext context, AgentToolRequest request, CancellationToken cancellationToken)
    {
        if (!TryParseEditArgs(request.ArgumentsJson, out var args, out var error))
        {
            return Error(request.ToolId, error!, "files-arguments-invalid");
        }

        var current = await target.ReadFileAsync(context, new AgentFileReadRequest(args.Path), cancellationToken);
        var next = args.ReplaceAll
            ? current.Content.Replace(args.OldString, args.NewString, StringComparison.Ordinal)
            : ReplaceOnce(current.Content, args.OldString, args.NewString);
        if (string.Equals(current.Content, next, StringComparison.Ordinal))
        {
            return Error(request.ToolId, "oldString was not found.", "edit-old-string-not-found");
        }

        var result = await target.WriteFileAsync(context, new AgentFileWriteRequest(args.Path, next), cancellationToken);
        return new AgentToolResult(request.ToolId, result.Summary, Content: result.Summary, IsError: result.IsError, ErrorCode: result.ErrorCode, BackendId: BackendId(target));
    }

    private async Task<AgentToolResult> ApplyPatchAsync(IAgentExecutionTarget target, AgentExecutionTargetContext context, AgentToolRequest request, CancellationToken cancellationToken)
    {
        if (!TryParseApplyPatchArgs(request.ArgumentsJson, out var args, out var error))
        {
            return Error(request.ToolId, error!, "files-arguments-invalid");
        }

        var operations = ParsePatch(args.PatchText);
        var summaries = new List<string>();

        foreach (var operation in operations)
        {
            switch (operation.Kind)
            {
                case PatchOperationKind.Add:
                    var addResult = await target.WriteFileAsync(context, new AgentFileWriteRequest(operation.Path, operation.Content ?? string.Empty, Overwrite: false), cancellationToken);
                    if (addResult.IsError)
                    {
                        return Error(request.ToolId, addResult.Summary, addResult.ErrorCode ?? "patch-add-failed");
                    }

                    summaries.Add($"Added {operation.Path}");
                    break;

                case PatchOperationKind.Delete:
                    var deleteResult = await target.DeleteFileAsync(context, new AgentFileDeleteRequest(operation.Path), cancellationToken);
                    if (deleteResult.IsError)
                    {
                        return Error(request.ToolId, deleteResult.Summary, deleteResult.ErrorCode ?? "patch-delete-failed");
                    }

                    summaries.Add($"Deleted {operation.Path}");
                    break;

                case PatchOperationKind.Update:
                    var current = await target.ReadFileAsync(context, new AgentFileReadRequest(operation.Path), cancellationToken);
                    var next = ApplyHunks(current.Content, operation.Hunks);
                    var updateResult = await target.WriteFileAsync(context, new AgentFileWriteRequest(operation.Path, next), cancellationToken);
                    if (updateResult.IsError)
                    {
                        return Error(request.ToolId, updateResult.Summary, updateResult.ErrorCode ?? "patch-update-failed");
                    }

                    summaries.Add($"Updated {operation.Path}");
                    break;
            }
        }

        var content = string.Join(Environment.NewLine, summaries);
        return new AgentToolResult(request.ToolId, BuildPatchSummary(operations), Content: content, BackendId: BackendId(target));
    }

    private async Task<AgentToolResult> GrepAsync(IAgentExecutionTarget target, AgentExecutionTargetContext context, AgentToolRequest request, CancellationToken cancellationToken)
    {
        if (!TryParseGrepArgs(request.ArgumentsJson, out var args, out var error))
        {
            return Error(request.ToolId, error!, "files-arguments-invalid");
        }

        var pathArg = string.IsNullOrWhiteSpace(args.Path) ? "." : args.Path;
        var result = await ExecuteCommandAsync(target, context, BuildRgGrepCommand(args, pathArg), cancellationToken);
        if (IsCommandMissing(result, "rg"))
        {
            result = await ExecuteCommandAsync(target, context, BuildGrepFallbackCommand(args, pathArg), cancellationToken);
        }

        var matches = ParseGrepResults(result.Output);
        return new AgentToolResult(
            request.ToolId,
            result.ExitCode <= 1 ? $"Found {FormatCount(matches.Count, "match", "matches")}" : $"Search failed with exit code {result.ExitCode} and {FormatCount(matches.Count, "match", "matches")}",
            Content: FormatGrepResults(matches, result.Output),
            StructuredPayloadJson: JsonSerializer.Serialize(matches, JsonOptions),
            WasTruncated: result.WasTruncated,
            IsError: result.ExitCode > 1,
            BackendId: BackendId(target));
    }

    private async Task<AgentToolResult> GlobAsync(IAgentExecutionTarget target, AgentExecutionTargetContext context, AgentToolRequest request, CancellationToken cancellationToken)
    {
        if (!TryParseGlobArgs(request.ArgumentsJson, out var args, out var error))
        {
            return Error(request.ToolId, error!, "files-arguments-invalid");
        }

        var pathArg = string.IsNullOrWhiteSpace(args.Path) ? "." : args.Path;
        var result = await ExecuteCommandAsync(target, context, BuildRgGlobCommand(args, pathArg), cancellationToken);
        var usedFallback = false;
        if (IsCommandMissing(result, "rg"))
        {
            usedFallback = true;
            result = await ExecuteCommandAsync(target, context, BuildGlobFallbackCommand(args, pathArg), cancellationToken);
        }

        var paths = result.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (usedFallback)
        {
            paths = paths
                .Where(path => MatchesGlobPattern(args.Pattern, pathArg, path))
                .ToArray();
        }

        var matches = paths
            .Select(path => new GlobMatch(path))
            .ToArray();
        return new AgentToolResult(
            request.ToolId,
            result.ExitCode <= 1 ? $"Found {FormatCount(matches.Length, "match", "matches")}" : $"Glob failed with exit code {result.ExitCode} and {FormatCount(matches.Length, "match", "matches")}",
            Content: string.Join(Environment.NewLine, matches.Select(match => match.Path)),
            StructuredPayloadJson: JsonSerializer.Serialize(matches, JsonOptions),
            WasTruncated: result.WasTruncated,
            IsError: result.ExitCode > 1,
            BackendId: BackendId(target));
    }

    private IAgentExecutionTarget? ResolveTarget(AgentWorkspaceBindingRecord? binding)
        => extensionCatalog.GetExtensions(PackageExtensionPoints.ExecutionTargets)
            .FirstOrDefault(target => binding is not null
                                      && (string.Equals(target.Descriptor.TargetId, binding.ContributionId, StringComparison.OrdinalIgnoreCase)
                                          || string.Equals(target.Descriptor.TargetKind, binding.ContributionId, StringComparison.OrdinalIgnoreCase)));

    private static bool TryParseReadArgs(string argumentsJson, out ReadArgs args, out string? error)
    {
        args = new ReadArgs(string.Empty);
        if (!TryParseArguments(argumentsJson, out var arguments, out error)
            || !arguments!.TryReadRequiredString("path", out var path, out error)
            || !arguments.TryReadOptionalInt32("offset", out var offset, out error)
            || !arguments.TryReadOptionalInt32("limit", out var limit, out error))
        {
            error = PrefixArgumentError("read", error);
            return false;
        }

        args = new ReadArgs(path!, offset, limit);
        return true;
    }

    private static bool TryParseWriteArgs(string argumentsJson, out WriteArgs args, out string? error)
    {
        args = new WriteArgs(string.Empty, string.Empty);
        if (!TryParseArguments(argumentsJson, out var arguments, out error)
            || !arguments!.TryReadRequiredString("path", out var path, out error)
            || !arguments.TryReadRequiredString("content", out var content, out error))
        {
            error = PrefixArgumentError("write", error);
            return false;
        }

        args = new WriteArgs(path!, content!);
        return true;
    }

    private static bool TryParseEditArgs(string argumentsJson, out EditArgs args, out string? error)
    {
        args = new EditArgs(string.Empty, string.Empty, string.Empty);
        if (!TryParseArguments(argumentsJson, out var arguments, out error)
            || !arguments!.TryReadRequiredString("path", out var path, out error)
            || !arguments.TryReadRequiredString("oldString", out var oldString, out error)
            || !arguments.TryReadRequiredString("newString", out var newString, out error)
            || !arguments.TryReadOptionalBoolean("replaceAll", out var replaceAll, out error))
        {
            error = PrefixArgumentError("edit", error);
            return false;
        }

        args = new EditArgs(path!, oldString!, newString!, replaceAll ?? false);
        return true;
    }

    private static bool TryParseApplyPatchArgs(string argumentsJson, out ApplyPatchArgs args, out string? error)
    {
        args = new ApplyPatchArgs(string.Empty);
        if (!TryParseArguments(argumentsJson, out var arguments, out error)
            || !arguments!.TryReadRequiredString("patchText", out var patchText, out error))
        {
            error = PrefixArgumentError("apply_patch", error);
            return false;
        }

        args = new ApplyPatchArgs(patchText!);
        return true;
    }

    private static bool TryParseGrepArgs(string argumentsJson, out GrepArgs args, out string? error)
    {
        args = new GrepArgs(string.Empty);
        if (!TryParseArguments(argumentsJson, out var arguments, out error)
            || !arguments!.TryReadRequiredString("pattern", out var pattern, out error)
            || !arguments.TryReadOptionalString("path", out var path, out error)
            || !arguments.TryReadOptionalString("include", out var include, out error))
        {
            error = PrefixArgumentError("grep", error);
            return false;
        }

        args = new GrepArgs(pattern!, path, include);
        return true;
    }

    private static bool TryParseGlobArgs(string argumentsJson, out GlobArgs args, out string? error)
    {
        args = new GlobArgs(string.Empty);
        if (!TryParseArguments(argumentsJson, out var arguments, out error)
            || !arguments!.TryReadRequiredString("pattern", out var pattern, out error)
            || !arguments.TryReadOptionalString("path", out var path, out error))
        {
            error = PrefixArgumentError("glob", error);
            return false;
        }

        args = new GlobArgs(pattern!, path);
        return true;
    }

    private static bool TryParseArguments(string argumentsJson, out AgentToolArgumentObject? arguments, out string? error)
        => AgentToolArgumentObject.TryParse(argumentsJson, out arguments, out error);

    private static string PrefixArgumentError(string toolName, string? error)
        => $"Invalid {toolName} arguments: {error ?? "arguments were empty or invalid."}";

    private static string? ExtractPath(string argumentsJson)
    {
        return AgentToolArgumentObject.TryParse(argumentsJson, out var arguments, out _)
               && arguments!.TryReadOptionalString("path", out var path, out _) ? path : null;
    }

    private static string? ResolvePermissionPath(string toolId, string argumentsJson)
    {
        var path = ExtractPath(argumentsJson);
        return toolId.ToLowerInvariant() switch
        {
            "grep" or "glob" when string.IsNullOrWhiteSpace(path) => ".",
            _ => path,
        };
    }

    private static async ValueTask<FilePermissionScope> ResolvePathPermissionScopeAsync(
        IAgentExecutionTarget? target,
        AgentToolExecutionContext context,
        string? path,
        CancellationToken cancellationToken)
    {
        if (target is null || context.Workspace is null || context.ExecutionBinding is null || string.IsNullOrWhiteSpace(path))
        {
            return FilePermissionScope.Unknown(path);
        }

        var resource = await target.ResolveFileResourceAsync(
            new AgentExecutionTargetContext(context.SessionId, context.ProfileId, context.Workspace, context.ExecutionBinding, AllowOutsideConfiguredScope: true),
            path,
            cancellationToken);
        return FilePermissionScope.FromResource(path, resource);
    }

    private static async ValueTask<FilePermissionScope> ResolvePatchPermissionScopeAsync(
        IAgentExecutionTarget? target,
        AgentToolExecutionContext context,
        string argumentsJson,
        CancellationToken cancellationToken)
    {
        if (target is null || context.Workspace is null || context.ExecutionBinding is null)
        {
            return FilePermissionScope.Unknown();
        }

        IReadOnlyList<PatchOperation> operations;
        try
        {
            if (!TryParseApplyPatchArgs(argumentsJson, out var args, out _))
            {
                return FilePermissionScope.Unknown();
            }

            operations = ParsePatch(args.PatchText);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return FilePermissionScope.Unknown();
        }

        var paths = operations
            .Select(operation => operation.Path)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (paths.Length == 0)
        {
            return FilePermissionScope.Unknown();
        }

        var targetContext = new AgentExecutionTargetContext(
            context.SessionId,
            context.ProfileId,
            context.Workspace,
            context.ExecutionBinding,
            AllowOutsideConfiguredScope: true);
        var hasUnknownBoundary = false;
        AgentResolvedResource? singleResource = null;
        var hasOutsideConfiguredScope = false;
        foreach (var path in paths)
        {
            AgentResolvedResource resource;
            try
            {
                resource = await target.ResolveFileResourceAsync(targetContext, path, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                hasUnknownBoundary = true;
                continue;
            }

            if (paths.Length == 1)
            {
                singleResource = resource;
            }

            if (string.Equals(resource.PermissionBoundaryId, AgentPermissionBoundaryIds.OutsideConfiguredScope, StringComparison.OrdinalIgnoreCase))
            {
                hasOutsideConfiguredScope = true;
            }
            else if (!string.Equals(resource.PermissionBoundaryId, AgentPermissionBoundaryIds.ConfiguredScope, StringComparison.OrdinalIgnoreCase))
            {
                hasUnknownBoundary = true;
            }
        }

        var boundaryId = hasOutsideConfiguredScope
            ? AgentPermissionBoundaryIds.OutsideConfiguredScope
            : hasUnknownBoundary
                ? AgentPermissionBoundaryIds.Unknown
                : AgentPermissionBoundaryIds.ConfiguredScope;
        var summaryTarget = paths.Length == 1
            ? paths[0]
            : $"{paths.Length} workspace files";
        return new FilePermissionScope(
            boundaryId,
            summaryTarget,
            paths.Length == 1 ? paths[0] : null,
            singleResource?.DisplayName,
            singleResource?.CanonicalReference);
    }

    private static AgentToolResult Error(string toolId, string message, string code)
        => new(toolId, message, Content: $"### File tool failed\n\n{message}", IsError: true, ErrorCode: code);

    private static string ReplaceOnce(string current, string oldString, string newString)
    {
        var index = current.IndexOf(oldString, StringComparison.Ordinal);
        return index < 0 ? current : current[..index] + newString + current[(index + oldString.Length)..];
    }

    private static async ValueTask<AgentShellCommandResult> ExecuteCommandAsync(
        IAgentExecutionTarget target,
        AgentExecutionTargetContext context,
        ProcessCommandSpec command,
        CancellationToken cancellationToken)
    {
        if (target is IAgentProcessExecutionTarget processTarget)
        {
            return await processTarget.ExecuteProcessAsync(
                context,
                new AgentProcessCommandRequest(command.FileName, command.Arguments),
                cancellationToken);
        }

        return await target.ExecuteShellAsync(
            context,
            new AgentShellCommandRequest(BuildShellCommand(command)),
            cancellationToken);
    }

    private static ProcessCommandSpec BuildRgGrepCommand(GrepArgs args, string pathArg)
    {
        var arguments = new List<string> { "--line-number", "--no-heading" };
        if (!string.IsNullOrWhiteSpace(args.Include))
        {
            arguments.Add("-g");
            arguments.Add(args.Include);
        }

        arguments.Add("--");
        arguments.Add(args.Pattern);
        arguments.Add(pathArg);
        return new ProcessCommandSpec("rg", arguments);
    }

    private static ProcessCommandSpec BuildRgGlobCommand(GlobArgs args, string pathArg)
        => new("rg", ["--files", "-g", args.Pattern, "--", pathArg]);

    private static string BuildShellCommand(ProcessCommandSpec command)
        => command.Arguments.Count == 0
            ? command.FileName
            : $"{command.FileName} {string.Join(" ", command.Arguments.Select(Quote))}";

    private static string Quote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    private static string FormatReadContent(string content, int? offset, int? limit, out bool wasTruncated)
    {
        var lines = content.Replace("\r\n", "\n").Split('\n');
        var start = Math.Max(1, offset ?? 1);
        var take = Math.Clamp(limit ?? 2000, 1, 2000);
        wasTruncated = start - 1 + take < lines.Length;
        return string.Join(Environment.NewLine, lines.Skip(start - 1).Take(take).Select((line, index) => $"{start + index}: {line}"));
    }

    private static string BackendId(IAgentExecutionTarget target) => $"{target.Descriptor.TargetKind}:{target.Descriptor.TargetId}";

    private static bool IsCommandMissing(AgentShellCommandResult result, string commandName)
        => result.ExitCode == 127
           || result.Output.Contains($"{commandName}: not found", StringComparison.OrdinalIgnoreCase)
           || result.Output.Contains($"{commandName}: command not found", StringComparison.OrdinalIgnoreCase)
           || result.Output.Contains("executable file not found", StringComparison.OrdinalIgnoreCase);

    private static ProcessCommandSpec BuildGrepFallbackCommand(GrepArgs args, string pathArg)
        => string.IsNullOrWhiteSpace(args.Include)
            ? new ProcessCommandSpec("grep", ["-E", "-RIn", "--", args.Pattern, pathArg])
            : new ProcessCommandSpec("find", [pathArg, "-type", "f", "-name", args.Include, "-exec", "grep", "-E", "-In", "--", args.Pattern, "{}", "+"]);

    private static ProcessCommandSpec BuildGlobFallbackCommand(GlobArgs args, string pathArg)
    {
        var patterns = BuildFindPathPatterns(pathArg, args.Pattern);
        if (patterns.Count == 0)
        {
            return new ProcessCommandSpec("find", [pathArg, "-type", "f", "-print"]);
        }

        var arguments = new List<string> { pathArg, "-type", "f", "(" };
        for (var index = 0; index < patterns.Count; index++)
        {
            if (index > 0)
            {
                arguments.Add("-o");
            }

            arguments.Add("-path");
            arguments.Add(patterns[index]);
        }

        arguments.Add(")");
        arguments.Add("-print");
        return new ProcessCommandSpec("find", arguments);
    }

    private static IReadOnlyList<string> BuildFindPathPatterns(string pathArg, string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return [];
        }

        var basePath = NormalizeFindBasePath(pathArg);
        var normalizedPattern = pattern.Trim().Replace('\\', '/');
        return ExpandDoubleStarZeroDirectoryVariants(normalizedPattern)
            .Select(candidate => PrefixFindPathPattern(basePath, candidate))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static IEnumerable<string> ExpandDoubleStarZeroDirectoryVariants(string pattern)
    {
        var pending = new Queue<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal) { pattern };
        pending.Enqueue(pattern);

        while (pending.Count > 0)
        {
            var current = pending.Dequeue();
            yield return current;

            var index = current.IndexOf("**/", StringComparison.Ordinal);
            if (index < 0)
            {
                continue;
            }

            var withoutZeroOrMoreDirectorySegment = current.Remove(index, 3);
            if (seen.Add(withoutZeroOrMoreDirectorySegment))
            {
                pending.Enqueue(withoutZeroOrMoreDirectorySegment);
            }
        }
    }

    private static string NormalizeFindBasePath(string pathArg)
    {
        if (string.IsNullOrWhiteSpace(pathArg))
        {
            return ".";
        }

        var normalized = pathArg.Trim().Replace('\\', '/');
        while (normalized.Length > 1 && normalized.EndsWith("/", StringComparison.Ordinal))
        {
            normalized = normalized[..^1];
        }

        return string.IsNullOrWhiteSpace(normalized) ? "." : normalized;
    }

    private static string PrefixFindPathPattern(string basePath, string pattern)
    {
        if (pattern.StartsWith("/", StringComparison.Ordinal))
        {
            return pattern;
        }

        if (pattern.StartsWith("./", StringComparison.Ordinal))
        {
            return pattern;
        }

        return basePath switch
        {
            "." => "./" + pattern,
            "/" => "/" + pattern,
            _ => basePath + "/" + pattern,
        };
    }

    private static bool MatchesGlobPattern(string pattern, string pathArg, string candidatePath)
    {
        if (string.IsNullOrWhiteSpace(pattern) || string.IsNullOrWhiteSpace(candidatePath))
        {
            return true;
        }

        var normalizedPattern = pattern.Trim().Replace('\\', '/');
        var normalizedCandidate = candidatePath.Trim().Replace('\\', '/');
        var basePath = NormalizeFindBasePath(pathArg);
        var relativeCandidate = NormalizeRelativeCandidate(basePath, normalizedCandidate);
        var patterns = ExpandGlobBraces(normalizedPattern).ToArray();

        foreach (var expandedPattern in patterns)
        {
            if (MatchesSingleGlobPattern(expandedPattern, normalizedCandidate, relativeCandidate))
            {
                return true;
            }
        }

        return false;
    }

    private static string NormalizeRelativeCandidate(string basePath, string candidatePath)
    {
        var normalizedBase = NormalizeFindBasePath(basePath);
        var normalizedCandidate = candidatePath;
        if (normalizedCandidate.StartsWith("./", StringComparison.Ordinal))
        {
            normalizedCandidate = normalizedCandidate[2..];
        }

        if (normalizedBase is ".")
        {
            return normalizedCandidate.TrimStart('/');
        }

        if (normalizedCandidate.Equals(normalizedBase, StringComparison.Ordinal))
        {
            return string.Empty;
        }

        if (normalizedCandidate.StartsWith(normalizedBase + "/", StringComparison.Ordinal))
        {
            return normalizedCandidate[(normalizedBase.Length + 1)..];
        }

        return normalizedCandidate.TrimStart('/');
    }

    private static bool MatchesSingleGlobPattern(string pattern, string candidatePath, string relativeCandidate)
    {
        if (pattern.StartsWith("./", StringComparison.Ordinal))
        {
            return MatchesGlobSegments(pattern[2..], relativeCandidate);
        }

        if (pattern.StartsWith("/", StringComparison.Ordinal))
        {
            return MatchesGlobSegments(pattern.TrimStart('/'), candidatePath.TrimStart('/'));
        }

        if (!pattern.Contains('/', StringComparison.Ordinal))
        {
            var fileName = relativeCandidate.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? relativeCandidate;
            return MatchesGlobSegment(pattern, fileName);
        }

        return MatchesGlobSegments(pattern, relativeCandidate);
    }

    private static bool MatchesGlobSegments(string pattern, string candidate)
    {
        var patternSegments = pattern.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var candidateSegments = candidate.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return MatchesGlobSegments(patternSegments, 0, candidateSegments, 0);
    }

    private static bool MatchesGlobSegments(IReadOnlyList<string> patternSegments, int patternIndex, IReadOnlyList<string> candidateSegments, int candidateIndex)
    {
        while (patternIndex < patternSegments.Count)
        {
            var segment = patternSegments[patternIndex];
            if (segment == "**")
            {
                if (patternIndex == patternSegments.Count - 1)
                {
                    return true;
                }

                for (var nextCandidateIndex = candidateIndex; nextCandidateIndex <= candidateSegments.Count; nextCandidateIndex++)
                {
                    if (MatchesGlobSegments(patternSegments, patternIndex + 1, candidateSegments, nextCandidateIndex))
                    {
                        return true;
                    }
                }

                return false;
            }

            if (candidateIndex >= candidateSegments.Count || !MatchesGlobSegment(segment, candidateSegments[candidateIndex]))
            {
                return false;
            }

            patternIndex++;
            candidateIndex++;
        }

        return candidateIndex == candidateSegments.Count;
    }

    private static bool MatchesGlobSegment(string pattern, string candidate)
        => Regex.IsMatch(candidate, "^" + GlobSegmentToRegex(pattern) + "$", RegexOptions.CultureInvariant);

    private static string GlobSegmentToRegex(string pattern)
    {
        var builder = new StringBuilder();
        for (var index = 0; index < pattern.Length; index++)
        {
            var ch = pattern[index];
            switch (ch)
            {
                case '*':
                    builder.Append(".*");
                    break;
                case '?':
                    builder.Append('.');
                    break;
                default:
                    builder.Append(Regex.Escape(ch.ToString()));
                    break;
            }
        }

        return builder.ToString();
    }

    private static IEnumerable<string> ExpandGlobBraces(string pattern)
    {
        var open = pattern.IndexOf('{', StringComparison.Ordinal);
        if (open < 0)
        {
            yield return pattern;
            yield break;
        }

        var close = pattern.IndexOf('}', open + 1);
        if (close < 0)
        {
            yield return pattern;
            yield break;
        }

        var prefix = pattern[..open];
        var suffix = pattern[(close + 1)..];
        foreach (var option in pattern[(open + 1)..close].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var expandedSuffix in ExpandGlobBraces(suffix))
            {
                yield return prefix + option + expandedSuffix;
            }
        }
    }

    private static string FormatCount(int count, string noun, string? plural = null)
        => count == 1 ? $"1 {noun}" : $"{count} {plural ?? noun + "s"}";

    private static IReadOnlyList<GrepMatch> ParseGrepResults(string output)
    {
        var matches = new List<GrepMatch>();
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var firstColon = line.IndexOf(':');
            if (firstColon < 0)
            {
                continue;
            }

            var secondColon = line.IndexOf(':', firstColon + 1);
            if (secondColon < 0 || !int.TryParse(line[(firstColon + 1)..secondColon], out var lineNumber))
            {
                continue;
            }

            matches.Add(new GrepMatch(line[..firstColon], lineNumber, line[(secondColon + 1)..]));
        }

        return matches;
    }

    private static string FormatGrepResults(IReadOnlyList<GrepMatch> matches, string fallbackOutput)
        => matches.Count == 0
            ? fallbackOutput
            : string.Join(Environment.NewLine, matches.Select(match => $"{match.Path}:{match.LineNumber}: {match.Text}"));

    private static IReadOnlyList<PatchOperation> ParsePatch(string patchText)
    {
        var lines = patchText.Replace("\r\n", "\n").Split('\n');
        if (lines.Length == 0 || !string.Equals(lines[0].Trim(), "*** Begin Patch", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Patch must start with '*** Begin Patch'.");
        }

        var operations = new List<PatchOperation>();
        var index = 1;
        while (index < lines.Length)
        {
            var line = lines[index];
            if (string.Equals(line.Trim(), "*** End Patch", StringComparison.Ordinal))
            {
                return operations;
            }

            if (line.StartsWith("*** Add File: ", StringComparison.Ordinal))
            {
                var path = line[14..].Trim();
                index++;
                var contentLines = new List<string>();
                while (index < lines.Length && !lines[index].StartsWith("*** ", StringComparison.Ordinal))
                {
                    if (!lines[index].StartsWith('+'))
                    {
                        throw new InvalidOperationException($"Add File lines must start with '+': {path}");
                    }

                    contentLines.Add(lines[index][1..]);
                    index++;
                }

                operations.Add(new PatchOperation(PatchOperationKind.Add, path, string.Join('\n', contentLines), []));
                continue;
            }

            if (line.StartsWith("*** Delete File: ", StringComparison.Ordinal))
            {
                operations.Add(new PatchOperation(PatchOperationKind.Delete, line[17..].Trim(), null, []));
                index++;
                continue;
            }

            if (line.StartsWith("*** Update File: ", StringComparison.Ordinal))
            {
                var path = line[17..].Trim();
                index++;
                var hunks = new List<PatchHunk>();
                while (index < lines.Length && !lines[index].StartsWith("*** ", StringComparison.Ordinal))
                {
                    if (!lines[index].StartsWith("@@", StringComparison.Ordinal))
                    {
                        index++;
                        continue;
                    }

                    index++;
                    var oldLines = new List<string>();
                    var newLines = new List<string>();
                    while (index < lines.Length
                           && !lines[index].StartsWith("@@", StringComparison.Ordinal)
                           && !lines[index].StartsWith("*** ", StringComparison.Ordinal))
                    {
                        var hunkLine = lines[index];
                        if (hunkLine.StartsWith("\\", StringComparison.Ordinal))
                        {
                            index++;
                            continue;
                        }

                        if (hunkLine.Length == 0)
                        {
                            oldLines.Add(string.Empty);
                            newLines.Add(string.Empty);
                        }
                        else if (hunkLine[0] == '-')
                        {
                            oldLines.Add(hunkLine[1..]);
                        }
                        else if (hunkLine[0] == '+')
                        {
                            newLines.Add(hunkLine[1..]);
                        }
                        else if (hunkLine[0] == ' ')
                        {
                            oldLines.Add(hunkLine[1..]);
                            newLines.Add(hunkLine[1..]);
                        }
                        else
                        {
                            throw new InvalidOperationException($"Unsupported patch hunk line: {hunkLine}");
                        }

                        index++;
                    }

                    hunks.Add(new PatchHunk(string.Join('\n', oldLines), string.Join('\n', newLines)));
                }

                operations.Add(new PatchOperation(PatchOperationKind.Update, path, null, hunks));
                continue;
            }

            throw new InvalidOperationException($"Unsupported patch line: {line}");
        }

        throw new InvalidOperationException("Patch must end with '*** End Patch'.");
    }

    private static string ApplyHunks(string content, IReadOnlyList<PatchHunk> hunks)
    {
        var next = content.Replace("\r\n", "\n");
        foreach (var hunk in hunks)
        {
            var index = next.IndexOf(hunk.OldText, StringComparison.Ordinal);
            if (index < 0)
            {
                throw new InvalidOperationException("Patch hunk did not match the current file content.");
            }

            next = next[..index] + hunk.NewText + next[(index + hunk.OldText.Length)..];
        }

        return next;
    }

    private sealed record ReadArgs(string Path, int? Offset = null, int? Limit = null);

    private sealed record WriteArgs(string Path, string Content);

    private sealed record EditArgs(string Path, string OldString, string NewString, bool ReplaceAll = false);

    private sealed record ApplyPatchArgs(string PatchText);

    private sealed record GrepArgs(string Pattern, string? Path = null, string? Include = null);

    private sealed record GlobArgs(string Pattern, string? Path = null);

    private sealed record GrepMatch(string Path, int LineNumber, string Text);

    private sealed record GlobMatch(string Path);

    private sealed record ProcessCommandSpec(string FileName, IReadOnlyList<string> Arguments);

    private sealed record PatchOperation(PatchOperationKind Kind, string Path, string? Content, IReadOnlyList<PatchHunk> Hunks);

    private sealed record PatchHunk(string OldText, string NewText);

    private sealed record FilePermissionScope(
        string BoundaryId,
        string SummaryTarget,
        string? Path,
        string? ResourceDisplayName,
        string? ResourceReference)
    {
        public static FilePermissionScope Unknown(string? path = null)
            => new(
                AgentPermissionBoundaryIds.Unknown,
                string.IsNullOrWhiteSpace(path) ? "workspace files" : path,
                path,
                ResourceDisplayName: null,
                ResourceReference: null);

        public static FilePermissionScope FromResource(string path, AgentResolvedResource resource)
            => new(
                resource.PermissionBoundaryId,
                path,
                path,
                resource.DisplayName,
                resource.CanonicalReference);
    }

    private enum PatchOperationKind
    {
        Add,
        Update,
        Delete,
    }

    private const string ReadInstructions = """
        Use this tool to read file contents or list directory entries in the selected workspace. It supports reading specific line ranges for large files.

        Usage:
        - Use the path parameter for the workspace-relative or executor-resolved file or directory path.
        - By default, this tool returns up to 2000 lines from the start of a file.
        - The offset parameter is the line number to start from, using 1-based indexing.
        - To read later sections, call this tool again with a larger offset.
        - Use grep to find specific content in large files or files with long lines.
        - If you are unsure of the correct path, use glob to look up filenames by glob pattern.
        - File contents are returned with each line prefixed by its line number.
        - Directory entries are returned one per line, with a trailing slash for subdirectories.
        - Avoid tiny repeated slices. If you need more context, read a larger window.
        """;

    private const string GlobInstructions = """
        Use this tool to find files by name or path pattern before using lower-priority executor commands.

        Usage:
        - Supports glob patterns like **/*.cs or src/**/*.ts.
        - Returns matching file paths sorted by modification time where supported by the executor.
        - Use this when you need to locate files by name pattern.
        - Prefer this over shell commands for file discovery.
        """;

    private const string GrepInstructions = """
        Use this tool to search file contents before using lower-priority executor commands.

        Usage:
        - Searches file contents using regular expressions.
        - Supports full regex syntax.
        - Use include to filter by file pattern when supported.
        - Returns matching file paths and line numbers.
        - Use this when you need to find references, symbols, text, or code patterns.
        - Prefer this over shell commands for content search.
        """;

    private const string WriteInstructions = """
        Use this tool to create a new file or overwrite a file when the complete intended content is known.

        Usage:
        - This tool overwrites the existing file if one exists at the provided path.
        - Prefer edit or apply_patch for modifying existing code.
        - Read an existing file first before replacing it so you do not accidentally discard content.
        - Do not create documentation files unless explicitly requested.
        - Avoid emojis unless the user explicitly asks for them.
        """;

    private const string EditInstructions = """
        Use this tool for precise edits to existing files by replacing exact text matches.

        Usage:
        - Read the target file before editing.
        - Preserve exact indentation and whitespace from the current file content.
        - The edit fails if oldString is not found.
        - The edit fails if oldString matches multiple locations unless replaceAll is used.
        - Include enough surrounding context in oldString to identify the intended location.
        - Use replaceAll for file-wide renames or repeated exact replacements.
        - Prefer this over write for focused changes to existing files.
        """;

    private const string ApplyPatchInstructions = """
        Use this tool for structured multi-file edits. The patch language is a stripped-down, file-oriented diff format.

        Patch format:
        *** Begin Patch
        [one or more file sections]
        *** End Patch

        Each operation starts with one of:
        *** Add File: <path>
        *** Delete File: <path>
        *** Update File: <path>

        Rules:
        - You must include a header with the intended action.
        - Add File content lines must be prefixed with +.
        - Use Update File for in-place changes and optional renames.
        - Use Delete File only when removing an existing file.
        - Prefer apply_patch for coordinated edits across multiple files.
        """;

    private const string ReadSchema = """
        {"type":"object","properties":{"path":{"type":"string"},"offset":{"type":"integer"},"limit":{"type":"integer"}},"required":["path"],"additionalProperties":false}
        """;

    private const string WriteSchema = """
        {"type":"object","properties":{"path":{"type":"string"},"content":{"type":"string"}},"required":["path","content"],"additionalProperties":false}
        """;

    private const string EditSchema = """
        {"type":"object","properties":{"path":{"type":"string"},"oldString":{"type":"string"},"newString":{"type":"string"},"replaceAll":{"type":"boolean"}},"required":["path","oldString","newString"],"additionalProperties":false}
        """;

    private const string ApplyPatchSchema = """
        {"type":"object","properties":{"patchText":{"type":"string"}},"required":["patchText"],"additionalProperties":false}
        """;

    private const string GrepSchema = """
        {"type":"object","properties":{"pattern":{"type":"string"},"path":{"type":"string"},"include":{"type":"string"}},"required":["pattern"],"additionalProperties":false}
        """;

    private const string GlobSchema = """
        {"type":"object","properties":{"pattern":{"type":"string"},"path":{"type":"string"}},"required":["pattern"],"additionalProperties":false}
        """;
}
