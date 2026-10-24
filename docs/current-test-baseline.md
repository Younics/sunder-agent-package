# Current-only Agent test baseline

## Reviewed expected inventory

The clean-slate contract is authored in
[`agent-current-inventory.json`](../compatibility/agent-current-inventory.json).
It is independent of generated receipts, owned/lock JSON and archived identity maps.
Source review uses each entry's explicit service declaration path, source provider
attributes, canonical project metadata and reviewed consumer intent.

| Boundary | Expected |
| --- | --- |
| Canonical project/package pairs | 15, including C# `Sunder.Package.Agent.Builder` → runtime `younics.sunder.package.maker` |
| Roles | 15 exact-RID framework-dependent Worker V1; 12 App (the three Tools packages have no App) |
| Public APIs | 18: `chat-provider` / `execution-target` / `tool-source` at `2.0.0`, fifteen at `1.0.0`; four concrete owner services, fourteen declaration-only contracts |
| Private APIs | 13 at package `2.0.0`: Agent owner + profile; Maker; Docker/Local presentation; MCP; Memory; four provider management/credential APIs; Skills; Subagents |
| Providers | 64 Runtime providers; public APIs/Stack use public audience, package-private APIs use owner audience |
| Agent consumer policy | 15 Runtime uses (behavior loop, run control and runtime catalog required); five App uses (owner/profile required) |
| Package-wide API consumers | MCP, Memory, Skills, Subagents, Files, Shell; Maker optionally; public Runtime uses only, with explicit required-contract lists |
| Package Maker | Ten-method owner-only private service with three bounded App relay methods; optional `maker.tools` / `maker.permissions`; no required Agent package/provider or direct implementation-assembly dependency |
| Web | Execution-target discovery only; no inferred invocation or App grant |
| External authority | `sunder.stack.contributor` / `sunder.core` / platform / `2.0.0` |
| Capabilities | Exact authored sets in `packages.json`, compared to the union of actual target capabilities |

The initial `agent-current-contracts.json` is now populated from qualified current
artifacts and separate source/intent review. Only Agent owns a public baseline file;
the fourteen private/provider-only families use qualified baseline absence. No
pre-V1 history or publication record is inherited. Docker's current capability set
omits `role-local-workspace.v1`, confirmed by its generated role closure after the
storage-validation cleanup. SDK freeze and native/release acceptance remain separate.

Agent explicitly opts into `rpc.call-ownership.v1` for activation-owned run work.
The reviewed `packages.json` capability union includes that declaration; the shared
current-output/archive oracle also requires it on Agent's Worker target, not merely
somewhere in the package union. Other families' capability sets, all 31 descriptor/ABI
records, the fifteen project/package pairs and the 12 App / 15 Worker role counts are
unchanged. This capability authorizes independent roots while preserving named-parent
deadlines and the process-wide active depth floor; it does not add service grants or
resource authority.

Package Maker's local-project refactor updates only its reviewed private descriptor
and consumer intent. The dated `qualificationInputs` in the contract record retain
the original first-baseline capture; they are not a current source hash manifest.
Fresh all-family qualified outputs and the current source-artifact gate must validate
the refactored package against the reviewed current descriptor. Earlier captures
are not relabeled as evidence for the refactor.

Builder's reviewed private API retains its original seven methods, including the
idempotent
`get-backend-id` read. Start carries that exact backend identity in its job request;
read/cancel require both `jobId` and `expectedBackendId`. A replacement rejects the
old identity with `agent.builder.backend-changed` rather than acknowledging or
replaying an old job. Its private descriptor/ABI changes do not alter any of the
18 public or other 12 private contract records. Its three additional relay methods
are `connect-development-app`, `read-development-command`, and
`complete-development-command`. Connect/complete are idempotent; read is not.
All three declare only `agent.builder.development-unavailable`; completion carries
the bounded typed failure in its existing nullable error string. The exact App
lease/request binding, cancellation and no-mutation-replay rules remain enforced.
Archive review must check these request shapes, outcomes, idempotency and unchanged
owner-only provider audience,
not just copy the generated hashes. Full all-family qualification remains required.

Separately, Agent's private pending-permission DTO includes the persisted
`approveForSession` boolean. This records approval intent, not a permission grant;
session permission still requires validated execution start. It does not alter a
public contract tuple.

Builder's App role also requires `app-state-updates.v1` for accepted bookmark
settlement. This is a host-owned App service, supplied by composition fixtures as
well as the production shell; it must never be required by a Worker role.
The App role additionally requires `package-development-revisions.v1` for sampled
and applied output identities, and `worker-execution-observations.v1` for exact
pre-Start execution capture and Host cleanup observations. Both are App-only:
the reviewed package capability union includes them, while the independently
admitted Worker target must not require either. Cleanup observations do not
manufacture a terminal job outcome or authorize Start replay.
The App also requires `app-activation-services.v1`, `package-development-reload.v1`,
`package-development-logs.v1`, `package-development-log-queries.v1`, and
`package-development-views.v1` and `package-development-feedback.v1`; its Worker uses
the declared extended-deadline capability without gaining these App capabilities.
The query capability separately gates filtered paging, encoded-byte budgets and
activation-retained post-unload diagnostics. The legacy logs capability alone
does not prove that an older App implements these members. Maker's Worker relays
portable query data; it never references the App-only SDK query type.
Feedback separately gates semantic view discovery/query, explicit opening and
native-inclusive scoped capture. Legacy view inspection alone does not advertise
these implementations. Its addition changes Maker's reviewed capability union,
not the 18 public or 13 private Agent contract identities/descriptors.

