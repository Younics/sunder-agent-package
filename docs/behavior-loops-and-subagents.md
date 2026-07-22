# Behavior Loops, Child Runs, And Subagents

A behavior loop controls one Agent run's provider/tool cycle. It is the most privileged orchestration extension: incorrect checkpoint, cancellation, tool, or trust handling can corrupt user-visible run semantics. Prefer providers, tools, or prompt contributors unless the orchestration itself must change.

## Behavior Loop Contract

Implement `IAgentBehaviorLoop` and register it through `PackageExtensionPoints.BehaviorLoops`.

```csharp
public sealed class AcmeBehaviorLoop : IAgentBehaviorLoop
{
    public AgentBehaviorLoopDescriptor Descriptor { get; } = new(
        "acme-review",
        "Acme Review",
        "Runs the Acme review workflow.",
        SourceId: "com.acme.sunder.agent.review",
        FeatureKinds: ["review"]);

    public ValueTask<AgentBehaviorLoopResult> RunAsync(
        AgentBehaviorLoopContext context,
        IAgentBehaviorLoopRuntime runtime,
        CancellationToken cancellationToken = default)
    {
        // Orchestration omitted. Use only the bounded runtime operations below.
        throw new NotImplementedException();
    }
}
```

`LoopId` plus optional `SourceId` is persisted on profiles. Keep both stable. `SettingsSchemaJson` is a loop-owned schema payload; validate profile settings defensively and preserve unknown values when evolving it.

When no selected loop is available, Agent falls back to the registered `default` loop and finally to its built-in default object. Never publish a custom loop with `LoopId = "default"`.

## Runtime Boundary

`IAgentBehaviorLoopRuntime` is the complete supported host surface for a loop:

- Confirm active run ownership with `IsCurrentRun`.
- Read persisted transcript snapshots.
- Build trusted instructions plus separately labeled supplementary context.
- List currently ready and profile-assigned tools.
- Create the selected provider's `IChatClient`.
- Save durable checkpoints.
- Write/complete an assistant turn.
- invoke tools through the permission and persistence pipeline.
- Publish lifecycle events.
- Emit structured package events.

Do not cast the runtime to an Agent implementation class or reference `Sunder.Package.Agent`. The only optional host capability intended for a wrapper loop is `IAgentInnerBehaviorLoopRuntime`, whose `RunDefaultLoopAsync` delegates to the base loop while preserving host invariants.

## Required Invariants

- Honor the supplied cancellation token at every async boundary.
- Check current run ownership before and after slow external work; stale revisions must not write.
- Persist assistant content only through runtime methods.
- Invoke every tool through `InvokeToolAsync`/`InvokeToolsAsync`; never call source/tool instances directly.
- Keep tool call ids stable and pair every persisted call with its result.
- Do not put supplementary context into provider system instructions.
- Return the checkpoint that represents the final state for this revision.
- Match `AgentBehaviorLoopCompletionKind` to the checkpoint/run state.
- Publish applicable lifecycle events exactly where the loop establishes the corresponding durable state.
- Bound provider cycles, tool calls, repeated no-progress requests, wall-clock time, output, and prompt growth.
- Treat provider/tool output as untrusted data.

Unhandled loop exceptions are converted by the outer run coordinator into a failed checkpoint and assistant-visible failure. This prevents process failure, but it cannot repair side effects or malformed transcript writes.

## Default Loop

The base loop performs:

1. Prompt projection and working-summary refresh.
2. Trusted system prompt composition.
3. Lower-trust context serialization into a user-role message.
4. Ready/assigned native tool discovery.
5. Provider streaming with durable assistant-turn projection.
6. Permission-aware tool execution, with parallel calls only when provider support and `ParallelSafe` descriptors allow it.
7. Prompt refresh after tools.
8. Completion, approval suspension, stop, interruption, failure, no-progress, and budget transitions.

The default loop owns core run budgets and transcript/tool pairing. A custom loop that replaces it must supply equivalent safety rather than assuming the provider SDK does so.

