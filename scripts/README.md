# Agent scripts

| Directory | Responsibility |
| --- | --- |
| `release/` | Approved cold inputs; native producer/capture/no-rebuild fan-in; exact family provenance; immutable publication; acceptance, atomic cohort promotion and reviewed reverse. |
| `dev/refresh-release-locks.sh` | Deliberate source-lock preparation from an exact Core feed. Never part of immutable publication/promotion. |
| `test/` | Test inventory, duration/evidence report and analyzer/report fixtures. |
| `test/release/` | Release tooling mocks, bounded native transport fixtures, and explicit real-input qualification commands. |
| `compatibility/` | Online Protocol-retirement protection and reviewed compatibility maintenance; historical paths remain stable. |

## Maintained local commands

```sh
python3 -B scripts/test/release/agent-release.fixture.py
python3 -B scripts/test/release/release-inputs.fixture.py
python3 -B scripts/test/release/native-family.fixture.py
python3 -B scripts/test/reconcile-service-successors.fixture.py
bash scripts/test/release/promote-agent-family.fixture.sh
bash scripts/test/generate-test-duration-report.fixture.sh
```

Use Python 3.9+ and the tools required by the selected fixture. The release-version
and analyzer fixtures run .NET; the two `real-*.fixture.py` commands require explicit
already-built inputs as described in [cold inputs](../docs/cold-release-inputs.md).
They are not implicit fast-loop checks or substitutes for native acceptance.

Local Core candidate preparation is documented by the Core producer's script guide.
Given its exact feed, run `bash scripts/dev/refresh-release-locks.sh 1.1.0 <feed>`;
inspect the resulting lock changes before release qualification.

### Current service successor evidence

After owning public Accept/Freeze and generation, reconcile the current public
fixture and explicitly selected current-private hashes with:

```sh
python3 -B scripts/test/reconcile-service-successors.py --rid osx-arm64 \
  --private-contract sunder.agent.owner.runtime \
  --private-contract sunder.package.agent.agent.profile --write
```

Omit `--write` for a read-only check. `--configuration` defaults to `Debug`.
This command reads the actual frozen public baseline and existing owning receipts;
it performs no build, acceptance, lock operation or network access. It checks public
descriptor/ABI snapshot hashes, receipt agreement and retention of all 19 original
public snapshots. Private ABI hashes come from the owning generator receipt; the
current C# descriptor readers perform full SDK schema/ABI validation. Private IDs,
versions and method sets require separate review if changed. Only the two current
successor fixtures can be written; the historical oracle, owned baselines, locks,
and unselected private records are preserved. Normal tests never refresh fixtures.
The focused Python fixture uses isolated copies of already-generated Debug/osx-arm64
evidence from this checkpoint; it does not build Agent or contact a Registry.

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

`promote-family-dist-tag.sh` and its fixture were removed. The release workflow
only called the old fixture; the operational release path already used
`promote-agent-family.sh` → `agent_cohort.py`. Manual consumers must use the
approved acceptance/cohort procedure in [Releases](../docs/RELEASES.md#promotion).
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
| Stale/recovery-required/conflicting response | Existing `test_recovery_required_stale_and_conflicting_success_never_fallback` |
| Verification failure/concurrent replacement | `test_post_commit_verification_failure_retains_result_without_compensation` |
| Read-only verification and partial-family rejection | `test_read_only_verification_rejects_partial_family_and_never_sends_credentials` |
| Restore previous tags/delete newly added tags | Existing `test_explicit_reverse_restores_old_tag_and_deletes_only_new_tag` plus `ReverseTests`; a separate reviewed atomic reverse replaces automatic per-package compensation. |

Keep exact 15-archive inventory, all six native RIDs, immutable tested role bytes,
no-rebuild fan-in, initially-Yanked publication, separate owner credentials,
pre/post-visibility acceptance, retained result verification and approved reverse.
No mock qualifies native execution, service approval or production release.

## Official language-runtime cutover

`release/verify-execution-runtime-availability.sh` and its shell fixture are retired:
Registry no longer publishes or resolves language-runtime artifacts. Host obtains
official .NET 10 Runtime and Node 24 bytes directly. Native **package** role capture,
fan-in and projection closure remain active. Agent acceptance consumes Host's current
installed-runtime DTO and native launch checks, with no runtime lock/signing fields.
Package cohort policy synchronization now sends only package/platform subjects and
their existing policy/checkpoint floors.
