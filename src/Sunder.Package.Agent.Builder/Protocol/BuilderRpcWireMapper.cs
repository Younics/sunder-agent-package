using System.Globalization;
using Sunder.Package.Agent.Builder.Protocol.Generated;
using Sunder.Package.Agent.Contracts.Models;
using Wire = Sunder.Package.Agent.Builder.Protocol.Generated;

namespace Sunder.Package.Agent.Builder;

internal static class BuilderRpcWireMapper
{
    internal static BuilderRpcRequest ToDomain(BuilderExecuteRequest request)
        => new(
            (BuilderRpcOperationKind)checked((int)request.Kind),
            request.WorkspaceId,
            request.FileName,
            request.Arguments,
            request.Command,
            request.WorkingDirectory,
            checked((int)request.TimeoutSeconds),
            request.ExecutionPath);

    internal static BuilderExecuteRequest ToWire(BuilderRpcRequest request)
        => new()
        {
            Kind = (int)request.Kind,
            WorkspaceId = request.WorkspaceId,
            FileName = request.FileName,
            Arguments = request.Arguments,
            Command = request.Command,
            WorkingDirectory = request.WorkingDirectory,
            TimeoutSeconds = request.TimeoutSeconds,
            ExecutionPath = request.ExecutionPath,
        };

    internal static BuilderRpcResponse ToDomain(BuilderExecuteResponse response)
        => new(
            response.Workspaces?.Select(ToDomain).ToArray(),
            response.Execution is null ? null : ToDomain(response.Execution),
            response.Process is null ? null : ToDomain(response.Process),
            response.PathMapping is null ? null : ToDomain(response.PathMapping));

    internal static BuilderExecuteResponse ToWire(BuilderRpcResponse response)
        => new()
        {
            Workspaces = response.Workspaces?.Select(ToWire).ToArray(),
            Execution = response.Execution is null ? null : ToWire(response.Execution),
            Process = response.Process is null ? null : ToWire(response.Process),
            PathMapping = response.PathMapping is null ? null : ToWire(response.PathMapping),
        };

    private static AgentWorkspaceRecord ToDomain(BuilderWorkspace workspace)
        => new(
            workspace.WorkspaceId,
            workspace.DisplayName,
            workspace.Description,
            ParseUtc(workspace.CreatedAtUtc),
            ParseUtc(workspace.UpdatedAtUtc),
            workspace.Paths.Select(ToDomain).ToArray(),
            workspace.Documents.Select(ToDomain).ToArray());

    private static BuilderWorkspace ToWire(AgentWorkspaceRecord workspace)
        => new()
        {
            WorkspaceId = workspace.WorkspaceId,
            DisplayName = workspace.DisplayName,
            Description = workspace.Description,
            CreatedAtUtc = FormatUtc(workspace.CreatedAtUtc),
            UpdatedAtUtc = FormatUtc(workspace.UpdatedAtUtc),
            Paths = workspace.Paths.Select(ToWire).ToArray(),
            Documents = workspace.Documents.Select(ToWire).ToArray(),
        };

    private static AgentWorkspacePathRecord ToDomain(BuilderWorkspacePath path)
        => new(
            path.PathId,
            path.WorkspaceId,
            path.HostPath,
            path.IsDefault,
            checked((int)path.SortOrder),
            ParseUtc(path.CreatedAtUtc),
            ParseUtc(path.UpdatedAtUtc));

    private static BuilderWorkspacePath ToWire(AgentWorkspacePathRecord path)
        => new()
        {
            PathId = path.PathId,
            WorkspaceId = path.WorkspaceId,
            HostPath = path.HostPath,
            IsDefault = path.IsDefault,
            SortOrder = path.SortOrder,
            CreatedAtUtc = FormatUtc(path.CreatedAtUtc),
            UpdatedAtUtc = FormatUtc(path.UpdatedAtUtc),
        };

    private static AgentWorkspaceDocumentRecord ToDomain(BuilderWorkspaceDocument document)
        => new(
            document.DocumentId,
            document.WorkspaceId,
            document.FilePath,
            checked((int)document.SortOrder),
            ParseUtc(document.CreatedAtUtc),
            ParseUtc(document.UpdatedAtUtc));

    private static BuilderWorkspaceDocument ToWire(AgentWorkspaceDocumentRecord document)
        => new()
        {
            DocumentId = document.DocumentId,
            WorkspaceId = document.WorkspaceId,
            FilePath = document.FilePath,
            SortOrder = document.SortOrder,
            CreatedAtUtc = FormatUtc(document.CreatedAtUtc),
            UpdatedAtUtc = FormatUtc(document.UpdatedAtUtc),
        };

