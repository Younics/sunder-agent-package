# Agent Family Releases

> **Source channel:** release tooling uses qualified Core role inputs and native
> schema 3. Native package compilation/fan-in, exact cold inputs, immutable
> provenance and atomic package cohort acceptance remain required. Host now obtains
> official language runtimes directly; no separately published runtime family is
> a prerequisite. Production/native qualification is distinct from local mocks.
> Historical canonical acceptance remains in [`../compatibility/README.md`](../compatibility/README.md).

Agent releases are coordinated family releases: exactly fifteen `.sunderpkg`
entries in `packages.json`, from one Agent commit, one Core commit and one version.
There is no Protocol NuGet artifact or Agent NuGet publish credential.

## Local Development Boundary

Creating unpublished local families requires no Registry. Ordinary package-tool
selection accepts validated local `.sunderpkg` candidates and a canonical local
Core catalog. Local selections omit `registryOrigin`; exact combined local restore
or complete verified cache evidence supports offline builds. Published dependencies
use the explicitly selected Registry, and publication evidence is a separate operation.
The production Registry's API/deployment state does not gate canonical development.

## Active Release Boundaries

- Core-qualified Worker/App role inputs, source/baseline/lock identity, all six native
  RIDs, safe transport and no-rebuild fan-in are described in [native inputs](native-release-inputs.md).
- Reviewed candidate/catalog delivery, isolated homes, lock-byte invariance and
  offline builds are described in [cold inputs](cold-release-inputs.md).
- Initial-Yanked publication, immutable retry/readback, distinct artifact/evidence
  commits and atomic cohort visibility remain the package release path below.
- Official .NET 10 Runtime installation and launch are Host-owned native acceptance
  evidence. Registry package requirements, package/platform permissions, WORM and
  revocation remain in force; Registry runtime availability/signing is retired.

## Core Coordination

Agent release CI resolves the immutable `sdk/v<CORE_SDK_VERSION>` release, verifies that the tag, Core release provenance, GitHub release assets, NuGet canonical content, NuGet repository signatures, package repository metadata, and every Agent release lock identify one Core commit and package version, and passes that full commit SHA to every Core test checkout. The Core tag, commit, and package version are recorded in `native-test-evidence.json`, the draft release notes, and `release-provenance.json`. Recovery accepts only those same materials.

Production release coordination requires this order (local development precedes it):

1. Land the compatible Core changes and publish the matching immutable Core `sdk/v*` release.
2. Publish the Core developer packages required by `Directory.Packages.props` and regenerate every Agent release lock against that exact remote package version.
3. Commit the coordinated Agent changes.
4. Confirm Agent smoke CI passes and the release workflow will resolve the expected Core SDK tag SHA.
5. Set `packages.json` `releaseVersion`, then create and push `agent/v<releaseVersion>` from the Agent release commit.

Do not create an Agent release tag until the matching `Sunder.Sdk`, `Sunder.Sdk.Avalonia`, `Sunder.Sdk.Stacks`, `Sunder.Sdk.Worker`, and `Sunder.Package.Build` packages and their public Core SDK release are final. Release CI runs the complete behavioral solution once against the exact provenance-matched Core source, then rebuilds the focused package-compatibility lane and all production bytes with `SunderUseSourceSdk=false`, locked mode, and NuGet.org. Agent production bytes are never built from the Core source checkout; that checkout also supplies the exact Runtime Host, Package Format, App, and Core metric gates.

During coordinated compatibility preparation, release locks can be refreshed before publication from an exact local Core candidate feed with `scripts/dev/refresh-release-locks.sh <version> <feed>`. The script restores with an isolated package and artifacts root, then verifies every referenced Core package lock hash against the candidate `.nupkg` bytes. Local candidate locks are preparation evidence only and never replace the immutable Core release checks required by production CI. See the [script guide](../scripts/README.md) for the supported Core feed producer and path migration.

The cleaned Agent solution now has **31 release locks**, verified against the supplied
local Core `1.1.0` candidates: **108 matching Core package entries**. Representative
Tools.Shell, Anthropic and Agent NuGet-mode Restore/contract-restore/Check/Build/Pack/
CLI checks passed in isolated Release/osx-arm64 copies. Authored inputs and original
Debug outputs were preserved. Exact remote Core tag/provenance/signature checks remain
required; these candidate bytes were not published by that qualification.

Release locks pin the complete declared six-RID graph. Native jobs restore that graph
without a one-RID restore override, then select their exact native RID for Build.
`restore -r <rid> --locked-mode` narrows the restore graph and fails NU1004 against
these locks; do not disable locked mode or rewrite the lock to hide the mismatch.

