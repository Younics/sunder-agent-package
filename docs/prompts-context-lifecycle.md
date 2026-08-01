# Prompts, Context, Lifecycle, And Trust

Agent 2.x has a hard boundary between privileged system policy, host-reserved user-role instructions, and reference data:

| Channel | Contract | Intended content |
| --- | --- | --- |
| System instructions | `IAgentSystemPromptContributor` and base Runtime policy | Trusted, package-owned policy and capability instructions |
| Reserved supplementary instructions | Selected profile plus owner-verified first-party Files context | User standing instructions and canonical directory-scoped `AGENTS.md` instructions |
| Supplementary reference context | `IAgentPromptContextContributor` | Retrieved, external, transcript-derived, assistant-produced, tool-produced, or ordinary reference data |

Supplementary context is always serialized into a user-role JSON message. The Runtime overwrites effective authority from catalog ownership and host-controlled provenance. An extension cannot gain behavioral authority by setting `Usage`, `Authority`, `HostIdentity`, `SourceId`, or scope metadata. No supplementary block grants permission.

## System Prompt Contributors

Implement `IAgentSystemPromptContributor`, adapt it with `AgentSystemPromptContributorRpc.CreateHandler`, and publish it under `sunder.agent.system.prompt.contributor`.

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

Implement `IAgentPromptContextContributor`, adapt it with `AgentPromptContextContributorRpc.CreateHandler`, and publish it under `sunder.agent.prompt.context.contributor`.

`AgentPromptContextRequest` exposes bounded snapshots:

- Session, run, and current-turn context.
- Selected transcript turns.
- The recent live buffer.
- An `AgentPromptContextPlan` with intent, query, preferred categories, entry count, and character limits.

Respect `ContextPlan.ShouldContribute`, `MaxEntryCount`, and `MaxChars`. A contributor should return `null` when no relevant context exists. Generic recall suppression skips optional contributors; host-reserved profile and scoped safety instructions are standing context and enforce separate fixed bounds.

```csharp
return new AgentPromptContextContribution(
[
    new AgentPromptContextBlock(
        "Acme Search Result",
        boundedExternalText,
        Priority: 50,
        SourceId: "com.acme.search",
        Provenance: AgentContextProvenance.Tool,
        Trust: AgentContextTrust.Untrusted)
    {
        Usage = AgentPromptContextUsage.Reference,
    },
]);
```

### Usage

| Value | Meaning |
| --- | --- |
| `Reference` | Default. Data that does not independently direct model behavior. |
| `ScopedInstruction` | A request for directory-scoped guidance. It has effect only after the Runtime verifies the Files package owner and canonical scope metadata. |
| `StandingInstruction` | A request for user standing guidance. It has effect only when created by the host-controlled selected-profile workflow. |

Use `ScopedInstruction` only with structured canonical root, subtree, document path, full-content hash, and execution-context identity. It cannot authorize tools, widen configured or approved paths, request secrets, or become standing guidance outside that subtree. Deeper applicable scopes take precedence only within their subtree.

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

`UserProvided` is not equivalent to a system instruction. Profile instructions become host-reserved `StandingInstruction` user-role context only through the explicit profile workflow, not by changing a context block's enum.

Agent orders ordinary reference blocks by descending priority then title, keeps at most 32, and truncates each to 16,000 characters. Host-reserved instructions use a separate 24-block/64,000-character channel and are never partially serialized: overflow fails prompt preparation. JSON includes effective authority and structured scope in addition to source/provenance/trust/usage/content.

The first-party Files source uses scoped blocks for exact `AGENTS.md` files. Workspace-root files are loaded even for shell-only profiles; nested files are claimed only after a structured file read/search target or mutation probe reaches that subtree. Directory listings do not claim descendants, shell commands create no arbitrary path claims, and approved paths outside configured roots never load or persist external instructions. Shell completion refreshes roots and already-known claims only.

Claims are session-local, survive restart, and invalidate on transcript rollback, workspace, binding, target/container identity, or configured-root changes. Discovery accepts at most 64 probes per target call, 64 directories per ancestor chain, 12,000 complete characters per document, 20 rendered documents, and 48,000 rendered scoped characters. Ancestor, document, or reserved-channel overflow fails closed instead of returning partial policy. Claim state is capped at 512 directories and 2 MiB serialized data.

Observed hashes are not marked presented during contribution. After final JSON retention, the Runtime synchronously acknowledges only exact scoped document hashes that survived serialization. Required discovery or acknowledgment failure stops provider progression. A structured Files mutation whose applicable hash is absent, changed, removed, partial, or unacknowledged returns a non-dispatched error with `RequiresPromptContextRefresh = true`; the provider must receive refreshed context and replan before retrying.

This is a structured Files-tool guarantee, not general process mediation. Shell command paths cannot be inferred safely, so shell mutations are not preflight-enforced against scoped instructions. Use `write`, `edit`, or `apply_patch` when that guarantee is required. A patch that changes `AGENTS.md` must contain no other path mutation.

Non-cancellation failures from optional reference contributors are ignored. The owner-verified first-party scoped source is required and its bounded failure is surfaced instead of silently dropping policy.

## Lifecycle Observers

