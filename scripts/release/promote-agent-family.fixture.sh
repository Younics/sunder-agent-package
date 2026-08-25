#!/usr/bin/env bash
set -euo pipefail
export LC_ALL=C

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repository_root="$(cd "$script_dir/../.." && pwd)"
orchestrator="$script_dir/promote-agent-family.sh"
acceptance_verifier="$script_dir/verify-acceptance-artifact.sh"
[[ -f "$orchestrator" ]] || { printf 'Agent family promotion orchestrator not found.\n' >&2; exit 1; }
[[ -f "$acceptance_verifier" ]] || { printf 'Acceptance artifact verifier not found.\n' >&2; exit 1; }

for required_command in cp cut grep jq mktemp mv sort tr wc; do
  command -v "$required_command" >/dev/null 2>&1 \
    || { printf '%s is required to run the Agent promotion fixture.\n' "$required_command" >&2; exit 127; }
done
if ! command -v sha256sum >/dev/null 2>&1 && ! command -v shasum >/dev/null 2>&1; then
  printf 'sha256sum or shasum is required to run the Agent promotion fixture.\n' >&2
  exit 127
fi

hash_file() {
  if command -v sha256sum >/dev/null 2>&1; then
    sha256sum "$1" | cut -d ' ' -f 1
  else
    shasum -a 256 "$1" | cut -d ' ' -f 1
  fi
}

hash_stream() {
  if command -v sha256sum >/dev/null 2>&1; then
    sha256sum | cut -d ' ' -f 1
  else
    shasum -a 256 | cut -d ' ' -f 1
  fi
}

file_size() {
  wc -c < "$1" | tr -d '[:space:]'
}

temp_root="${TMPDIR:-/tmp}"
fixture_root="$(mktemp -d "${temp_root%/}/sunder-agent-family-promotion.XXXXXX")"
trap 'rm -rf "$fixture_root"' EXIT
fixture_repository="$fixture_root/repository"
mock_bin="$fixture_root/mock-bin"
asset_source="$fixture_root/release-assets"
mkdir -p "$fixture_repository/scripts/release" "$mock_bin" "$asset_source"
cp "$orchestrator" "$fixture_repository/scripts/release/promote-agent-family.sh"
cp "$acceptance_verifier" "$fixture_repository/scripts/release/verify-acceptance-artifact.sh"
cp "$repository_root/packages.json" "$fixture_repository/packages.json"

export MOCK_ACCEPTANCE_ARTIFACT=sunder-agent-production-acceptance
export MOCK_ACCEPTANCE_ARTIFACT_DIGEST=sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
export MOCK_ACCEPTANCE_ARTIFACT_ID=7001
export MOCK_ACCEPTANCE_ATTEMPT=2
export MOCK_ACCEPTANCE_RUN_ID=9001
export MOCK_ACCEPTANCE_WORKFLOW='Sunder Agent Production Acceptance'
export MOCK_ACCEPTANCE_WORKFLOW_PATH=.github/workflows/sunder-agent-production-acceptance.yml
export MOCK_DRAFT_STATE="$fixture_root/draft-state"
export MOCK_EVENT_LOG="$fixture_root/events.log"
export MOCK_EXPECTED_CLI_TOKEN=sunder_cli_0123456789012345678901234567890123456789012
export MOCK_GH_REPO=Younics/sunder-agent-package
export MOCK_PROMOTED_STATE="$fixture_root/promoted-state"
export MOCK_RELEASE_CALL_COUNT="$fixture_root/release-call-count"
export MOCK_RELEASE_ASSETS="$asset_source"
export MOCK_RELEASE_ID=424242
export MOCK_SOURCE_SHA=1111111111111111111111111111111111111111
export MOCK_TAG=agent/v2.0.0
export MOCK_TAG_CALL_COUNT="$fixture_root/tag-call-count"
export MOCK_VERSION=2.0.0
core_sha=2222222222222222222222222222222222222222

package_ids=()
while IFS= read -r package_id; do package_ids+=("$package_id"); done \
  < <(jq -r '.packages | map(select(.artifactType == "sunderpkg")) | sort_by(.packageId)[] | .packageId' "$fixture_repository/packages.json")
[[ "${#package_ids[@]}" -eq 15 ]]
runtime_names=()
for package_id in "${package_ids[@]}"; do
  name="$package_id.$MOCK_VERSION.sunderpkg"
  runtime_names+=("$name")
  printf 'canonical fixture archive for %s\n' "$package_id" > "$asset_source/$name"
done
protocol_name="Sunder.Package.Agent.Protocol.$MOCK_VERSION.nupkg"
printf 'canonical fixture protocol\n' > "$asset_source/$protocol_name"

final_artifacts="$fixture_root/final-artifacts.json"
printf '[]\n' > "$final_artifacts"
for name in "${runtime_names[@]}" "$protocol_name"; do
  jq \
    --arg path "$name" \
    --arg sha "$(hash_file "$asset_source/$name")" \
    --argjson size "$(file_size "$asset_source/$name")" \
    '. + [{path: $path, sha256: $sha, size: $size}]' \
    "$final_artifacts" > "$final_artifacts.next"
  mv "$final_artifacts.next" "$final_artifacts"
