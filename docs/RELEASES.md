# AI Agent Family Releases

AI Agent releases select version-bumped `.sunderpkg` entries from the fifteen-package
`packages.json` inventory. Each canonical `.csproj` owns its literal `<Version>`;
there is no implicit family-wide version override in CI. `packages.json.releaseVersion`
remains a legacy compatibility/default-contract marker, not the release selector.
There is no Protocol NuGet or Agent NuGet publishing credential.

The owner `younics.ai.agent` publishes first. The explicit family includes
`younics.sunder.package.maker` (Sunder Package Maker) alongside the AI Agent
extensions; a shared runtime-ID prefix is not the family inventory. Release
workflow uses **Release**. It runs on protected `main` pushes or
manual dispatch on `main`, compares authored literal versions with paginated
**published GitHub releases**, and automatically creates `<inventory-key>/v<version>`
tags/releases only for newer versions (for example `agent-provider-openai/v2.1.0`).
Tags without a published release do not count as completed publication. A run with
no newer versions succeeds without SDK setup, dependency preparation, component
validation/tests, builds, packaging or publication. Comparison is within each
package's authored major, so supported-major maintenance can coexist with newer
majors. A lower/equal version within that major reuses exact published bytes.
GitHub hides draft releases from callers without push access, so the protected
planning job needs `contents:write` to discover interrupted drafts. The planner
issues only reads and invokes no build tooling. Selected versions are subsequently
checked against evaluated Release/CI properties in the dependency-preparation job,
with GitHub/Registry credentials removed from MSBuild's environment.

`packages.json` records the predicted package-wide capability union. Local
Execution drops `background-services.v1` and `runtime-generations.v1`; Files,
Shell and Web drop `logging.v1`. Removing the explicit extended-deadline
declarations from Agent, MCP and Subagents narrows role requirements while retaining
that capability in the affected package unions. Qualify each actual role and union from newly generated
manifests at the artifact checkpoint.

## Local Development Boundary

Unpublished family development uses exact local owner candidates and a local Core
catalog. It needs no Registry or production approval. Local selections omit
`registryOrigin`; published selections pin an explicit Registry. Builds never
silently replace selected candidates or rewrite locks to match a new archive.

## Active Release Boundaries

The GitHub entry points are:

- `sunder-package-smoke.yml` (**Validate**): PR/merge-queue versions compare against
  the immutable event base before toolchain setup; weekly/manual full source and
  stress validation remains available. Releases supply their exact selected IDs
  and additionally run locked-package compatibility. Manual dispatch can also
  select comma-separated `packages` or `all`; full selection includes locked-SDK
  compatibility. `package-tests` adds compatibility to a partial selection, and
  `builder-diagnostics` selects the separate focused/full Builder diagnostic lane.
- `sunder-package-release.yml` (**Release**): the version-selected protected-main pipeline from validation
  through six-RID native capture, no-rebuild fan-in, publication, visibility and announcement.
- `sunder-agent-cohort-rollback.yml`: an explicit reviewed reverse of an unannounced
  cohort, sharing the release concurrency group.

They share `sunder-package-component.yml` for component tests, native production/
composition and per-package publication. Each operation has a strict outcome summary.

Both workflows end with **Summary**, which rejects failed, cancelled or unexpectedly
skipped selected jobs. Interrupted publication recovers the retained plan and bytes
and skips validation/build. A completed same-source publication becomes a no-op.

Test ownership comes from `SunderTestOwner`. Component jobs select exactly their
owned suites and evaluate their real project-reference closure for restore and
contract hydration. Shared `agent-family` assertions run once per reference mode
and behavior platform during full validation. Linux retains the owning filesystem
security and real-Docker cases. Per-project discovery, reports and their expected
inventory use the same selection. The all-fifteen source-artifact lane remains a
required full-validation integration checkpoint.

After owned source tests and any required/stress cases pass, each selected canonical
project runs `PackSunderPackage`. Its archive payload must match the existing role
outputs used by those tests, and packing must leave those outputs unchanged. The
scoped archives, payload inventories, pack logs and hash/size receipt are retained
under `artifacts/source-components`. The full-family lane additionally exercises
the combined behavioral probes and requires all fifteen archives.

