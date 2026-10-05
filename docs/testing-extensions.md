# Testing Agent Extensions

Test at three levels: contract behavior, package composition, and generated artifact/runtime lifecycle. A project compiling is necessary but does not prove role placement, package dependency metadata, permissions, or unload safety.

## Unit Tests

Instantiate the public contract directly with deterministic fakes for package context, storage, secrets, HTTP/process backends, and time where applicable.

### Common Assertions

- Descriptor and source ids are stable, non-empty, and case-insensitively unique.
- Expected missing configuration returns readiness, not an exception.
- Cancellation is rethrown and no success state is persisted after cancellation.
- Input limits, malformed JSON, duplicate/case-colliding properties, and boundary values are rejected.
- Expected failures use stable result/error codes and bounded safe messages.
- No credential, prompt, private content, or unrestricted payload reaches logs/results.
- Repeated calls and retries are idempotent where the host may replay work.

For a tool, test descriptor, readiness, valid execution, every validation error, permission request, result bounds, and cancellation independently. For a provider, test model/readiness catalogs and the complete response translation without live credentials.

## Composition Tests

Call the worker configuration factory with a deterministic fake `IPackageContext`, then inspect the returned `SunderWorkerOptions` and its immutable provider registrations. If the worker builds a service provider, resolve through its `SunderWorkerComposition` rather than constructing a second graph.

Assert:

- The intended provider id and RPC handler are registered exactly once.
- The instance is the DI-owned singleton, not a second allocation.
- Settings schema, owner RPC, callbacks, and lifecycle hooks are registered in the correct role.
- Runtime authority is absent from App services/registrations.
- App presentation and typed Runtime clients are absent from headless Runtime composition.

Exercise the handler through an `ISunderRpcClient` test broker rather than calling another package's implementation object. The broker should validate requests and outputs against the bundled descriptor, stamp caller/provider identity, and model endpoint retirement.

Use canonical lowercase test package ids because owner validation is part of the contract. Model references as exact endpoint activations rather than wrappers that re-query by package or semantic id. A retirement test should remove the old endpoint from discovery, reject new calls through retained references, cancel active calls, wait for those calls to drain, and permit a same-id replacement independently.

## Capability Test Matrix

| Capability | Required scenarios |
| --- | --- |
| Chat provider | Model/readiness states, auth modes, text and tool streams, malformed upstream payload, timeout/transient failure, cancellation/retirement, same-id replacement isolation, option/attachment translation |
| Embedding provider | Empty/mixed batches, index preservation, dimensions, model identity, malformed counts/indexes, readiness, cancellation |
| Static tool | Profile assignment, readiness, strict arguments, expected failure results, permission policy, concurrency claim |
| Dynamic source | Context-dependent discovery, invalidation, identity stability, stale advertisement denial, retirement during execution, same-id replacement isolation, native declaration parity |
| Permission surface | Every action/boundary/default, unknown boundary behavior, canonical resource summary, session approval and stale-run safety |
| Execution target | Strict Local no-follow root/child/reparse/device rejection, durable claim comparison, retained-handle capability transfer/disposal, distinct invocation-bound outside capabilities, configured restart rebind, outside restart reapproval, bounded patch planning and failure/cancellation cleanup, exact post-mutation add/update/delete compensation, final-only absence, reserved-name hiding with raw-entry charging, physically coordinated quarantine capacity, exact rollback/conflict preservation, read ranges, output bounds, compare-and-swap writes/deletes, aggregate glob budgets, POSIX literal-backslash paths, structured search cancellation/binary/newline behavior, process arguments, timeout/tree cleanup, and optional interface conformance; Docker additionally tests no transient configured authority, exact post-state compensation claims, explicit endpoint pinning on every command, daemon/container namespace claims, fresh mount-generation checkout, logical and physical nested-root deletion protection, host UID/GID policy, and the bidirectional mount challenge after create/start/reuse; scoped discovery additionally tests exact casing, longest root, fail-closed probe/ancestor/content bounds, linked roots/targets/documents, newline-safe paths, target namespace, and outside-root omission |
| Prompt contributor | Dedup identity, ordering/limits, no user/dynamic text in system blocks, cancellation and isolated failure |
| Context contributor | Plan suppression for optional recall, reserved safety-context exemption/bounds, host authority normalization, retirement during collection, exact-activation final-retention acknowledgment, malicious self-declared authority remains reference data, and scoped instructions never widen permissions |
| Lifecycle observer | All relevant events, event-id idempotency, historical-before-future ordering, bounded replay/recovery, poison retry, versioned content-erasure receipts, monotonic membership/reactivation, retirement/compaction barriers, durable cleaner retry, exception/payload log redaction, cancellation, direct-user-only promotion |
| Behavior loop | Run revision ownership, checkpoint/completion consistency, provider/tool cycles, approval suspension, no-progress/budget limits, cancellation |
| Child runs | Parent correlation, task retries, workspace/profile snapshot, permission inheritance, wait/resume/restart, superseding run, executor retirement, same-id replacement isolation |
| MCP integration | Local/remote parsing, secret separation, transport policy, discovery/result bounds, OAuth callback/session validation, cleanup |

