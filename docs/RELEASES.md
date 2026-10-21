# AI Agent Family Releases

AI Agent releases contain exactly fifteen `.sunderpkg` entries from `packages.json`,
one Agent source commit, one final Core SDK commit and one family version.
There is no Protocol NuGet or Agent NuGet publishing credential.

The owner `younics.ai.agent` publishes first. The explicit family includes
`younics.sunder.package.maker` (Sunder Package Maker) alongside the AI Agent
extensions; a shared runtime-ID prefix is not the family inventory. Release
workflow/provenance uses **AI Agent Family Release**, and the release title is
**AI Agent Family &lt;version&gt;**. Tags remain `agent/v*`; repository URLs,
inventory keys, project paths and artifact labels keep their technical names.

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

There are three GitHub workflows:

- `sunder-package-smoke.yml`: Linux/Windows source CI, scheduled/manual stress,
  plus locked-package compatibility when called by a release.
- `sunder-package-release.yml`: the integrated `agent/v*` pipeline from validation
  through six-RID native capture, no-rebuild fan-in, publication, visibility and announcement.
- `sunder-agent-cohort-rollback.yml`: an explicit reviewed reverse of an unannounced
  cohort, sharing the release concurrency group.

Agent CircleCI and the separate acceptance/promotion dispatch workflows are retired.
Native byte qualification, exact input validation, package/platform policy and
immutable retries remain required. See [native inputs](native-release-inputs.md)
and [cold release inputs](cold-release-inputs.md).

## Ordinary CI Inputs

`scripts/ci/agent_ci.py` shares the release input owner's complete byte/lock
validator. It invokes Core's `RestoreSunderContracts`, uses an isolated cache and
retained toolset, runs normal parallel builds, then explicitly prepares the generated
provider fixture before tests. It does not write approval or cold-release receipts.

Set the repository variable `SUNDER_AGENT_CI_INPUTS` to a numeric GitHub asset
locator: `{repository, releaseId, assetId, name, size, sha256}`. The located bounded
JSON document contains exactly:

- `schemaVersion: 1`;
- `coreCommit`: the exact Core commit to check out;
- `candidates`: numeric asset locators for every selected owner archive;
- `platformCatalog`: the numeric asset locator for the canonical Core catalog.

Each locator binds the actual uploaded file's ID, name, size and SHA-256.
Preparation verifies every committed package content identity, complete public
inventory and platform descriptor/ABI tuple. A different rebuilt owner archive
is not a substitute for the archive selected by the locks. Missing input configuration
fails before build. Ordinary CI uses no production approval or offline-runner variables.

For local validation, explicit retained files can replace download configuration:

```sh
python3 -B scripts/ci/agent_ci.py prepare --core ../sunder-core \
  --candidate /tmp/selected-agent.sunderpkg --catalog /tmp/selected-core-catalog.json \
  --work /tmp/agent-ci
python3 -B scripts/ci/agent_ci.py run --state /tmp/agent-ci/ci-state.json -- \
  dotnet build Sunder.AgentPackage.slnx -c Release --no-restore
python3 -B scripts/ci/agent_ci.py fixtures --state /tmp/agent-ci/ci-state.json
python3 -B scripts/ci/agent_ci.py run --state /tmp/agent-ci/ci-state.json -- \
  dotnet test Sunder.AgentPackage.slnx -c Release --no-build --no-restore
```

Use a fresh temporary work directory. On macOS, use physical paths consistently
when invoking package projects beneath `/var` (`/private/var`). No baseline or lock
selection is updated by these commands. `--mode package` is the focused compatibility
profile against the final locked SDK packages; production release callers additionally
verify their approved cold-input manifest.

## Core Coordination

Before an Agent tag, publish the compatible final Core `sdk/v*` release and developer
packages, review every Agent release lock against those exact packages, commit the
Agent changes and require CI success. The release verifies Core tag/provenance,
GitHub assets, NuGet canonical content, repository signatures and the same full Core
commit on each runner. `agent/v<version>` must equal `packages.json` `releaseVersion`.

Local `scripts/dev/refresh-release-locks.sh <version> <feed>` is explicit preparation,
not a release-time fallback. Source baseline publication evidence remains a separate
authoring operation; the immutable artifact-source tag and frozen histories never move.

## Native Build Boundary

