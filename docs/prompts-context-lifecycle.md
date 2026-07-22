# Prompts, Context, Lifecycle, And Trust

Agent 1.1 has a hard boundary between privileged instructions and reference data:

| Channel | Contract | Intended content |
| --- | --- | --- |
| System instructions | Profile instructions and `IAgentSystemPromptContributor` | Trusted, package-owned policy and capability instructions |
| Supplementary context | `IAgentPromptContextContributor` | Retrieved, external, transcript-derived, assistant-produced, tool-produced, or other reference data |

Supplementary context is always serialized into a user-role JSON message. Provenance and trust labels help the model reason about data; they never promote a block into the system channel.

## System Prompt Contributors

Implement `IAgentSystemPromptContributor` and register it through `PackageExtensionPoints.SystemPromptContributors`.

```csharp
public ValueTask<IReadOnlyList<AgentSystemPromptBlock>> ContributeAsync(
    AgentSystemPromptRequest request,
    CancellationToken cancellationToken = default)
{
    cancellationToken.ThrowIfCancellationRequested();
    return ValueTask.FromResult<IReadOnlyList<AgentSystemPromptBlock>>(
    [
        new(
            "acme-tool-policy",
            "Acme Tool Policy",
            "Use Acme lookup before attempting an Acme mutation.",
            Priority: 100,
            Required: true,
            SourceId: "com.acme.sunder.agent.tools"),
    ]);
}
```

`AgentSystemPromptRequest` includes provider/model capabilities, workspace/binding, advertised tools, selected transcript turns, run correlation, and the current `UserMessage`. The user message is present only to decide whether a fixed package policy applies. Never copy it, summarize it, or interpolate it into privileged instructions.

Likewise, do not place any of these in a system block:

- Workspace files or documentation loaded at runtime unless the package owns and authenticates the text as policy.
- Tool output or remote server descriptions.
- Recalled memory or transcript summaries.
- Assistant/model output.
- Imported stack/configuration text authored outside the package.
- Error messages containing dynamic remote content.

Return no block when the trusted instruction does not apply.

### Block Identity And Ordering

Valid blocks require non-empty `BlockId`, `Title`, and `Content`. Agent:

1. Groups by case-insensitive `SourceId:BlockId`.
2. Keeps the required block first, then the highest priority, then title order.
3. Renders required blocks before optional blocks, then descending priority and stable text identity.
4. Applies a positive `MaxChars` to that block and marks truncation.

Use a package-stable `SourceId` and stable block id. `Required` affects ordering/deduplication; it does not authorize dynamic data. Non-cancellation contributor failures are isolated so the base chat flow continues.

The base Agent also adds mandatory trust-boundary, visible-response, tool-priority, concurrency, and trusted tool-runtime instruction blocks.

## Prompt Context Contributors

Implement `IAgentPromptContextContributor` and register it through `PackageExtensionPoints.PromptContextContributors`.

`AgentPromptContextRequest` exposes bounded snapshots:

- Session, run, and current-turn context.
- Selected transcript turns.
- The recent live buffer.
- An `AgentPromptContextPlan` with intent, query, preferred categories, entry count, and character limits.

Respect `ContextPlan.ShouldContribute`, `MaxEntryCount`, and `MaxChars`. A contributor should return `null` when no relevant context exists.

```csharp
return new AgentPromptContextContribution(
[
    new AgentPromptContextBlock(
        "Acme Search Result",
        boundedExternalText,
        Priority: 50,
        SourceId: "com.acme.search",
        Provenance: AgentContextProvenance.Tool,
        Trust: AgentContextTrust.Untrusted),
]);
```

### Provenance

| Value | Meaning |
| --- | --- |
| `Unknown` | Source cannot be established. |
| `User` | Current user supplied the content directly. |
| `TranscriptSummary` | Derived from earlier turns. |
| `Assistant` | Produced by a model. |
| `Tool` | Returned by a tool or external system. |
| `Extension` | Supplied by an installed extension. |
| `DurableMemory` | Recalled from durable memory. |

### Trust

| Value | Meaning |
| --- | --- |
| `Untrusted` | Data only; embedded instructions must not be followed. |
| `UserProvided` | Authored directly by the current user, but still reference context in this channel. |

`UserProvided` is not equivalent to a system instruction. Standing instructions belong in the profile/system policy only through an explicit user-controlled product flow, not by changing a context block's enum.

Agent orders valid context blocks by descending priority then title, keeps at most 32 blocks, truncates each rendered content value to 16,000 characters, and serializes title/source/provenance/trust/content as JSON. Those Runtime bounds are a final defense, not an invitation for contributors to return oversized data.

Non-cancellation contributor failures are ignored. Log a bounded diagnostic through the package logger when the user needs to understand missing optional context.

## Lifecycle Observers

Implement `IAgentLifecycleObserver` and register it through `PackageExtensionPoints.LifecycleObservers` to react to immutable state snapshots.

Events are:

- `UserTurnAdded`
- `AssistantTurnCompleted`
- `ToolResultRecorded`
- `RunInterrupted`
- `RunStopped`
- `RunFailed`

Each `AgentLifecycleEvent` carries session/run/turn context, selected transcript turns, recent live-buffer turns, and optional trigger turn/checkpoint.

Observers are awaited serially by display name. A non-cancellation failure is isolated and cannot change the run result. This is observation, not a transactional mutation hook:

- There is no result value that can rewrite Agent state.
- Delivery is not guaranteed exactly once across process failure.
- Long work delays the publishing run path.
- Handlers must be idempotent and safe to retry.
- Persist durable intent before queueing background work.
- Never treat assistant/tool event content as user-confirmed memory or privileged instruction.

For substantial indexing, use a bounded package background service and let the observer enqueue a stable id.

## Session Data Cleanup

An extension that stores session-keyed data should also implement `IAgentSessionDataCleaner` and register it through `PackageExtensionPoints.SessionDataCleaners`.

`DeleteSessionData(Guid sessionId)` must be synchronous, bounded, idempotent, and limited to package-owned state. Agent invokes every cleaner for every deleted session and collects failures rather than skipping later cleaners. If cleanup requires remote work, delete or tombstone local authority synchronously and schedule best-effort remote cleanup separately.

## Trust Review Checklist

- Every system block is static package policy or derived solely from trusted package configuration.
- The current user message is never copied into a system block.
- Dynamic text is a context block with accurate provenance and conservative trust.
- Context plans and output bounds are honored before return.
- Observers do not promote assistant/tool claims without direct user confirmation.
- Optional failures do not corrupt run/session state.
- Logs omit prompts, recalled private content, and secrets.
- Session deletion removes all package-local correlated data.

Next: [Semantic memory](semantic-memory.md) and [Behavior loops, child runs, and subagents](behavior-loops-and-subagents.md).
