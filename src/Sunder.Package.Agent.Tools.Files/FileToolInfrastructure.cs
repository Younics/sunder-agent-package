using System.Text;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.Shared.Presentation;

namespace Sunder.Package.Agent.Tools.Files;

internal static class FileToolResult
{
    public static AgentToolResult Error(string toolId, string message, string code)
        => Success(toolId, message, AgentToolPresentationMarkdown.BuildFailureMarkdown("File tool failed", message), null) with
        { IsError = true, ErrorCode = code };

    public static AgentToolResult Success(string toolId, string summary, string? content, string? backendId,
        bool wasTruncated = false, string? structuredPayloadJson = null, string? presentationPayloadJson = null) => new()
    {
        ToolId = toolId, Summary = summary, Content = content, BackendId = backendId,
        WasTruncated = wasTruncated, StructuredPayloadJson = structuredPayloadJson, PresentationPayloadJson = presentationPayloadJson,
        Sources = null, IsError = false, ErrorCode = null, RequiresPromptContextRefresh = false,
    };

    public static AgentToolResult ReadError(string toolId, AgentFileReadResult result)
        => Error(
            toolId,
            result.ErrorMessage ?? $"Unable to read '{result.Path}'.",
            result.ErrorCode ?? "file-read-failed");

    public static string BackendId(FileExecutionTarget target)
        => $"{target.Descriptor.TargetKind}:{target.Descriptor.TargetId}";

    public static string FormatCount(int count, string noun, string? plural = null)
        => count == 1 ? $"1 {noun}" : $"{count} {plural ?? noun + "s"}";
}

internal static class FileSystemPromptBuilder
{
    public static async ValueTask<AgentPromptContextContribution?> BuildAsync(
        FileExecutionTarget? target,
        AgentPromptContextRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!request.AvailableTools.Any(tool => string.Equals(tool.SourceId, "files", StringComparison.OrdinalIgnoreCase))
            || request.Workspace is null
            || request.ExecutionBinding is null
            || target is null
            || !FilesExecutionFacets.SupportsFacet(target, AgentExecutionFacetIds.ExecutionScope))
        {
            return null;
        }

        var scope = await target.Service.ExecutionScopeAsync(
            FileExecutionTarget.Context(request.Session.SessionId, request.Profile?.ProfileId, request.Workspace, request.ExecutionBinding),
            cancellationToken);
        if (scope.WorkspacePaths.Count == 0)
        {
            return null;
        }

        var content = new StringBuilder();
        content.Append("Workspace file and search tools are scoped to the selected ")
            .Append(scope.DisplayName)
            .AppendLine(" workspace.")
            .AppendLine()
            .AppendLine("Configured workspace paths:");
        foreach (var root in scope.WorkspacePaths)
        {
            content.Append("- ").AppendLine(root);
        }

        if (!string.IsNullOrWhiteSpace(scope.DefaultWorkingDirectory))
        {
            content.AppendLine().AppendLine("Default working directory:").AppendLine(scope.DefaultWorkingDirectory);
        }

        if (!string.IsNullOrWhiteSpace(scope.PathStyleDescription))
        {
            content.AppendLine().AppendLine(scope.PathStyleDescription.Trim());
        }

        content.AppendLine()
            .AppendLine("Use these exact workspace paths when an absolute path is needed. Prefer relative paths from the default working directory when possible. Do not invent paths from other user profiles or machines. Paths outside the configured workspace paths require permission.");

        return new AgentPromptContextContribution
        {
            Blocks = [new()
            {
                Title = "Workspace File Scope", Content = content.ToString().Trim(), Priority = 90,
                SourceId = FileToolDescriptorRegistry.SourceId, Provenance = AgentContextProvenance.Extension, Trust = AgentContextTrust.Untrusted,
                Usage = AgentPromptContextUsage.Reference, Scope = null, Authority = AgentPromptContextAuthority.Reference, HostIdentity = null,
            }],
        };
    }
}
