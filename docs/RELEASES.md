# Agent Family Releases

Agent releases are coordinated family releases. One `agent/vX.Y.Z` tag builds and publishes `Sunder.Package.Agent.Protocol` plus every `sunderpkg` entry in `packages.json` from one Agent commit, one Core commit, and one version.

## Core Coordination

Agent release CI resolves the immutable `sdk/v<CORE_SDK_VERSION>` release, verifies that the tag, Core release provenance, GitHub release assets, NuGet canonical content, NuGet repository signatures, package repository metadata, and every Agent release lock identify one Core commit and package version, and passes that full commit SHA to every Core test checkout. The Core tag, commit, and package version are recorded in `native-test-evidence.json`, the draft release notes, and `release-provenance.json`. Recovery accepts only those same materials.

Cross-repository changes require this order:

1. Land the compatible Core changes and publish the matching immutable Core `sdk/v*` release.
2. Publish the Core developer packages required by `Directory.Packages.props` and regenerate every Agent release lock against that exact remote package version.
3. Commit the coordinated Agent changes.
4. Confirm Agent smoke CI passes and the release workflow will resolve the expected Core SDK tag SHA.
5. Set `packages.json` `releaseVersion`, then create and push `agent/v<releaseVersion>` from the Agent release commit.

Do not create an Agent release tag until the matching `Sunder.Sdk`, `Sunder.Sdk.Avalonia`, `Sunder.Sdk.Stacks`, `Sunder.Sdk.Worker`, and `Sunder.Package.Build` packages and their public Core SDK release are final. Release CI runs the complete behavioral solution once against the exact provenance-matched Core source, then rebuilds the focused package-compatibility lane and all production bytes with `SunderUseSourceSdk=false`, locked mode, and NuGet.org. Agent production bytes are never built from the Core source checkout; that checkout also supplies the exact Runtime Host, Package Format, App, and Core metric gates.

During coordinated compatibility preparation, release locks can be refreshed before publication from an exact local Core candidate feed with `scripts/release/refresh-release-locks.sh <version> <feed>`. The script restores with an isolated package and artifacts root, then verifies every referenced Core package lock hash against the candidate `.nupkg` bytes. Local candidate locks are preparation evidence only and never replace the immutable Core release checks required by production CI.

## Native Build Boundary

The release has one native job for each supported RID: `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64`, and `osx-arm64`. Each job must run on that exact native architecture, builds exactly the 15 Worker leaves, and creates current-RID smoke archives without cross-compiling another release RID. Each smoke archive is packed from the canonical project with `SunderPackageUsePrebuiltLeaves=true`, `BuildProjectReferences=false`, and `SunderPackageComposeRuntimeIdentifiers=<rid>`, so packing cannot rebuild the signed leaf.

Windows apphosts are Authenticode-signed with Azure Trusted Signing. macOS apphosts are timestamped Developer ID binaries. Signing runs through Core's `SunderPackageWorkerSigningTargets` pre-index hook over its private publish-closure copy; the job then independently verifies the platform signature and signer identity. Linux apphosts remain unsigned. The native job launches all 15 resulting workers through a real Runtime Host lifecycle and records Core-enforced archive size, current-RID installed size, startup latency, process-count, and steady-RSS evidence for each worker. The same gate exercises owner unary and server-stream RPC, MCP callback dispatch, OpenAI host auth, both Agent SQLite baselines, forced worker crash recovery, disable/enable retirement, process cleanup, and temporary-directory cleanup.

Each job uploads an opaque tarball so Unix executable modes and signed bytes survive Actions artifact transfer. Fan-in verifies every file hash and metric record, extracts all six sets, builds only App leaves and Protocol against locked remote Core packages, and invokes `PackSunderPackage` on each canonical Worker with `SunderPackageUsePrebuiltLeaves=true` and `BuildProjectReferences=false`. No one-RID override is supplied, so all six signed leaves are composed without rebuilding any Worker or App. CI additionally compares every final RID apphost hash with its native signed-leaf hash.

## Stable Boundary

Before a stable tag, finalize the Protocol API ledger by moving accepted API entries into `PublicAPI.Shipped.txt`. `PublicAPI.Unshipped.txt` may retain `#nullable enable` and comments, but no API entries. The release prepare job enforces this condition.

The generated Agent compatibility candidate and its public-release evidence live
under [`compatibility/`](../compatibility/README.md). Regeneration is an explicit
review step; normal tests reject an uncommitted candidate delta.

