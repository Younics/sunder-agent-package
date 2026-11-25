# Agent scripts

| Directory | Responsibility |
| --- | --- |
| `ci/` | Ordinary exact-input hydration, parallel builds and test execution. |
| `release/` | Automatic dependencies; explicit cold acceptance; native capture/no-rebuild fan-in; immutable publication, atomic cohort visibility and reviewed reverse. |
| `dev/` | Optional contract-cache diagnostics/orchestration and deliberate source-lock preparation from an exact Core feed. Never a normal authoring prerequisite or part of immutable publication/promotion. |
| `test/` | Test inventory, duration/evidence report and analyzer/report fixtures. |
| `test/release/` | Release tooling mocks, bounded native transport fixtures, and explicit real-input qualification commands. |
| `compatibility/` | Current dependency/artifact boundary checks; protected history is outside product discovery. |

## Maintained local commands

### Local contract preparation

For the ordinary build, the published Build SDK hydrates the contract cache.
`agent_ci.py prepare` accepts the selected platform catalog and exact
Agent `.sunderpkg` selected by the lock:

```sh
python3 -B scripts/ci/agent_ci.py prepare --work /tmp/agent-public-build \
  --candidate /absolute/path/to/exact-owner.sunderpkg \
  --catalog /absolute/path/to/selected-catalog.json
```

For direct `dotnet` calls, pass `SunderPackageCandidatePath` and
`SunderPlatformCatalogPath` on restore. These are byte sources, not defaults for
API Add/Update/Implement commands, and the SDK verifies them against the locks.

```sh
dotnet restore Sunder.AgentPackage.slnx --locked-mode \
  -p:SunderPackageCandidatePath=/absolute/path/to/exact-owner.sunderpkg \
  -p:SunderPlatformCatalogPath=/absolute/path/to/sunder-platform-contracts.v1.json
dotnet build Sunder.AgentPackage.slnx --no-restore
```

No lock refresh or API acceptance is needed. A sibling checkout cannot override
the NuGet selection. Hosted App tests need an explicit published App distribution.

The Python wrapper remains an **optional** all-project diagnostic/recovery tool.
It requires Python 3.9+ and restored NuGet compiler references, and can use retained
inputs from any location:

```sh
python3 -B scripts/dev/prepare-contracts.py \
  --candidate "/absolute/path/to/exact-owner.sunderpkg" \
  --catalog "/absolute/path/sunder-platform-contracts.v1.json"

# Check the existing cache without restoring contract evidence:
python3 -B scripts/dev/prepare-contracts.py --check
```

This validates the complete selected inputs before invoking Core's owning restore
target for every canonical project in `packages.json`, stopping on the first failure,
then checks every project's exact cache readiness. Main selects only the platform
catalog; its candidate is supplied only to projects whose locks import Main.
With `SunderContractCacheRoot` unset, it hydrates the normal user-profile
`.sunder/contracts/v1/sha256` cache used by both terminal and IDE builds.
`--cache-root` or an explicit `SunderContractCacheRoot` selects a different
cache; `SUNDER_HOME` and the Host-managed execution runtime are independent of this
authoring cache. Restoring these existing selections preserves the contract locks
and owned baselines. Ordinary restore repeats missing-input hydration when the
cache is cleared; no repeated manual preparation is needed.
The same package version or filename is not enough: the archive's exact content
identity, inventory and model projection must match the current locks. An older
qualification or a fresh rebuild may select different bytes. No default Registry,
lock refresh, baseline acceptance or cache-file copying is part of preparation.
Compilation, no-restore, design-time and Pack never acquire contract inputs or
repair corruption. Core also exposes the
read-only `dotnet msbuild <canonical-project> -t:CheckSunderContractCache` target;
its diagnostics report the effective cache root and missing selected input kinds.

### Local checks

The same discovered script-check groups used by GitHub can be listed or executed locally:

```sh
python3 -B scripts/test/run-ci-checks.py source --list
python3 -B scripts/test/run-ci-checks.py release
python3 -B scripts/ci/agent_ci.py matrices
python3 -B scripts/ci/agent_ci.py preflight
python3 -B scripts/ci/agent_ci.py validate --inputs /tmp/agent-inputs
python3 -B scripts/ci/agent_ci.py validate --inputs /tmp/agent-inputs --builder-diagnostics
```

`preflight` generates and validates the test inventory and runs dependency-independent
script checks (source checks on each behavior platform, release checks on Linux).
Actions gates dependency production on its Linux/Windows results after version
selection. Both `preflight` and `validate` accept `--packages '[]'` to stop before
tool execution when no versions are selected.

`validate` discovers tests, resolves and hydrates
exact inputs, builds the selected projects and their provider dependencies, executes the selected suites and
verifies combined evidence. Its temporary cache is cleaned on success or
failure. Supply `--inputs` or `--candidate`/`--catalog` to use existing reviewed
local inputs. Source readers precede the required all-family artifact producer;
required Linux/Docker cases run once in their owning lane. `--stress` adds the
scheduled Linux repetitions. Report directories replace prior CI runs; hosted
reports expire after fourteen days, while immutable release handoffs keep their
release-specific retention.