done
runtime_package_evidence="$(printf '%s\n' "${package_ids[@]}" | jq -Rsc 'split("\n")[:-1] | map({packageId: .})')"
jq -n \
  --arg agent "$MOCK_SOURCE_SHA" \
  --arg core_commit "$core_sha" \
  --arg version "$MOCK_VERSION" \
  --argjson final_artifacts "$(< "$final_artifacts")" \
  --argjson packages "$runtime_package_evidence" '
    {
      schemaVersion: 1,
      version: $version,
      agentCommit: $agent,
      core: {tag: "sdk/v1.1.0", commit: $core_commit, packageVersion: "1.1.0"},
      nativeRuns: (["linux-arm64","linux-x64","osx-arm64","osx-x64","win-arm64","win-x64"]
        | map({rid: ., packages: $packages})),
      finalArtifacts: $final_artifacts
    }
  ' > "$asset_source/native-test-evidence.json"

primary_names=("${runtime_names[@]}" "$protocol_name" native-test-evidence.json)
primary_artifacts="$fixture_root/primary-artifacts.json"
printf '[]\n' > "$primary_artifacts"
: > "$asset_source/SHA256SUMS"
sbom_files="$fixture_root/sbom-files.json"
printf '[]\n' > "$sbom_files"
for name in "${primary_names[@]}"; do
  sha="$(hash_file "$asset_source/$name")"
  size="$(file_size "$asset_source/$name")"
  printf '%s  %s\n' "$sha" "$name" >> "$asset_source/SHA256SUMS"
  jq --arg path "$name" --arg sha "$sha" --argjson size "$size" \
    '. + [{path: $path, sha256: $sha, size: $size}]' \
    "$primary_artifacts" > "$primary_artifacts.next"
  mv "$primary_artifacts.next" "$primary_artifacts"
  jq --arg name "./$name" --arg sha "$sha" \
    '. + [{fileName: $name, checksums: [{algorithm: "SHA256", checksumValue: $sha}]}]' \
    "$sbom_files" > "$sbom_files.next"
  mv "$sbom_files.next" "$sbom_files"
done
jq -n \
  --arg agent "$MOCK_SOURCE_SHA" \
  --arg core "$core_sha" \
  --argjson artifacts "$(< "$primary_artifacts")" '
    {
      schemaVersion: 1,
      buildType: "https://sunderapp.io/provenance/github-actions/v1",
      invocation: {workflow: "Sunder Agent Family Release", runId: "8001", runAttempt: "1"},
      sources: [{name: "sunder-agent-package", sha: $agent}, {name: "sunder-core", sha: $core}],
      materials: [{name: "sunder-core-sdk", tag: "sdk/v1.1.0", sha: $core, packageVersion: "1.1.0"}],
      artifacts: $artifacts
    }
  ' > "$asset_source/release-provenance.json"
jq -n --argjson files "$(< "$sbom_files")" '{spdxVersion: "SPDX-2.3", files: $files}' \
  > "$asset_source/release-sbom.spdx.json"
printf 'dotnet-version: 10.0.100\nrunner-os: fixture\n' > "$asset_source/toolchain.txt"

evidence_names=(SHA256SUMS release-provenance.json release-sbom.spdx.json toolchain.txt)
release_names=("${primary_names[@]}" "${evidence_names[@]}")
accepted_assets="$fixture_root/accepted-assets.json"
printf '[]\n' > "$accepted_assets"
release_assets="$fixture_root/release-assets.json"
printf '[]\n' > "$release_assets"
asset_id=1000
for name in "${release_names[@]}"; do
  sha="$(hash_file "$asset_source/$name")"
  size="$(file_size "$asset_source/$name")"
  asset_id=$((asset_id + 1))
  jq --arg name "$name" --arg sha "$sha" --argjson id "$asset_id" --argjson size "$size" \
    '. + [{id: $id, name: $name, sha256: $sha, size: $size}]' \
    "$accepted_assets" > "$accepted_assets.next"
  mv "$accepted_assets.next" "$accepted_assets"
  jq \
    --arg digest "sha256:$sha" \
    --arg name "$name" \
    --arg url "https://api.github.com/repos/$MOCK_GH_REPO/releases/assets/$asset_id" \
    --argjson id "$asset_id" \
    --argjson size "$size" \
    '. + [{id: $id, name: $name, size: $size, digest: $digest, url: $url}]' \
    "$release_assets" > "$release_assets.next"
  mv "$release_assets.next" "$release_assets"
done

registry_packages="$fixture_root/registry-packages.json"
printf '[]\n' > "$registry_packages"
for package_id in "${package_ids[@]}"; do
  jq \
    --arg package_id "$package_id" \
    --arg version "$MOCK_VERSION" \
    --arg sha "$(hash_file "$asset_source/$package_id.$MOCK_VERSION.sunderpkg")" \
    '. + [{packageId: $package_id, version: $version, canonicalSha256: $sha}]' \
    "$registry_packages" > "$registry_packages.next"
  mv "$registry_packages.next" "$registry_packages"