Locked-SDK compatibility uses each suite's declared `PackageCompatibility` cases.
A component with no owned compatibility cases retains `compatibility-selection.json`
and exits before restore or build. A declared lane that discovers zero tests fails.

Agent CircleCI and the separate acceptance/promotion dispatch workflows are retired.
Native byte qualification, exact input validation, package/platform policy and
immutable retries remain required. See [native inputs](native-release-inputs.md)
and [cold release inputs](cold-release-inputs.md).

## Ordinary CI Inputs

Workflow configuration has these owners:

- `global.json` and the exact Core checkout supply .NET/Node/npm toolchains through
  `.github/actions/setup/action.yml`. The action also binds native Bash for nested
  Python script calls through `SUNDER_BASH_EXECUTABLE` (Git Bash on Windows).
- `eng/ci/platforms.json` supplies native, preflight and source/package behavior matrices.
  Native runner overrides retain the existing `SUNDER_AGENT_RUNNER_<RID>` variables.
- `scripts/test/ci-scenarios.json` owns required test selections; `agent_ci.py test`
  and `report` execute/check them. `run-ci-checks.py source|release` discovers script checks.
- `compatibility/agent-current-inventory.json` remains the independently reviewed
  family expectation. Release helpers derive membership/counts from it and still
  require every package and native RID. Produced archives cannot redefine that policy.

Tool and verifier paths come from evaluated project outputs; native inputs are
discovered from Core's qualified role bundles and ambiguous outputs fail. Workflows
own ordering, permissions, protected credentials, artifact transport and action pins.

`scripts/ci/agent_ci.py` shares the release input owner's complete byte/lock
validator. It invokes Core's `RestoreSunderContracts`, uses an isolated cache and
retained toolset, and runs normal parallel builds. Generated test providers are a
project dependency built with the tests; discovery and execution retain per-run
evidence. It does not write approval or cold-release receipts.

`dependency_inputs.py` discovers dependencies automatically. The locked
`Sunder.Package.Build` NuGet version identifies its published Core release/tag and
source commit. Other SDK packages retain their independent locked versions.
Published Agent archive/catalog assets are located from their actual identities;
identical coordinator copies are allowed, conflicting copies are rejected.

If an exact selected owner was never published, discovery retains its exact catalog
and schedules six native package/RID leaves against locked NuGet dependencies.
One composition job verifies all six qualified leaves and produces the immutable
owner candidate. Admission checks full content identities, public inventories and
descriptor/ABI tuples against the committed locks before any consumer build.
It never silently selects another
version, substitutes mismatched bytes or rewrites a lock. Missing published bytes
without matching authored source are a genuine dependency error, not a request for
an operator-supplied SHA or asset locator.

Actions runs discovery once on Linux and prepares verified Core tools once per
needed platform. It retains `agent-prepared-dependencies`, optional
`agent-candidate-<package>` bootstrap outputs, then seals `agent-dependency-inputs`.
The `cold-inputs.json` SHA-256 is passed to source, package-mode and native jobs.
Each consumer checks that digest,
source/Core commits, locks and candidate/catalog bytes before hydration. Runners
never independently regenerate the selected owner archive. A failed identity check
retains the rejected bytes and original log and stops downstream work.

Before dependency production, **Check scripts** runs inventory generation and
dependency-independent script checks on Linux and Windows. It uses the same
version-selection gate, so an empty selection installs no toolchains and runs no
preflight. The Summary requires its successful outcome for every active selection.
Behavior jobs generate their own scoped inventories from their actual checkout;
script suites run once per preflight platform. Inventory failures print and retain
stdout, stderr, command and exit status without leaving invalid/stale JSON evidence.

This bundle is dependency evidence, not native release qualification. Final family
verification still requires the composed native owner identity to equal every
committed dependent selection. A missing/mismatched selected archive requires
restoring its exact bytes or explicit authoring-tool selection review before a
release can succeed; the pipeline does not rewrite locks or substitute merely
descriptor-compatible archives.