## Native Build Boundary

The release has one native job for each supported RID: `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64`, and `osx-arm64`. Each job must run on that exact native architecture and build all fifteen canonical projects, including generated App roles where present. Role leaves live at `bin/Release/net10.0/sunder-roles/{worker,app}/<rid>/sunder-dev`; the combined native leaf is `bin/Release/net10.0/sunder-targets/<rid>/sunder-dev`. The scripts select exact Core V1 qualification and request current-RID smoke archives using `SunderPackageUsePrebuiltLeaves=true`, `BuildProjectReferences=false`, and `SunderPackageComposeRuntimeIdentifiers=<rid>`, without rebuilding retained roles.

Every Worker is an untrimmed framework-dependent .NET 10 module with `SelfContained=false`, `UseAppHost=false`, `PublishSingleFile=false`, and `PublishTrimmed=false`. Each RID leaf contains the canonical `lib/<AssemblyName>.dll`, its `.deps.json`, and a runtimeconfig requiring `Microsoft.NETCore.App` 10.x. Package-owned managed and native dependencies remain in the leaf, while the .NET runtime, BCL, SDK, and toolchain do not. The native job launches all 15 resulting modules through a real Runtime Host lifecycle and records Core-enforced archive size, current-RID installed size, startup latency, process-count, and steady-RSS evidence for each Worker. The same gate exercises owner unary and server-stream RPC, MCP callback dispatch, OpenAI host auth, both Agent SQLite baselines, forced Worker crash recovery, disable/enable retirement, process cleanup, and temporary-directory cleanup. It also invokes Builder through an App-scoped RPC call, the packaged Builder Worker, the Agent workspace resolver, and the selected Local execution-target Worker. That regression prepends the exact CI-selected SDK directory through Builder RPC, pins its exact version with `global.json`, and creates, builds, and publishes a project while requiring the SDK executable to remain outside the shared Worker execution-runtime root.

Each job uploads a bounded qualified tarball so exact tested bytes survive Actions artifact transfer. Fan-in verifies every module, runtimeconfig, dependency-manifest, leaf-file, archive and metric hash before extracting all six sets. Generated App payloads remain in the retained RID leaves. Canonical `PackSunderPackage` uses Core V1 role-input selection, `SunderPackageUsePrebuiltLeaves=true` and `BuildProjectReferences=false`; it performs no rebuild. CI's complete Worker/App/native/shared projection comparisons remain required.

## Shell Fixture Scope

The inventory/path migration is covered by
`promote-agent-family.fixture.sh`, `verify-agent-publication.fixture.sh` and
`generate-agent-publication-handoff.fixture.sh`. They use temporary fixture bytes
and mocked GitHub/Registry operations, preserve the publication-before-promotion
and promotion-before-finalization checks, and make no production writes.
Publication verification mocks only the evidence writer's `dotnet --version` and
`--list-sdks` probes; it does not need a .NET build.

The owning Registry publication, atomic cohort, retained acceptance and
official Host evidence checks remain required. The existing
`native-release.fixture.sh` checks source policy/release-lock metadata and a
wrong-Core-lock rejection; it uses read-only `dotnet sln list`, not native builds.
Its pass is not six-RID evidence or proof that prebuilt packing succeeds. Final
release qualification still requires Core's qualified role-input contract, native
jobs and clean immutable source/provenance.

## Stable Boundary

Canonical Pack requires current source-matched, release-frozen owned contracts and
reviewed import locks. Protocol public-API ledgers are historical migration evidence,
not the stable authoring/release baseline. Retain their compatibility and feed-audit
obligations through actual project retirement.

The generated Agent compatibility candidate and its public-release evidence live
under [`compatibility/`](../compatibility/README.md). Regeneration is an explicit
review step; normal tests reject an uncommitted candidate delta.

The Agent 2.x line uses the fixed `>=2.0.0 <3.0.0` family contract for runtime dependencies. The coordinated `releaseVersion` must itself satisfy that runtime range under the Sunder SDK's strict SemVer precedence rules: stable `2.0.0` and later satisfying 2.x patch or prerelease versions are valid, while `2.0.0-preview.*` is below the lower bound. The dependency range remains fixed rather than being derived from `releaseVersion`. Core SDK dependencies retain their independent supported minor line.

## Publication Order

### Initial-Yanked Publisher Primitive

`publish-registry-package.sh` supports an explicit, independently retained expected
publication tuple:

```sh
bash scripts/release/publish-registry-package.sh \
  --initial-yanked expected-publication.json \
  https://registry.example package.sunderpkg example.package 2.0.0
```

