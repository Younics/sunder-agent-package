namespace Sunder.Package.Agent.Contracts.Contracts;

/// <summary>
/// Indicates that <see cref="IAgentExecutionTarget.ReadFileAsync"/> validates and applies the requested one-based line offset and limit before returning file content.
/// Implementations return range metadata on successful file reads and a structured read error for invalid or out-of-bounds ranges.
/// </summary>
public interface IAgentRangedFileExecutionTarget : IAgentExecutionTarget
{
}