CI package builds normalize generated-role compiler paths and omit debug symbols
and commit-dependent assembly/Source Link metadata. This makes unchanged payloads
reproducible across checkout locations and lock-only commits; the exact source
commit remains bound by release provenance and qualified native inputs. The
repository's LF checkout policy preserves source bytes across operating systems.
Use a clean checkout, or `dotnet clean` before switching an existing output tree
to these CI settings, so stale portable symbols cannot enter the archive.

The local resolver generates `ci-inputs.json` with local `{name,size,sha256}` records.
The shared Actions producer binds those records to source/Core/locks in
`cold-inputs.json`; the same verifier also accepts historical durable input bundles.
No `SUNDER_AGENT_CI_INPUTS`, approval reference or per-release input manifest needs
configuration. Ordinary CI and production native preparation use normal prepared
execution; explicit denied-network/cold acceptance remains a separate profile.

For local validation:

```sh
python3 -B scripts/ci/agent_ci.py preflight
python3 -B scripts/ci/agent_ci.py resolve --core ../sunder-core --output /tmp/agent-inputs
python3 -B scripts/ci/agent_ci.py prepare --core ../sunder-core \
  --inputs /tmp/agent-inputs --work /tmp/agent-ci
python3 -B scripts/ci/agent_ci.py run --state /tmp/agent-ci/ci-state.json -- \
  dotnet build Sunder.AgentPackage.slnx -c Release --no-restore
python3 -B scripts/ci/agent_ci.py run --state /tmp/agent-ci/ci-state.json -- \
  dotnet test Sunder.AgentPackage.slnx -c Release --no-build --no-restore
```

Use a fresh temporary work directory. On macOS, use physical paths consistently
when invoking package projects beneath `/var` (`/private/var`). No baseline or lock
selection is updated by these commands. `--mode package` is the focused compatibility
profile against the final locked SDK packages. Explicit `--candidate` and `--catalog`
files remain available for local authoring checks; their identities are checked too.

For selected local behavior validation, add
`--packages '["younics.ai.agent.provider.openai"]'` to `agent_ci.py validate`.
`--packages '[]'` stops before SDK/dependency work; omitting the option runs the full
profile. Temporary integration probe projects explicitly use the Debug configuration
required by Core 1.0.2's fixture driver; canonical source outputs, toolsets and role
snapshots follow the test assembly's configuration, including Release CI.

## Core Coordination

Publish the declared Core developer dependencies first. The resolver accepts the
Build project's `nuget-sunder-package-build/v*` release or a historical combined
`sdk/v*` release, resolves its immutable tag, and checks out that source on all
standalone runners. No manually maintained Core commit/version variable is needed.
Workspace CI instead uses its checked-out Core source. Exact NuGet release locks
still bind real dependency versions/content hashes; no common SDK version is imposed.

Local `scripts/dev/refresh-release-locks.sh <feed>` is explicit preparation,
not a release-time fallback. Source baseline publication evidence remains a separate
authoring operation; the immutable artifact-source tag and frozen histories never move.

## Native Build Boundary

Each native job builds only selected canonical projects for its actual `win-x64`,
`win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64` or `osx-arm64` architecture.
Workers are framework-dependent, untrimmed .NET10 modules with no bundled runtime,
apphost, SDK or toolchain. Generated App roles are retained where authored.

Unchanged packages are downloaded from exact numeric published-release assets,
with their original hash/size and six-RID native evidence. They are not rebuilt or
repacked. Every native smoke test still installs and exercises **all fifteen**
packages: the exact final selected archives plus unchanged universal archives. Mixed
package versions are expected. The final output audit and Runtime/App gates still
inspect all fifteen archives. Exact reviewed dependency candidates/catalogs remain
separate dependency inputs; changing a version never regenerates a dependency lock.

Release restore preserves the complete six-RID graph. One package × RID job selects
its native RID after locked restore and explicit contract hydration. Each package
is composed once from all six Core-qualified leaves with
`SunderPackageUsePrebuiltLeaves=true`, `BuildProjectReferences=false` and an explicit
`SunderPackagePrebuiltRoleInputsFile`; they never rebuild captured role bytes.