The Agent 2.x line uses `[2.0.0,3.0.0)` for `Sunder.Package.Agent.Protocol` and the fixed `>=2.0.0 <3.0.0` family contract for runtime dependencies. The coordinated `releaseVersion` must itself satisfy that runtime range under the Sunder SDK's strict SemVer precedence rules: stable `2.0.0` and later satisfying 2.x patch or prerelease versions are valid, while `2.0.0-preview.*` is below the lower bound. The dependency range remains fixed rather than being derived from `releaseVersion`. The Protocol package keeps its Sunder SDK dependency on the SDK's independent supported minor line.

## Publication Order

Immutable publication is triggered only by the exact `agent/v<releaseVersion>` tag. That workflow:

1. Verifies the exact Core SDK tag/package provenance, runs the complete source-mode behavioral solution against that Core commit, and separately runs the focused package-mode compatibility lane against locked remote packages. The focused lane is not described or counted as the complete suite.
2. Runs the six native jobs described above, including required platform signing and Core metric budgets.
3. Fans in only those exact native leaves and packs one Protocol NuGet package plus all 15 immutable universal archives. CI caps Protocol at 16 MiB, each runtime archive at 256 MiB compressed and 640 MiB indexed payload, and the compressed family at 4 GiB; it also requires the complete six-RID SQLite native matrix.
4. Validates the exact fan-in bytes with provenance-matched `Sunder.Package.Format`, reruns the complete family install/activate/unload/reload/reinstall lifecycle, and transfers the Runtime-produced Agent UI snapshot into the real App package host.
5. Uploads one Actions artifact containing exactly the tested 16 publishable artifacts and `native-test-evidence.json`; the immutable publication job downloads that artifact by exact name and verifies all hashes before any external write.
6. Creates a GitHub draft whose notes and evidence record the exact Agent commit and Core SDK tag/package/commit plus the SHA-256 of every release-evidence asset, uploads the exact asset inventory, downloads it again, and byte-compares all 21 primary and evidence assets. Recovery and final verification require those same body-bound evidence bytes, including `toolchain.txt`; structural validation alone is insufficient.
7. Publishes Protocol to NuGet.org and verifies canonical NuGet content plus the repository signature.
8. Publishes every Registry version with a package-scoped publish token and explicit `X-Sunder-Set-Latest: false` plus multipart `setLatest=false`, then refetches and verifies every canonical archive, projection inventory, projection identity, and payload hash.
9. Re-downloads and verifies the exact GitHub draft. It does not receive a Registry CLI token, move `latest` or `preview`, or finalize the GitHub Release.

A failure can leave verified immutable NuGet or Registry versions without promoted Registry tags. Once immutable external publication may have started, the workflow preserves the draft and its evidence rather than deleting or replacing it. Rerun the same tag to recover publication: the workflow requires the same draft, Agent commit, Core SDK tag/package/commit, six native runs, exact inventory, and bytes. A mismatch stops recovery without overwriting evidence. Immutable publication cannot recover through an already-finalized release.

## Production Acceptance Evidence

Production acceptance runs after immutable publication against the exact public versions and draft bytes. The only trusted producer is the workflow named `Sunder Agent Production Acceptance` at `.github/workflows/sunder-agent-production-acceptance.yml`, invoked by `workflow_dispatch`. The acceptance system must archive one Actions artifact named `sunder-agent-production-acceptance` containing exactly one file, `agent-family-acceptance.json`. The completed successful workflow run, exact run attempt, repository, and `head_sha` must identify the release source commit.

The protected promotion handoff separately records the exact positive GitHub artifact id, GitHub-reported `sha256:<lowercase digest>`, and lowercase SHA-256 of the extracted JSON. These values cannot be authored inside the selected artifact as an authoritative self-binding because the artifact id is assigned and its archive digest is calculated after upload. Promotion therefore treats the dispatch values plus GitHub's artifact and attempt APIs as the authority, not JSON self-report: it fetches the exact attempt, exact artifact id, platform digest, repository/run/source relation, and every paginated job for that attempt, then requires the artifact creation time to fall within those attempt jobs. `actions/download-artifact` downloads by `artifact-ids`, not by name.

That production-acceptance workflow is not implemented in this repository because its production Registry, native lifecycle, browser, and cleanup infrastructure remains externally blocked. Promotion therefore fails closed on the designated path and name; evidence from another successful workflow, event, run attempt, repository, or artifact is never accepted as a substitute.

The evidence document is schema version 2 and records:

