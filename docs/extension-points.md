# Extension-Point Catalog

`PackageExtensionPoints` defines 18 typed Agent extension points. This catalog records their role, data-flow direction, effective cardinality, ordering, ownership, and failure behavior in the 1.1 implementation.

## Catalog Rules

- **Role is local.** Runtime and App have separate `IPackageExtensionCatalog` instances. Register Runtime behavior through `ISunderRuntimeContributionRegistry`; register App presentation through `ISunderAppContributionRegistry`.
- **The registrar owns the contribution.** `RegisterExtension` attributes every instance to the activating package. The host retains it for that activation and removes it on deactivation. `GetExtensionContributions` is the authoritative owner lookup.
- **Catalog order is deterministic host order, not a precedence API.** Agent consumers often sort or select by stable ids. Never depend on package activation order to win an id collision.
- **Duplicate semantic ids are not rejected at registration.** Depending on the consumer, the first match wins or later entries are deduplicated. Use globally stable ids and test with other installed packages.
- **Cancellation propagates.** Optional pipelines that isolate contributor failures still rethrow `OperationCanceledException`.
- **Base ports are not open competitions.** `RuntimeCatalogs`, `WorkspaceExecutionResolvers`, and `ChildRunExecutors` are published by `sunder.package.agent` for peer packages to consume. Do not register a competing implementation.

Cardinality below describes the effective active catalog. `0..N` means any number of packages may contribute. `1 base` means peers should consume the single base-Agent contribution with `FirstOrDefault`-style absence handling.

## Providers And Orchestration

| Extension point and contract | Role | Direction | Cardinality | Agent ordering/resolution | Ownership | Failure semantics |
| --- | --- | --- | --- | --- | --- | --- |
| `ChatProviders` (`sunder.package.agent:chat-providers`), `IAgentChatProvider` | Runtime | Extension -> Agent | `0..N` | UI lists by descriptor display name. A profile binding resolves the first case-insensitive `ProviderId` match. | Provider package owns the instance; set descriptor `PackageId` from `IPackageContext.PackageId`, but catalog ownership remains authoritative. | Non-ready state should be returned from `GetReadinessAsync`. Selected-provider exceptions fail or interrupt the run; `AgentChatProviderException` supplies safe visible failure content. |
| `EmbeddingProviders` (`sunder.package.agent:embedding-providers`), `IAgentEmbeddingProvider` | Runtime | Extension -> Agent and memory packages | `0..N` | UI lists by display name. Bindings resolve the first case-insensitive `ProviderId` match. | Embedding package owns the instance. | Non-ready or missing providers make semantic retrieval unavailable without disabling lexical memory. Generation failures are recorded by the memory indexing worker; cancellation propagates. |
| `BehaviorLoops` (`sunder.package.agent:behavior-loops`), `IAgentBehaviorLoop` | Runtime | Extension -> Agent | `1..N`, including base default | UI lists by display name. Runtime selects first `LoopId` plus optional `SourceId`; otherwise it falls back to the registered default, then the built-in default object. | Loop package owns custom loops; base Agent owns `default`. | An unhandled custom-loop exception is converted into a failed run and visible assistant failure turn. The loop must return a checkpoint and matching completion kind. |
| `ProfileCapabilityConsumers` (`sunder.package.agent:profile-capability-consumers`), `IAgentProfileCapabilityConsumer` | Runtime | Extension -> Agent profile editor | `0..N` | Catalog order; Agent tests whether any consumer declares a case-insensitive capability kind. | Consumer package owns its declaration. | Synchronous declaration failures are not isolated by the catalog and reach the caller. Keep declarations static and side-effect free. |
| `ProfileSelectableCapabilityProviders` (`sunder.package.agent:profile-selectable-capability-providers`), `IAgentProfileSelectableCapabilityProvider` | Runtime | Extension -> Agent profile editor | `0..N` | Providers by display name; results are validated, deduplicated by kind/source/id, then sorted by capability display name. Optional `IAgentProfileSelectableCapabilityChangeNotifier` refreshes active views. | Provider package owns descriptors and notifier subscriptions for its activation. | Listing exceptions fail that catalog refresh. Raise change notifications only after state is durable; do not throw from event accessors or callbacks. |

