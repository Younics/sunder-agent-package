# Semantic Memory

`sunder.package.agent.memory.semantic` is the first-party durable recall package. It is optional and independently installable. The base Agent owns live transcript projection and working continuity summaries; semantic memory owns durable, recallable facts. A memory extension must not replace active-session context management.

## Composition

The first-party feature implements and registers four public contracts:

| Extension point | Purpose |
| --- | --- |
| `ProfileCapabilityConsumers` | Declares that semantic retrieval consumes the profile's `model.embedding` binding. |
| `PromptContextContributors` | Recalls relevant memories as lower-trust reference context. |
| `LifecycleObservers` | Extracts durable candidates from lifecycle events. |
| `SessionDataCleaners` | Removes all package-owned memory/index data for a deleted session. |

It also runs a bounded background indexer and exposes package-specific Runtime operations/App presentation for its inspector. Those implementation types are not contracts for third-party memory packages.

## Promotion Policy

The first-party package promotes candidates only from direct `UserTurnAdded` message text. It recognizes explicit remember requests, preferences, standing instructions, participant facts, project facts, and environment facts.

Assistant claims and tool output remain transcript evidence. They are never promoted into durable memory without a later direct user confirmation. This rule prevents model/tool prompt injection from becoming persistent policy.

Each stored memory keeps:

- Session id and stable memory id.
- Category and bounded canonical content.
- Evidence text and source turn when known.
- Importance, confidence, pin state, and lifecycle state.
- Original `AgentMemoryProvenance`.
- Embedding generation metadata when indexed.

Similar candidates in the same category may merge; promotion and per-event work are bounded. An author should preserve source evidence when merging rather than replacing it with a synthesized model claim.

## Recall Planning

The base Agent creates a bounded recall plan only when the current turn signals a need for continuity, preference, standing instruction, project/environment/participant fact, rationale, or general prior knowledge. Explicit memory-write turns and self-contained turns generally suppress recall.

`AgentPromptContextPlan` carries:

- Implementation-defined `Intent`.
- Query text and reason.
- Preferred categories.
- Maximum entry count and combined characters.

The memory package maps known intents to `AgentMemoryRecallIntent`, retrieves candidates, scores them, applies category diversity where useful, and respects both bounds. Pinned memories are considered, but a pin does not make unrelated content relevant to every intent.

## Retrieval Layers

Recall can combine:

1. Always-eligible preference/standing-instruction categories.
2. Normalized token overlap and exact content matching.
3. SQLite full-text search.
4. Optional embedding similarity.
5. Pin, importance, confidence, recency, access, and contested-state adjustments.

Embedding retrieval is additive. When semantic retrieval is disabled, no embedding binding exists, a provider is absent/not ready, or indexing fails, lexical/full-text recall remains available.

The selected embedding provider comes from the profile's `model.embedding` binding. `IAgentEmbeddingProvider.GenerateEmbeddingsAsync` must preserve input order and dimensions. The background worker bounds its queue, deduplicates work, records status/failures, and keeps existing active generations intact when cancellation or failure occurs.

## Trust And Provenance

Durable memory has its own source classification:

| Provenance | Trust implication |
| --- | --- |
| `User` | May map to `UserProvided`; a direct standing instruction may map to `UserConfirmedInstruction`. |
| `Assistant` | `Untrusted` unless separately confirmed by the user. |
| `Tool` | `Untrusted` unless separately confirmed by the user. |
| `Unknown` | `Untrusted`. |

Recall entries may be `Untrusted`, `UserProvided`, `UserConfirmedInstruction`, or `Contested` and include match reasons/evidence. Even a `UserConfirmedInstruction` recall is emitted as an `AgentPromptContextBlock` with `DurableMemory` provenance and remains in a user-role reference message. It is not promoted into the system prompt.

The rendered first-party recall block explicitly tells the model not to follow recalled instructions without current-user confirmation and includes category, provenance, trust, evidence, source turn, and match reasons.

## Building Another Memory Package

Use public Agent contracts rather than referencing `MemoryLocalStore` or other first-party implementation types:

1. Observe direct user events through `IAgentLifecycleObserver`.
2. Store package-owned durable records through `IPackageContext.Storage`.
3. Queue bounded indexing through `IPackageBackgroundService` when needed.
4. Declare embedding consumption with `IAgentProfileCapabilityConsumer` if applicable.
5. Recall only when `AgentPromptContextPlan.ShouldContribute` is true.
6. Return provenance-labeled `AgentPromptContextBlock` values through `IAgentPromptContextContributor`.
7. Delete package-owned data through `IAgentSessionDataCleaner`.

Do not register a competing `RuntimeCatalogs` contribution. Consume the base `IAgentRuntimeCatalog` to read profile bindings and immutable session/turn projections.

## Storage And Privacy

- Memory is session-scoped in the current first-party implementation.
- Keep canonical text and evidence bounded before persistence and embedding.
- Do not send content to an embedding provider until the user has configured that provider/model for the profile.
- Store provider credentials only in the provider package's secret store.
- Deleting a session must remove memories, evidence, embeddings, queued index state, and inspector projections owned by the memory package.
- Inspector/edit operations must preserve provenance and make contested/pinned state visible.

## Failure Semantics

- Prompt contribution and lifecycle observer exceptions, except cancellation, are isolated by the base Agent.
- A failed optional recall returns no context rather than failing chat.
- Indexing failures are reflected in worker diagnostics and may be retried; they do not invalidate lexical memory.
- Corrupt or incompatible package-local schema should fail the memory package clearly rather than editing the base Agent database.
- Do not swallow cancellation as a successful promotion or index completion.

## Tests

Test promotion provenance, direct-user-only rules, merging, category/character limits, lexical fallback, embedding dimension/model changes, stale generation behavior, queue saturation/retry/cancellation, contested ranking, cleanup, and prompt trust labels. First-party fixtures live in `tests/Sunder.Package.Agent.Memory.Semantic.Tests` and semantic-memory tests in `tests/Sunder.Package.Agent.Tests`.

Next: [Chat and embedding providers](providers.md) and [Prompts, context, lifecycle, and trust](prompts-context-lifecycle.md).