`native-family.py collect-component`, `compose-component`, `gather`, `record-final`
and `finalize` own bounded transport and exact module/runtimeconfig/deps/shared/App
byte comparisons. Schema 5 binds every native smoke record to the final archive
hash and size; fan-in verifies and copies those bytes without another Pack. The
schema-3/4 readers remain for retained evidence. Native smoke exercises real
official .NET installation, every Worker lifecycle, domain RPC/auth/SQLite behavior,
crash recovery, cleanup and Builder's separate SDK path. Core metric budgets and
the current-RID installed-projection gate remain enforced. Fan-in runs the retained
Agent archive tests and the real Core Runtime/App family tests before publication.

## Stable Boundary

Pack requires source-matched frozen public contracts, exact locks and qualified
role inputs. Private-only baseline absence and historical private records remain
inputs too. Do not Accept/Freeze, regenerate a historical oracle, change source,
or rebuild native inputs during fan-in or publication.

## Publication Order

`agent_selection.py` owns read-only planning. `agent_pipeline.py` is the single
publication pipeline; `agent_publication.py` owns bounded numeric GitHub transport
and public install-plan postchecks. The pipeline:

1. Verifies the canonical selection, clean protected-main source/Core/lock
   coordinates and full-family native evidence before any tag/release write.
2. Creates per-selected-package tags and numeric drafts. The first selected package
   is the coordinator and retains all selected tested archives for recovery; other
   drafts retain their own archive. Each retains the plan, full-family reports,
   source/material/byte provenance, checksums, SBOM and original toolchain record.
3. Publishes the selected owner first, then independent consumer leaves through
   `publish-registry-package.sh --initial-yanked`. Each leaf retains an immutable
   `agent-publication-<package>` receipt; consumers require the selected owner's
   receipt. Unchanged versions are verified read-only as Listed. A single writer
   merges the exact receipt set, and the coordinator retains
   `registry-publications.json`: the Registry-returned publication evidence for
   every tested package, including its actual owner. Retries and later readback
   compare that immutable evidence before visibility changes or announcement.
4. Retains one complete cohort request with its idempotency key and current policy
   revision on the draft **before** any availability mutation.
5. Commits the selected-batch Yanked→Listed transition and its dist tags in
   one Registry operation; preserves its exact request/result for recovery.
6. Confirms the committed result against fresh policy and the owner's retained
   operation, rechecks public package/projection bytes and six-RID install plans,
   then finalizes those drafts with `make_latest=false`, coordinator last.

Forward publication and visibility use the same scoped API key. Set
`SUNDER_REGISTRY_COHORT_CREDENTIAL_SOURCE=api-key` for forward cohort operations;
there is no human CLI credential fallback in that mode. No per-package
visibility loop, automatic compensation or new request after an ambiguous result
is permitted. The metadata/provenance and native hashes continue to bind the same
source and bytes throughout the pipeline.

## Promotion

Visibility and finalization are jobs in the main workflow, not separate dispatches.
Stable releases advance Registry `latest`; prereleases advance `preview`. Publishing
an older supported major lists its exact version while preserving any newer channel
target. The chosen tag subset is retained with the original atomic request and
replayed unchanged. Forward visibility cannot downgrade a tag. A fresh policy fence can fail if another operation changed
the Registry; an unknown outcome must retain the original request/key/revision.

Synchronization uses a fresh nonce, the pinned HTTPS Registry origin and the complete
exact package/platform subject set. Initial visibility requests `minimumPackagePolicy=null`;
confirmation supplies the retained committed revision/fingerprint and rejects older or
same-revision conflicting identities. Responses require a valid policy fingerprint and
the fixed 24-hour UTC observation interval. The confirmation check additionally requires
Allowed Listed packages and Supported platform catalogs before exact owner-token result
readback. PostgreSQL is authoritative: this retained floor checks client consistency and
does not independently prove absence of database rollback. Checkpoint/topology fields and
external policy stores are absent from the current release protocol.