done

release_body="$(printf '%s\n\nAgent source commit: %s\nCore source commit: %s\nCore SDK release: sdk/v1.1.0\nCore SDK package version: 1.1.0' \
  "Fixture Agent family release" "$MOCK_SOURCE_SHA" "$core_sha")"
release_title="Sunder Agent Family $MOCK_VERSION"
jq -n \
  --arg body "$release_body" \
  --arg name "$release_title" \
  --arg tag "$MOCK_TAG" \
  --argjson assets "$(< "$release_assets")" \
  --argjson id "$MOCK_RELEASE_ID" \
  '{id: $id, tag_name: $tag, name: $name, draft: true, prerelease: false, body: $body, assets: $assets}' \
  > "$fixture_root/release-template.json"
cp "$fixture_root/release-template.json" "$fixture_root/release.json"
export MOCK_RELEASE_JSON="$fixture_root/release.json"
export MOCK_RELEASE_TEMPLATE_JSON="$fixture_root/release-template.json"

acceptance="$fixture_root/agent-family-acceptance.json"
jq -n \
  --arg artifact "$MOCK_ACCEPTANCE_ARTIFACT" \
  --arg attempt "$MOCK_ACCEPTANCE_ATTEMPT" \
  --arg repository "$MOCK_GH_REPO" \
  --arg run_id "$MOCK_ACCEPTANCE_RUN_ID" \
  --arg source "$MOCK_SOURCE_SHA" \
  --arg tag "$MOCK_TAG" \
  --arg version "$MOCK_VERSION" \
  --arg workflow "$MOCK_ACCEPTANCE_WORKFLOW" \
  --arg workflow_path "$MOCK_ACCEPTANCE_WORKFLOW_PATH" \
  --arg provenance_sha "$(hash_file "$asset_source/release-provenance.json")" \
  --arg body "$release_body" \
  --arg body_sha "$(printf '%s' "$release_body" | hash_stream)" \
  --arg title "$release_title" \
  --argjson assets "$(< "$accepted_assets")" \
  --argjson packages "$(< "$registry_packages")" \
  --argjson release_id "$MOCK_RELEASE_ID" '
    {
      schemaVersion: 2,
      decision: "accepted",
      repository: $repository,
      tag: $tag,
      version: $version,
      sourceCommit: $source,
      acceptanceRunId: $run_id,
      acceptanceRunAttempt: $attempt,
      acceptanceWorkflow: {name: $workflow, path: $workflow_path, event: "workflow_dispatch", artifactName: $artifact},
      acceptedAtUtc: "2026-08-24T12:00:00Z",
      draftRelease: {
        id: $release_id,
        draft: true,
        title: $title,
        body: $body,
        bodySha256: $body_sha,
        provenanceSha256: $provenance_sha,
        assets: $assets
      },
      registryPackages: $packages
    }
  ' > "$acceptance"
acceptance_sha="$(hash_file "$acceptance")"
fixture_receipt_json="$(jq -cnS \
  --arg artifact_digest "$MOCK_ACCEPTANCE_ARTIFACT_DIGEST" \
  --arg artifact_id "$MOCK_ACCEPTANCE_ARTIFACT_ID" \
  --arg artifact_name "$MOCK_ACCEPTANCE_ARTIFACT" \
  --arg attempt "$MOCK_ACCEPTANCE_ATTEMPT" \
  --arg body_sha "$(printf '%s' "$release_body" | hash_stream)" \
  --arg dist_tag latest \
  --arg event workflow_dispatch \
  --arg evidence_sha "$acceptance_sha" \
  --arg name "$MOCK_ACCEPTANCE_WORKFLOW" \
  --arg path "$MOCK_ACCEPTANCE_WORKFLOW_PATH" \
  --arg repository "$MOCK_GH_REPO" \
  --arg run_id "$MOCK_ACCEPTANCE_RUN_ID" \
  --arg source "$MOCK_SOURCE_SHA" \
  --arg tag "$MOCK_TAG" \
  --arg version "$MOCK_VERSION" \
  --argjson release_id "$MOCK_RELEASE_ID" '
    {
      schemaVersion: 1,
      releaseId: $release_id,
      tag: $tag,
      sourceCommit: $source,
      version: $version,
      distTag: $dist_tag,
      acceptedBodySha256: $body_sha,
      acceptance: {
        workflow: {
          repository: $repository,
          name: $name,
          path: $path,
          event: $event,
          artifactName: $artifact_name
        },
        runId: $run_id,
        runAttempt: $attempt,
        artifactId: $artifact_id,
        artifactDigest: $artifact_digest,
        evidenceSha256: $evidence_sha
      }
    }
  ')"