- `decision: "accepted"`, the repository, exact `agent/v*` tag, version, full source commit, acceptance run id and attempt, fixed workflow identity/event/artifact, and UTC acceptance time;
- `draftRelease.id`, `draft: true`, exact title, exact full body bytes and their SHA-256, the SHA-256 of `release-provenance.json`, and the exact numeric id/name/SHA-256/size tuple for all 21 draft assets;
- exactly 15 Registry entries containing package id, exact version, and accepted canonical `.sunderpkg` SHA-256.

The exact document shape is:

```json
{
  "schemaVersion": 2,
  "decision": "accepted",
  "repository": "Younics/sunder-agent-package",
  "tag": "agent/v2.0.0",
  "version": "2.0.0",
  "sourceCommit": "<40 lowercase hex characters>",
  "acceptanceRunId": "<workflow run id>",
  "acceptanceRunAttempt": "<positive run attempt>",
  "acceptanceWorkflow": {
    "name": "Sunder Agent Production Acceptance",
    "path": ".github/workflows/sunder-agent-production-acceptance.yml",
    "event": "workflow_dispatch",
    "artifactName": "sunder-agent-production-acceptance"
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
  "registryPackages": [
    { "packageId": "sunder.package.agent", "version": "2.0.0", "canonicalSha256": "<64 lowercase hex characters>" }
  ]
}
```

The illustrated arrays are abbreviated; production evidence must contain all 21 unique assets and all 15 unique Registry packages, with no additional top-level or nested fields.

The detached protected-dispatch selector is:

```json
{
  "acceptance_run_id": "<positive run id>",
  "acceptance_run_attempt": "<positive attempt>",
  "acceptance_artifact_id": "<positive GitHub artifact id>",
  "acceptance_artifact_digest": "sha256:<64 lowercase hex characters>",
  "acceptance_evidence_sha256": "<64 lowercase hex characters>"
}
```

The evidence is an acceptance result, not something promotion infers or generates. Archive it only after the required production-path resolve/download/install/activation, native lifecycle, clean-state, restart, update, uninstall, browser, and cleanup checks complete. Record the lowercase SHA-256 of the JSON separately for the protected promotion dispatch.

## Promotion

Dispatch `Sunder Agent Family Promotion` with the exact `tag`, full `source_sha`, successful `acceptance_run_id`, exact `acceptance_run_attempt`, numeric `acceptance_artifact_id`, GitHub `acceptance_artifact_digest`, and accepted `acceptance_evidence_sha256`. The one promotion job is protected by `sunder-agent-release-promotion` and all Agent family promotions share one global concurrency group.

Before the first mutation, promotion fails closed unless all of these agree:

- the exact `refs/tags/<tag>` Git ref peels to the accepted source, and the checked-out scripts/inventory, positive numeric draft release database id, `draft: true`, exact title, exact full body and body hash, prerelease flag, release notes, and provenance identify the accepted tag, version, Agent commit, Core commit, and Core SDK release;
- the GitHub release has exactly the expected 15 `.sunderpkg` archives, Protocol package, native evidence, and four release-evidence files, with every downloaded byte matching archived acceptance, provenance, native fan-in evidence, `SHA256SUMS`, and the SBOM;
- the archived acceptance artifact has the exact selected numeric id and GitHub digest, came from the fixed workflow path/name and exact `workflow_dispatch` run/repository/source/attempt, was created during jobs returned by that attempt-specific API, completed successfully, has the exact artifact name, and its extracted evidence bytes match the separately supplied SHA-256;
- all 15 immutable Registry versions exist and their downloaded canonical archives and every projection match the accepted canonical archives.

After those potentially long checks, `promote-family-dist-tag.sh` snapshots and validates all 15 current dist-tag targets. It then invokes a token-free just-in-time guard that again fetches the exact attempt/artifact binding, exact tag ref, and accepted numeric release, requiring the same draft state, title, full body, and exact 21 asset ids/names/sizes/GitHub digests. After that potentially long guard returns, the promoter discards the entire earlier Registry snapshot, fetches all 15 members again before evaluating any member, and reruns monotonicity and idempotence classification. The first applicable PUT follows that refreshed classification, and compensation is bound to the refreshed snapshot rather than state observed before the guard. A newer target installed while the guard runs therefore stops promotion without a downgrade PUT. The family operation is a globally serialized sequence of 15 Registry mutations, not an atomic Registry transaction. On an observed failure, guarded compensation restores only tags that still equal this attempt's target; it refuses to overwrite a concurrent replacement. A hard runner loss can prevent shell traps from compensating and leave a partial family. Rerunning the same accepted draft promotion converges old and already-current members to the target, while monotonic checks fail closed if any package moved to a newer version.

