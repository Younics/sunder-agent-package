# RPC Contract Catalog

Agent 2.x exposes every cross-package capability through exactly 18 schema-first Sunder RPC contracts. There is no second object-discovery path. Each provider declaration has a stable provider id, contract id, contract version, host-stamped owner, and opaque endpoint reference.

## Contract Rules

- Bundle every imported or provided descriptor locally. `Sunder.Package.Agent.Protocol` supplies all 18 first-party descriptors through `buildTransitive` assets.
- Declare providers with `SunderRpcProvider` and publish the matching handler with `RegisterRpcProvider`.
- Declare consumers with `SunderUsesContract`; discovery and invocation remain default-denied unless the package manifest grants the relevant action.
- Retain endpoint references, not provider objects. A reference names one exact activation and becomes stale when that activation retires.
- The host validates each request, response, and stream event against the selected method schema before forwarding it.
- Provider retirement cancels in-flight work with `StaleEndpoint` and waits for exact-activation calls to drain.
- Catalog order is not a precedence API. Resolve semantic ids explicitly and reject or deterministically handle duplicates.
- Runtime catalog, run control, workspace resolution, and child execution are base-Agent-owned ports. Peer packages consume them and must not publish competing providers.

## Provider Contracts

| Contract id | Service | Typical owner | Shape |
| --- | --- | --- | --- |
| `sunder.agent.chat.provider` | `chat-provider` | Model provider package | Metadata/readiness unary calls plus bounded chat event stream |
| `sunder.agent.embedding.provider` | `embedding-provider` | Model provider package | Metadata, space identity, single and batch generation |
| `sunder.agent.behavior.loop` | `behavior-loop` | Base Agent or orchestration package | Loop descriptor and one run invocation |
| `sunder.agent.tool.source` | `tool-source` | Tool, MCP, skill, or subagent package | Discovery, readiness, preflight, permission, execution, presentation |
| `sunder.agent.execution.target` | `execution-target` | Local, container, or remote execution package | Declared execution facets and bounded operations |
| `sunder.agent.permission.surface` | `permission-surface` | Tool or execution package | Stable action and boundary declarations |
| `sunder.agent.system.prompt.contributor` | `system-prompt-contributor` | Trusted instruction package | Identity and trusted prompt blocks |
| `sunder.agent.prompt.context.contributor` | `prompt-context-contributor` | Memory, skill, files, or subagent package | Reference context and optional acknowledgment |
| `sunder.agent.durable.lifecycle.observer` | `durable-lifecycle-observer` | Package with durable derived state | Identity and at-least-once lifecycle delivery |
| `sunder.agent.profile.capability.consumer` | `profile-capability-consumer` | Capability-consuming package | Static profile capability declaration |
| `sunder.agent.selectable.capability.provider` | `selectable-capability-provider` | MCP, skill, or subagent package | Capability list plus change stream |
| `sunder.agent.session.cleaner` | `session-cleaner` | Package with session-local state | Identity and idempotent session deletion |
| `sunder.agent.workspace.path.migrator` | `workspace-path-migrator` | Execution package | Prior-path discovery and completion |
| `sunder.agent.workspace.editor` | `workspace-editor` | Execution package | Editor sections and validated save requests |

## Base-Agent Ports

| Contract id | Service | Purpose |
| --- | --- | --- |
| `sunder.agent.runtime.catalog` | `runtime-catalog` | Bounded session, profile, workspace, transcript, and change projections |
| `sunder.agent.run.control` | `run-control` | Invocation-scoped behavior-loop operations and chat stream |
| `sunder.agent.workspace.execution.resolver` | `workspace-execution-resolver` | Resolve a workspace to one exact execution-target endpoint |
| `sunder.agent.child.run.executor` | `child-run-executor` | Launch and correlate durable child sessions |

## Failure Semantics

- Use typed readiness or result failures for expected provider conditions.
- Let caller cancellation propagate.
- Treat `StaleEndpoint` as exact-provider retirement; rediscover only when the operation is safe to retry against a replacement.
- Treat schema validation failures as protocol defects, not transient provider failures.
- Durable lifecycle handlers must be idempotent by event id because delivery is at least once.
- Session cleaners must be bounded and idempotent because pending jobs survive restart and provider absence.

Generated DTOs, clients, and provider interfaces live under `Sunder.Package.Agent.Protocol.Generated.*`. First-party adapters in `Sunder.Package.Agent.Protocol` map those wire shapes to package-local implementation interfaces. Every first-party package registers handlers only through `RegisterRpcProvider` using provider ids declared in its generated manifest.