export MOCK_EXPECTED_FINALIZED_BODY="$fixture_root/expected-finalized-body.txt"
jq -jr --arg receipt "$fixture_receipt_json" \
  '.draftRelease.body + "\n\n<!-- sunder-agent-promotion-receipt:v1 " + $receipt + " -->"' \
  "$acceptance" > "$MOCK_EXPECTED_FINALIZED_BODY"

cat > "$mock_bin/git" <<'MOCK_GIT'
#!/usr/bin/env bash
set -euo pipefail
if [[ "$*" == 'rev-parse HEAD' ]]; then
  printf '%s\n' "$MOCK_SOURCE_SHA"
  exit 0
fi
printf 'Mock git does not support: %s\n' "$*" >&2
exit 2
MOCK_GIT

cat > "$mock_bin/gh" <<'MOCK_GH'
#!/usr/bin/env bash
set -euo pipefail
[[ "${1:-}" == api ]] || { printf 'Only gh api is allowed in promotion.\n' >&2; exit 2; }
shift
method=GET
input=''
endpoint=''
while [[ $# -gt 0 ]]; do
  case "$1" in
    --method)
      method="$2"
      shift 2
      ;;
    --input)
      input="$2"
      shift 2
      ;;
    -H)
      shift 2
      ;;
    --paginate|--slurp)
      shift
      ;;
    *)
      [[ -z "$endpoint" ]] || { printf 'Unexpected gh api argument: %s\n' "$1" >&2; exit 2; }
      endpoint="$1"
      shift
      ;;
  esac
done