## Artifact Validation

Build and pack the canonical primary Worker project directly:

```powershell
dotnet restore path/to/Extension.csproj
dotnet build path/to/Extension.csproj --no-restore
dotnet msbuild path/to/Extension.csproj -t:PackSunderPackage -p:Configuration=Release
```

Inspect generated `sunder-dev/sunder-package.json` and the `.sunderpkg`:

- Correct package id/name/version.
- Runtime dependency `sunder.package.agent` with `>=2.0.0 <3.0.0`.
- No source-authored manifest overriding generated metadata.
- Expected entry assembly and assets.
- Required inferred capabilities and no accidental capabilities from wrong-role code.
- No SDK assemblies, test binaries, credentials, local config, or private files.

Validate the complete behavioral solution in source-SDK mode and the focused compatibility lane in NuGet mode before release. A focused package identity count is not a complete-suite count. Run artifact-backed tests only with their required archive/native inputs and fail the selected lane when those inputs are absent.

## Runtime Lifecycle Smoke

Use clean host state and test:

1. Install base Agent and all required extension dependencies.
2. Install and activate the extension.
3. Discover/configure/select its capability.
4. Exercise one successful and one expected-failure path.
5. Deactivate/unload the extension graph.
6. Confirm background work, processes, connections, events, and callbacks stop cleanly.
7. Reactivate and verify durable state.
8. Reinstall the same immutable version and repeat activation.
9. Delete a related Agent session and verify package-local cleanup.

If the extension has an App module, also validate App snapshot activation and behavior while Runtime is temporarily unavailable.

## Repository Validation

Start local edits with the changed class/method in its owning test project:

```sh
dotnet test tests/Sunder.Package.Agent.Tests/Sunder.Package.Agent.Tests.csproj --no-restore --filter FullyQualifiedName~AgentRuntimeCorrectnessTests
```

Replace the selector with the actual changed behavior; add relevant consumer and
security tests. Reuse already-built test binaries with `--no-build` only when source
has not changed. At a coherent integration checkpoint, run the full affected projects:

```powershell
dotnet restore Sunder.AgentPackage.slnx
dotnet build Sunder.AgentPackage.slnx --no-restore
dotnet test tests/Sunder.Package.Agent.Tests/Sunder.Package.Agent.Tests.csproj --no-restore
dotnet test tests/Sunder.Package.Agent.Provider.OpenAI.Tests/Sunder.Package.Agent.Provider.OpenAI.Tests.csproj --no-restore
```

Execution changes should include the relevant `Sunder.Package.Agent.Execution.Local.Tests`
cases; UI changes should include presentation tests; provider changes should include
that provider's project. Full solution and all-family artifact runs belong at shared
build/contract changes, migration checkpoints and final acceptance, not every edit.

### Implementation-First Source Migration Lane

With the sibling `sunder-core` checkout restored, build its owning package-tool
tests before selecting the source migration lane:

This is an expensive cross-family integration lane. Select it deliberately for a
migration checkpoint or a shared generator/contract change; do not use it as the
ordinary inner loop for an isolated business or view-model edit.

```powershell
dotnet build ../sunder-core/languages/csharp/dotnet/tests/Sunder.Package.Build.Tests/Sunder.Package.Build.Tests.csproj --no-restore
$env:SUNDER_REQUIRE_AGENT_SOURCE_MIGRATION = "1"
$env:SUNDER_REQUIRE_BUILDER_SOURCE_MIGRATION = "1"
dotnet test tests/Sunder.Package.Agent.Tests/Sunder.Package.Agent.Tests.csproj --no-restore
```