Each native job builds all fifteen canonical projects for its actual `win-x64`,
`win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64` or `osx-arm64` architecture.
Workers are framework-dependent, untrimmed .NET10 modules with no bundled runtime,
apphost, SDK or toolchain. Generated App roles are retained where authored.

Release restore preserves the complete six-RID graph. Native Build selects its RID
only after locked restore and explicit cold hydration. Current-RID smoke archives
and final fan-in consume Core-qualified role inputs with
`SunderPackageUsePrebuiltLeaves=true`, `BuildProjectReferences=false` and an explicit
`SunderPackagePrebuiltRoleInputsFile`; they never rebuild captured role bytes.

`native-family.py record`, `assemble` and `verify` own bounded transport and exact
module/runtimeconfig/deps/shared/App byte comparisons. Native smoke exercises real
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

`agent_pipeline.py` owns release sequencing; Core's `github_release.py` owns shared
numeric GitHub transport. The pipeline:

1. Verifies clean protected tag/source/Core/lock coordinates and native evidence.
2. Stages the fifteen tested archives, two native reports and four evidence files
   on one numeric draft. Partial/equal retries reuse exact retained bytes.
3. Calls `publish-registry-package.sh --initial-yanked` in dependency order. Each
   immutable version is downloaded and verified before advancing.
4. Retains one complete cohort request with its idempotency key and current policy
   revision on the draft **before** any availability mutation.
5. Commits the family-wide Yanked→Listed transition and all selected dist tags in
   one Registry operation; preserves its exact request/result for recovery.
6. Confirms the committed result against fresh policy and the owner's retained
   operation, rechecks public package/projection bytes and six-RID install plans,
   then finalizes the same draft with `make_latest=false`.

The scoped publish token is never used as an owner-token fallback. No per-package
visibility loop, automatic compensation or new request after an ambiguous result
is permitted. The metadata/provenance and native hashes continue to bind the same
source and bytes throughout the pipeline.

## Promotion

Visibility and finalization are jobs in the tag workflow, not separate dispatches.
Stable releases set Registry `latest`; prereleases set `preview`. Forward visibility
cannot downgrade a tag. A fresh policy fence can fail if another operation changed
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

Use **Re-run failed jobs** on the same workflow/tag/source. Do not rebuild successful
native jobs or replace their artifacts. Retained candidate/native artifacts last 90 days.
After a visibility failure, inspect/reconcile the retained operation before deciding
whether administrator recovery or a reviewed reverse is needed. Missing or conflicting
immutable bytes require investigation and a new version, not clobbering an old one.

### Reviewed Non-Security Reverse

The explicit rollback workflow uses a new reviewed idempotency key and current
policy revision, requires an unannounced numeric draft and the exact forward
receipt, then restores the captured prior tag map and Yanks the cohort atomically.
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
`test_agent_pipeline.py`, `native-release.fixture.sh`, and the existing Registry
publish/Core SDK signature/version fixtures. They cover input tampering, preserved
native bytes, exact retries, policy fences, public projection checks and reviewed
reverse behavior without publishing. The actual six native runners remain a
separate execution result.

## Repository Configuration

- Protected `agent/v*` tags and the `sunder-agent-release-promotion` environment.
- `SUNDER_AGENT_ARTIFACT_SOURCE_COMMIT`, `SUNDER_AGENT_LOCK_SET_SHA256`,
  `SUNDER_AGENT_RELEASE_APPROVAL_REFERENCE`, `SUNDER_AGENT_RELEASE_OWNER_PRINCIPAL_ID`,
  and `SUNDER_REGISTRY_API_URL` for production release jobs.
- `SUNDER_AGENT_COLD_INPUT_MANIFEST` with approved exact production source/lock/Core
  bindings, plus required native offline adapters from [cold inputs](cold-release-inputs.md).
- Separate secrets `SUNDER_REGISTRY_PUBLISH_TOKEN` and `SUNDER_REGISTRY_CLI_TOKEN`.
- Optional `SUNDER_AGENT_RUNNER_<RID>` JSON label arrays and
  `SUNDER_AGENT_OFFLINE_RUNNER_<RID>`/`SUNDER_AGENT_OFFLINE_RUNNER_SHA256_<RID>`.
  RID suffixes use uppercase underscores. Defaults use native Windows2022/Windows11ARM,
  Ubuntu24x64/ARM and macOS15Intel/ARM runners.
- `SUNDER_AGENT_CI_INPUTS` supplies ordinary CI; it carries artifact locations, not
  production approval or Registry credentials.
