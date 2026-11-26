# Qualified native release inputs

The Agent native scripts consume Core's **qualified generated-role inputs V1**.
Current final-archive native provenance is **schema 6**; the schema-3 single-version,
schema-4 selective and schema-5 final-archive readers remain supported for history.
New release staging requires schema 6. Schema-2 marker-only native evidence is
rejected; it cannot be upgraded by relabeling or rehashing an old handoff.

Core's authoritative contract is
`sunder-core/docs/package-development/PREBUILT-ROLE-INPUTS.md`, with schemas in
`sunder-core/specs/package/v1/prebuilt-role-inputs/`. Core validates the complete
canonical evidence, frozen owned APIs, import lock, evaluated source/configuration,
restored dependency graph, toolset, role plan, receipts and native leaf index.
The Agent transport validates bounded membership and byte identity before extraction;
it does not replace that qualification or generate receipts.

## Producer and native test handoff

Protected-main releases qualify one package at a time. Its owned tests produce
the six native leaves and `compose-component` packs them once. Execution covers
the subject alone for Agent, or the subject and its locked published Agent for
an extension. Schema-3 execution receipts bind that subject; a receipt for a
sibling or the historical full-family scope is not interchangeable. The scoped
selection is schema 3 and the final native provenance remains schema 6.
The all-family descriptions and PowerShell examples below cover full integration,
lock authoring and historical handoffs. [Releases](RELEASES.md) describes the
current package workflow.

Requirements: PowerShell 7, Python 3.9+, Bash, and the pinned .NET SDK. Review and
freeze **all** owned baselines before the first native Build. The producer verifies
their frozen/published state; it does not refresh locks or freeze contracts. Keep
source, locks and tools unchanged during the complete native operation.

From the Agent repository root:

```powershell
./scripts/release/build-native-family.ps1 -Rid osx-arm64 -Version 2.0.0 `
  -Package younics.ai.agent.tools.web -OutputDirectory artifacts/native-build/web/osx-arm64 `
  -InputsDirectory /resolved/agent-inputs -ToolsDirectory /verified/core-tools
```

First use `agent_selection.py retain` to fetch unchanged archive/evidence assets into
`SUNDER_AGENT_RETAINED_ARCHIVE_DIR`. The canonical, source/lock-bound plan is produced
by `agent_selection.py plan`; do not hand-author or modify it during release.
The component producer explicitly restores its locked project graph and builds one
selected canonical project for the exact native RID. `collect-component` retains
the qualified bytes. After all six leaves finish, `compose-component` uses a compact
Core V1 selection and prebuilt Pack to compose that package exactly once. `gather`
adds exact unchanged archives to the selected candidates. The archive job checks
integrity, roles, and App native dependency closure before the native matrix
starts. Each native job verifies its inputs match the inspected archive records
before executing, without downloading a second copy alongside the producer ZIPs.
It does not call `GetSunderPackageRoleInputs` after Build: that target depends on
Build. The only SDK source is the declared public NuGet feed.

The producer invokes `agent_ci.py prepare` before its first Build. It allocates a
new isolated Sunder home/cache, validates automatically resolved candidates/catalog
against the committed locks, hydrates through the published Build package, and routes
Build through that same prepared state. All six producers and composition restore
the declared packages normally. No operator manifest locator, dependency checkout or
offline-runner hash is required. A warm cache cannot replace selected input bytes.
Explicit denied-network diagnostics remain in [cold acceptance](cold-release-inputs.md),
not in the ordinary production native path.

Ordinary Core Build exports, beside each canonical RID leaf:

```text
src/<project>/bin/Release/<target-framework>/sunder-targets/<rid>/
  role-inputs.v1.json
  role-inputs.v1.json.sha256
  sunder-dev/                    # complete Worker and optional App inputs
```

The producer discovers this qualified bundle rather than embedding a framework
version in CI. It rejects missing/ambiguous output and validates the framework
against the authored project policy. Native RIDs come from `eng/ci/platforms.json`;
complete-family membership comes from the independently reviewed current inventory.

Apply native signing using Core's **pre-index Worker signing hooks**, before this
qualification is exported. Subsequent signing, reindexing or editing invalidates
the qualified bytes. Native tests must consume the producer's retained archives.

The reusable component workflow calls `collect-component` and `compose-component`;
the release workflow then calls `record-final` after the native lifecycle/metric gate. It:

1. Checks the whole qualified leaf against Core's exact file list and digest sidecar.
2. Matches every non-composed native file to the final tested archive, allowing Core's
   package-shared/role-shared/RID storage projection without changing any bytes.
3. Checks exact Core metric identity, counts and thresholds.
4. Writes `native-leaves-<rid>.tar.gz`, `native-leaf-evidence-<rid>.json` and metric copies.

Each selected package in native schema 3/4/5/6 retains `workerProject`, `leaf`, `testedArchive`,
`metricsSha256` and `metrics`. It adds:

```json
{
  "qualification": {
    "path": "younics.ai.agent/osx-arm64/role-inputs.v1.json",
    "sha256": "<exact reviewed Core evidence digest>",
    "size": 12345
  },
  "metricsSize": 1234,
  "sourceRoleUses": {
    "lockSha256": "<exact qualified contract-lock digest>",
    "roleUses": []
  }
}
```