    private static BuilderWorkspaceExecutionProjection ToDomain(Wire.BuilderWorkspaceExecution execution)
        => new(
            ToDomain(execution.Workspace),
            ToDomain(execution.Binding),
            ToDomain(execution.Target),
            ToDomain(execution.Scope),
            ToDomain(execution.Shell),
            execution.SupportsPathMapping,
            execution.SupportsPathEnvironment);

    private static Wire.BuilderWorkspaceExecution ToWire(BuilderWorkspaceExecutionProjection execution)
        => new Wire.BuilderWorkspaceExecution
        {
            Workspace = ToWire(execution.Workspace),
            Binding = ToWire(execution.Binding),
            Target = ToWire(execution.Target),
            Scope = ToWire(execution.Scope),
            Shell = ToWire(execution.Shell),
            SupportsPathMapping = execution.SupportsPathMapping,
            SupportsPathEnvironment = execution.SupportsPathEnvironment,
        };

    private static AgentWorkspaceBindingRecord ToDomain(BuilderWorkspaceBinding binding)
        => new(
            binding.BindingId,
            binding.WorkspaceId,
            binding.ExtensionPointId,
            binding.ContributionId,
            binding.Role,
            binding.IsEnabled,
            checked((int)binding.SortOrder),
            ParseUtc(binding.CreatedAtUtc),
            ParseUtc(binding.UpdatedAtUtc));

    private static BuilderWorkspaceBinding ToWire(AgentWorkspaceBindingRecord binding)
        => new()
        {
            BindingId = binding.BindingId,
            WorkspaceId = binding.WorkspaceId,
            ExtensionPointId = binding.ExtensionPointId,
            ContributionId = binding.ContributionId,
            Role = binding.Role,
            IsEnabled = binding.IsEnabled,
            SortOrder = binding.SortOrder,
            CreatedAtUtc = FormatUtc(binding.CreatedAtUtc),
            UpdatedAtUtc = FormatUtc(binding.UpdatedAtUtc),
        };

    private static AgentExecutionTargetDescriptor ToDomain(BuilderExecutionTarget target)
        => new(
            target.TargetKind,
            target.TargetId,
            target.DisplayName,
            target.Description,
            target.SupportsShell,
            target.SupportsFiles)
        {
            Facets = target.Facets,
        };

    private static BuilderExecutionTarget ToWire(AgentExecutionTargetDescriptor target)
        => new()
        {
            TargetKind = target.TargetKind,
            TargetId = target.TargetId,
            DisplayName = target.DisplayName,
            Description = target.Description,
            SupportsShell = target.SupportsShell,
            SupportsFiles = target.SupportsFiles,
            Facets = target.Facets,
        };

    private static AgentExecutionScopeDescriptor ToDomain(BuilderExecutionScope scope)
        => new(
            scope.DisplayName,
            scope.WorkspacePaths,
            scope.DefaultWorkingDirectory,
            scope.PathStyleDescription);

    private static BuilderExecutionScope ToWire(AgentExecutionScopeDescriptor scope)
        => new()
        {
            DisplayName = scope.DisplayName,
            WorkspacePaths = scope.WorkspacePaths,
            DefaultWorkingDirectory = scope.DefaultWorkingDirectory,
            PathStyleDescription = scope.PathStyleDescription,
        };

    private static AgentExecutionShellDescriptor ToDomain(BuilderExecutionShell shell)
        => new(
            shell.ShellId,
            shell.DisplayName,
            shell.ExecutablePath,
            shell.SyntaxKind,
            shell.Description);

    private static BuilderExecutionShell ToWire(AgentExecutionShellDescriptor shell)
        => new()
        {
            ShellId = shell.ShellId,
            DisplayName = shell.DisplayName,
            ExecutablePath = shell.ExecutablePath,
            SyntaxKind = shell.SyntaxKind,
            Description = shell.Description,
        };

    private static BuilderProcessResult ToDomain(Wire.BuilderProcessResult process)
        => new(
            checked((int)process.ExitCode),
            process.StandardOutput,
            process.StandardError,
            process.WasTruncated);

    private static Wire.BuilderProcessResult ToWire(BuilderProcessResult process)
        => new()
        {
            ExitCode = process.ExitCode,
            StandardOutput = process.StandardOutput,
            StandardError = process.StandardError,
            WasTruncated = process.WasTruncated,
        };

    private static AgentExecutionPathMapping ToDomain(BuilderPathMapping mapping)
        => new(mapping.ExecutionPath, mapping.HostPath, mapping.IsInsideAllowedRoot);

    private static BuilderPathMapping ToWire(AgentExecutionPathMapping mapping)
        => new()
        {
            ExecutionPath = mapping.ExecutionPath,
            HostPath = mapping.HostPath,
            IsInsideAllowedRoot = mapping.IsInsideAllowedRoot,
        };

    private static string FormatUtc(DateTimeOffset value)
        => value.ToUniversalTime().ToString(
            "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",
            CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseUtc(string value)
        => DateTimeOffset.Parse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
}