The source candidates are compiled in fresh temporary projects and compared with
the frozen migration oracle. The combined Agent fixture includes all twenty owned
contracts and the authenticated Stack provider import. Core's test assembly supplies
the fixed conformance catalog/trust composition and offline restore; its ordinary
generator validates the selected descriptor and creates local registrations. The
App is built from the owned export and must retain exactly its three reviewed uses.
Worker receipts must also match all sixteen canonical Runtime consumer uses,
including the three required readiness predicates, without passing those grants to
the App or to the Stack provider import.
Real profile/workspace Stack operations, owner commands, content and lifecycle are
exercised through generated handlers. Missing imports and corrupted cached catalogs must
leave the previously generated output and owned baseline intact.

These source lanes require the sibling checkout and prebuilt tooling. Setting the
environment variables makes a missing prerequisite fail the selected lane rather
than allowing a standalone-clone skip.

Generated provider declarations also run through the production
`DeferredAgentRpcServiceHandler` and `AgentWorkerLifecycle`. Check all eleven
provider tuples before any service resolution, activation after generation commit,
pending-call completion, and full unwind when a resolved registration differs from
its declaration. The activation bridge has an independent SDK/Worker-only compile
check. Its original source is retained as a hash-checked historical fixture while
the normal lifecycle and generated-composition tests exercise the migrated source.

The canonical source lane also evaluates the actual Worker's compile/reference
inputs, links the full business source graph with the generated provider catalog,
and builds its executable entry point using an exact isolated catalog fixture.
It builds the existing App presentation gateway against the generated owner facade,
then exercises real startup, profile/workspace/session persistence, Stack discovery,
content and all owner request/stream mappings. The generated App now compiles its own
presentation/history records, gateway interfaces, business helpers and typed operation
tokens without OwnerProtocol. The complete artifact lane also activates the actual
Avalonia App module's validated DI graph, checks distinct App model identities and
ensures the App dependency manifest excludes the helper. The archive confines the
owner provider port and business models to their owning role assemblies: the generated
Worker now compiles its own port/models too, and the complete archive and both dependency
manifests exclude OwnerProtocol. A compiled test-only adapter preserves the full retained
owner behavior probe across distinct model identities, including Worker-local domain
error mapping. The actual generated composition is exercised separately. Central/shared
semantic dependencies and normal project retirement remain explicit migration work.
Production catalog selection remains an external release gate.

The Agent artifact also runs as a Format-projected native Worker. Check candidate abort
without database creation, activation-time SQLite initialization, owner profile/workspace/
session mutations, clean shutdown and persisted reads in a new process/generation. This
test uses isolated state and a bounded protocol peer; actual Host installation and the
protected native release matrix are separate qualification lanes.

`CredentialSourceMigrationTests` covers the Gemini, Anthropic and LM Studio private
credential contracts. Each ordinary service preserves its frozen descriptor/ABI,
builds an isolated generated App client, and exercises the real credential accessor
against test-owned secrets. Query/set/clear, omission/validation, cancellation,
storage-fault sanitization and provider retirement are checked without live API keys.

`McpManagementSourceMigrationTests` covers the mixed content/stream boundary. It
compiles the source service and isolated App facade, then drives the real management
handler/catalog/editor through host-mediated content: catalog/save/document/delete,
secret redaction, change notifications, size bounds, faults, cancellation and
retirement. The original content preimage stays in a hash-checked fixture while the
SDK-content helper is shared by the source and migration envelope adapters.

The OpenAI provider suite has its own required source lane under the same
`SUNDER_REQUIRE_AGENT_SOURCE_MIGRATION=1` switch. It checks the private management
service/App pair against the frozen descriptor and uses the real auth operation
handler, cached session store and credential accessor with test-owned secrets.

The remaining private source lanes use `PrivateManagementSourceFixture` for fresh
Accept/Freeze/Build/Check projects and SHA-pinned App export handoffs:

- `SkillsManagementSourceMigrationTests`: real archive transfer/import, unsafe-path
  rejection and cleanup, query/delete, notifications, cancellation and retirement.
- `SubagentsManagementSourceMigrationTests`: real persistence, revision replay,
  content-authority retirement, expired replay, overflow/resnapshot and fault mapping.
- `LocalPresentationSourceMigrationTests`: settings, custom shells, catalog revision
  conflicts and cancellation through the actual runtime handler.
- `DockerPresentationSourceMigrationTests`: all eight runtime presentation operations
  with a deterministic CLI boundary, scoped multi-MiB image catalogs and size limits.