The JSON must match the existing `RegistryPackagePublicationEvidence` DTO:
`package` contains exact `packageId`, `version` and `packageContentIdentity`;
the remaining fields are `ownerPrincipalId`, `publicInventorySha256` and
`initialAvailability: "Yanked"`. Derive content/inventory from canonical archive
inspection and owner identity from reviewed Registry authority inputs. Do not use
archive SHA-256 as package content identity or copy the response being checked into
the expected file. The helper bounds/snapshots this non-link JSON and rejects
duplicate/unknown members before any request; this explicit mode requires Python 3.

It retains the scoped publish headers and `setLatest=false`, adds multipart
`initialAvailability=Yanked`, and requires exact `.publication` readback plus current
`.isYanked == true`, canonical archive bytes and every projection/payload check.
Add `--verify-only` for token-free read-only verification. A lost/malformed upload
response may succeed only after exact matching readback; missing/conflicting state
fails without reupload, tag mutation, unyank or compensation. Retrying the same
retained inputs verifies an already accepted identity instead of uploading it again.

The family publisher now uses this mode for all fifteen retained archives. Its
release metadata helper validates complete selected public inventories against the
committed locks, checks ordinary dependency ranges, and derives a deterministic
dependency order with the base owner first. Origin-free entries remain origin-free;
a present origin must equal the approved Registry. A dependency outside the retained
Agent cohort fails closed pending separately reviewed candidates. Core remains the
archive/descriptor validator; these release projections do not replace it.

After listing, `--verify-only --initial-yanked <expected.json>
--current-availability Listed` verifies the original Yanked publication and current
Listed state. `Either` is restricted to read-only exact-retry reconciliation; only
the Registry's retained cohort operation can prove an ambiguous listing completed.

The closed canonical `release-provenance.json` schema 1 binds `artifactSourceCommit`,
the existing invocation/Agent/Core/material identities, `controlPlane` (numeric
repository/run IDs, workflow path/file commit and push event), the approved
publication origin/owner, and `lockSetSha256`. The lock digest is SHA-256 of compact
canonical UTF-8, without BOM or final newline, of ordinally path-sorted
`{path,sha256}` records for all fifteen production `sunder-contracts.lock.json`
files. Each entry hashes exact file bytes. The sorted `artifacts` array contains
only the fifteen primary archives, with path, package ID/version, size, archive
SHA-256, inspected package content identity and complete public inventory hash.
Supporting native/output evidence remains covered by `SHA256SUMS` and the SBOM,
outside that array. Unknown/duplicate/missing fields, source/lock drift and changed
primary identities fail. The handoff additionally pins every numeric asset ID,
including the provenance asset, and repeats the artifact-source and lock-set binding.

### Family Orchestration

Immutable publication is triggered only by the exact `agent/v<releaseVersion>` tag.
The protected publication orchestration is:

1. Verifies the exact Core SDK tag/package provenance, runs the complete source-mode behavioral solution against that Core commit, and separately runs the focused package-mode compatibility lane against locked remote packages. The focused lane is not described or counted as the complete suite.
2. Runs the six native jobs described above, including exact managed-module evidence and Core metric budgets.
3. Fans in only those exact native leaves and packs all 15 immutable universal archives. CI caps each runtime archive at 256 MiB compressed and 640 MiB indexed payload, and the compressed family at 4 GiB. The deterministic output audit separately requires each six-RID selected-current-RID family installed projection to be no larger than 360,003,313 bytes, a 75 percent reduction from the fixed 1,440,013,254-byte Phase 0 baseline, and preserves the complete package-owned SQLite, Onigwrap, and `System.Management` closure. Universal indexed payload remains diagnostic and is not used as the installed-footprint gate.
4. Validates the exact fan-in bytes with provenance-matched `Sunder.Package.Format`, reruns the complete family install/activate/unload/reload/reinstall lifecycle, and transfers the Runtime-produced Agent UI snapshot into the real App package host.
5. Uploads one Actions artifact containing exactly 17 tested inputs: the 15 archives, `native-test-evidence.json`, and deterministic `package-output-audit.json`. The immutable publication job downloads that artifact by exact name, invokes the owning qualified-native verifier, regenerates the output audit, byte-compares it, and verifies all hashes before any external write.
6. Preserves each package target's logical runtime requirement. Host-native acceptance proves official .NET 10 Runtime installation and Worker launch from verified private roots. Package publication does not consult a Registry runtime catalog or require language-runtime release assets.
7. Creates a GitHub draft whose notes and evidence record the exact Agent commit, Core SDK tag/package/commit, numeric release id, publication workflow/ref, run id, and originating run attempt plus the SHA-256 of every release-evidence asset. It uploads the exact inventory, downloads it again, regenerates the output audit, and byte-compares all 21 primary and evidence assets. After all immutable publication checks pass, the current attempt archives those exact 21 files plus deterministic Actions-only `publication-handoff-provenance.json` as `sunder-agent-publication-evidence-<release-id>-<current-run-attempt>`. Recovery and final verification require the same body-bound release bytes, including `toolchain.txt`, and the 22nd file must reproduce the publisher's canonical SHA-256 over their complete path/hash/size inventory while binding it to the exact current attempt; structural validation alone is insufficient.
8. Publishes the owner first and all dependents in complete dependency order using `initialAvailability=Yanked`, a package-scoped publish token, explicit `X-Sunder-Set-Latest: false` and multipart `setLatest=false`. Before upload and after an ambiguous outcome, only exact owner/content/inventory/initial-current availability plus canonical/projection bytes confirm acceptance. Every production lock hash remains unchanged.
9. Re-downloads and verifies the exact GitHub draft. It does not receive a Registry CLI token, move `latest` or `preview`, or finalize the GitHub Release.