`leaf.path` is now `<packageId>/<rid>/sunder-dev`; `leaf.hasApp` binds the qualified
role set. Obsolete `markerPath` and `markerSha256` are removed. `leaf.files` still uses `{path, sha256, size}`; Core's
unchanged evidence uses `{path, bytes, sha256}`. Tar membership is exactly the
qualification, its LF-terminated digest sidecar, every qualified leaf file,
`metrics/<packageId>.<rid>.json` and, for schema 6, the bound original reports under
`execution/<rid>/`. Paths are independent of the producer checkout.
The digest is bound into the owning CI provenance; an unauthenticated sidecar or
locally fabricated provenance alone is not release approval or native attestation.

`sourceRoleUses` is copied from the qualified Core Worker receipt and rechecked
against that receipt during transport. It is carried through fan-in so the release
reader can verify separately authored typed Runtime uses against the current lock
and all six RID qualifications. Package API `consumerRoles`/`requiredContracts`
still govern their complete import groups. Provider imports grant no consumer use
by themselves: an additional use must match the source receipt's exact contract,
descriptor/ABI, role, required flag and actions. In particular, Web's optional
execution-target discovery does not grant invocation, subscription or App use.
Older schema-3 captures without this projection are usable only when their qualified
receipts contain no separately authored role uses.

Schemas 4, 5 and 6 bind `planSha256`, selected `packageVersions`, all fifteen `familyVersions`,
exact `retainedArtifacts` and their previously authenticated `retainedSourceRoleUses`.
Every RID run includes `familyTests` for all fifteen archive/measurement identities,
including unchanged packages. Selected leaves still require Core qualifications on
all six RIDs. Missing unchanged-worker tests, mismatched versions, substituted
retained bytes or lost source-backed use evidence reject fan-in. Schema 5 additionally
requires each RID's `testedArchive` hash and size to equal the final published
archive, including selected packages. Earlier schemas bind the qualified role
bytes but may have tested a current-RID projection.

### Two-phase execution authority

`run-native-runtime-smoke.sh <archives> <new-results> development`
uses supported local development-import preparation for the exact final archives.
It does not install unpublished packages or fabricate Registry observations. The
required methods exercise every Worker and Maker's separate create → author
→ build → archive → validate/extract → development activation → supervised App RPC
→ unload/cleanup journey without Agent loaded. This qualifies backend execution,
not Maker view/UI, a desktop install/update or published-installed acceptance.
The development smoke separately projects MCP's exact App target and tokenizes JSON
in a fresh App process using the normal package load context. Highlighting must use
the projected native library; a host or cache fallback is not acceptance. The
Worker runtime under test and the installed-size ceiling are pinned in
[`eng/ci/native-release.json`](../eng/ci/native-release.json), independently of
package versions and the build SDK.

`prepare_maker_inputs.py` stages the published App/Host, the selected platform
catalog and, in development mode, the authoring packages restored from
[`maker-nuget.lock.json`](../eng/ci/maker-nuget.lock.json). Maker's template pins
its own toolset; Agent's Build dependency does not replace it. `inputs.json`
records the App release asset, catalog and actual Maker test inputs. Existing
outputs and failed reports are never replaced. The test-only RPC and snapshot hooks
runs inside the actual native App and retains OS-peer admission, load permits,
role permissions, and scoped content checks.

Schema 6 adds `execution` to each RID run: exact TRX method identities, development
scope, archive hashes, Maker jobs/journey and preparation/catalog records. `finalize`
retains their original bytes in six `native-execution-<rid>.tar.gz` files, bound by
`executionBundles` in `native-test-evidence.json` and durably stored on the coordinator.
Rehashed old evidence, success display names or omitted/skipped methods cannot
qualify the new phase. Original report hashes remain distinct from native attestation.

After immutable publication, the separate `published` mode requires an explicit
HTTPS Registry origin, not a human login token or publication API key. It anonymously
downloads each exact canonical archive to compare it with the qualified bytes.
It previews and applies the actual Registry install and reinstall in Host: the
preview binds exact versions, current-RID projections, runtime prerequisites,
capability consent and the resolution fingerprint. A canonical download alone
confers no installation authority. The harness checks installed Registry
provenance and nonce-bound policy observations, then tests
installed lifecycle/RPC/SQLite/recovery/cleanup. It uses `--host-only` preparation and
never rebuilds the selected family archives. It does not run Maker's development job
journey as a substitute for installed acceptance.
Schema-3 `scope/published-registry.json` records
`archiveDownloadAuthentication: "anonymous"`, both committed operations, their
projection identities, exact downloads and policy leases. Download-only
historical reports cannot satisfy a scoped release. New and
legacy credential environment names are stripped before preparation, builds and tests.

Each RID exports `native-installed-<rid>.json` and `.tar.gz`. Strict raw-tar membership,
report hashes, policy authority, metric identities/budgets and exact source/archive/
Registry binding are checked before all six form `installed-native-test-evidence.json`.
The coordinator must retain that set before visibility or announcement; recovery
verifies the originals without native-leaf remote writes. Native App UI, installers,
update and deployed recovery acceptance remain additional gates.