- `MemoryInspectorSourceMigrationTests`: all query/command kinds against the real
  SQLite inspector, state/optional/timestamp conversion, indexing notifications,
  bounded projection content and streaming errors.

These complete the 19-public/12-private source parity inventory. Canonical package
selection, artifact-derived dependent locks and remaining semantic/helper relocation
are separate integration work. Each App fixture checks its exact required-use actions
and absence of Worker/SDK Worker assembly references; retained semantic Protocol
references remain visible until their coordinated relocation.

`AgentSourceArtifactMigrationTests` builds the complete evaluated Worker and Avalonia
App source graphs into a fresh current-RID owner archive using the normal manifest,
dev-output, aggregation and Pack tasks. Its isolated Core test driver supplies the
same canonical catalog fixture as the source lane; ordinary generation/Pack tasks
require no trust-injection override. Consumer/provider selections then use
that exact archive offline: the consumer receives all 19 package-authority APIs,
provider-only roots receive one contract without uses, and platform inventory
provenance remains distinct from package consumption. Repacking after creating
dependent locks must preserve archive bytes.

This lane also compiles the real Semantic Memory session-cleaner source adapter
against its generated imported interface and exercises durable deletion, evidence
cleanup, session isolation, idempotence and cancellation. Damaged archive content,
private/platform consumer requests and missing exact catalog evidence must leave
the previous lock/generated tree intact. The owner artifact still has explicit
Protocol semantic dependencies pending final relocation and residue cleanup.

The Semantic Memory extension of this lane combines all four imported public
providers with its private inspector and verifies the generated five-provider
catalog. Its ordinary Agent API import has exactly one required contract, Runtime
Catalog. The associated App receives only its private inspector export. Real
lifecycle promotion/replay, memory-consistency barriers, bounded recall, pinned
rollback behavior, deletion and App queries run across the generated boundaries.
Business-model references remain explicit migration dependencies while their
Runtime consumer adapters and final ownership are relocated.

The Runtime-consumer portion uses the package-owned `ISemanticMemoryRpcCatalog`
boundary with generated catalog and embedding clients from the fresh artifact's
Agent API selection. It verifies no discovery before activation, real embedding
index persistence, ordered nullable batches, lookup failure distinctions, exact
generation fencing, in-flight retirement, same-ID replacement and drained shutdown.
The resolver's current constructor is bridged through a small migration adapter;
its historical source preimage remains hash-checked during this ownership change.

The full Semantic Memory Worker portion evaluates its actual source graph, selects
generated transport bindings, builds the associated App and emits a complete
dependent archive through the package tasks. The Format resolver combines all
payload layers for the current-RID process check. A bounded Worker V1 peer verifies
provider/settings advertisement, no database before activation, SQLite startup,
generated capability/cleanup calls, clean shutdown and durable state across restart.
The existing Program and split dependency metadata retain explicit historical
preimage checks. This local process lane uses the installed .NET toolchain; approved
runtime-store/Host installation and all-RID release qualification are separate lanes.

`MemoryAppSourceMigrationTests` exercises the real presentation gateway with its
generated inspector transport in a fresh source/App pair. The full artifact lane
also resolves that gateway through the actual App module's DI registration. Checks
cover unavailable/foreign providers, real store mutation, cancellation, declared and
sanitized faults, large content, malformed-event reconnect, same-ID replacement and
drained disposal. The gateway's operation/observer logic is common to both transport
selections, and its original source is retained as a hash-checked migration preimage.

The complete Semantic Memory artifact now compiles the relocated business models and
inspector ports separately into Worker and App. The role-local test resolves the
actual Worker composition and App module, verifies distinct CLR ownership, and
exercises mutations, notifications and replacement through generated transport.
Archive and `.deps.json` checks reject the package-local Protocol assembly. Normal
migration builds still link the owner files into that helper until coordinated
cutover; the moved/split sources and exact added links retain historical hash checks.

The Skills artifact portion uses role-local installed records and owner ports, three
Agent provider imports, the authenticated Stack import, and generated private App
transport. It checks local-folder transfer/import, skill and resource tools, prompt
content, capability notifications, Stack export discovery and cancellation. The
current-RID Worker process lane verifies that orphan cleanup waits for activation
and shutdown drains started cleanup. Archive/dependency-manifest checks exclude the
Skills Protocol assembly. The normal project's capability set remains exact through
its existing callback lifecycle.