A failure can leave verified immutable Registry versions without promoted Registry tags. Once immutable external publication may have started, the workflow preserves the draft and every asset rather than deleting, replacing, or relabeling them. A later attempt of the same GitHub run can recover only after independently verifying that exact draft and external publication, then emitting a new handoff that binds the successful current attempt to the unchanged provenance from an exact failed origin attempt. Cross-run, future-attempt, successful-origin, source, tag, release-id, provenance-digest, asset-hash, or artifact-attempt mismatches fail closed. The workflow always requires the same draft, Agent commit, Core SDK tag/package/commit, six native runs, exact inventory, and bytes. Immutable publication cannot recover through an already-finalized release.

Recovery skips source/native/fan-in production jobs and downloads the retained
seventeen tested inputs from the existing draft. It does not rebuild accepted
identities or regenerate their immutable evidence. Missing/incomplete/conflicting
draft evidence stops recovery rather than replacing assets.

### Publication Evidence Commit

After all fifteen versions are accepted while Yanked, use a separate clean detached
checkout of the artifact source:

```sh
bash scripts/release/import-agent-publication-evidence.sh \
  /checkouts/agent-evidence /retained/release-assets https://registry.example
```

This invokes Core's existing `ImportSunderContractPublication` operation for every
canonical project, after exact anonymous publication readback. It requires already
restored tooling; it neither selects/binds locks nor creates commits/tags. Review
the resulting owned-baseline changes and record one untagged direct-child
`publicationEvidenceCommit`. Acceptance and promotion verify the direct parent,
unchanged file modes, narrow owned-baseline path allowlist, immutable descriptor/ABI,
exposure, history and frozen release fields, and matching publication identity.
Previously published evidence is immutable. The tagged checkout remains clean and
its lock set remains identical; no evidence-commit build is claimed as artifact
production. This initial V1 allowlist admits baseline publication fields only;
additional receipt-manifest paths require an explicit policy change.
Registry readback also compares each imported publication timestamp using Core's
UTC/fraction normalization (`--published-at-utc` in read-only verification), so a
syntactically valid but fabricated baseline publication time cannot pass promotion.

## Production Acceptance Evidence

Production acceptance has two explicit phases against the exact retained versions and draft bytes. The only trusted producer is the workflow named `Sunder Agent Production Acceptance` at `.github/workflows/sunder-agent-production-acceptance.yml`, invoked from protected `main` by `workflow_dispatch`. Policy scripts come only from that protected-main commit; the immutable release commit is checked out separately as data and never supplies acceptance or promotion policy. Acceptance downloads `sunder-agent-publication-evidence-<release-id>-<publication-run-attempt>` from the explicitly selected publication run, requires the artifact creation time to fall within jobs from that exact successful attempt, and revalidates its workflow, source, tag, numeric draft, metadata, all 21 immutable release files, and the additive handoff linking them to their original provenance attempt. Its protected fan-in persists canonical `agent-family-acceptance.json` bytes as `agent-family-acceptance-<run-id>-<run-attempt>.json` and archives a redundant 90-day Actions artifact named `sunder-agent-production-acceptance-<run-id>-<run-attempt>`. The acceptance run `head_sha` identifies the protected policy commit; the document separately identifies the immutable release commit and successful publication handoff attempt. An equal same-attempt retry reuses exact bytes; a new attempt uses a new asset name and leaves prior complete or partial attempt assets intact.