`agent-family-cohort-<request-sha256>.json` is a schema2 receipt containing
`registryOrigin`, `artifactSourceCommit`, numeric `releaseId`, `request` and the
actual Registry `result`. Numeric asset readback checks its canonical bytes and
release/source binding. It is not a synthetic production acceptance certificate.

Use **Re-run failed jobs** on the original workflow/source. Do not rebuild successful
native jobs or replace their artifacts. A later dispatch of the same protected source
recovers the original plan and tested bytes from the coordinator, skipping native
builds. If staging stopped before retaining all selected archives, rerun the original
failed job with its existing tested artifact; no rebuild fallback is allowed.
Retained candidate/native Actions artifacts last 90 days; release assets are durable.
An unfinished draft from another source blocks adoption. A GitHub/Registry mismatch
requires reconciliation, not an automatic overwrite or an inferred successful tag.
After a visibility failure, inspect/reconcile the retained operation before deciding
whether administrator recovery or a reviewed reverse is needed. Missing or conflicting
immutable bytes require investigation and a new version, not clobbering an old one.

### Reviewed Non-Security Reverse

The explicit rollback workflow uses a new reviewed idempotency key and current
policy revision, requires an unannounced numeric draft and the exact forward
receipt, then restores the captured prior tag map and Yanks the cohort atomically.
Supply `release_id`, `expected_package_policy_revision`, `idempotency_key` and
`confirmation=reverse-unannounced-cohort`. The protected-main resolver derives the
source commit, tag and forward-receipt digest from that draft and rejects missing
or ambiguous receipts. Those coordinates are not manually maintained inputs.
It cannot reverse a published announcement or overwrite concurrent tag changes.
Security revocation and database recovery remain administrator operations. Service or
transport failures retry only the exact retained request; an unresolved outcome retains
its evidence for reconciliation and cannot trigger an automatic reverse.

## Production Acceptance Evidence

Native tests and checked-in post-publication checks replace external scenario-driver
dispatch plumbing. Their results describe only the exercised scope. Deployed
database recovery, performance budgets, native App acceptance and clean-source
qualification retain their owning product release requirements. Mocked transport
or metadata tests do not prove those qualifications passed.

## Shell Fixture Scope

Run the release input/native/cohort Python fixtures, `test_agent_ci.py`,
`test_agent_pipeline.py`, `test_agent_selection.py`, `test_selective_pipeline.py`,
`test_dependency_inputs.py`,
`native-release.fixture.sh`, and the existing Registry
publish/NuGet signature/version fixtures. They cover input tampering, preserved
native bytes, exact retries, policy fences, public projection checks and reviewed
reverse behavior without publishing. The actual six native runners remain a
separate execution result.

## Repository Configuration

- Protected `main` and a main-only `sunder-agent-release-promotion` environment. Allow the
  release job's `contents:write` token to create the automatic package-key tags;
  tags do not trigger a second publication workflow. No manually created tag needed.
- Environment variable `SUNDER_REGISTRY_API_URL`: the Registry HTTPS origin, without an `/api/v1` suffix.
  Source/lock hashes and dependency locations are derived, never per-release settings.
- One environment secret `SUNDER_REGISTRY_PUBLISH_TOKEN`, created at Registry
  **Dashboard → API keys**, with `package:publish` and `package:release` for all
  fifteen exact package IDs. The Registry derives the publisher from the key and
  enforces package ownership/maintainer permissions; no owner ID is configured in
  GitHub. First publication requires the publisher's package reservation, updates
  preserve the existing owner, and atomic cohort release requires that owner's key.
  `package:promote-latest` is unnecessary for staged atomic releases. API keys support
  a 1–90 day lifetime or **Never**; rotate expiring keys before their selected expiry.
- The separate reviewed reverse workflow still requires a human
  `SUNDER_REGISTRY_CLI_TOKEN`; a release API key intentionally cannot roll back.
- Optional `SUNDER_AGENT_RUNNER_<RID>` JSON label arrays.
  RID suffixes use uppercase underscores. Defaults use native Windows2022/Windows11ARM,
  Ubuntu24x64/ARM and macOS15Intel/ARM runners.

Historical release/tag deletion is not part of this workflow. Require successful
replacement publication and explicit confirmation of cleanup scope first.