Skills' source-selected Runtime now uses generated Runtime Catalog and Execution
Target consumers through a package-owned bindings port. The artifact lane exercises
profile lookup and resource resolution from the actual tool/prompt providers, plus
missing profiles, binding/facet mismatch, exact activation/session identity, stale
responses, cancellation, lookup denial/unavailability, sanitized provider faults and
same-ID replacement. It also verifies preactivation inactivity and drained call
scopes/metadata watches. The source Worker excludes its legacy Skills Runtime adapter
and has no central catalog service; normal migration builds retain the explicit
legacy selection. Exact feature-source reconstruction preserves the frozen oracle.

The Subagents artifact portion builds all six providers and the complete App from the
fresh owner/platform selections. It checks independent Worker/App record, projection
and cursor types and excludes the package-local Protocol assembly from references,
payloads and dependency manifests. Runtime Catalog, Behavior Loop, Run Control and
Child Run Executor remain required. Real management persistence, single/batch child
delegation, profile/correlation mapping, result presentation, prompt context, Stack
discovery, change observation and same-ID owner replacement are exercised through
the actual composition/module. Behavior-loop callbacks cover stale identity, caller
isolation, cancellation and retirement. Current-RID process checks cover candidate
abort, activation, generated calls and shutdown. Both normal and source startup defer
catalog listeners, whose synchronous RPC construction runs off the protocol reader.
The behavior-loop callback now uses the generated Run Control client. The artifact
test loads the fresh owner Worker's generated provider and checks all provider identity
fields, caller-bound/expired invocation IDs, rich/minimal contexts, declared and
sanitized failures, deadline clamping, cancellation, stale responses, retirement and
same-ID replacement. Lookup and dispatch must use the invocation-owned call scope;
the test verifies disposal on every path.

Subagents' Runtime Catalog and Child Run consumers also use package-owned bindings
in the complete source Worker. Their tests use the fresh generated owner providers
and cover catalog queries/nulls, transcript-header/detail facets, session/turn/profile/
reset notifications, child-run result mapping, cancellation, lookup failures, stale
responses, same-ID replacement and shutdown with a borrowed client outstanding.
The bindings start on activation and drain watches, scopes and leases at shutdown;
legacy constructor/binding bridges are excluded from the source-selected Worker.
Exact inverse source edits retain the frozen adapter evidence.

The source-selected Subagents Worker also uses generated chat/model, tool, selectable-
capability and behavior-loop catalog consumers and has no central catalog service.
Narrow package-owned interfaces expose the consumed metadata operations. Checks cover
model options/dates/order, tool and capability deduplication, alias/read-only batch
gating, Stack package ownership, notification subscriptions, cancellation, denied
lookup, retirement and same-ID replacement. Capability retirement releases borrowed
notification leases, and full composition shutdown drains them. Normal migration
constructors remain explicit; the legacy local management bridge is excluded from
the source-selected artifact.

The MCP artifact portion builds the complete Worker/App with six generated providers
and role-local configuration, management and bounded-content types. References,
payloads and dependency manifests exclude the package-local Shared assembly. The real
App gateway verifies persistence, redacted documents, cancellation and notifications.
A test-owned loopback HTTP MCP peer exercises the actual SDK discovery/readiness and
tool-call path, observed dispatch acknowledgment, abandoned-stream draining, in-flight
cancellation and session cleanup. The native Worker lane covers candidate/abort,
activation, generated metadata calls and shutdown. Configuration paths are isolated
from developer home directories, and Runtime Catalog stays optional. The existing
SDK composition/callback lifecycle and frozen source/metadata preimages remain checked.

MCP workspace synchronization uses a package-owned discovery interface and generated
Runtime Catalog client in the source-selected Worker, with no central catalog service.
Tests use the fresh owner's generated provider and real importer for global/workspace
configuration, duplicate paths and repeat synchronization. They verify optional versus
denied discovery/lookup, provider read failures, stale-response rejection, cancellation,
retirement/replacement, nonblocking startup and shutdown with an active discovery.
All discovery and query operations must use their owned call scope, and the native
Worker peer handles the corresponding scope-open/scope-close protocol. Legacy discovery
and constructor bridges remain confined to normal migration builds.