Dispatch `phase=pre-visibility` with the exact `publication_evidence_commit` while
the family remains Yanked. Its native checks cover retained/native bytes, verified
official-runtime acquisition/launch, contracts, native assets, performance/security,
clean artifact-source reproduction, evidence-only publication changes, and exclusion from ordinary
resolution. The approved coordinator must also establish the remaining final-gate
conditions evaluable while Yanked, including initial-Yanked admission behavior.
The document retains the exact proposed cohort request and a null result. It does
not claim an ordinary production install.

After the protected cohort stage lists the family, dispatch a new
`phase=post-visibility` acceptance attempt with the same evidence commit and the
retained `cohort_request_sha256`. It downloads the actual committed request/result
by numeric release asset and requires six-RID ordinary fresh resolution, clean
installation and the complete production lifecycle before announcement. Driver
receipts use schema 2, kind `https://sunderapp.io/acceptance/agent-native/v2`, and
bind `phase`, `releaseEvidence`, and `cohortRequestSha256` (null before visibility).
Reviewed external drivers/coordinator must implement these phase arguments and
receipt bindings; an older driver or a fixture cannot satisfy them.

The protected promotion handoff separately records the publication run id/attempt, positive numeric release id, acceptance run id/attempt, protected acceptance workflow SHA, and lowercase SHA-256 of the durable JSON. Promotion itself must be dispatched from protected `main`; it checks out promotion policy and immutable release source separately. It independently downloads and verifies the publication Actions artifact, fetches the exact successful acceptance workflow attempt and every paginated job for that attempt, then downloads the uniquely named release asset by numeric asset id and requires its size, GitHub `sha256:` digest, downloaded bytes, document coordinates, and protected-policy SHA to agree. The acceptance Actions artifact is redundant, but the publication artifact is an explicit promotion input.

The dispatch-only producer requires the protected `sunder-agent-production-acceptance` environment, package Registry origin, six native runner labels/profiles, six Agent scenario drivers with reviewed SHA-256 values, and the acceptance coordinator with its reviewed SHA-256. Runner/profile variables are `SUNDER_AGENT_PRODUCTION_ACCEPTANCE_RUNNER_<RID>` and `SUNDER_AGENT_PRODUCTION_ACCEPTANCE_PROFILE_<RID>` (uppercase RID with `_`). Each external executable runs from a hash-matching non-writable snapshot, receives its digest and binds that digest into its output. Publication credentials are withheld from scenario execution. Official source configuration belongs to Host; this workflow accepts no vendor source override, runtime lock, signer, attestation or runtime-publication prerequisite.

Each native lane re-verifies the exact Agent tag and Core SDK provenance and records `runtimeInstallation` using Host's current `InstalledExecutionRuntimeSummary` DTO. Identity includes official source, actual vendor patch version, RID, module ABI and vendor checksum (SHA-512 for .NET). `installedArtifact` binds the original archive's SHA-256/size, private content identity and Host download provenance. Required checks include `official-runtime-install-launch`, `official-runtime-source-checksum` and `ambient-runtime-isolation`; no signature or runtime authorization receipt is fabricated. Docker and credentialed Web Search coverage may use deterministic fakes only when at least one accepted native receipt proves each prerequisite with a real runner. Fan-in requires all six phase-specific receipts, exact package/source/cohort bindings and the existing schema-4 acceptance document. Numeric asset identity, hashes, immutable readback and successful trusted run/attempt gates retain their existing semantics.

Drivers obtain that DTO from the released Host's management API
(`GetExecutionRuntimesAsync` / `GetExecutionRuntimeAsync`) after acquisition and
verification, retaining health, support and provenance. Match the exact official
identity to each package target requirement, installed binding and private-root
Worker launch. A raw download-cache entry or private on-disk receipt is supplementary
audit evidence, not a substitute management snapshot. Preserve the actual vendor
patch selected by Host; sample versions and requirement floors are not observations.

The evidence document is schema version 4 and records:

- `decision: "accepted"`, the repository, exact `agent/v*` tag, version, full release-source commit, publication run id/attempt and fixed workflow/artifact identity, acceptance run id/attempt, attempt-specific Actions artifact name, protected-main acceptance workflow ref/SHA, coordinator SHA-256, and UTC acceptance time;
- `draftRelease.id`, `draft: true`, exact title, exact full body bytes and their SHA-256, the SHA-256 of `release-provenance.json`, and the exact numeric id/name/SHA-256/size tuple for all 21 draft assets;
- exactly six native receipt entries containing RID, receipt SHA-256, and external driver SHA-256;
- exactly 15 Registry entries containing package id, exact version, and accepted canonical `.sunderpkg` SHA-256.
- `phase`, the distinct artifact/evidence commits and aggregate lock-set digest in
  `releaseEvidence`, and `cohort` containing the existing Registry request/result
  DTOs. A pre-visibility result is null; a post-visibility result must reproduce the
  retained committed request, exact identities, policy/checkpoint and complete tag maps.