## Base-Agent Service Ports

| Extension point and contract | Role | Direction | Cardinality | Agent ordering/resolution | Ownership | Failure semantics |
| --- | --- | --- | --- | --- | --- | --- |
| `RuntimeCatalogs` (`sunder.package.agent:runtime-catalogs`), `IAgentRuntimeCatalog` | Runtime | Agent -> peer extensions | `1 base` | Consumers use the first contribution. | Base Agent owns session/profile/workspace projections and events. | Absence means Agent is inactive; peers should report unavailable and avoid mutation. Method exceptions propagate to the peer operation. |
| `WorkspaceExecutionResolvers` (`sunder.package.agent:workspace-execution-resolvers`), `IAgentWorkspaceExecutionResolver` | Runtime | Agent -> peer extensions | `1 base` | Consumers use the first contribution. | Base Agent owns workspace/binding resolution; returned execution target remains owned by its package. | `ResolveAsync` throws for missing workspace, binding, target, readiness, or execution scope. Callers should present the message and preserve cancellation. |
| `ChildRunExecutors` (`sunder.package.agent:child-run-executors`), `IAgentChildRunExecutor` | Runtime | Agent -> orchestration extensions | `1 base` | Consumers use the first contribution. | Base Agent owns durable child sessions, run state, and parent correlation. | Absence disables child runs. Validation or run failures are returned/thrown through the invoking tool pipeline; parent/run revisions must not be fabricated. |

## Tools, Execution, And Workspace Editing

| Extension point and contract | Role | Direction | Cardinality | Agent ordering/resolution | Ownership | Failure semantics |
| --- | --- | --- | --- | --- | --- | --- |
| `Tools` (`sunder.package.agent:tools`), `IAgentTool` | Runtime | Extension -> Agent | `0..N` | Fixed tools are wrapped by the installed-package source, ordered by display name for lookup, then advertised by priority, display name, and id. First case-insensitive `ToolId` wins. | Tool package owns each fixed tool. | Discovery/readiness exceptions can fail run preparation. Execution exceptions are converted to error `AgentToolResult`; caller cancellation is rethrown. |
| `ToolSources` (`sunder.package.agent:tool-sources`), `IAgentToolSource` | Runtime | Extension -> Agent | `0..N` | Sources by display name; advertised tools by priority, display name, and id. Execution re-resolves the exact advertised source/id/read-only identity. | Source package owns discovered descriptors and execution. Returned descriptors are snapshots, not host-owned services. | Discovery/readiness exceptions can fail preparation. Execute exceptions become error tool results. A tool that is no longer ready, assigned, or identically advertised is denied. |
| `ExecutionTargets` (`sunder.package.agent:execution-targets`), `IAgentExecutionTarget` | Runtime | Extension -> Agent and workspace tools | `0..N` | UI sorts by display name, kind, and id. A binding resolves the first target whose `TargetId` or `TargetKind` matches `ContributionId`. | Target package owns execution and any resources it creates. Agent owns workspace/binding records. | Return `NeedsConfiguration` or `Failed` readiness for expected conditions. Unexpected exceptions reach the requesting tool/run; never silently widen path scope. |
| `WorkspaceEditorContributors` (`sunder.package.agent:workspace-editor-contributors`), `IAgentWorkspaceEditorContributor` | Runtime and App, role-local | Execution package -> Agent editor | `0..N` per role | App uses host order, filters with `CanEdit`, and appends sections in returned order. First failed save stops the sequence. | Each role owns its own contributor. App contributors should be presentation/proxy objects; Runtime owns settings and mutation. | A discovery exception puts the whole editor refresh in an error state. Return `AgentEditorSaveResult.Failed` for expected validation errors. |
| `WorkspacePathMigrationContributors` (`sunder.package.agent:workspace-path-migration-contributors`), `IAgentWorkspacePathMigrationContributor` | Runtime | Execution package -> Agent workspace store | `0..N` | Host order; applicable path items are aggregated only for workspaces with no current paths. Completion runs after paths are persisted. | Migrator owns only legacy configuration; Agent owns migrated workspace paths. | Per-contributor discovery and cleanup failures are isolated. Failed cleanup does not roll back already persisted paths. Methods must be idempotent. |
| `PermissionSurfaces` (`sunder.package.agent:permission-surfaces`), `IAgentPermissionSurface` | Runtime | Tool/execution package -> Agent permissions | `0..N` | Actions flatten in host order, duplicate `ActionId` keeps first, final UI sorts by display name. | Surface package owns action/boundary definitions; Agent owns user overrides, pending requests, and approvals. | A missing action id is denied. Unknown action/boundary asks rather than allows. Surface enumeration exceptions reach permission evaluation and can fail the tool cycle. |

