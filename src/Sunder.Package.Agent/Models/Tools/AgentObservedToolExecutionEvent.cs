using Sunder.Sdk.Services;

namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>Reports dispatch acknowledgement before the terminal tool result.</summary>
[SunderUnion(typeof(AgentToolDispatchedEvent), typeof(AgentToolExecutionCompletedEvent))]
[SunderDiscriminator("kind")]
public abstract record AgentObservedToolExecutionEvent;

[SunderVariant("dispatched")]
public sealed record AgentToolDispatchedEvent : AgentObservedToolExecutionEvent;

[SunderVariant("result")]
public sealed record AgentToolExecutionCompletedEvent(AgentToolResult Result) : AgentObservedToolExecutionEvent;