The exact document shape is:

```json
{
  "schemaVersion": 4,
  "phase": "pre-visibility",
  "releaseEvidence": {
    "artifactSourceCommit": "<the tagged source commit>",
    "publicationEvidenceCommit": "<its evidence-only direct child>",
    "lockSetSha256": "<canonical production lock-set SHA-256>"
  },
  "cohort": {
    "request": {
      "idempotencyKey": "<retained reviewed request key>",
      "expectedPackagePolicyRevision": 1,
      "versions": [],
      "distTags": []
    },
    "result": null
  },
  "decision": "accepted",
  "repository": "Younics/sunder-agent-package",
  "tag": "agent/v2.0.0",
  "version": "2.0.0",
  "sourceCommit": "<40 lowercase hex characters>",
  "publicationRunId": "<publication workflow run id>",
  "publicationRunAttempt": "<positive publication run attempt>",
  "publicationWorkflow": {
    "name": "Sunder Agent Family Release",
    "path": ".github/workflows/sunder-package-release.yml",
    "event": "push",
    "artifactName": "sunder-agent-publication-evidence-<release-id>-<publication-run-attempt>"
  },
  "acceptanceRunId": "<workflow run id>",
  "acceptanceRunAttempt": "<positive run attempt>",
  "acceptanceWorkflow": {
    "name": "Sunder Agent Production Acceptance",
    "path": ".github/workflows/sunder-agent-production-acceptance.yml",
    "event": "workflow_dispatch",
    "artifactName": "sunder-agent-production-acceptance-<run-id>-<run-attempt>",
    "coordinatorSha256": "<64 lowercase hex characters>",
    "workflowRef": "refs/heads/main",
    "workflowSha": "<40 lowercase hex characters>"
  },
  "acceptedAtUtc": "2026-08-24T12:00:00Z",
  "draftRelease": {
    "id": 123456789,
    "draft": true,
    "title": "Sunder Agent Family 2.0.0",
    "body": "<exact full GitHub release body>",
    "bodySha256": "<SHA-256 of the exact UTF-8 body bytes>",
    "provenanceSha256": "<64 lowercase hex characters>",
    "assets": [
      { "id": 987654321, "name": "<exact asset name>", "sha256": "<64 lowercase hex characters>", "size": 1 }
    ]
  },
  "nativeReceipts": [
    {
      "rid": "win-x64",
      "sha256": "<64 lowercase hex characters>",
      "driverSha256": "<64 lowercase hex characters>"
    }
  ],
  "registryPackages": [
    { "packageId": "sunder.package.agent", "version": "2.0.0", "canonicalSha256": "<64 lowercase hex characters>" }
  ]
}
```

The illustrated arrays are abbreviated; production evidence must contain all 21 unique publication assets, all six native receipts, all 15 unique Registry packages, and the exact fifteen-version/tag cohort, with no additional top-level or nested fields. Its `draftRelease.assets` snapshot excludes acceptance and cohort-result documents. These are additional immutable supporting release assets; prior attempt assets remain and are never replaced. Cohort versions and changes use Registry's ordinal `packageId/version` and `packageId/tag` order, while complete snapshots sort by package ID. Upload dependency order is a separate ordering.

The detached protected-dispatch selector is:

```json
{
  "release_id": "<positive numeric release id>",
  "publication_run_id": "<positive publication run id>",
  "publication_run_attempt": "<positive publication attempt>",
  "acceptance_run_id": "<positive run id>",
  "acceptance_run_attempt": "<positive attempt>",
  "acceptance_workflow_sha": "<40 lowercase hex characters>",
  "acceptance_evidence_sha256": "<64 lowercase hex characters>"
}
```

The evidence is a phase-specific acceptance result, not something promotion infers or generates. Archive it only after that phase's required checks complete. Record the lowercase SHA-256 separately for the protected promotion dispatch. Only a post-visibility result can authorize announcement.

## Promotion

Dispatch `Sunder Agent Family Promotion` from protected `main` with the exact `tag`, full `source_sha`, numeric `release_id`, successful `publication_run_id` and `publication_run_attempt`, successful `acceptance_run_id` and `acceptance_run_attempt`, protected-main `acceptance_workflow_sha`, and accepted `acceptance_evidence_sha256`. The one promotion job is protected by `sunder-agent-release-promotion` and all Agent family promotions share one global concurrency group.

