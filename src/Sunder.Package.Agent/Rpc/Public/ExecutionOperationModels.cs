using System.Text.Json.Serialization;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Rpc.Public;

public sealed class AgentShellCommandRequest
{
    public required CommandText Command { get; init; }
    public SunderRpcOptional<TimeoutSeconds> TimeoutSeconds { get; init; }
    public SunderRpcOptional<PathText> WorkingDirectory { get; init; }
}

public sealed class ExecutionShellRequest
{
    public required AgentExecutionTargetContextWire Context { get; init; }
    public required AgentShellCommandRequest Request { get; init; }
}

public sealed class AgentShellCommandResult
{
    public required ProcessExitCode ExitCode { get; init; }
    public required ContentText Output { get; init; }
    public required Flag TimedOut { get; init; }
    public required Flag WasTruncated { get; init; }
    public SunderRpcOptional<PathText> WorkingDirectory { get; init; }
}

public sealed class AgentFileReadRequest
{
    public SunderRpcOptional<PageSize> Limit { get; init; }
    public SunderRpcOptional<SignedInteger> Offset { get; init; }
    public required PathText Path { get; init; }
}

public sealed class ExecutionFileReadRequest
{
    public required AgentExecutionTargetContextWire Context { get; init; }
    public required AgentFileReadRequest Request { get; init; }
}

public sealed class AgentFileReadResult
{
    public required ContentText Content { get; init; }
    public SunderRpcOptional<SignedInteger> EndLine { get; init; }
    public SunderRpcOptional<TextValue> ErrorCode { get; init; }
    public SunderRpcOptional<DisplayText> ErrorMessage { get; init; }
    public required Flag IsDirectory { get; init; }
    public required Flag IsError { get; init; }
    public required PathText Path { get; init; }
    public SunderRpcOptional<SignedInteger> StartLine { get; init; }
    public SunderRpcOptional<SignedInteger> TotalLines { get; init; }
    public required Flag WasTruncated { get; init; }
}

public sealed class AgentFileWriteRequest
{
    public required ContentText Content { get; init; }
    public SunderRpcOptional<Sha256Text> ExpectedContentHash { get; init; }
    public required Flag Overwrite { get; init; }
    public required PathText Path { get; init; }
}

public sealed class ExecutionFileWriteRequest
{
    public required AgentExecutionTargetContextWire Context { get; init; }
    public required AgentFileWriteRequest Request { get; init; }
}

public sealed class AgentFileMutationResult
{
    public SunderRpcOptional<TextValue> ErrorCode { get; init; }
    public required Flag IsError { get; init; }
    public required PathText Path { get; init; }
    public required DisplayText Summary { get; init; }
}

public sealed class AgentFileMutationResultWire
{
    public SunderRpcOptional<AgentResolvedResourceWire> PostMutationResource { get; init; }
    public required AgentFileMutationResult Result { get; init; }
}

public sealed class AgentFileDeleteRequest
{
    public SunderRpcOptional<Sha256Text> ExpectedContentHash { get; init; }
    public required PathText Path { get; init; }
    public required Flag Recursive { get; init; }
}

public sealed class ExecutionFileDeleteRequest
{
    public required AgentExecutionTargetContextWire Context { get; init; }
    public required AgentFileDeleteRequest Request { get; init; }
}

public sealed class AgentProcessCommandRequest
{
    public required TextValues Arguments { get; init; }
    public required TextValue FileName { get; init; }
    public SunderRpcOptional<TimeoutSeconds> TimeoutSeconds { get; init; }
    public SunderRpcOptional<PathText> WorkingDirectory { get; init; }
}

public sealed class ExecutionProcessRequest
{
    public required AgentExecutionTargetContextWire Context { get; init; }
    public required AgentProcessCommandRequest Request { get; init; }
}

public sealed class AgentExecutionPathMapping
{
    public required PathText ExecutionPath { get; init; }
    public required PathText HostPath { get; init; }
    public required Flag IsInsideAllowedRoot { get; init; }
}

public enum AgentFileSearchKind
{
    [JsonStringEnumMemberName("grep")] Grep,
    [JsonStringEnumMemberName("glob")] Glob,
}

public sealed class AgentFileSearchRequest
{
    public SunderRpcOptional<TextValue> Include { get; init; }
    public required AgentFileSearchKind Kind { get; init; }
    public required PathText Path { get; init; }
    public required TextValue Pattern { get; init; }
}

public sealed class ExecutionFileSearchRequest
{
    public required AgentExecutionTargetContextWire Context { get; init; }
    public required AgentFileSearchRequest Request { get; init; }
}

public sealed class AgentFileSearchMatch
{
    public SunderRpcOptional<SignedInteger> LineNumber { get; init; }
    public required PathText Path { get; init; }
    public SunderRpcOptional<TextValue> Text { get; init; }
}

[SunderRpcTransparent]
public sealed class AgentFileSearchMatchCollection
{
    public required IReadOnlyList<AgentFileSearchMatch> Value { get; init; }
}

public sealed class AgentFileSearchResult
{
    public SunderRpcOptional<TextValue> ErrorCode { get; init; }
    public SunderRpcOptional<DisplayText> ErrorMessage { get; init; }
    public required Flag IsError { get; init; }
    public required AgentFileSearchMatchCollection Matches { get; init; }
    public required Flag WasTruncated { get; init; }
}