The Local execution artifact uses three provider-only Agent imports and its private
App export, with no Runtime consumer grants. All four providers and the actual App
module/settings view model compile from the owning sources; payload/dependency checks
exclude the package-local Protocol assembly. The lane exercises all execution-target
RPC methods against the real target, including file/process operations, ranged reads,
search/instructions, transient resource authority, post-mutation receipts, revocation,
configuration fencing and cancellation. Workspace editing/path migration and App
settings/revision behavior also pass through generated bindings. Projected current-RID
Worker checks retain the existing SDK composition/settings lifecycle. Run the separate
execution-hardening suite for broader filesystem, process and platform-specific cases.

The Docker artifact builds five generated providers and the full App with provider-only
Agent imports and authenticated Stack. Role-local presentation/image records exclude
the private Protocol assembly from the source payload. A test-owned CLI adapter is
compiled against the fresh Worker; it preserves the real mount-identity verifier and
executes its bidirectional challenges against isolated mapped directories. The lane
covers all execution routes, the missing resource-resolution facet, security options,
structured files/claims/receipts, image/configuration fencing, cancellation, settings,
Stack export and image-catalog content bounds. Oversized transport projections use a
controlled response so package-state validation remains enforced. Native Worker checks
cover storage validation on activation and clean shutdown. Real-daemon and platform-
specific qualification remain separate test lanes.

The Gemini artifact builds generated chat, embedding and credential providers and the
full App from two provider-only imports, excluding the private Protocol assembly.
The actual App credential client and shared Worker accessor verify status, mutations,
cancellation, readiness and retirement. A controlled loopback Google endpoint exercises
the real streaming/tool-completion and embedding adapters, including named binary
attachments, options, thought signatures, tool correlation and nullable batch indexes.
Additional stream probes cover bounded buffering/coalescing, cancellation/abandonment,
safe error projection, infrastructure sanitization and client disposal. Retained clients
must accept generated event discriminator ordering. Native Worker checks cover candidate
abort, activation, generated metadata calls and shutdown. Run the owning Gemini suite
for broader translation, upstream failure and telemetry cases.

The Anthropic artifact builds generated chat/credential providers and the full App from
one provider-only import. A controlled HTTP handler exercises the real SDK's standard
and fast-mode streaming/tool-completion paths: headers, options, image/PDF transfers,
signed reasoning, tool continuations, usage, malformed responses, context-limit errors
and cancellation. Credential rotation is observed by an existing chat client, response
content is disposed, and the private App client recovers after same-ID replacement.
Native Worker checks retain callback-based startup/shutdown and settings contributions.
Shared chat-stream probes run against both Anthropic and Gemini to verify in-flight
retirement, exact-endpoint replacement isolation and cleanup-error precedence alongside
bounded buffering, error projection and exactly-once client disposal.

The LM Studio artifact builds generated chat/embedding/credential providers and the
full App from two provider-only imports. Its real shared connection/catalog and SDK
use a controlled HTTP handler. Tests cover anonymous and bearer requests, endpoint/key
changes, shared cache fetches, TTL refresh, cancellation and stale cache completion,
model classification/readiness, nullable embedding batches and vendor-index validation.
Chat checks include roles/options, streaming tool assembly/order, usage, upstream errors
and cancellation, plus the common retirement/replacement/cleanup probes. App settings
validate and normalize URLs and utility selection. Package shutdown disposes the shared
connection; native Worker metadata checks avoid contacting the developer's local server.

The OpenAI artifact builds generated chat, embedding, continuation-cleaner and management
providers plus the full App, compiling independent role-local models without the private
Shared payload. Controlled HTTP covers real API-key Responses/embedding SDK paths and
connected-account requests, cached auth/refresh/cancellation, regular continuation reuse,
responses-lite session identity and generated session cleanup. Actual App management
checks credential isolation and replacement. The standard stream lifecycle probes also
run against OpenAI. Provider usage construction stays in the shared helper, with exact
wire optionality; vendor totals can still be inferred. Artifact project reconstruction
must not turn transitive source-reference NuGet metadata into published package imports.
Run all affected provider suites when changing this shared response behavior.

The Files artifact is headless and combines four generated providers with a scoped,
exact-handle Execution Target consumer. It is tested against the freshly built Local
execution provider. Coverage includes scoped instruction acknowledgment, withheld reads,
mutation deferral, file/search/patch operations, compare-and-swap rollback, authority
capabilities/receipts, configuration fencing and session cleanup. Metadata lookup and
dispatch must use owned scopes, respect caller deadlines and reject changed identities
or empty facets without inferring unsupported capabilities. Tests exercise cancellation,
active retirement, same-ID replacement and drained shutdown. Package API selection is
package-wide with optional Runtime contracts. Run the execution-hardening suite for
normal compatibility behavior and broader filesystem/platform cases.