## Prompting, Observation, And Cleanup

| Extension point and contract | Role | Direction | Cardinality | Agent ordering/resolution | Ownership | Failure semantics |
| --- | --- | --- | --- | --- | --- | --- |
| `SystemPromptContributors` (`sunder.package.agent:system-prompt-contributors`), `IAgentSystemPromptContributor` | Runtime | Extension -> Agent prompt | `0..N` | Contributors by display name. Valid blocks deduplicate by `SourceId:BlockId`, preferring required then higher priority, and render required/high-priority first. | Package owns trusted instruction text and stable block ids. | Non-cancellation exceptions are ignored so optional instructions cannot block chat. Invalid/empty blocks are dropped; `MaxChars` truncates individual content. |
| `PromptContextContributors` (`sunder.package.agent:prompt-context-contributors`), `IAgentPromptContextContributor` | Runtime | Extension -> Agent prompt | `0..N` | Contributors by display name. Blocks later render by descending priority, title, and global count/size bounds. | Package owns source labels; runtime owns final serialization and trust boundary. | Non-cancellation exceptions are ignored. All blocks remain user-role reference data regardless of claimed provenance or trust. |
| `LifecycleObservers` (`sunder.package.agent:lifecycle-observers`), `IAgentLifecycleObserver` | Runtime | Agent -> extension | `0..N` | Observers by display name, awaited serially. | Observer package owns side effects and related storage. Event records are immutable snapshots. | Non-cancellation exceptions are ignored so observers cannot change run outcome. Delivery is not a transaction or exactly-once contract; handlers must be idempotent. |
| `SessionDataCleaners` (`sunder.package.agent:session-data-cleaners`), `IAgentSessionDataCleaner` | Runtime | Agent -> extension | `0..N` | Host order for every deleted session id. | Extension owns only its package-local session data; Agent owns deletion orchestration. | All cleaners are attempted. Failures are collected and reported after cleanup rather than short-circuiting later cleaners. Implement idempotent deletion. |

## Selecting Static Versus Dynamic Contracts

Use the narrowest contract that matches the lifetime:

- Fixed process-local tool: `IAgentTool` through `Tools`.
- Session/profile/workspace-dependent tools: `IAgentToolSource` through `ToolSources`.
- Framework-native dynamic declaration: add `IAgentNativeToolSource` to the source.
- Tool-specific approval request: add `IAgentPermissionAwareTool` or `IAgentPermissionAwareToolSource`.
- Permission settings shown to users: also register `IAgentPermissionSurface` through `PermissionSurfaces`.
- Trusted package policy: `IAgentSystemPromptContributor`.
- Retrieved or external data: `IAgentPromptContextContributor`.
- Read-only reaction to completed state: `IAgentLifecycleObserver`.

Next: [Package family architecture](package-family-architecture.md) and the capability-specific guides in the [documentation index](README.md).