Use `AgentDurableLifecycleObserverRpc.CreateHandler(observer)` under `sunder.agent.durable.lifecycle.observer` for durable package-owned effects. Agent 2.x does not retain the old process-local lifecycle projection.

Durable events are:

- `UserTurnAdded`
- `AssistantTurnCompleted`
- `ToolResultRecorded`
- `RunInterrupted`
- `RunStopped`
- `RunFailed`
- `TranscriptRolledBack`
- `SessionDeleted`
- `WorkspaceDeleted`

The Runtime writes the event and the represented Agent state in one SQLite transaction. Each envelope has a deterministic event id, global monotonic sequence, ordering key, current payload hash, bounded JSON, and parsed payload. Delivery is at least once, so commit the event id in the same package-owned transaction as the derived effect. Do not use sequence alone as a global exactly-once guarantee.

Stored payload JSON is capped at 256 KiB. The Runtime first keeps up to 64 recent turns and then progressively reduces turn, item, and text limits until the envelope fits; the trigger turn receives a larger floor so the transition remains useful. Every retained item whose text, identifiers, tool arguments/result, summary, structured payload, sources, presentation payload, error/backend metadata, or tool provenance was shortened has `WasTruncated = true`. Observers must treat omitted history as unavailable rather than infer that no earlier content exists.

Delivery is serialized per persisted subscription and ordering scope. A root-session event cannot pass an earlier pending, in-flight, or poisoned event for that root. A workspace tombstone also waits for earlier roots in that workspace incarnation. Independent ordering scopes may progress while another scope is retrying.

The subscription identity is owning package id plus stable `ObserverId` and contract kind. On first registration, `ReplayStartSequence` captures the first retained eligible event. A persisted monotonic membership generation, not a wall clock, determines whether that subscription existed when a payload was erased. `ReconciledThroughSequence` advances in batches of at most 256 and survives restart; dispatch and startup lease/poison recovery also process at most 256 rows per pass and re-wake until complete. This bounds startup transactions and worker monopolization without silently dropping active-session history. Newly committed events cannot be claimed ahead of an incomplete historical cursor.

Active source payloads remain available for historical backfill until their session or workspace is deleted. Deletion uses SQLite secure deletion and, in the same transaction as the durable tombstone, replaces earlier payload JSON with the canonical `ContentErased` receipt. Event id, sequence, type, ordering metadata, current receipt hash, and `OriginalPayloadHash` remain for ordering and idempotency. Every then-existing eligible subscription gets a delivery row: a prior delivered row is re-armed, an in-flight lease is version-invalidated, and poison remains an ordering barrier with its diagnostics and cooldown. New subscriptions exclude erased rows and can receive the retained deletion tombstone, never the deleted content.

An observer not seen for 30 days is retired in bounded batches. If the same package, observer id, and contract return, the subscription receives a new monotonic membership and replays retained available history without receiving erasures from its prior membership. Completed deleted-owner history is retained for at least seven days, then compacted in batches only after every eligible non-retired subscription has acknowledged it. Active-session history is never compacted by this policy. Retired and superseded delivery rows are removed independently in bounded batches.

Observer failures never roll back or change source Agent state. Transient failures use bounded exponential delay. After five failed attempts, or immediately for an integrity failure, a delivery enters poison state and remains an ordering barrier. Poison deliveries retry after a five-minute cooldown and are made immediately eligible on Runtime restart; they are not marked delivered or skipped. Stored and logged diagnostics contain only a stable failure code and bounded exception type, never the observer exception message or exception object.

This is observation, not a transactional mutation hook:

- There is no result value that can rewrite Agent state.
- Delivery can repeat after callback failure, lease expiry, process failure, or poison recovery.
- `ContentErased` is a no-op receipt. A handler must acknowledge it without attempting to reconstruct the original payload; `OriginalPayloadHash` permits a prior receipt to be recognized.
- Handlers must be idempotent by event id and tolerate an erasure receipt after a prior attempt saw the source payload.
- Persist durable intent before queueing background work.
- Never treat assistant/tool event content as user-confirmed memory or privileged instruction.
- Never log raw payloads, prompts, tool arguments/results, sources, summaries, or callback exceptions.
- If payload-derived data is retained, minimize and encrypt it as appropriate and implement `IAgentSessionDataCleaner` so Agent deletion also removes package-owned copies.

For substantial indexing, commit bounded package-owned work with the event receipt, then let a bounded package background service process it. Returning before that durable intent commits can lose work; performing unbounded indexing in the callback can delay replay and hold ordering barriers.

## Session Data Cleanup

An extension that stores session-keyed data should also implement `IAgentSessionDataCleaner` and publish `AgentSessionCleanerRpc.CreateHandler(cleaner)` under `sunder.agent.session.cleaner`.

`DeleteSessionData(Guid sessionId)` must be synchronous, bounded, idempotent, and limited to package-owned state. Agent persists one ids-only job per owning package id, stable `CleanerId`, and deleted session in the same transaction as authoritative deletion. A worker acquires the exact package activation before invocation. Failure or package retirement leaves the job pending with redacted diagnostics; startup and package reactivation retry it until success without rolling back or surfacing an error from the already-committed Agent deletion. If cleanup requires remote work, delete or tombstone local authority in this callback and schedule best-effort remote cleanup separately.

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