The Shell artifact is headless and uses three generated providers plus a narrow scoped
Execution Target consumer. Fresh Local execution exercises real commands, `shell`/`bash`
metadata, permissions, shell context, output/exit codes, working-directory/configuration
fencing and timeouts. Invalid argument returns must close acquired leases without command
dispatch. POSIX process checks capture a test-owned PID and verify exit after cancellation,
retirement and Worker shutdown. Scope/watcher drain and same-ID replacement isolation are
also checked. Shell retains target-wide command approval and prompt refresh semantics;
structured Files instruction enforcement does not apply to opaque commands.

The Web artifact uses one provider-only import and a package-owned tool source with
active-call cancellation/draining. A backend compiled against the fresh Worker supplies
controlled DNS and HTTP responses while retaining the real URL/IP policy and fetch/Exa
services. Tests cover redirect revalidation, rebinding, response/render limits, MCP
handshake/pagination/session headers, result normalization, secret-safe errors and
cancellation at DNS/fetch/search stages. Shutdown drains concurrent requests and disposes
clients/streams; retirement and same-ID replacement are exercised through the generated
provider. Native discovery avoids external traffic. These controlled responses do not
replace socket/TLS qualification of the production pinned-client factory.

The complete Builder artifact selects generated Runtime/App transport and compiles
role-local models/content validation without its private Protocol payload. Its scoped
execution consumer is tested with the fresh Agent resolver and Local target providers.
The actual App module/application services run prerequisite/create/build/publish flows
through a test-owned process backend, with persistence, failure and cancellation checks.
No external build, template install or Registry publish occurs in that backend. Other
checks cover real shell/path calls, large result content, exact target handles, facets,
resolver/target retirement and drained shutdown. The independent source pilot retains
descriptor/ABI and App-export parity checks. All 15 runtime families now participate in
the complete fresh artifact lane; this evidence does not imply normal cutover or release
qualification is complete.

Builder's package API selection must explicitly require Workspace Execution Resolver;
Agent package presence alone does not establish required-provider readiness. Its other
eighteen Runtime uses remain optional, and its private App use remains required.

The complete artifact lane also audits every packed family against the frozen migration
oracle: exact provider identities/audiences, App grants, Runtime readiness/actions,
dependency ranges/presence, package/provider/platform import groups, bundle authority,
canonical descriptor bytes and Host capabilities. Package-wide imports add optional
Runtime uses for the remaining public contracts, while provider-only imports add none.
Files, Shell and Web explicitly retain `logging.v1` in their owning projects so this
reviewed requirement survives removal of the legacy transport's capability inference.
The audit requires all fifteen artifacts and rejects duplicate or missing package IDs.

The central semantic relocation check compares `Models/AgentSemanticSources.props` with
the frozen ownership ledger, rejects duplicate/missing files and surviving old copies,
and preserves original source hashes. The compatibility project still links those
sources until central consumer migration is complete. `AgentCatalogSourceMigrationTests`
separately compiles the owning catalog/lease sources with SDK-only references and checks
cancellation, exact activation handles, retirement/replacement and binding disposal.

Agent's source-selected embedding consumer uses its generated owned-contract facade
through a provider-bound catalog factory. History search, profile discovery and Stack
dependency discovery select this binding. Artifact tests pair it with the fresh Gemini
provider, including the controlled real Google backend, nullable batches, exact identity
checks before/after dispatch, cancellation, lookup failures, replacement and drained
scope disposal. The catalog retains the selected immutable provider snapshot when
constructing the semantic client; it must not silently reselect a same-ID replacement.

The generated session-cleaner and durable-lifecycle consumers share the embedding
client's scoped identity/retirement/drain primitive. Artifact tests use the actual
Agent cleanup/lifecycle dispatchers and fresh Memory providers: verify ids-only job
retry after store restart, duplicate delivery with unchanged payload/hash, acknowledgement
only after success, cancellation/retirement recovery without attempt inflation, and
same-ID replacement. Real Memory promotion/receipt/deletion checks retain an unrelated
session. A cleaner that has already entered synchronous deletion must drain before
client shutdown completes; cancellation must not abandon that side effect.

