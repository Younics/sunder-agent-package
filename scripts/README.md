# Agent scripts

| Directory | Responsibility |
| --- | --- |
| `ci/` | Ordinary exact-input hydration, parallel builds and generated provider-fixture preparation. |
| `release/` | Approved cold inputs; native capture/no-rebuild fan-in; immutable publication, atomic cohort visibility and reviewed reverse. |
| `dev/refresh-release-locks.sh` | Deliberate source-lock preparation from an exact Core feed. Never part of immutable publication/promotion. |
| `test/` | Test inventory, duration/evidence report and analyzer/report fixtures. |
| `test/release/` | Release tooling mocks, bounded native transport fixtures, and explicit real-input qualification commands. |
| `compatibility/` | Current dependency/artifact boundary checks; protected history is outside product discovery. |

## Maintained local commands

### Local contract preparation

NuGet restore and Sunder contract restore are separate steps. After NuGet restore,
prepare a fresh or cleared authoring cache with the exact Main `.sunderpkg` candidate
and Core platform catalog selected by the current `sunder-contracts.lock.json` files.
Obtain those retained inputs from the matching local qualification or release handoff.
Core validates their pinned content and projections against each existing lock.

Run this from the Agent repository root with Python 3, supplying both input paths:

```sh
python3 -B - "/absolute/path/sunder-platform-contracts.v1.json" "/absolute/path/younics.ai.agent.2.0.0.sunderpkg" <<'PY'
import json
from pathlib import Path
import subprocess
import sys

catalog, candidate = [str(Path(value).expanduser().resolve(strict=True)) for value in sys.argv[1:]]
inventory = json.loads(Path("packages.json").read_text(encoding="utf-8"))
for package in inventory["packages"]:
    project = Path(package["projectPath"])
    lock = json.loads(project.with_name("sunder-contracts.lock.json").read_text(encoding="utf-8"))
    command = [
        "dotnet", "msbuild", str(project),
        "-t:RestoreSunderContracts",
        f"-p:SunderPlatformCatalogPath={catalog}",
    ]
    if "younics.ai.agent" in lock["packageArtifacts"]:
        command.append(f"-p:SunderPackageCandidatePath={candidate}")
    subprocess.run(command, check=True)
PY
```

This invokes Core's owning restore target for every canonical project in
`packages.json`, stopping on the first failure. Main selects only the platform
catalog; its candidate is supplied only to projects whose locks import Main.
With `SunderContractCacheRoot` unset, it hydrates the normal user-profile
`.sunder/contracts/v1/sha256` cache used by both terminal and IDE builds.
An explicit `SunderContractCacheRoot` selects a different
cache; `SUNDER_HOME` and the Host-managed execution runtime are independent of this
authoring cache. Restoring these existing selections preserves the contract locks
and owned baselines. Repeat preparation when selected inputs change or the cache
is cleared, then use the ordinary solution build.

### Local checks

```sh
node --test scripts/compatibility/check-protocol-retirement.test.mjs
python3 -B scripts/test/release/agent-release.fixture.py
python3 -B scripts/test/release/release-inputs.fixture.py
python3 -B scripts/test/release/native-family.fixture.py
python3 -B scripts/test/verify-current-contracts.fixture.py
python3 -B scripts/test/test_prepare_shared_provider_fixture.py
python3 -B scripts/test/verify-current-contracts.py --source-only
python3 -B scripts/test/release/test_agent_ci.py
python3 -B scripts/test/release/test_agent_pipeline.py
bash scripts/test/generate-test-duration-report.fixture.sh
```

Use Python 3.9+ and the tools required by the selected fixture. The release-version
and analyzer fixtures run .NET; the two `real-*.fixture.py` commands require explicit
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
Given its exact feed, run `bash scripts/dev/refresh-release-locks.sh 1.1.0 <feed>`;
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
- `scripts/generate-release-evidence.sh` → `scripts/release/generate-release-evidence.sh`.
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
| Missing/malformed owner token, no publish-token fallback | `test_missing_or_malformed_owner_credential_never_falls_back_to_publish_token` |
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