Removed API: `younics.ai.agent.workspace.path.migrator`, including Local/Docker
providers and test-only generated providers. No predecessor identity translation
or pre-V1 source/storage reconstruction participates in current acceptance.

## Coverage transfer

| Unique invariant previously coupled to history | Current owner |
| --- | --- |
| Fifteen exact canonical projects; Library authoring; six RID defaults; no authored split roles, bootstrap targets or nested build overrides | `CurrentCompatibilityTests.CurrentTopology_*` |
| Explicit `GetSunderPackageRoleReference` Worker/App selection; real solution/reference targets; captures excluded from compilation | `CurrentCompatibilityTests`, existing `ArchitectureReferenceTests` |
| Full provider/consumer/contract closure, required/action grants, audience, authority, dependencies, package/provider/platform imports and capabilities | `CurrentPackagePolicyAssertions`, used by both current outputs and `AgentSourceArtifactMigrationTests.Policy` |
| Independent exact descriptor/ABI bytes; public owned baseline and generated receipt agreement | `CurrentContractRecords`, `CanonicalDescriptorInventory`, `RepositoryArchitectureTests`, `PackageFamilyArchiveContractTests` |
| Strict value profile, reachable definitions, domain errors, finite semantic bounds, envelope-free bindings | `PackageLocalRpcDescriptorProfileTests`, `RepositoryArchitectureTests`, public/owner/catalog functional suites |
| Current source-backed secrets, cancellation, failed activation, App/Worker isolation and stack behavior | `CredentialSourceMigrationTests`, `OpenAiManagementSourceTests`, `AgentOwnerSourceMigrationTests` (only historical reads removed) |
| Workspace-path persistence, atomic aggregate rollback/concurrency and explicit current config | `WorkspaceTests.AgentWorkspaceService_SaveWorkspacePathsAndDocuments_PersistsHydratedWorkspace`, aggregate-stage/concurrency cases, Local/Docker config and editor suites |
| Full exact-activation stale-identity matrix, post-invoke invalidation, pre-cancel no-dispatch, receiver retirement/drain and scope disposal | Existing `VerifyAgentExecutionConsumer` and `VerifyAgentWorkspaceEditorConsumer`; migrator-only duplicates removed |
| Real all-family build/pack/import/activation and behavioral/security probes | `CompleteOwnerArtifact_DerivesConsumerAndProviderLocksFromFreshSourcePayload`; all fifteen family calls and required-input flag retained |
| Exact skip owner/reason/platform/capability and actual test identities | `generate-test-duration-report.py`; current owners only, no reverse mapping or alternate hash acceptance |
| No Protocol package dependency, build/release path or artifact | Offline boundary fixtures, evaluated project checks and existing archive/deps checks; no remote absence audit |

`AgentCompatibilityCandidateTests*` and `AgentSourceMigrationProjectChanges*`
reconstruction tests/readers are removed. Counts of retired projects, inverses,
relocations and old descriptors are historical facts, not current invariants.
Frozen captures and genuine domain source bytes remain intact.

## Current qualified-artifact record proposal

The verifier consumes a complete index with this shape (all fifteen rows required):

```json
{
  "schemaVersion": 1,
  "scope": "local-unpublished-current-RID",
  "inventorySha256": "<SHA-256 of compatibility/agent-current-inventory.json>",
  "configuration": "Debug",
  "rid": "osx-arm64",
  "packages": [
    {
      "packageId": "younics.ai.agent",
      "qualification": { "path": "artifacts/current/agent/role-inputs.v1.json", "sha256": "<exact SHA-256>" },
      "archive": { "path": "artifacts/current/younics.ai.agent.2.0.0.sunderpkg", "sha256": "<exact SHA-256>" }
    }
  ]
}
```

Paths are repository-relative regular files. The verifier authenticates the current
source frame, evaluated compile/resources, owned/lock inputs, embedded Worker
receipt, exact archive file bytes and descriptor/provider/use closure. Existing
release tooling validates lock-selected import projection. It does not build or
contact a Registry. Local evidence does not grant native or publication authority.

`--propose` prints `schemaVersion`, `inventorySha256`, all 31 canonical descriptor
records and qualified-input hashes. Review this output against the authored
inventory, current source and semantic tests; only then add
`compatibility/agent-current-contracts.json`. Default mode compares qualified
observations to that reviewed record. Tests never update expected records.
Public contract evolution continues through Core's normal owning tools, including
future history/projection reviews; no identity-fork receipt is needed by Agent.

## Qualification prerequisites

1. Finish clean-slate source and private value-shape adjustments.
   The expected 13 private identities and methods come from current service source;
   any intentional change requires a separate inventory review.
2. Qualify owning public Accept/Freeze and all family selections through Core tools
   after source completion. Refresh active owned/lock JSON only through owning
   tools. The previous 19-public receipts/archives cannot seed current expectations.
3. Produce fresh role qualifications and all fifteen
   archives; prepare the explicit input index above and review the proposal.
4. After build inputs are ready, run focused current inventory/architecture
   checks, functional PublicSource/Owner/Catalog/Credential suites, the full affected
   suite and the required all-family gate. Then collect actual Linux source/package
   TRX evidence and deliberately refresh `tests/test-evidence-expectations.json`.
   Its old identity hashes are not waived or fabricated; the report rejects drift.

Shared descriptor fixtures are linked through `tests/CurrentContractFixtures.props`;
`tests/RegressionTestHarness.props` imports it. Main tests compile their local copies.
OpenAI links only descriptor helpers and does not gain an Agent production reference.
No .NET/npm build or test is required to run the offline verifier fixtures.
