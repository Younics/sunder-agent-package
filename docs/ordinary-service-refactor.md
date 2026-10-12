# Ordinary services: current source and qualification history

## Current source

Agent's clean public surface uses declaration-only interfaces for all 19 unpublished
families, with real business implementations bound through `SunderServiceProvider`.
The authoring features are implemented and qualified across multiple local
checkpoints. Current public successors are accepted/frozen, with mixed contract
versions independent of Agent package 2.0.0. Workspace Restore and normal parallel
Build/Rebuild and fresh coherent Main/artifact qualification pass, completing local
authoring integration. Release qualification remains separate. The dated results
below qualify their earlier source only.

Import the canonical flat namespaces:

```csharp
using Sunder.Generated.Packages.Sunder_Package_Agent_Services;
using Sunder.Generated.Packages.Sunder_Package_Agent_Models;
using Sunder.Sdk.Services;
```

- Selected-Agent consumers inject `IPackageApi`; for example,
  `agent.Sunder_Agent_Runtime_Catalog.RuntimeCatalog.GetProfileAsync(profileId, token)`.
  The private owner service projects as `IAgentRuntimeService` from `agent-runtime`.
- Multi-provider consumers inject `IServiceProviders<TService>`, call
  `provider.Service` and pass `provider.Reference` for typed exact-provider values.
  Generated DI owns binding observation and retirement. Business lifetimes may use
  `RetirementToken`, `EnsureCurrentAsync` and `TryReportInvariantViolationAsync`.
- Business records are authored once; importers use generated Models. Generated
  codecs and envelopes replace copied values, JSON mapping, authored RPC catalogs,
  invocation helpers and transport leases. Genuine permission, persistence, vendor
  translation and UI behavior remain in their business owners.
- Multi-contract implementations repeat `SunderServiceProvider` and use explicit
  interface methods where names collide. New owned services default to private;
  imported types and references do not infer role/action grants.
- Content uses `SunderContent` and ordinary async disposal. Transient authority
  stays `[JsonIgnore]`, with `SunderServiceMember` only for deliberate service
  transfer. Server context remains only for authenticated-caller policy. Long
  operations declare `SunderMethod(TimeoutSeconds = 2100)` on the contract method.
- A transferred direct nullable reference may explicitly use
  `SunderOptionalServiceReference` for safe absence as null; nullable syntax alone
  stays strict. Permissions, unsafe failures and caller cancellation still apply.
  Minimal imported Runtime use is explicit, for example
  `SunderRuntimeUse(typeof(IExecutionTargetService), Actions = ["discover"])`.
- Replayable content-output watches can set `CompleteOnResponseContentRetirement`
  on the provider implementation; consumers resnapshot/resume on authenticated
  response-authority retirement. Other failures propagate. Generated JSON metadata
  avoids collectible-type reflection-cache retention and handles completion without
  an authored Unit/request type.

The coordinated public reviews established new accepted history for the unpublished
surface, with current versions 2.0.0, 2.1.0, 3.0.0 and 4.0.0. They did not rewrite
original descriptors, migration fixtures
or retirement evidence. The 15 canonical families and 26-project retirement remain
the established topology.

