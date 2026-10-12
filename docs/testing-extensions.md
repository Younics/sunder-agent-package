# Testing Agent Extensions

Test the changed behavior at its owning boundary: domain behavior, generated-role
composition, or fresh artifact/runtime lifecycle. Ordinary-service authoring uses
`Sunder.Sdk.Services`; advanced wire/content APIs remain in `Sunder.Sdk.Rpc`.
[Ordinary services](ordinary-service-refactor.md) records the completed migration,
retained adapter responsibilities and dated qualification scopes.

## Local iteration

Start with the changed class or method, then the affected owning project:

```sh
dotnet test tests/Sunder.Package.Agent.Tests/Sunder.Package.Agent.Tests.csproj \
  --no-restore --filter FullyQualifiedName~AgentRuntimeCorrectnessTests
```

Replace the selector with the actual behavior. Add coverage for a distinct failure
or boundary; extend an existing fixture before adding another integration journey.
Reuse test binaries with `--no-build` only while their relevant inputs are unchanged.
For test-only edits, `-p:BuildProjectReferences=false` can reuse matching, already-built
canonical role outputs. A filtered test still has its project's build dependencies.

| Changed behavior | Owning selection |
| --- | --- |
| Agent persistence, run control, permissions, workspaces, history | Relevant class in `Sunder.Package.Agent.Tests` |
| Local/Files execution and filesystem boundaries | `Sunder.Package.Agent.Execution.Local.Tests`; Docker integration also has explicit cases in `WorkspaceTests` |
| Provider translation, credentials, readiness | The provider's own test project; affected consumers for shared provider behavior |
| Presentation, initialization, dispatch and viewport state | `Sunder.Package.Agent.Presentation.Tests`, `Sunder.Package.Agent.Presentation.State.Tests`, or the owning package view-model class |
| Memory, Skills or Subagents behavior | Owning package suite and relevant Agent consumers |
| Generated role composition/private transport | Focused canonical/source class, using matching built Worker/App roles |
| Shared generator/contracts, package output or migration checkpoint | Required fresh all-family artifact case and affected Core boundary checks |

Run the full affected projects once at a coherent integration checkpoint. Full
solution, all-family artifact and native runs belong at their shared-boundary or
release checkpoints, not after each business/view-model edit. Record the actual
selection, passed/failed/skipped counts and prerequisites. A focused pass is not a
complete-suite pass. The full Agent solution includes all eleven test projects,
including `Sunder.Package.Agent.Presentation.State.Tests`; workspace-only restore
does not establish assets for every child-solution project.

## Behavioral and composition invariants

Use deterministic package context, storage, secrets, HTTP/process and time fakes.
Keep strict input limits, malformed/duplicate/case-colliding JSON, cancellation,
safe error codes/messages and credential/content redaction independently observable.
Expected missing configuration reports readiness; cancellation cannot persist success.

Resolve services from the actual generated composition. Assert exactly-once provider
registration, DI-owned singleton identity, immutable provider metadata and correct
Worker/App placement. App references must not acquire Worker implementations or
runtime authority. Exercise cross-package calls through the descriptor-validating
test RPC broker, with canonical lowercase package IDs and exact activation identities.

Retirement tests must reject new calls through retained references, cancel and drain
active calls, and allow a same-ID replacement independently. Preserve caller-owned
content scopes, explicit deadlines, callback identity and disposal ownership.

| Capability | Distinct behavior to retain |
| --- | --- |
| Chat / embeddings | Model/readiness/auth states, vendor response/tool/attachment translation, malformed upstream data, ordered nullable batches, dimensions/indexes, stream bounds, cancellation, retirement/replacement and exactly-once disposal |
| Tools / dynamic discovery | Strict arguments, permissions, profile assignment, concurrency claims, output bounds, identity stability, stale advertisement rejection and exact-activation discovery |
| Permissions / execution | Allow/Ask/Deny and durable precedence; no-follow containment, handle-bound capabilities, restart reapproval, compare-and-swap and post-mutation receipts, rollback/conflict preservation, aggregate read/search/patch budgets, cancellation and process-tree cleanup |
| Docker / scoped discovery | Explicit endpoint and mount-generation pinning, create/start/reuse mount challenges, nested-root protection, host identity policy, exact casing/longest root, linked-path and namespace rejection, bounded probes and outside-root omission |
| Prompt / context | Ordering, deduplication, truncation, optional failure, host-normalized authority, scoped-instruction acknowledgment, receipt routing, plan suppression and cancellation; user/reference content never widens permissions |
| Lifecycle / durable cleanup | Event-id idempotence, historical-before-future ordering, replay/recovery/poison retry, versioned erasure receipts, retirement/compaction barriers, durable cleaner retry and sanitized logs |
| Runs / children | Revision ownership, admission/checkpoint consistency, approval/budget limits, parent correlation, immutable workspace/profile snapshots, permission inheritance, wait/resume/restart and superseding-run isolation |
| MCP / presentation | Transport/OAuth/secret separation, bounded documents and streams, availability/reconnect/revision handling, dispatcher affinity, stale completion rejection and drained shutdown |