## Fan-in prerequisites and execution

Before composing a selected package, `agent_ci.py prepare` explicitly restores its
canonical project with the same declared SDK and dependency locks used by the
producers, including all declared RID graphs. The equivalent NuGet restore is:

```sh
dotnet restore <canonical-project.csproj> --locked-mode --configfile NuGet.Config \
  -p:Configuration=Release -p:ContinuousIntegrationBuild=true
```

Then `compose-component` validates all six retained leaves and packs the package once.
The composer performs **no Restore, Build, Compile, source generation, package
execution or Host launch**. It validates all six uploaded bundles before extracting
any of them, extracts into a fresh private directory beneath the artifact output,
and emits a compact Core selection for the package. Its Pack call uses:

```text
-t:PackSunderPackage
-p:Configuration=Release
-p:BuildProjectReferences=false
-p:SunderPackageUsePrebuiltLeaves=true
-p:SunderPackageComposeRuntimeIdentifiers=win-x64%3Bwin-arm64%3Blinux-x64%3Blinux-arm64%3Bosx-x64%3Bosx-arm64
-p:SunderPackagePrebuiltRoleInputsFile=<private-selection.json>
```

All six RID selections are explicit; evidence paths, property order, compact UTF-8,
no BOM and no terminal newline follow Core V1. There is no current-machine fallback.
Core rejection ends the operation, without an ordinary-Pack or build retry.

After all six platforms have smoked the exact final family, `finalize` verifies
their complete schema-6 handoffs and writes `native-test-evidence.json`. It does
not Pack again. `verify` rechecks those archives before publication. The retained
`record`/`assemble` commands support earlier current-RID schema-3/4 callers.

### Transport and publication behavior

- Exact six-RID selected-leaf inventory plus complete fifteen-package smoke/final
  archive inventory; no extra native input artifacts or selection
  entries. Optional standalone metric copies must be complete and byte-identical.
- Portable ASCII paths, depth/length limits, no rooted/parent/device/backslash paths,
  case collisions, duplicate entries, file/directory collisions, links/reparse
  points, sparse files or unapproved tar metadata.
- Full tar preflight precedes extraction. Only regular files and a bounded POSIX
  long-path extension are supported; extraction never uses `tar -x`/`extractall`.
- Core's 64 MiB qualification, 16,384 files, depth-32 discovery, 65,536 discovered
  entries and 2 GiB per-leaf bounds apply. Tar metadata is bounded before allocation;
  provenance JSON has independent size/depth/structural limits.
- Handoffs/output belong under `artifacts/` or outside reviewed source. Producer
  paths are never extraction destinations. No native `obj` or generated tree is needed.
- Final verification compares **all** retained non-composed files, including App
  bytes, through the final archive's shared/RID projection. Manifest and content
  index composition remain Core-owned.
- The retained schema-3/4 assembler preserves the prior family on Pack/verification
  failure. Its local publication is serialized
  by `.native-family-publication.lock`, replaces archives individually, and writes
  `native-test-evidence.json` last. A process interruption during publication can
  leave an incomplete family: verification fails closed and the same handoff can
  be rerun. If the publication lock survives a terminated process, remove that
  directory only after confirming the prior publisher has stopped. Never publish
  a directory until `native-family.py verify` succeeds.

## Release-owner integration

The entry points in [releases](RELEASES.md) enforce this ordering:

1. Provision Python 3.9+ on producer and fan-in runners, alongside existing tools.
2. Use the same reviewed Agent source and declared public NuGet dependencies on
   all native jobs and composition.
3. Retain `agent-leaf-<package>-<rid>` and `agent-candidate-<package>` artifacts.
   Final smoke uses `sunder-agent-native-<rid>` containing
   `native-leaf-evidence-<rid>.json` and `native-leaves-<rid>.tar.gz`. Merge exactly
   six final smoke handoffs before `finalize`.
4. The promotion fixture now creates fresh qualification, metric, leaf and bundle
   bytes through `native-promotion.fixture.py`, captures them with the real native
   implementation and invokes the actual schema-3 final verifier. It includes
   qualification digests, sizes, portable paths and `leaf.hasApp`; marker-only
   schema-2 data is not relabeled or accepted.
5. Keep native lifecycle, metric, output-audit, publication and provenance gates.
   The selected owner publishes first; component receipts are merged by one
   coordinator before atomic family activation.

## Local checks and qualification scope

```sh
python3 -B scripts/test/release/native-family.fixture.py
bash scripts/test/release/native-release.fixture.sh
```

The Python fixture exercises actual capture/transport/verification, a real MSBuild
RID-list boundary probe and mocked packing: relocation after deleting producer outputs, preserved Worker/App
and shared payloads, bounded long paths, unsafe tar inputs, tamper/missing/extra/
mixed/stale inputs, immutable-tool selection, Core rejection without fallback,
failure preservation and resume. It does not execute real Workers, compile a family,
qualify six native operating-system runners, attest signatures or publish anything.