See the [extension quickstart](extension-quickstart.md), [providers](providers.md)
and [Core ordinary-service guide](https://github.com/Younics/sunder-core/blob/main/docs/package-development/ORDINARY-SERVICES.md)
for current authoring.

## Historical qualification — 2026-10-06–07

The remainder records the previous source and its executed checks. Its old generated
names and exact-public-byte claims describe that checkpoint, not the current API.

2026-10-07, final Builder/Package.Build follow-up. **Builder's ordinary initialization
and the final Core task/feed/artifact closure are complete:** 26 focused Builder tests,
the required 15-family artifact case, and 15 restored-state prebuilt validations
passed. The final feed refresh replaces only Package.Build 1.1.0; the other five
packages are byte-identical. All 31 release locks / 108 hashes are verified.
See the final follow-up below for the current identities. Earlier owning behavioral
and first post-retirement evidence retain their original scopes.

## Product result

- All 15 canonical package roots use neutral authoring metadata from
  **`Sunder.Sdk.Services`**. The final scan covers **91 active authoring files**.
- The actual `AgentProfileService` exposes its 15 async application operations.
  The profile view injects generated `IAgentProfileService`; its notification and
  availability state retains the presentation gateway as a separate dependency.
- The actual `AgentOwnerRuntimeProvider` is the private ordinary service. The App
  uses generated `IAgentOwnerRuntimeProvider` for its 19 unary operations and two
  streams. Handwritten operation tokens, the ordinary RPC client, mirrored owner
  values, generic JSON mapping and the forwarding owner service are removed.
- Anthropic, Gemini and LM Studio settings inject generated
  `ICredentialSettingsService`. Natural get/set/clear methods return credential
  presence only. The three handwritten clients, duplicated ports and credential
  query/command/value/snapshot envelopes are removed. Secret ownership, blank-key
  validation, trimming, cancellation and fault sanitization remain exercised.
- Skills' ordinary profile read uses injected `IAgentPackageApi` and explicit
  public-value-to-domain projection. Compatible installed versions **2.0.0 and
  2.0.1** both pass the retained genuine consumer regression with the fixed Core
  range resolver.
- MCP workspace fan-in uses explicit typed projection instead of a JSON
  serialize/deserialize roundtrip. Multiple optional providers and exact-activation
  checks remain part of its synchronization behavior.
- A subsession warmup race found during qualification is corrected: a reset that
  supersedes the initial read cannot complete initialization before a current
  snapshot is applied. The existing initialization fixture now covers that reset,
  caller cancellation and repeat-navigation sharing.
- Builder initializes the ordinary private-service template with target-owned
  create/restore operations, followed by ordinary Build. The initial private
  `AcceptSunderContracts` step and progress message are removed. Publish retains
  Check/Freeze/Pack for reviewed public APIs and describes APIs/package creation in
  the UI. Execution-target ownership, path containment and cancellation are preserved.

### Mechanical removal accounting

**19 product files / 1,763 original lines** are deleted:

| Removed surface | Files | Original lines |
| --- | ---: | ---: |
| Original `Rpc/Owner` value/model, mapper, forwarding service, client, token and gateway files | 14 | 1,468 |
| Handwritten `IAgentOwnerClient` and `IAgentOwnerRuntimeProvider` ports | 2 | 136 |
| Anthropic/Gemini/LM Studio `CredentialClient.cs` | 3 | 159 |

These are gross before-image counts for deleted product files. They do not count
test removals or additional deleted blocks in retained files, and do not subtract
the retained content-session implementation. Each credential service also shrank
from 65 to 31 lines while replacing its envelope API with natural methods.

## Calls after the migration

Application services use the ordinary namespace:

```csharp
using Sunder.Sdk.Services;

[SunderService]
public sealed partial class AgentProfileService
{
    // The existing domain implementation owns these operations.
}
```

Actual consumer calls now have these forms:

```csharp
// Profile UI: generated interface in the application service's namespace.
var profiles = await profileService.ListProfilesAsync(cancellationToken);
var created = await profileService.CreateProfileAsync("Research", cancellationToken);

// App owner operation: generated interface, no operation token or JSON bridge.
var snapshot = await owner.GetChatSnapshotAsync(new(), cancellationToken);

// Provider settings: generated private interface; Runtime returns presence only.
bool configured = await credentials.GetStatusAsync(cancellationToken);
await credentials.SetAsync(enteredCredential, cancellationToken);
await credentials.ClearAsync(cancellationToken);

// Selected external API: Sunder.Generated.Services.IAgentPackageApi.
var profile = await agent.SunderAgentRuntimeCatalog.RuntimeCatalog.GetProfileAsync(
    new() { Value = profileId }, cancellationToken);
```

The external call uses the reviewed requested range/prerelease policy and exact
contract/descriptor/ABI tuple. Same-package private clients retain exact owning
version and activation binding. Restore content provenance is not runtime version
selection authority.

## Retained adapters and their responsibilities

| Surface | Retained responsibility |
| --- | --- |
| `Runtime/AgentOwnerResources.cs` / `IAgentOwnerResources` | Caller-owned content scopes, single-use attachments, digest/length validation and activation-keyed resource negotiation. No ordinary unary/stream dispatcher. |
| `Runtime/AgentAppRuntimeGateway*.cs` | Presentation caches, revisions/replay, availability, command settling, cancellation and disposal. Ordinary dispatch is through generated interface methods. |
| `Rpc/Owner/AgentStackProviders.cs`, `AppComposition.cs`, `ProviderComposition.cs` | Real Stack contribution and role composition; these are the three remaining original owner-folder files. |
| Base `Rpc/Consumers` and provider-reference lifetime support | Heterogeneous provider discovery, exact-activation leases, cached descriptors, domain capability interfaces, permission/control contexts and retirement. These are not selected single-provider package calls. |
| MCP `RuntimeWorkspaceCatalog` | Optional multi-provider fan-in, best-effort provider failure isolation and start/stop/drain ownership. |
| Semantic Memory/Subagents runtime catalogs and capability clients | Synchronous domain catalogs, event/replay/cache projection and provider retirement. Replacing them with a single selected-package client would change discovery/activation semantics. |
| Builder/Files/Shell/Skills execution bindings and child/run-control clients | Exact target selection, invocation-bound capabilities, caller-owned content, explicit deadlines and correlated run/control lifetimes. |
| Remaining management/content and OpenAI auth presentation adapters | Bounded document/archive transfer, transfer cleanup, revision notifications, auth/callback state and domain presentation projection. |

No new production class named `Generated*` or mirror implementation interface was
introduced to disguise a forwarding tier.

## Compatibility and source-history closure

- All **19 public Agent canonical descriptor byte images and descriptor/ABI hashes**
  remain exact. Stack's descriptor/ABI also remain exact.
- The **31-contract historical oracle** is unchanged. All **28 protected historical
  files**, including the original retirement/checkpoint evidence, match their
  before-image hashes.
- Owning migrations preserve **12 complete original private records** under
  `retiredPrivateContracts`. Private current versions derive from the owning
  package version **2.0.0**.
- `compatibility/agent-ordinary-private-successors.json` independently records the
  **13 reviewed current private successors**, including the new profile service.
  Current conformance tests check these tuples rather than modifying the oracle.
- Historical source reconstruction still authenticates **96 original adapter
  sources**, the original project topology and the **26-project retirement**.
  Execution.Common remains live.
- `Fixtures/OrdinaryServices` contains two authenticated retired-port captures and
  exact inverse records for six changed historical business sources. Current and
  reconstructed hashes are checked; these records are never compiler inputs. Six
  drift cases verify that unreviewed current changes cannot disappear behind history.
- Legacy package API intent remains Runtime-only where migrated from a missing
  `consumerRoles` field. No consumer-role widening was performed.

### Remaining legacy spellings

- Active product RPC-prefixed authoring aliases: **0**.
- Neutral authoring types incorrectly qualified under `Sunder.Sdk.Rpc`: **0**.
- Active references to removed Agent owner plumbing or the three credential RPC
  clients: **0**.
- Four historical/topology test readers retain literal `SunderRpcProvider` MSBuild
  item names to interpret captured history and reject obsolete inputs. Frozen
  historical fixtures retain their original text.
- Wire/runtime SDK names such as `ISunderRpcClient`, `SunderRpcInvocationContext`,
  `SunderRpcContentReference` and descriptor idempotency enums remain legitimate
  advanced RPC APIs, not authoring aliases.

## Behavioral/source-amendment verification — 2026-10-06

All 15 canonical roots built successfully using the Services-namespace Core closure
before alias retirement. The owning checkpoint used source SDK mode and disabled
project-reference builds after those role outputs were prepared. These complete
behavioral suites are reused for the post-retirement integration checkpoint.

| Project/scope | Passed | Failed | Skipped |
| --- | ---: | ---: | ---: |
| Agent (all tests except the separately qualified all-family artifact case) | 1,843 | 0 | 6 |
| Anthropic | 47 | 0 | 0 |
| Gemini | 48 | 0 | 0 |
| LM Studio | 47 | 0 | 0 |
| OpenAI | 231 | 0 | 0 |
| Semantic Memory | 50 | 0 | 0 |
| Skills | 61 | 0 | 0 |
| Subagents | 2 | 0 | 0 |
| Presentation State | 38 | 0 | 0 |
| Presentation | 198 | 0 | 0 |
| Local Execution | 285 | 0 | 12 |
| **Owning checkpoint total** | **2,850** | **0** | **18** |
| **Pre-retirement all-family source-artifact case** | **1** | **0** | **0** |

The 18 skips are retained prerequisites: one real Docker-daemon case, five
universal release-archive cases, and twelve Linux/Windows-specific filesystem
cases on this macOS host. The required source-artifact lane had no skips and
retains all 15 qualified current-RID archives.

That successful all-family run took **2,296.325s**. It covered normal canonical
restore/build/pack, generated App DI and views, actual Worker processes, persistence,
all-family provider/consumer behavior, exact public/Stack/private policy closure,
cold local candidate selection and deterministic repack. Two earlier attempts
stopped before full-family completion on obsolete App-probe assumptions and an
out-of-root fixture lock; the final run fixes both without relaxing Core's source
fence. Test-local selections now live under content-keyed project `obj` paths.

## First post-retirement SDK/package qualification — 2026-10-07

This is the preceding six-package/three-representative checkpoint. The final
Package.Build follow-up below supersedes its feed and source-artifact identities;
its 18 representative commands were not rerun or relabeled as the final task run.

The fresh local feed was produced by Release owning restore/pack operations for all
six SDK packages, with isolated `--artifacts-path`, package cache and restore-lock
copies. The RID-neutral pack graph retained all **41 checked package version/hash
pins** from the existing Core locks. Core's normal source-mode locks and outputs
were preserved.

The package-set owner verifier passed. Payload checks cover **80 archive entries**,
including current task/analyzer/build assets, SDK assemblies, Stack contract assets
and stamped template content. Seven matching assembly/PDB pairs authenticate **297
authored source documents** and 19 retained generated documents; 62 compiler-virtual
JSON-generator documents are recorded separately. The compiled SDK exposes the
17 Services declarations and contains none of the **14 retired attributes or two
retired authoring enums**. The packaged editor generator matches the current
workspace generator.

| SDK package, exact version 1.1.0 | `.nupkg` SHA-256 |
| --- | --- |
| `Sunder.Sdk` | `eb3fecdf24b95d21300a621fb402065ed36bc59591799dce7f1b5a707648f92a` |
| `Sunder.Sdk.Avalonia` | `1de9a8b41d8d97736256d93c58b8af38554a0bb93689e0f5e5e253a94604b4c8` |
| `Sunder.Sdk.Stacks` | `a31b61b6e076f0a37ea10f5d1b0b09cf6b080b836b3be42475bfdcddbb7681e0` |
| `Sunder.Sdk.Worker` | `a1ce4f2018676341c1616f3be13e531d33acf85d01b2e86f0e5565174e706e63` |
| `Sunder.Package.Build` | `b4c516828a8282eae19930041f72f6e91889ded8e7f560fd71688f60ec3a8e85` |
| `Sunder.Package.Templates` | `5b2bb34f24af5c9034adc6d7be4e868cfa09c45315e2a13aa757719274f7d732` |

The owning `scripts/release/refresh-release-locks.sh` refreshed and verified **31
release locks / 108 Core package entries** against this feed: SDK 30, Avalonia 24,
Stacks 12, Worker 27 and Package.Build 15. Templates is part of the coordinated feed
and has no Agent lock entry. Package versions remain exactly 1.1.0.

### NuGet-only representatives

Tools.Shell, Provider.Anthropic and Agent each completed the ordinary six-command
workflow: Restore, `RestoreSunderContracts`, `CheckSunderContracts`, Build,
`PackSunderPackage`, and CLI validation. **18 commands passed**, with zero build
warnings/errors, using Release/osx-arm64, fresh caches and copied source without a
sibling Core checkout. Every SDK dependency resolves as a NuGet package with the
fresh feed's exact archive/content hashes.

Private service checks use the current package-version tuples and preserved retired
private history; public baselines remain frozen. All 1,904 copied source files match
their before-images. The three retained native bundles bind the fresh Release task
and SDK bytes, and every qualified leaf file matches its final archive.

### Final coherent 15-family artifact case

The required
`AgentSourceArtifactMigrationTests.CompleteOwnerArtifact_DerivesConsumerAndProviderLocksFromFreshSourcePayload`
passed **1 / 0 failed / 0 skipped** after **2,612.219s** of runner time (43m30s test
duration), using the matching post-retirement Debug source SDK/tool closure.

All release-lock refreshes and the 14 owning local-candidate updates completed
before this fixed-source wave. All **15 archives and complete qualified native
Worker/App bundles** are retained. The final base archive is byte-identical to the
candidate selected before the wave, so no post-qualification authored lock update
was needed. Test-local `obj` selections also match the authored lock bytes.

The retained archive audit confirms all 19 public Agent byte images and tuples,
Stack parity, all 13 reviewed current private successors, 12 complete retired private
records and 28 protected historical files. Each native bundle binds current source,
owned baseline, import lock, complete managed toolset and Worker/App receipts;
every indexed leaf file matches its retained archive. The 11-project **2,850-pass /
18-skip** behavioral result above is reused, not rerun.

### Workspace asset state

All **405 normal workspace restore assets** are byte-identical to their pre-checkpoint
state. SDK dependencies in those assets remain source-project references; no
isolated feed/cache paths leaked into normal assets. The normal Debug SDK and entire
task closure also remain exact. Normal source-mode Build/Rebuild inputs are ready
for the coordinator's workspace checkpoint.

## First post-retirement candidate and toolset handoff

The 14 dependent authored locks were updated through `UpdateSunderPackageApi` with
network fallback disabled before qualification to select the retained base archive. Public
descriptor tuples, versions, import intent and owned history remain exact.
Artifact content and the provenance-bound public inventory digest changed through
the owning tool as intended.

| Evidence | SHA-256 / identity |
| --- | --- |
| Core Debug task DLL | `125ee01dbc23e4473120099e4ff85458539e534f8ac2697b48be5bcb8c9f1cba` |
| Core Debug SDK DLL | `0c9617f1a1055d0f629b6abbca71a180ee2552ed6b746ae9f0db04d684a7d6fc` |
| Packaged Release task DLL | `486229b38fd83a8eb4be8a0c2d0eafa2f7370c2087e6787f142e686e41e33e7c` |
| Packaged Release SDK DLL | `89f269d670adb7db7065094ec030aee5b9f250e11e8c62f8bfaee52fd6630dad` |
| Six-package feed inventory | `16bf8f09504e860ef3a445ce2506873303b680ef7d4e899eb6a31afb1506ec06` |
| Qualified base archive | `0b13ce3e60f3e13dc0e4deb9e5014294e377fcd7ba7f3bf26c5da59e21984747` |
| Selected base package content identity | `f73305513dac1d255e01a51c679f4c189a0d0c3c358e0e775ef4f560a91d1ed0` |
| Selected public inventory evidence | `b7133492f00b793361badacd7cd93e435ad93327a1ef860f9222b5d0a9444242` |
| Fifteen-archive evidence report | `46df109e9b0352bb7ac4e0fa8306e3214238b46b0b4ff8d42db6cdfbac495550` |

That checkpoint's retained local engineering evidence is under:

```text
/var/folders/fx/6_dmjj8j40b50b8wcr7n_vx40000gn/T/opencode/agent-post-alias-sdk-20261007/
```

The feed is the `feed/` child directory. Its inventory identity is SHA-256 over
path-sorted path/size/sha256 records encoded as compact sorted-key JSON without LF.

Key files are `final-qualification.json`, `feed-verification.json`,
`pdb-current-source-verification.json`, `sdk-owner-restore-pin-proof.json`,
`release-lock-verification.json`, `package-mode-summary.json`,
`package-mode-operations.jsonl`, `local-candidate-updates.json`,
`agent-all-family-source-artifacts.trx` / `.log`, and `final-workspace-state.json`.
`qualified-source-artifacts/source-artifact-evidence.json` indexes all 15 archives.
`qualified-native-inputs/` and `nuget-native-inputs/` retain complete native bundles,
including `role-inputs.v1.json` and digest sidecars. `final-qualification.json` binds
each archive, qualification digest, toolset, source/lock proof and role receipt.

The earlier behavioral/source-amendment evidence remains unchanged under
`agent-ordinary-services-20261006/`: `final-agent-handoff.json`,
`owning-checkpoint-results.jsonl`, `owning-final-*.trx`,
`agent-all-family-source-artifacts-3.trx`, and its original archive/candidate records.

This completes the local post-alias SDK/current-RID integration checkpoint.
Coordinator workspace Build/Rebuild, Registry integration, the six-RID native/release
matrix and production publication retain their own acceptance evidence. This local
feed has not been published remotely.

## Final Builder/Package.Build follow-up — 2026-10-07

### Builder correction and focused evidence

`BuilderProjectApplicationService` now creates and restores the private-template
project through the resolved execution target. The template needs no authored
private baseline. The owning initialization theory covers success, create failure
and restore failure, and proves the successful create/restore/ordinary-Build command
sequence uses the same project root. The generated-source Builder artifact fixture
enforces that sequence and still exercises public Check/Freeze/Pack, target identity,
capability, cancellation, retirement and path ownership boundaries.

| Current verification | Actual result |
| --- | --- |
| Complete `BuilderServiceTests` class | **26 passed / 0 failed / 0 skipped** |
| Required all-family source-artifact case, including genuine generated Builder Worker/App lifecycle | **1 passed / 0 failed / 0 skipped**, 39m18s test duration; 2,359.985s runner time |
| Restored-state owning prebuilt Pack validations | **15 passed**, all archives byte-identical to the tested source-artifact archives |

The earlier 2,850-pass behavioral checkpoint is reused. Core's separate final owner
report, `joint-core-checkpoint/POSTFIX-RESULTS.md`, records the one green full
Package.Build TRX (**1,315 passed / two expected skips**) and the prior full PublicApi
scope (**269 passed**).

### Final isolated feed

Only `Sunder.Package.Build.1.1.0.nupkg` is selected from the fresh isolated Release
owner pack. The five other package archives are copied byte-for-byte from the first
post-retirement feed and checked after final feed assembly. Their source inputs and
dependency-resolution records remain exact; Core's normalized RID restore-profile
labels are recorded separately.

The source-lock owner's SDK child also emits an incidental SDK package. That output
is retained with the initial pack attempt, not selected into the final feed. The
task's private SDK dependency is its actual fresh Release output; the public SDK
package remains the previously qualified archive. Current assembly/PDB source proof
and all task/analyzer/build assets are retained. The final owning release-lock refresh
checks **31 files / 108 entries**; only the **15 Package.Build content hashes** differ
from the preceding qualified lock set.

### Why all 14 local candidates were updated

The current base archive is **not** byte-identical to the preceding
`0b13ce3e…` archive. Exactly four entries differ: Worker DLL, App DLL, App PDB and
content index. The role comparison records identical generated-source bodies and
contract bytes; each role receipt differs only in `inputFingerprint`. The current
App reference set also contains a different `Sunder.Sdk.Avalonia` binary hash.

The new base is `0515e5d2…`. All 14 dependent selections were therefore updated by
`UpdateSunderPackageApi` before the fixed-source final artifact wave. Import intent,
roles, public descriptor/ABI tuples and owned history remain exact. The final tested
base is byte-identical to that pre-wave candidate, and all 15 current archives carry
the reviewed selection. Public19/Stack and all 13 current-private successors pass.

### Normal workspace restoration and current role qualification

The successful artifact test's nested restores temporarily normalized five Core
NuGet source locks and 15 generated restore files. The owning Core restores returned
all five locks to their exact initial profiles; three remaining derived cache stamps
were restored from authenticated pre-run backups. All **405 normal restore assets**,
Core product input hashes, normal Debug task/SDK outputs and staged diffs now match
their pre-run state.

The test-run native bundles remain separately retained. Against the restored state,
the normal owning capture target and `ManageSunderPackageRoleInputsTask` Export
revalidated the original immutable compiled plans, references, generated files,
receipts and payloads. This refresh changes qualification envelopes only. Standard
prebuilt `PackSunderPackage` then validated all 15 refreshed bundles and reproduced
every tested archive exactly, without another behavior run or role compilation.

### Exact final identities and handoff

| Material | SHA-256 / identity |
| --- | --- |
| Core Debug task DLL | `4fabe8d1f69c2356e3d5ffb65bb5ceb151f5626bb6af0e9f189f305c2bc65c75` |
| Core Debug SDK DLL | `0c9617f1a1055d0f629b6abbca71a180ee2552ed6b746ae9f0db04d684a7d6fc` |
| New Release task DLL | `0beb5383c1aaf0b2b64e777cf6ae21c3b6714cb364f597dfcff23f3e7b8ae960` |
| Release task's private SDK DLL | `b68e23232d8f64c8f58eea79da8650089fb6a968c341787944058a83dc191aeb` |
| Final Package.Build 1.1.0 archive | `aa066ed74d7ecf7cfebd19ee1a98024356c31908f2d78a8b807de2c6f0c49ae4` |
| Final six-package feed inventory | `52d8e6be3454820d3e79893615e15eb0b8a0371b5f93c82d87ea46607f4ea5f9` |
| Final base archive | `0515e5d271c864dbb0115d2c9f4e6bdc3f84742c55dab08cf06bf71019c1cc6a` |
| Selected base content identity | `9c44b6b6e506f24ebfd12a8ec7094b9163d60de2648b166883ffee098617aaf5` |
| Selected public inventory | `02757b34ec31ca06b44c401a64a1fbfb66a114ebd2be079127a170b982cb4534` |
| Final Builder archive | `40a9351cc0692ef62b934f3d780ba252dd818173fbaaa5015af2ec7a5d5e0816` |
| Final Builder role evidence | `9228a5284d79ed794fa2dd1eb5dcebd2c70d7602c856a76903ac6735ad3c5570` |
| Final fifteen-archive evidence | `29ddca5ec28968539fe0d8d55bbdd7a981c02281052f31f52350e05b1e8050c5` |

Final evidence root:

```text
/var/folders/fx/6_dmjj8j40b50b8wcr7n_vx40000gn/T/opencode/agent-builder-postfix-20261007/
```

Use **`feed-final/`**, not the retained initial pack output. `HANDOFF.md` and
`final-qualification.json` bind all final archive/role hashes. The proof includes
`feed-verification.json`, `five-package-reuse-proof.json`,
`release-lock-verification.json`, `base-comparison.json`,
`base-role-input-comparison.json`, `local-candidate-updates.json`,
`builder-focused-tests.trx`, `agent-all-family-source-artifacts.trx`,
`normal-restore-recovery.json`, `restored-role-evidence.json`, and
`final-workspace-state.json`, with exact commands/logs in `operations.jsonl`.

`qualified-source-artifacts/` contains all 15 tested archives.
`artifact-run-native-inputs/` preserves their original test-run bundles;
`qualified-native-inputs/` contains the refreshed normal-state qualification and
explicit prebuilt selections. Parent workspace Build/Rebuild remains the next
coordinator checkpoint.
