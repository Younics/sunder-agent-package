# RPC Contract Catalog

AI Agent 2.x owns 18 public contracts under `younics.ai.agent`, each starting its
new contract identity family at `1.0.0`, separate from package version `2.0.0`.
Private contracts follow package version `2.0.0`; the platform-owned
`sunder.stack.contributor` remains `2.0.0`. Four public contracts are owned concrete
services; 14 are declaration-only extension contracts. The third-party integration surface
contains 13 provider contracts and four base-Agent APIs;
`younics.ai.agent.tool.execution.observed` is the reserved first-party bridge.
Each provider declaration has a stable provider id,
contract id/version, host-stamped owner and opaque endpoint reference. See the
[ordinary-service status](ordinary-service-refactor.md).

## Contract Rules

- Select a reviewed Agent archive through the owning package API/provider import commands. Build tooling generates the local descriptor bundles and role-local bindings from the selected lock; there is no Protocol NuGet dependency.
- Declare imported providers with `SunderServiceProvider` from `Sunder.Sdk.Services`, implementing the generated imported provider interface. Core generates matching immutable Worker registrations and the Worker entry point.
- Ordinary consumers inject a contract-scoped API such as `Sunder.Generated.Packages.Younics_Ai_Agent_Services.CYounics_Ai_Agent_Runtime_Catalog.IPackageApi`; generated values live in `Sunder.Generated.Packages.Younics_Ai_Agent_Models`. Reviewed `consumerRoles`, readiness and action policy determine uses; type availability never grants access.
- Use `IServiceProviders<TService>` for business selection among implementations. Call `provider.Service` and pass `provider.Reference` for an exact typed binding. Entries are borrowed; generated DI owns their lifetime and a retired reference never retargets.
- The host validates each request, response, and stream event against the selected method schema before forwarding it.
- Provider retirement cancels in-flight work with `StaleEndpoint` and waits for exact-activation calls to drain.
- Catalog order is not a precedence API. Resolve semantic ids explicitly and reject or deterministically handle duplicates.
- Nullable references remain strict when non-null unless the declaring transferred property explicitly uses `SunderOptionalServiceReference`. Safe absence then becomes null; permissions, exact identity and unsafe-failure semantics remain enforced.
- Narrow imported Runtime use can declare `SunderRuntimeUse(typeof(IExecutionTargetService), Actions = ["discover"])` on the consuming class. The contract must already be selected; this adds no invocation action.
- Runtime catalog, run control, workspace resolution, and child execution are base-Agent-owned ports. Peer packages consume them and must not publish competing providers.

## Provider Contracts

| Contract id | Service | Typical owner | Shape |
| --- | --- | --- | --- |
| `younics.ai.agent.chat.provider` | `chat-provider` | Model provider package | Metadata/readiness unary calls plus bounded chat event stream |
| `younics.ai.agent.embedding.provider` | `embedding-provider` | Model provider package | Metadata, space identity, single and batch generation |
| `younics.ai.agent.behavior.loop` | `behavior-loop` | Base Agent or orchestration package | Loop descriptor and one run invocation |
| `younics.ai.agent.tool.source` | `tool-source` | Tool, MCP, skill, or subagent package | Discovery, readiness, preflight, permission, execution, presentation |
| `younics.ai.agent.execution.target` | `execution-target` | Local, container, or remote execution package | Declared execution facets and bounded operations |
| `younics.ai.agent.permission.surface` | `permission-surface` | Tool or execution package | Stable action and boundary declarations |
| `younics.ai.agent.system.prompt.contributor` | `system-prompt-contributor` | Trusted instruction package | Identity and trusted prompt blocks |
| `younics.ai.agent.prompt.context.contributor` | `prompt-context-contributor` | Memory, skill, files, or subagent package | Reference context and optional acknowledgment |
| `younics.ai.agent.durable.lifecycle.observer` | `durable-lifecycle-observer` | Package with durable derived state | Identity and at-least-once lifecycle delivery |
| `younics.ai.agent.profile.capability.consumer` | `profile-capability-consumer` | Capability-consuming package | Static profile capability declaration |
| `younics.ai.agent.selectable.capability.provider` | `selectable-capability-provider` | MCP, skill, or subagent package | Capability list plus change stream |
| `younics.ai.agent.session.cleaner` | `session-cleaner` | Package with session-local state | Identity and idempotent session deletion |
| `younics.ai.agent.workspace.editor` | `workspace-editor` | Execution package | Editor sections and validated save requests |

## Base-Agent Ports

| Contract id | Service | Purpose |
| --- | --- | --- |
| `younics.ai.agent.runtime.catalog` | `runtime-catalog` | Bounded session, profile, workspace, transcript, and change projections |
| `younics.ai.agent.run.control` | `run-control` | Invocation-scoped behavior-loop operations and chat stream |
| `younics.ai.agent.workspace.execution.resolver` | `workspace-execution-resolver` | Resolve a workspace to one exact execution-target endpoint |
| `younics.ai.agent.child.run.executor` | `child-run-executor` | Launch and correlate durable child sessions |

## Reserved First-Party Bridge

`younics.ai.agent.tool.execution.observed` carries observed tool execution between
first-party packages without making it a supported third-party provider
contract. It is bundled for first-party manifest composition but is excluded
from the 17 third-party integration contracts.

## Capability group selection

The supplying package owns bulk-selection presentation. Set
`AgentToolDescriptor.SelectionGroupAllowsBulkSelection` for a tool group or
`AgentProfileSelectableCapabilityDescriptor.GroupAllowsBulkSelection` for a
selectable-capability group. Authored properties default to `false`; generated
DTO initializers explicitly supply the boolean. Groups without opt-in have no
group checkbox. Conflicting declarations disable bulk selection, and groups are
scoped to their source rather than merged by display name.

Bulk selection changes only currently selectable members in the editor draft.
It uses the existing Save flow and grants no permissions or workspace access.

## Failure Semantics

- Use typed readiness or result failures for expected provider conditions.
- Let caller cancellation propagate.
- Treat `StaleEndpoint` as exact-provider retirement; rediscover only when the operation is safe to retry against a replacement.
- Treat schema validation failures as protocol defects, not transient provider failures.
- Agent reports package-local invariant violations against the retained exact endpoint reference. The Host rejects stale or unauthorized reports and attributes an accepted report only to that endpoint's current owning package activation.
- Durable lifecycle handlers must be idempotent by event id because delivery is at least once.
- Session cleaners must be bounded and idempotent because pending jobs survive restart and provider absence.

The 14 declaration-only contracts live under the owner's `Rpc/Public`; the four
owned public services declare their APIs on their implementations under `Services`.
External implementations use contract-scoped generated Services and the shared
Models namespace. File-local type aliases keep signatures natural; the
[C# name map](ordinary-service-refactor.md#imported-c-name-map) gives the exact
contract scopes. Owned/source-shaped interfaces stay in the flat Services namespace.
A class may bind multiple
contracts with repeated `SunderServiceProvider` attributes and explicit interface
methods. Business records are authored once; Core owns model projection, codecs,
Worker/App composition and exact binding lifetime. Content uses `SunderContent`
with normal disposal. See the [extension quickstart](extension-quickstart.md).