Before the first mutation, promotion fails closed unless all of these agree:

- the exact `refs/tags/<tag>` Git ref peels to the accepted source, and the protected-main policy scripts, separately checked-out release-source inventory, positive numeric draft release database id, `draft: true`, exact title, exact full body and body hash, prerelease flag, release notes, and provenance identify the accepted tag, version, Agent commit, Core commit, and Core SDK release;
- the GitHub release has exactly the expected 15 `.sunderpkg` archives, native evidence, deterministic package-output audit, and four release-evidence files, plus one or more well-formed immutable attempt-specific acceptance assets; the selected acceptance asset and every publication byte must match acceptance, provenance, native fan-in evidence, `SHA256SUMS`, and the SBOM, and promotion independently regenerates and byte-compares the package-output audit before mutation;
- the publication artifact belongs to the exact successful tag-push run attempt, matches all 21 draft assets byte-for-byte, and contains a deterministic handoff binding their original provenance attempt and SHA-256/size inventory to that successful attempt; when those attempts differ, the origin must be an exact failed attempt of the same run, while the durable acceptance asset has one exact numeric id/name/size/GitHub digest, its downloaded JSON matches the separately supplied SHA-256, and its fixed workflow path/name/ref/SHA plus exact successful protected-main `workflow_dispatch` run/attempt match the selected completed job set;
- all 15 immutable Registry versions exist and their downloaded canonical archives and every projection match the accepted canonical archives.

For pre-visibility acceptance, `agent_cohort.py` snapshots the complete public tag
maps and denies a forward downgrade. A token-free just-in-time guard rechecks the
accepted attempt, source/evidence commits, tag and exact numeric draft metadata and
bytes. It submits one retained `PackageVersionCohortAvailabilityTransitionV1` to
`POST /api/v1/me/package-cohorts/availability`: all fifteen exact identities change
from Yanked to Listed with all fifteen release tags. The expected policy revision
fences concurrent changes. Request hashing uses Registry's declared DTO order and
`sunder.package-cohort-request.v1\0` domain; an isolated probe against the compiled
Core DTO fixes the interop test vector, including SemVer `+` escaping.

The actual result must match the key/request hash, exact versions, next policy
revision, checkpoint, commit time and complete previous/resulting tag snapshots.
A lost response retries the identical body/key/revision. Recovery-required pauses
for authenticated administrator replay; it never selects a per-package fallback.
The result is stored as immutable `agent-family-cohort-<request-sha256>.json`, binding
the pre-visibility acceptance digest and actual Registry DTOs. Upload ambiguity is
resolved by exact numeric-asset readback. This stage exits with the draft unchanged.