Restore and build each use one ordinary MSBuild graph. Multiple selected roots
produce a temporary `.slnf` over the authored solution; a single project or the
full solution stays unchanged. Dependencies and generated providers are still
built normally, with locked public restores.
The prepared graph and solution bytes are integrity-bound. Build selection,
UTF-8 output, elapsed time and exit status are retained under `artifacts/reports/`.
This does not replace the fresh all-family artifact test with prebuilt packing.

Completed assertion failures retain discovery/TRX evidence and allow the remaining
independent source/package test projects to run. Capture, integrity, discovery,
incomplete-run and cancellation errors stop execution. UTF-8 logs and command
records retain child status and diagnostic failures; the console prints a bounded
failure summary, and reporting failures remain attached to the original error.

`--builder-diagnostics` prepares the locked Release inputs, builds Builder,
then runs its portability fixtures followed by the complete Builder project. Each
stage verifies fresh discovery, TRX and declared skips, retaining reports under
`artifacts/builder-diagnostics/`. This is a local, owning-suite diagnostic command,
not a feature switch in **Validate**, **Qualify** or **Release**. Those workflows
always use the normal package/inventory-selected validation lanes.

**Qualify** (`sunder-package-qualify.yml`) is manual-only, defaults to `all`, and
accepts the same package IDs as **Validate**. It includes locked-package checks,
does not publish, and does not cancel an active qualification on a later push.
The old branch-specific parity wrapper is retired.

Required platform execution inventories live in
`scripts/test/required-platform-cases.json`, not workflow Python or feature
branches. The reusable validation summary downloads only matching owner/mode/RID
reports and runs `scripts/ci/required_test_evidence.py`. Exact identities/counts,
missing scopes, duplicates, failures and skips remain strict. Changes to that
test-owned policy require review; generic orchestration must not regenerate or
silently relax it. Scenario selectors in `scripts/test/ci-scenarios.json` likewise
belong to test policy, not version-selected build/deploy decisions.

Nested Python-to-Bash calls use `scripts/ci/bash_runtime.py`. Actions binds the
running shell's absolute executable in `SUNDER_BASH_EXECUTABLE`; local Windows
commands locate and validate Git Bash. A custom binding must be an existing
absolute executable path. Inventory generation retains separate stdout/stderr and
command diagnostics in `artifacts/reports/test-inventory.*`; the JSON inventory is
installed atomically only after successful generation and validation. Failures
remove stale inventories and retain the original subprocess exit code.

Individual checks remain useful for focused iteration:

```sh
node --test scripts/compatibility/check-package-boundaries.test.mjs
python3 -B scripts/test/release/agent-release.fixture.py
python3 -B scripts/test/release/release-inputs.fixture.py
python3 -B scripts/test/release/native-family.fixture.py
python3 -B scripts/test/verify-current-contracts.fixture.py
python3 -B scripts/test/test_prepare_contracts.py
python3 -B scripts/test/verify-current-contracts.py --source-only
python3 -B scripts/test/release/test_agent_ci.py
python3 -B scripts/test/release/test_agent_pipeline.py
python3 -B scripts/test/release/test_dependency_inputs.py
python3 -B scripts/test/test_test_evidence.py
```

Use Python 3.9+ and the tools required by the selected fixture. The analyzer
fixtures run .NET; the two `real-*.fixture.py` commands require explicit
already-built inputs as described in [cold inputs](../docs/cold-release-inputs.md).
They are not implicit fast-loop checks or substitutes for native acceptance.

For fresh current local archives, verify all fifteen with the Python release reader
and the handoff's existing Core inspections/source qualifications:

```sh
python3 -B scripts/test/release/real-release-metadata.fixture.py --local-handoff . \
  artifacts/current/handoff.json
```

This mode launches no .NET process and uses explicit local unpublished authority.
It requires `scope: local-unpublished-current-RID`, checks pinned current
qualifications and complete source frames, each owning lock, and separately
authored `roleUses`. Validation binds `inventorySha256` to the current expected
inventory. Its result is current-RID metadata evidence, not native or publication
qualification.

The offline package-boundary fixtures check active release coordinates, providers,
roles and capability owners against `compatibility/agent-current-inventory.json`.
Project evaluation distinguishes NuGet `PackageId` from runtime
`SunderPackageFamilyId`, including Package Maker's explicit identity. Protocol
dependencies and artifacts remain prohibited; no remote absence audit is used.

Local Core candidate preparation is documented by the Core producer's script guide.
Given its exact feed, run `bash scripts/dev/refresh-release-locks.sh <feed>`;
inspect the resulting lock changes before release qualification.

### Current contract records

After owning public Accept/Freeze, current selection and all-family qualification,
check current artifacts against independently authored source expectations:

```sh
python3 -B scripts/test/verify-current-contracts.py --evidence artifacts/current/inputs.json --propose
python3 -B scripts/test/verify-current-contracts.py --evidence artifacts/current/inputs.json
```

Both modes are read-only. `--propose` prints a candidate byte record for separate
review; it cannot update the expected inventory, records, active owned/lock files
or captured history. Default mode compares to reviewed
`compatibility/agent-current-contracts.json`. The initial 18-public/13-private record
is checked in from reviewed current qualified inputs. See [input format and coverage transfer](../docs/current-test-baseline.md).
The retired capture/identity-inverse/reconciliation executables have no current
replacement requirement; preserved data is inert. Generic SDK future contract
evolution and public baseline history remain supported.

## Breaking path migration

- `scripts/release/*.fixture.{sh,py}` → `scripts/test/release/`, except the retired
  sequential dist-tag fixture below.
- `scripts/generate-test-*` and `scripts/test-silent-return-analyzer.fixture.sh`
  → `scripts/test/`.
- The generic `generate-release-evidence.sh` is retired; `agent_pipeline.py` owns
  current selected-package provenance and evidence.
- `scripts/release/refresh-release-locks.sh` → `scripts/dev/refresh-release-locks.sh`.

CI, Python imports and current architecture script references use the new paths.
Historical migration reports retain their original command/evidence bytes. No
private-workspace dependency or old internal-path wrapper is introduced.

## Sequential dist-tag publisher retirement

`promote-family-dist-tag.sh` and its fixture were removed. The integrated
`agent_pipeline.py` → `agent_cohort.py` path owns visibility. Manual consumers use
the retained-request procedure in [Releases](../docs/RELEASES.md#promotion).
The old positional CLI cannot be forwarded: a cohort requires exact identities,
an approved retained request, idempotency key and policy revision.

The old 516-line publisher / 325-line fixture responsibilities map to existing
`CohortTests` in [`agent-release.fixture.py`](test/release/agent-release.fixture.py):

| Retired case/invariant | Retained owning protection |
| --- | --- |
| Stable and numeric-preview downgrade refusal | `test_forward_downgrade_stable_and_numeric_preview_stops_before_mutation` |
| Concurrent newer tag during the guard | `test_concurrent_policy_change_during_guard_keeps_original_fence_and_never_compensates`; the atomic policy fence replaces sequential snapshot/compensation. |
| Failed, credential-free just-in-time guard | `test_guard_failure_stops_before_post_and_guard_receives_no_registry_credentials` and existing request-tamper test |
| Missing/malformed human owner token, no API-key fallback | `test_missing_or_malformed_owner_credential_never_falls_back_to_api_key` |
| Ambiguous write and idempotent rerun | Existing `test_lost_response_retries_byte_identical_request_and_retains_result` |
| Unavailable/stale/conflicting response | `test_unavailable_stale_and_conflicting_success_never_fallback`; server failures retry the same retained request and never create a compensation request. |
| Verification failure/concurrent replacement | `test_post_commit_verification_failure_retains_result_without_compensation` |
| Read-only verification and partial-family rejection | `test_read_only_verification_rejects_partial_family_and_never_sends_credentials` |
| Restore previous tags/delete newly added tags | Existing `test_explicit_reverse_restores_old_tag_and_deletes_only_new_tag` plus `ReverseTests`; a separate reviewed atomic reverse replaces automatic per-package compensation. |

Keep exact 15-archive inventory, all six native RIDs, immutable tested role bytes,
no-rebuild fan-in, initially-Yanked publication, separate owner credentials,
pre/post-visibility checks, retained result verification and approved reverse.
No mock qualifies native execution, service approval or production release.

## Official language-runtime cutover

`release/verify-execution-runtime-availability.sh` and its shell fixture are retired:
Registry no longer publishes or resolves language-runtime artifacts. Host obtains
official .NET 10 Runtime and Node 24 bytes directly. Native **package** role capture,
fan-in and projection closure remain active. Agent acceptance consumes Host's current
installed-runtime DTO and native launch checks, with no runtime lock/signing fields.
Package cohort policy synchronization sends only `nonce`, exact `packages`, exact
`platformCatalogs` and `minimumPackagePolicy`. Initial visibility sends an explicit
null package-policy floor; announcement confirmation uses the retained committed
`packagePolicy` revision/fingerprint. Lower revisions and same-revision fingerprint
conflicts are rejected. These are client cached-consistency checks against authoritative
PostgreSQL state, not independent proof against database rollback. No checkpoint or
topology members are sent, accepted or synthesized.

Both release consumers validate the response's nonce, pinned HTTPS origin, complete
exact subject set, policy identity and fixed 24-hour UTC interval. Confirmation also
requires Allowed Listed packages and Supported platform catalogs before the owner-token
idempotent readback. The publish credential is never substituted for the owner credential.
`agent-release.fixture.py` covers these boundaries, retained-request retry/reverse and
the pipeline's initial synchronization with mocked transports and synthetic local files;
it launches no .NET or live Registry/GitHub operation.