These are behavior owners, not a requirement to repeat every case at every layer.
Use temporary test-owned state, observable barriers and bounded waits. Never depend
on developer credentials, home-directory state or an ambient MCP/Docker service.
Do not weaken TLS, path or permission rules to simplify a fixture.

## Canonical roles and fresh artifacts

Focused source classes such as `AgentCanonicalSourceMigrationTests`,
`CredentialSourceMigrationTests` and the private management source fixtures consume
the already-built canonical roles. Their behavioral probes also support fresh-artifact
qualification; sharing probes does not make reused-role evidence fresh-build evidence.

At a shared generator/contract or migration checkpoint, select the owning artifact
case explicitly, after preparing matching Core test tooling, family dependencies
and the exact local Core catalog selected by the family locks:

```sh
SUNDER_REQUIRE_AGENT_SOURCE_MIGRATION=1 \
SUNDER_AGENT_SOURCE_PLATFORM_CATALOG=/absolute/path/to/canonical-core-catalog.json \
dotnet test tests/Sunder.Package.Agent.Tests/Sunder.Package.Agent.Tests.csproj \
  --no-build --no-restore \
  --filter FullyQualifiedName~AgentSourceArtifactMigrationTests.CompleteOwnerArtifact_DerivesConsumerAndProviderLocksFromFreshSourcePayload
```

This deliberately expensive case builds and packs all fifteen canonical roots,
uses fresh owner archives for dependent selections, checks generated App/Worker
composition and policy, exercises process/lifecycle behavior, and proves unchanged
authored evidence and deterministic repacking. Missing required inputs must fail the
selected lane. The fixture must not substitute a conformance catalog or Registry
for the required local catalog. Builder's focused source selection uses
`SUNDER_REQUIRE_BUILDER_SOURCE_MIGRATION=1` when required.

Inspect generated manifests/archives for identity, dependency ranges/presence,
provider readiness, exact contract tuples, role isolation and intended capabilities.
Exclude SDK/test/tool binaries, credentials and private configuration from payloads.
Private services use the effective package version without a separate public
Accept/Freeze lifecycle; reviewed public API evolution remains explicit.

Keep full source-SDK behavior, NuGet compatibility, actual Host installation,
native six-RID/security and release gates. The local artifact case is not a substitute
for those scopes. See [native inputs](native-release-inputs.md),
[cold inputs](cold-release-inputs.md) and [release requirements](RELEASES.md).

## Historical evidence

Historical reconciliation and current generated behavior have different owners.
Preserve the complete original captures and baseline bytes; do not regenerate them
from current source or replace captured source with hashes alone. Read these large
fixtures only for their owning history/compatibility work:

- [Historical project captures](../tests/Sunder.Package.Agent.Tests/Fixtures/PreCanonicalProjects/README.md)
- [Retirement evidence and owning checks](../tests/Sunder.Package.Agent.Tests/Fixtures/PreCanonicalProjects/RETIREMENT.md)
- [Ordinary-service history and current private successors](ordinary-service-refactor.md#compatibility-and-source-history-closure)

Captured retired code is data, never a production or test compilation input.
Current role tests use generated implementations. Keep the public descriptor/ABI
oracle, archived private records and separately reviewed current private tuples
distinct. Run-specific TRX/duration reports belong in ignored artifacts and CI
evidence, rather than new checked-in case ledgers or duplicated session narratives.

Next: [Release, compatibility, and troubleshooting](release-compatibility-troubleshooting.md).
