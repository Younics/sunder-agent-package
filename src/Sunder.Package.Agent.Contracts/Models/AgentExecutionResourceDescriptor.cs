namespace Sunder.Package.Agent.Contracts.Models;

public sealed record AgentExecutionResourceRequest(
    Guid? SessionId,
    AgentProfileRecord? Profile,
    AgentWorkspaceRecord? Workspace,
    AgentWorkspaceBindingRecord? ExecutionBinding);

public sealed record AgentExecutionResourceDescriptor(
    string ResourceId,
    string ResourceKind,
    string SourceId,
    string DisplayName,
    string HostPath,
    string PreferredExecutionPath,
    AgentExecutionResourceAccessMode AccessMode = AgentExecutionResourceAccessMode.ReadOnly,
    IReadOnlyDictionary<string, string>? Metadata = null);

public sealed record AgentResolvedExecutionResource(
    string ResourceId,
    string ResourceKind,
    string SourceId,
    string DisplayName,
    string HostPath,
    string ExecutionPath,
    AgentExecutionResourceAccessMode AccessMode = AgentExecutionResourceAccessMode.ReadOnly,
    IReadOnlyDictionary<string, string>? Metadata = null);

public enum AgentExecutionResourceAccessMode
{
    ReadOnly = 0,
    ReadWrite = 1,
}