case "$endpoint" in
  "repos/$MOCK_GH_REPO/actions/runs/$MOCK_ACCEPTANCE_RUN_ID/attempts/$MOCK_ACCEPTANCE_ATTEMPT")
    printf 'ATTEMPT\n' >> "$MOCK_EVENT_LOG"
    jq -n \
      --arg conclusion "${MOCK_RUN_CONCLUSION:-success}" \
      --arg event "${MOCK_RUN_EVENT:-workflow_dispatch}" \
      --arg head "${MOCK_RUN_HEAD_SHA:-$MOCK_SOURCE_SHA}" \
      --arg name "${MOCK_WORKFLOW_NAME:-$MOCK_ACCEPTANCE_WORKFLOW}" \
      --arg path "${MOCK_WORKFLOW_PATH:-$MOCK_ACCEPTANCE_WORKFLOW_PATH}" \
      --arg repository "${MOCK_RUN_REPOSITORY:-$MOCK_GH_REPO}" \
      --arg status "${MOCK_RUN_STATUS:-completed}" \
      --argjson attempt "${MOCK_RUN_ATTEMPT_RESULT:-$MOCK_ACCEPTANCE_ATTEMPT}" \
      --argjson id "${MOCK_RUN_ID_RESULT:-$MOCK_ACCEPTANCE_RUN_ID}" \
      '{id: $id, run_attempt: $attempt, name: $name, path: $path, event: $event,
        repository: {id: 1234, full_name: $repository}, head_sha: $head, status: $status, conclusion: $conclusion}'
    ;;
  "repos/$MOCK_GH_REPO/actions/artifacts/"*)
    printf 'ACCEPTANCE_ARTIFACT\n' >> "$MOCK_EVENT_LOG"
    jq -n \
      --arg created_at "${MOCK_ARTIFACT_CREATED_AT:-2026-08-24T12:05:00Z}" \
      --arg digest "${MOCK_ARTIFACT_DIGEST_RESULT:-$MOCK_ACCEPTANCE_ARTIFACT_DIGEST}" \
      --arg head "$MOCK_SOURCE_SHA" \
      --arg name "${MOCK_ARTIFACT_NAME:-$MOCK_ACCEPTANCE_ARTIFACT}" \
      --arg repository "$MOCK_GH_REPO" \
      --argjson artifact_id "${MOCK_ARTIFACT_ID_RESULT:-$MOCK_ACCEPTANCE_ARTIFACT_ID}" \
      --argjson run_id "$MOCK_ACCEPTANCE_RUN_ID" '
      {
        id: $artifact_id,
        name: $name,
        digest: $digest,
        expired: false,
        size_in_bytes: 4096,
        url: ("https://api.github.com/repos/" + $repository + "/actions/artifacts/" + ($artifact_id | tostring)),
        archive_download_url: ("https://api.github.com/repos/" + $repository + "/actions/artifacts/" + ($artifact_id | tostring) + "/zip"),
        created_at: $created_at,
        workflow_run: {id: $run_id, repository_id: 1234, head_sha: $head}
      }'
    ;;
  "repos/$MOCK_GH_REPO/actions/runs/$MOCK_ACCEPTANCE_RUN_ID/attempts/$MOCK_ACCEPTANCE_ATTEMPT/jobs?per_page=100")
    printf 'ATTEMPT_JOBS\n' >> "$MOCK_EVENT_LOG"
    jq -n \
      --argjson attempt "$MOCK_ACCEPTANCE_ATTEMPT" \
      --argjson run_id "$MOCK_ACCEPTANCE_RUN_ID" '
      [{total_count: 1, jobs: [{
        id: 8001,
        run_id: $run_id,
        run_attempt: $attempt,
        started_at: "2026-08-24T12:00:00Z",
        completed_at: "2026-08-24T12:10:00Z"
      }]}]'
    ;;
  "repos/$MOCK_GH_REPO/git/ref/tags/"*)
    printf 'TAG_REF\n' >> "$MOCK_EVENT_LOG"
    tag_call_count=$(( $(< "$MOCK_TAG_CALL_COUNT") + 1 ))
    printf '%s\n' "$tag_call_count" > "$MOCK_TAG_CALL_COUNT"
    tag_sha="${MOCK_TAG_SHA_RESULT:-$MOCK_SOURCE_SHA}"
    if [[ "${MOCK_TAG_DRIFT_ON_CALL:-}" == "$tag_call_count" ]]; then
      tag_sha=3333333333333333333333333333333333333333
    fi
    jq -n \
      --arg ref "refs/tags/$MOCK_TAG" \
      --arg sha "$tag_sha" \
      '{ref: $ref, object: {type: "commit", sha: $sha}}'
    ;;
  "repos/$MOCK_GH_REPO/releases/$MOCK_RELEASE_ID")
    if [[ "$method" == PATCH ]]; then
      [[ "$input" == - ]] || { printf 'Release PATCH must use JSON stdin.\n' >&2; exit 2; }
      payload="$(command cat)"
      jq -e --rawfile expected_body "$MOCK_EXPECTED_FINALIZED_BODY" '
        keys == ["body","draft"]
        and .draft == false
        and .body == $expected_body
      ' <<< "$payload" >/dev/null
      [[ "$(< "$MOCK_PROMOTED_STATE")" == true ]] \
        || { printf 'Release finalized before family promotion.\n' >&2; exit 2; }
      finalized_body="$(jq -er '.body' <<< "$payload")"
      jq --arg body "$finalized_body" '.draft = false | .body = $body' \
        "$MOCK_RELEASE_JSON" > "$MOCK_RELEASE_JSON.next"
      mv "$MOCK_RELEASE_JSON.next" "$MOCK_RELEASE_JSON"
      printf 'FINALIZE\n' >> "$MOCK_EVENT_LOG"
      printf 'false\n' > "$MOCK_DRAFT_STATE"
      if [[ "${MOCK_FAIL_AFTER_FINALIZE:-false}" == true ]]; then
        exit 75
      fi
    else
      printf 'RELEASE_GET\n' >> "$MOCK_EVENT_LOG"
      release_call_count=$(( $(< "$MOCK_RELEASE_CALL_COUNT") + 1 ))
      printf '%s\n' "$release_call_count" > "$MOCK_RELEASE_CALL_COUNT"
    fi
    jq \
      --arg body "${MOCK_RELEASE_BODY_RESULT:-}" \
      --arg title "${MOCK_RELEASE_TITLE_RESULT:-}" \
      --argjson draft "$(< "$MOCK_DRAFT_STATE")" \
      --argjson drift "$([[ "${MOCK_RELEASE_DRIFT_ON_CALL:-}" == "${release_call_count:-0}" ]] && printf true || printf false)" \
      --argjson id "${MOCK_RELEASE_ID_RESULT:-$MOCK_RELEASE_ID}" \
      '.draft = $draft
        | .id = $id
        | if $title != "" then .name = $title else . end
        | if $body != "" then .body = $body else . end
        | if $drift then .assets[0].digest = "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" else . end' \
      "$MOCK_RELEASE_JSON"
    ;;
  https://api.github.com/repos/*/releases/assets/*)
    asset_id="${endpoint##*/}"
    asset_name="$(jq -er --argjson id "$asset_id" '.assets[] | select(.id == $id) | .name' "$MOCK_RELEASE_JSON")"
    printf 'ASSET %s\n' "$asset_name" >> "$MOCK_EVENT_LOG"
    if [[ "${MOCK_TAMPER_ASSET:-}" == "$asset_name" ]]; then
      printf 'tampered asset bytes\n'
    else
      command cat "$MOCK_RELEASE_ASSETS/$asset_name"
    fi
    ;;
  *)
    printf 'Unsupported mock gh endpoint: %s %s\n' "$method" "$endpoint" >&2
    exit 2
    ;;
esac
MOCK_GH