## Child Run Service Port

The base Agent publishes one `IAgentChildRunExecutor` through `PackageExtensionPoints.ChildRunExecutors`. Orchestration packages consume it; they must not register a competing executor.

`AgentChildRunRequest` requires:

- Parent session id.
- Parent run id and exact revision.
- Parent tool call id.
- Workspace id.
- Optional stable task id.
- An immutable child profile snapshot.
- User message/title and agent kind.

The base executor validates the parent, persists the child profile as internal, creates or resolves a child session, correlates it to the parent run/tool call, uses the parent's workspace, queues the child message, and returns terminal or waiting state plus bounded assistant content.

Use a stable `TaskId` when retries should resolve the same logical child session. A task id is scoped with parent/profile correlation; it is not a global idempotency token. Never reuse parent correlation against a newer run revision.

Child sessions participate in the permission hierarchy. Session approvals and Unrestricted Mode can be inherited from the parent/root session. This is intentional authority propagation, so child profiles must contain only capabilities the user selected for that child.

## First-Party Subagents

`sunder.package.agent.subagents` composes several public extension points:

- A selectable `subagent` capability provider.
- Dynamic `task` and `delegate_tasks` tools.
- Trusted tool-use instructions through a system prompt contributor.
- The `orchestrated` behavior loop.

The orchestrated loop is a thin wrapper: it requires `IAgentInnerBehaviorLoopRuntime` and delegates to the base default loop. It does not rediscover or recursively decorate another behavior loop. Child profile snapshots explicitly select the default loop, preventing accidental orchestration recursion.

### Profile And Capability Inheritance

A child profile snapshot:

- Uses a subagent chat provider/model override when configured, otherwise the parent chat binding.
- Carries the parent embedding binding.
- Uses the subagent's instructions and selectable capability assignments.
- Is internal and identified by a hash of relevant parent/subagent configuration.
- Uses the base default behavior loop.

Changing parent or subagent configuration creates a distinct profile snapshot identity rather than mutating the historical child run's meaning.

### Single And Batch Delegation

`task` launches one enabled subagent. `delegate_tasks` launches up to three tasks concurrently, but only when every selected subagent can be proven read-only. Any unresolved assignment, group assignment, or mutating tool makes batch delegation ineligible; use single-task delegation instead.

If a child waits for approval, the tool result uses `child-waiting-for-approval` and the parent durable run suspends/join state rather than pretending the child completed. Parent continuation work is durable, claimed with run/revision tokens, and resumed only after all required child results are recorded. Process restart can drain pending continuation work.

## Building An Orchestration Package

1. Register a stable behavior loop only if profile-level selection is required.
2. Consume the base `RuntimeCatalogs` contribution for immutable profile/session lookup.
3. Consume the base `ChildRunExecutors` contribution for child state and correlation.
4. Expose delegation through an `IAgentToolSource` so normal profile assignment and tool security apply.
5. Pass the current `AgentToolExecutionContext` correlation into each child request.
6. Use structured payloads to retain child session ids/status, but bound content returned to the parent model.
7. Treat waiting, failed, stopped, and interrupted child states distinctly.
8. Make retries and process restart safe.

## Tests

- Loop selection/fallback by loop and source ids.
- Stale run revision and cancellation during provider/tool writes.
- Checkpoint/completion-kind consistency.
- Tool invocation only through Runtime permissions.
- Wrapper behavior with and without `IAgentInnerBehaviorLoopRuntime`.
- Child parent/workspace/tool-call correlation and stable task retries.
- Child approval suspension, parent continuation, restart recovery, and superseding runs.
- Capability inheritance and prevention of recursive orchestration.
- Batch read-only proof, maximum count, mixed success/wait/failure, and result bounds.

See `tests/Sunder.Package.Agent.Tests/AgentRunCoordinatorTests.cs`, `SubagentRuntimeOrchestrationTests.cs`, and `AgentLocalStoreDurableRunTests.cs`. Next: [Testing extensions](testing-extensions.md).