For post-visibility acceptance, promotion checks the already Listed family and
authenticates the retained result with the owner route. First a fresh anonymous
policy synchronization binds every exact package/platform catalog and proves a
policy/checkpoint floor newer than the original request's expected revision. The
same owner POST can therefore only return its retained idempotent result or fail
stale; it cannot create a new visibility transition. The retained result must equal
the accepted document. The superseded standalone `promote-family-dist-tag.sh`
helper has been removed; its CLI is not compatible with the approved atomic cohort
contract. The [retirement mapping](../scripts/README.md#sequential-dist-tag-publisher-retirement)
records retained concurrency, downgrade, credential and recovery checks.

After post-visibility acceptance and retained-result confirmation, promotion re-fetches the handoff, tag, and accepted numeric draft, re-downloads all 21 publication assets plus the selected durable acceptance document, validates metadata for retained attempt assets, revalidates all 15 immutable Registry identities, and verifies the full promoted family. Because those are long operations, it then performs one final just-in-time sequence: handoff first, followed by a fresh exact tag ref and numeric release fetch requiring `draft: true`, title, original body, and all accepted asset metadata, followed by another read-only verification of the complete resulting Registry tag map and availability. That family verification is the last remote operation before the numeric release PATCH. A replacement draft or a family member changed in the former gap cannot be finalized.

The numeric PATCH atomically sets `draft: false` and appends one deterministic `sunder-agent-promotion-receipt:v2` JSON footer to the accepted body. The canonical receipt binds the numeric release id, tag, source commit, version, selected dist tag, original accepted body SHA-256, exact publication workflow/artifact/run coordinates, protected acceptance workflow ref/SHA and durable handoff coordinates, and protected promotion policy path/ref/SHA. It contains no timestamp or runner-local value, so the same accepted handoff and policy commit always produce the same exact finalized body. Post-finalization verification requires that exact body and receipt while preserving the accepted title and unchanged 21 publication assets, requiring the selected acceptance bytes to match, and rejecting malformed or unhashed retained acceptance-attempt assets.

Promotion never restores, builds, packs, signs, or publishes a Registry version. A failure while the accepted release remains a draft can be retried against the same immutable bytes. If a runner is lost after GitHub applies the atomic PATCH, a retry accepts `draft: false` only when the finalized body contains the exact deterministic receipt, all 21 publication assets remain unchanged, every retained acceptance-attempt asset has valid uploaded hash metadata, the selected acceptance bytes still match, all 15 immutable Registry identities still match, and the complete Registry family is promoted. It repeats the handoff, tag, numeric release metadata/receipt/assets, and promoted-family checks immediately before reporting recovered success. A missing, altered, or forged receipt, changed selected asset, or partial family fails closed.

### Reviewed Non-Security Reverse

If visibility-dependent acceptance fails, retain the unannounced draft and forward
receipt. `Sunder Agent Cohort Rollback` is a separate protected-main dispatch under
the same promotion environment and concurrency group. Supply its exact numeric
release/tag/source, forward request digest, a **new** reviewed idempotency key and
expected current package-policy revision, and `reverse-unannounced-cohort`.
It derives one Listed-to-Yanked request restoring the captured previous tag map,
including deletion of newly introduced tags. Source/tag/draft/forward receipt and
current complete tag maps are rechecked before POST. Retain the same reverse key
and revision on an ambiguous retry. A successful result must restore the exact
old map; sequential yanks/compensation are never attempted. Real security decisions
use the separate terminal block/revocation process.

## Repository Configuration

Release and promotion automation require:

- The reviewed [cold release-input contract](cold-release-inputs.md):
  `SUNDER_AGENT_COLD_INPUT_MANIFEST` selects an independently hash-pinned numeric
  GitHub asset containing exact candidate/catalog identities. Source, native and
  fan-in CI allocate isolated homes, prove the missing-cache diagnostic, hydrate
  through Core without modifying locks, and build in a verified offline process tree.
  Configure the documented hash-pinned offline-runner adapter where OS-native
  isolation is unavailable. All four workflows explicitly require Python 3.9+.

- Optional repository variables `SUNDER_AGENT_RUNNER_WIN_X64`, `SUNDER_AGENT_RUNNER_WIN_ARM64`, `SUNDER_AGENT_RUNNER_LINUX_X64`, `SUNDER_AGENT_RUNNER_LINUX_ARM64`, `SUNDER_AGENT_RUNNER_OSX_X64`, and `SUNDER_AGENT_RUNNER_OSX_ARM64`. Each value is a JSON array of hosted, larger-runner, or self-hosted labels. Defaults use `windows-latest`, `windows-11-arm`, `ubuntu-24.04`, `ubuntu-24.04-arm`, `macos-15-intel`, and `macos-14` respectively.
- Repository variable `SUNDER_REGISTRY_API_URL` containing the package Registry's absolute HTTPS origin without a path. Official language-runtime acquisition uses Host policy and does not consume this origin or Registry credentials.
- Protected `sunder-agent-production-acceptance` configuration containing the coordinator path/SHA-256, each RID-specific Agent scenario driver path/SHA-256 and matching native runner/profile. Host installer/launcher qualification supplies official vendor/source/version/RID/checksum evidence; it needs no Sunder runtime-publication key or approval.
- Repository secret `SUNDER_REGISTRY_PUBLISH_TOKEN` containing a `sunder_pub_v1` token with an exact `package:publish` grant for every family package. Only immutable tag publication receives it; publication always sends the expected package id and cannot move a dist tag.
- Protected GitHub environment `sunder-agent-release-promotion`, with required reviewers and `SUNDER_AGENT_ARTIFACT_SOURCE_COMMIT`, `SUNDER_AGENT_LOCK_SET_SHA256`, `SUNDER_AGENT_RELEASE_APPROVAL_REFERENCE`, and `SUNDER_AGENT_RELEASE_OWNER_PRINCIPAL_ID`. Preparation and immutable publication both require this approved exact source/lock binding.
- The `SUNDER_REGISTRY_CLI_TOKEN` environment secret belongs to the immutable owner of every cohort package. The manual promotion/rollback jobs use it for the retained atomic transition, idempotent confirmation, or separately reviewed reverse; token-free guards and anonymous downloads never receive it.

Never commit token, password, or API-key values.