cat > "$fixture_repository/scripts/release/publish-registry-package.sh" <<'MOCK_REGISTRY_VERIFY'
#!/usr/bin/env bash
set -euo pipefail
[[ "${1:-}" == --verify-only && $# -eq 5 ]] \
  || { printf 'IMMUTABLE_WRITE\n' >> "$MOCK_EVENT_LOG"; exit 2; }
package_id="$4"
version="$5"
[[ "$version" == "$MOCK_VERSION" ]]
printf 'REGISTRY_VERIFY %s\n' "$package_id" >> "$MOCK_EVENT_LOG"
[[ "${MOCK_REGISTRY_MISMATCH_PACKAGE:-}" != "$package_id" ]] \
  || { printf 'Fixture Registry canonical identity mismatch.\n' >&2; exit 1; }
MOCK_REGISTRY_VERIFY

cat > "$fixture_repository/scripts/release/promote-family-dist-tag.sh" <<'MOCK_FAMILY_PROMOTE'
#!/usr/bin/env bash
set -euo pipefail
if [[ "${1:-}" == --verify-only ]]; then
  verify_call_count=$(( $(< "$MOCK_FAMILY_VERIFY_CALL_COUNT") + 1 ))
  printf '%s\n' "$verify_call_count" > "$MOCK_FAMILY_VERIFY_CALL_COUNT"
  printf 'FAMILY_VERIFY\n' >> "$MOCK_EVENT_LOG"
  [[ "$(< "$MOCK_PROMOTED_STATE")" == true ]] \
    || { printf 'Family is not fully promoted.\n' >&2; exit 1; }
  if [[ "${MOCK_FAMILY_DRIFT_ON_VERIFY_CALL:-}" == "$verify_call_count" ]]; then
    printf 'false\n' > "$MOCK_PROMOTED_STATE"
  fi
  exit 0
fi
[[ "${SUNDER_REGISTRY_CLI_TOKEN:-}" == "$MOCK_EXPECTED_CLI_TOKEN" ]] \
  || { printf 'Promotion did not receive the exact CLI token.\n' >&2; exit 2; }
printf 'FAMILY_SNAPSHOT_DONE\n' >> "$MOCK_EVENT_LOG"
[[ -n "${SUNDER_REGISTRY_PRE_MUTATION_GUARD_JSON:-}" ]]
guard_command=()
while IFS= read -r argument; do guard_command+=("$argument"); done \
  < <(jq -r '.[]' <<< "$SUNDER_REGISTRY_PRE_MUTATION_GUARD_JSON")
env -u SUNDER_REGISTRY_CLI_TOKEN "${guard_command[@]}"
printf 'PROMOTE\n' >> "$MOCK_EVENT_LOG"
printf 'true\n' > "$MOCK_PROMOTED_STATE"
MOCK_FAMILY_PROMOTE

chmod +x "$mock_bin/git" "$mock_bin/gh"

export MOCK_FAMILY_VERIFY_CALL_COUNT="$fixture_root/family-verify-call-count"

reset_fixture() {
  : > "$MOCK_EVENT_LOG"
  cp "$MOCK_RELEASE_TEMPLATE_JSON" "$MOCK_RELEASE_JSON"
  printf 'true\n' > "$MOCK_DRAFT_STATE"
  printf 'false\n' > "$MOCK_PROMOTED_STATE"
  printf '0\n' > "$MOCK_FAMILY_VERIFY_CALL_COUNT"
  printf '0\n' > "$MOCK_RELEASE_CALL_COUNT"
  printf '0\n' > "$MOCK_TAG_CALL_COUNT"
  unset \
    MOCK_ARTIFACT_CREATED_AT \
    MOCK_ARTIFACT_DIGEST_RESULT \
    MOCK_ARTIFACT_ID_RESULT \
    MOCK_ARTIFACT_NAME \
    MOCK_FAIL_AFTER_FINALIZE \
    MOCK_FAMILY_DRIFT_ON_VERIFY_CALL \
    MOCK_INPUT_ARTIFACT_DIGEST \
    MOCK_INPUT_ARTIFACT_ID \
    MOCK_REGISTRY_MISMATCH_PACKAGE \
    MOCK_RELEASE_BODY_RESULT \
    MOCK_RELEASE_DRIFT_ON_CALL \
    MOCK_RELEASE_ID_RESULT \
    MOCK_RELEASE_TITLE_RESULT \
    MOCK_RUN_ATTEMPT_RESULT \
    MOCK_RUN_CONCLUSION \
    MOCK_RUN_EVENT \
    MOCK_RUN_HEAD_SHA \
    MOCK_RUN_ID_RESULT \
    MOCK_RUN_REPOSITORY \
    MOCK_RUN_STATUS \
    MOCK_TAG_DRIFT_ON_CALL \
    MOCK_TAG_SHA_RESULT \
    MOCK_TAMPER_ASSET \
    MOCK_WORKFLOW_NAME \
    MOCK_WORKFLOW_PATH
}

run_orchestrator() {
  local case_name="$1"
  local expected_hash="${2:-$acceptance_sha}"
  (
    cd "$fixture_repository"
    PATH="$mock_bin:$PATH" \
    ACCEPTANCE_ARTIFACT_DIGEST="${MOCK_INPUT_ARTIFACT_DIGEST:-$MOCK_ACCEPTANCE_ARTIFACT_DIGEST}" \
    ACCEPTANCE_ARTIFACT_ID="${MOCK_INPUT_ARTIFACT_ID:-$MOCK_ACCEPTANCE_ARTIFACT_ID}" \
    ACCEPTANCE_EVIDENCE_SHA256="$expected_hash" \
    ACCEPTANCE_RUN_ATTEMPT="$MOCK_ACCEPTANCE_ATTEMPT" \
    ACCEPTANCE_RUN_ID="$MOCK_ACCEPTANCE_RUN_ID" \
    GH_REPO="$MOCK_GH_REPO" \
    GH_TOKEN=fixture-token \
    SUNDER_REGISTRY_CLI_TOKEN="$MOCK_EXPECTED_CLI_TOKEN" \
      bash scripts/release/promote-agent-family.sh \
        "$MOCK_TAG" "$MOCK_SOURCE_SHA" https://registry.test "$acceptance" \
        "$fixture_root/verification-$case_name"
  )
}

expect_rejected_without_mutation() {
  local case_name="$1"
  local expected_hash="${2:-$acceptance_sha}"
  if run_orchestrator "$case_name" "$expected_hash" > "$fixture_root/$case_name.log" 2>&1; then
    printf 'Promotion fixture unexpectedly accepted case: %s\n' "$case_name" >&2
    exit 1
  fi
  if grep -Eq '^(PROMOTE|FINALIZE|IMMUTABLE_WRITE)$' "$MOCK_EVENT_LOG"; then
    printf 'Rejected promotion case mutated state: %s\n' "$case_name" >&2
    exit 1
  fi
}

expect_rejected_after_promotion_without_finalization() {
  local case_name="$1"
  if run_orchestrator "$case_name" > "$fixture_root/$case_name.log" 2>&1; then
    printf 'Promotion fixture unexpectedly accepted post-promotion drift: %s\n' "$case_name" >&2
    exit 1
  fi
  [[ "$(grep -c '^PROMOTE$' "$MOCK_EVENT_LOG")" -eq 1 ]]
  if grep -Eq '^(FINALIZE|IMMUTABLE_WRITE)$' "$MOCK_EVENT_LOG"; then
    printf 'Post-promotion drift reached finalization or immutable publication: %s\n' "$case_name" >&2
    exit 1
  fi
}

reset_fixture
run_orchestrator valid > "$fixture_root/valid.log"
valid_events=()
while IFS= read -r event; do valid_events+=("$event"); done < "$MOCK_EVENT_LOG"
promote_index=-1
finalize_index=-1
snapshot_index=-1
for ((index = 0; index < ${#valid_events[@]}; index++)); do
  [[ "${valid_events[$index]}" != PROMOTE ]] || promote_index=$index
  [[ "${valid_events[$index]}" != FINALIZE ]] || finalize_index=$index
  [[ "${valid_events[$index]}" != FAMILY_SNAPSHOT_DONE ]] || snapshot_index=$index
done
(( snapshot_index >= 0 && promote_index > snapshot_index && finalize_index > promote_index ))
[[ "${valid_events[$((promote_index - 1))]}" == RELEASE_GET ]]
[[ "${valid_events[$((finalize_index - 1))]}" == FAMILY_VERIFY ]]
[[ "$(grep -c '^ATTEMPT$' "$MOCK_EVENT_LOG")" -eq 5 ]]
[[ "$(grep -c '^ACCEPTANCE_ARTIFACT$' "$MOCK_EVENT_LOG")" -eq 5 ]]
[[ "$(grep -c '^ATTEMPT_JOBS$' "$MOCK_EVENT_LOG")" -eq 5 ]]
[[ "$(grep -c '^RELEASE_GET$' "$MOCK_EVENT_LOG")" -eq 5 ]]
[[ "$(grep -c '^ASSET ' "$MOCK_EVENT_LOG")" -eq 63 ]]
[[ "$(grep -c '^REGISTRY_VERIFY ' "$MOCK_EVENT_LOG")" -eq 45 ]]
[[ "$(grep -c '^TAG_REF$' "$MOCK_EVENT_LOG")" -eq 5 ]]
[[ "$(grep -c '^FAMILY_VERIFY$' "$MOCK_EVENT_LOG")" -eq 3 ]]
[[ "$(grep -c '^FINALIZE$' "$MOCK_EVENT_LOG")" -eq 1 ]]
! grep -Eq '^(IMMUTABLE_WRITE|BUILD|NUGET|PACK|PUBLISH|SIGN)' "$MOCK_EVENT_LOG"
jq -e --slurpfile template "$MOCK_RELEASE_TEMPLATE_JSON" '.assets == $template[0].assets' \
  "$MOCK_RELEASE_JSON" >/dev/null

reset_fixture
export MOCK_WORKFLOW_PATH=.github/workflows/untrusted.yml
expect_rejected_without_mutation wrong-workflow

reset_fixture
export MOCK_RUN_ID_RESULT=9002
expect_rejected_without_mutation wrong-run

reset_fixture
export MOCK_RUN_ATTEMPT_RESULT=3
expect_rejected_without_mutation wrong-attempt

reset_fixture
expect_rejected_without_mutation wrong-evidence "$(printf '%064d' 0)"

reset_fixture
export MOCK_ARTIFACT_NAME=untrusted-acceptance
expect_rejected_without_mutation wrong-artifact

reset_fixture
export MOCK_INPUT_ARTIFACT_ID=7002
expect_rejected_without_mutation wrong-artifact-id

reset_fixture
export MOCK_INPUT_ARTIFACT_DIGEST=sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc
expect_rejected_without_mutation wrong-artifact-digest

reset_fixture
export MOCK_ARTIFACT_CREATED_AT=2026-08-24T11:59:59Z
expect_rejected_without_mutation artifact-from-wrong-attempt-window

reset_fixture
export MOCK_RUN_EVENT=push
expect_rejected_without_mutation wrong-event

reset_fixture
export MOCK_RELEASE_ID_RESULT=424243
expect_rejected_without_mutation draft-replacement

reset_fixture
export MOCK_RELEASE_TITLE_RESULT='Replacement title'
expect_rejected_without_mutation release-title-mismatch

reset_fixture
export MOCK_RELEASE_BODY_RESULT='Replacement body'
expect_rejected_without_mutation release-body-mismatch

reset_fixture
export MOCK_TAG_SHA_RESULT=3333333333333333333333333333333333333333
expect_rejected_without_mutation tag-drift

reset_fixture
export MOCK_TAG_DRIFT_ON_CALL=2
expect_rejected_without_mutation tag-drift-before-mutation
grep -Fqx 'FAMILY_SNAPSHOT_DONE' "$MOCK_EVENT_LOG"

reset_fixture
export MOCK_RELEASE_DRIFT_ON_CALL=2
expect_rejected_without_mutation release-drift-before-mutation
grep -Fqx 'FAMILY_SNAPSHOT_DONE' "$MOCK_EVENT_LOG"

reset_fixture
export MOCK_TAMPER_ASSET=toolchain.txt
expect_rejected_without_mutation asset-tamper

reset_fixture
export MOCK_REGISTRY_MISMATCH_PACKAGE="${package_ids[7]}"
expect_rejected_without_mutation registry-mismatch

reset_fixture
export MOCK_RELEASE_DRIFT_ON_CALL=4
expect_rejected_after_promotion_without_finalization release-drift-before-finalization

reset_fixture
export MOCK_TAG_DRIFT_ON_CALL=4
expect_rejected_after_promotion_without_finalization tag-drift-before-finalization

reset_fixture
export MOCK_FAMILY_DRIFT_ON_VERIFY_CALL=1
expect_rejected_after_promotion_without_finalization family-drift-before-finalization
[[ "$(grep -c '^FAMILY_VERIFY$' "$MOCK_EVENT_LOG")" -eq 2 ]]

# Simulate runner loss after GitHub atomically persists draft=false and the exact receipt.
reset_fixture
export MOCK_FAIL_AFTER_FINALIZE=true
if run_orchestrator runner-loss-after-finalize > "$fixture_root/runner-loss-after-finalize.log" 2>&1; then
  printf 'Promotion unexpectedly reported success after simulated runner loss.\n' >&2
  exit 1
fi
[[ "$(< "$MOCK_DRAFT_STATE")" == false ]]
[[ "$(< "$MOCK_PROMOTED_STATE")" == true ]]
grep -Fqx 'FINALIZE' "$MOCK_EVENT_LOG"
unset MOCK_FAIL_AFTER_FINALIZE
: > "$MOCK_EVENT_LOG"
printf '0\n' > "$MOCK_RELEASE_CALL_COUNT"
printf '0\n' > "$MOCK_TAG_CALL_COUNT"
printf '0\n' > "$MOCK_FAMILY_VERIFY_CALL_COUNT"
run_orchestrator runner-loss-retry > "$fixture_root/runner-loss-retry.log"
grep -Fq 'Verified durable promotion receipt' "$fixture_root/runner-loss-retry.log"
! grep -Eq '^(PROMOTE|FINALIZE)$' "$MOCK_EVENT_LOG"
[[ "$(grep -c '^FAMILY_VERIFY$' "$MOCK_EVENT_LOG")" -eq 2 ]]

reset_fixture
printf 'false\n' > "$MOCK_DRAFT_STATE"
printf 'true\n' > "$MOCK_PROMOTED_STATE"
expect_rejected_without_mutation finalized-retry-without-receipt

reset_fixture
printf 'false\n' > "$MOCK_DRAFT_STATE"
printf 'true\n' > "$MOCK_PROMOTED_STATE"
forged_body="$release_body"$'\n\n<!-- sunder-agent-promotion-receipt:v1 {"schemaVersion":1,"sourceCommit":"3333333333333333333333333333333333333333"} -->'
jq --arg body "$forged_body" '.draft = false | .body = $body' \
  "$MOCK_RELEASE_JSON" > "$MOCK_RELEASE_JSON.next"
mv "$MOCK_RELEASE_JSON.next" "$MOCK_RELEASE_JSON"
expect_rejected_without_mutation forged-finalized-receipt

printf 'Agent promotion orchestration fixture passed: exact handoff binding, refreshed final family guard, atomic receipt finalization, runner-loss retry, and forged-state rejection verified.\n'