After the family reports the exact target, promotion re-fetches the handoff, tag, and accepted numeric draft, re-downloads all 21 assets, revalidates all 15 immutable Registry identities, and verifies the full promoted family. Because those are long operations, it then performs one final just-in-time sequence: handoff first, followed by a fresh exact tag ref and numeric release fetch requiring `draft: true`, title, original body, and all accepted asset metadata, followed by another read-only verification of the complete promoted Registry family. That family verification is the last remote operation before the numeric release PATCH, so acceptance-artifact pagination cannot create an unchecked family-drift gap. A replacement draft or a family member changed in the former gap cannot be finalized.

The numeric PATCH atomically sets `draft: false` and appends one deterministic `sunder-agent-promotion-receipt:v1` JSON footer to the accepted body. The canonical receipt binds the numeric release id, tag, source commit, version, selected dist tag, original accepted body SHA-256, fixed acceptance workflow repository/name/path/event/artifact name, run id, run attempt, artifact id, GitHub artifact digest, and detached evidence SHA-256. It contains no timestamp or runner-local value, so the same accepted handoff always produces the same exact finalized body. Post-finalization verification requires that exact body and receipt while preserving the accepted title and unchanged 21-asset metadata and bytes.

Promotion never restores, builds, packs, signs, publishes NuGet, or publishes a Registry version. A failure while the accepted release remains a draft can be retried against the same immutable bytes. If a runner is lost after GitHub applies the atomic PATCH, a retry accepts `draft: false` only when the finalized body contains the exact deterministic receipt, all 21 accepted assets still match byte-for-byte, all 15 immutable Registry identities still match, and the complete Registry family is promoted. It repeats the handoff, tag, numeric release metadata/receipt/assets, and promoted-family checks immediately before reporting recovered success. A missing, altered, or forged receipt, changed asset, or partial family fails closed.

## Repository Configuration

Release and promotion automation require:

- GitHub environment `sunder-agent-release-signing`, protected as appropriate for production signing.
- Optional repository variables `SUNDER_AGENT_RUNNER_WIN_X64`, `SUNDER_AGENT_RUNNER_WIN_ARM64`, `SUNDER_AGENT_RUNNER_LINUX_X64`, `SUNDER_AGENT_RUNNER_LINUX_ARM64`, `SUNDER_AGENT_RUNNER_OSX_X64`, and `SUNDER_AGENT_RUNNER_OSX_ARM64`. Each value is a JSON array of hosted, larger-runner, or self-hosted labels. Defaults use `windows-latest`, `windows-11-arm`, `ubuntu-24.04`, `ubuntu-24.04-arm`, `macos-15-intel`, and `macos-14` respectively.
- Azure OIDC values `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, and `AZURE_SUBSCRIPTION_ID`; Trusted Signing values `AZURE_TRUSTED_SIGNING_ENDPOINT`, `AZURE_TRUSTED_SIGNING_ACCOUNT_NAME`, and `AZURE_TRUSTED_SIGNING_CERTIFICATE_PROFILE_NAME`; and required verification pin `WINDOWS_PUBLISHER_SUBJECT`.
- Apple secrets `APPLE_BUILD_CERTIFICATE_BASE64`, `APPLE_CERTIFICATE_PASSWORD`, `APPLE_SIGNING_KEYCHAIN_PASSWORD`, `APPLE_SIGN_APP_IDENTITY`, and `APPLE_TEAM_ID`.
- Repository variable `SUNDER_REGISTRY_API_URL` containing the Registry's absolute HTTPS origin without a path.
- Repository secret `SUNDER_REGISTRY_PUBLISH_TOKEN` containing a `sunder_pub_v1` token with an exact `package:publish` grant for every family package. Only immutable tag publication receives it; publication always sends the expected package id and cannot move a dist tag.
- Protected GitHub environment `sunder-agent-release-promotion`, with required reviewers and the `SUNDER_REGISTRY_CLI_TOKEN` environment secret. The interactive `sunder_cli` user must manage every family package. Only the manual promotion job receives this credential, and it uses it solely for coordinated dist-tag promotion and guarded compensation.
- Repository secret `NUGET_API_KEY` authorized to publish `Sunder.Package.Agent.Protocol`.

Never commit signing material, token, password, or API-key values.