The profile-capability/system-prompt consumers use the same exact-provider lifetime.
Verify the actual profile service's optional discovery and replacement behavior, and the
prompt composer's empty-provider baseline, required/priority ordering, deduplication,
truncation, optional failures and cancellation. The prompt fixture implements a generated
provider-only import from the fresh Agent artifact with no Agent CLR reference or consumer
grants; it does not add a system-prompt provider to the production inventory. Rich request
values remain data, while test contributions contain only static package instructions.

The selectable-capability consumer adds a generated change stream to the same scoped
lifetime. Its profile change observer receives the owning service selector explicitly.
Verify one subscription across repeated refreshes, live profile notifications, sorting/
deduplication and Stack package ownership. Stream checks cover exact identity before each
event, natural completion, upstream failure, malformed events, concurrent list cancellation,
retirement/replacement and drained shutdown. Actual Skills import/delete operations must
update the Agent consumer. Keep observer disposal distinct from catalog-owned client
disposal: removing a listener must stop its notifications, while the catalog owns the
binding's final retirement and stream cleanup.

The generated workspace-path and permission-surface consumers are exercised against
fresh Local/Docker and Files/Shell Workers. Workspace checks must prove canonical paths
are committed before legacy cleanup, cleanup failures preserve that commit, failed or
cancelled reads do not persist paths, and replacement/restart preserve retry and identity
behavior. Keep contributor-owned shell/image/PATH settings intact and retain existing
path ordering/default/deduplication and binding eligibility rules. Permission checks use
the actual Agent evaluator and SQLite store: named Allow/Ask/Deny mapping, sorted and
case-insensitively deduplicated actions, generic/unknown Ask, durable Deny precedence
over session mode, provider absence/replacement and malformed metadata rejection.
Both consumers retain exact-provider scoped calls and cancellation/drain checks.

Tool preparation's generated Runtime-catalog consumer has an owning profile-only port.
Test it with fresh Agent DI, SQLite persistence and the generated owner provider: profile
absence/update/deletion, assignment and source matching, opaque settings, exact provider
identity, failure propagation, replacement and blocked-lookup shutdown. Compare transport
values to the committed profile snapshot: storage normalizes model capability keys and
binding timestamps. This consumer must not open a catalog watch it does not consume.
The same behavior runs in the shorter canonical-source lane for early feedback and in
the complete fresh-artifact lane for final policy/output coverage.

The generated prompt-context consumer retains complete request/receipt mapping and the
exact selected execution-target handle. Check contributor identity, nullable contributions,
safe faults, cancellation and drain in the focused canonical-source lane. Exercise the
actual coordinator's owner-verified authority normalization and one-use session/run/epoch
receipt routing, including optional failure, required recall suppression, ambiguous sinks
and provider replacement. In the full artifact lane, fresh Agent → Files → Local calls
must withhold reads until scoped instructions are acknowledged and reject stale document
hashes or workspace-scope bindings. Files also emits ordinary reference context: it must
retain Reference authority alongside host-reserved scoped instructions. The frozen V1
turn-summary field remains part of compatibility mapping despite its domain deprecation.

The generated Agent execution-target client is selected by target discovery, tool
snapshots, workspace Stack ownership and prompt-reference reconstruction. Optional
facets must follow the provider's descriptor even when the client implements all CLR
facet interfaces; an empty facet list grants none. Check cancellation-aware description,
detached cached facets, exact identity and disposal in the canonical-source lane. The
Local and controlled Docker artifact scenarios route their operations through this domain
client and check transient authority, receipts/revocation, stale configuration, filesystem
and process behavior. Keep Docker's local unsupported-facet rejection separate from its
direct provider Domain-error assertion. The public and combined-owner source fixtures
compile the same execution consumer/helper sources needed by prompt reconstruction.

For the complete Agent test inventory, restore and test `Sunder.AgentPackage.slnx`,
including `Sunder.Package.Agent.Presentation.State.Tests`. A workspace-only restore does
not establish that every child-solution-only test project has an assets file.

## Test Hygiene

- Never require a developer's real API key, OAuth cache, Docker image mutation, MCP server, or home-directory state.
- Use temporary directories and delete only test-owned paths.
- Do not weaken TLS/path/permission policy to simplify a fixture.
- Bound waits and process output; kill only processes created by the test.
- Avoid ordering assertions based on package activation when the contract uses semantic ids/sorting.
- Include package unload/disposal tests for event subscriptions and background services.

Next: [Release, compatibility, and troubleshooting](release-compatibility-troubleshooting.md).
