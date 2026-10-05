namespace Sunder.Package.Agent.Contracts.Contracts;

/// <summary>
/// Indicates that <see cref="IAgentExecutionTarget.ReadFileAsync"/> natively validates and applies a requested one-based line offset and limit.
/// </summary>
/// <remarks>
/// Implementations return range metadata on successful file reads, set truncation when unread lines remain, and return a structured read error
/// for invalid or out-of-bounds ranges. The marker introduces no separate resource ownership or threading rules; all behavior remains governed
/// by <see cref="IAgentExecutionTarget"/>.
/// </remarks>
public interface IAgentRangedFileExecutionTarget : IAgentExecutionTarget
{
}
