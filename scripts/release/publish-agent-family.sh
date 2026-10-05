#!/usr/bin/env bash
set -euo pipefail
export LC_ALL=C

if [[ $# -ne 1 ]]; then
  printf 'Usage: publish-agent-family.sh <release-assets-directory>\n' >&2
  exit 2
fi

asset_directory="$(cd "$1" && pwd)"
: "${CORE_COMMIT:?CORE_COMMIT is required}"
: "${CORE_TAG:?CORE_TAG is required}"
: "${CORE_SDK_VERSION:?CORE_SDK_VERSION is required}"
: "${GH_TOKEN:?GH_TOKEN is required}"
: "${GH_REPO:?GH_REPO is required}"
: "${GITHUB_REF:?GITHUB_REF is required}"
: "${GITHUB_REF_NAME:?GITHUB_REF_NAME is required}"
: "${GITHUB_RUN_ATTEMPT:?GITHUB_RUN_ATTEMPT is required}"
: "${GITHUB_RUN_ID:?GITHUB_RUN_ID is required}"
: "${GITHUB_SHA:?GITHUB_SHA is required}"
: "${GITHUB_WORKFLOW:?GITHUB_WORKFLOW is required}"
: "${GITHUB_WORKFLOW_REF:?GITHUB_WORKFLOW_REF is required}"
: "${IS_PRERELEASE:?IS_PRERELEASE is required}"
: "${REGISTRY_API_URL:?REGISTRY_API_URL is required}"
: "${SUNDER_REGISTRY_PUBLISH_TOKEN:?SUNDER_REGISTRY_PUBLISH_TOKEN is required}"
: "${VERSION:?VERSION is required}"
: "${SUNDER_AGENT_ARTIFACT_SOURCE_COMMIT:?Approved artifact source is required}"
: "${SUNDER_AGENT_LOCK_SET_SHA256:?Approved production lock set is required}"
: "${SUNDER_AGENT_RELEASE_APPROVAL_REFERENCE:?Release approval reference is required}"
: "${SUNDER_AGENT_RELEASE_OWNER_PRINCIPAL_ID:?Approved issuer-qualified Registry owner ID is required}"

[[ "$CORE_COMMIT" =~ ^[0-9a-f]{40}$ ]] \
  || { printf 'CORE_COMMIT must be a full lowercase Git SHA.\n' >&2; exit 2; }
[[ "$GITHUB_SHA" =~ ^[0-9a-f]{40}$ ]] \
  || { printf 'GITHUB_SHA must be a full lowercase Git SHA.\n' >&2; exit 2; }
[[ "$GITHUB_SHA" == "$SUNDER_AGENT_ARTIFACT_SOURCE_COMMIT" \
    && "${GITHUB_WORKFLOW_SHA:-}" == "$GITHUB_SHA" \
    && "${GITHUB_EVENT_NAME:-}" == push \
    && "$SUNDER_AGENT_RELEASE_APPROVAL_REFERENCE" =~ ^https://[^/?#]+/[^[:space:]#]+$ ]] \
  || { printf 'Publication is not bound to its approved artifact source and workflow.\n' >&2; exit 1; }
[[ "$(git rev-parse HEAD)" == "$GITHUB_SHA" && -z "$(git status --porcelain --untracked-files=no)" ]] \
  || { printf 'Publication requires the exact clean artifact-source checkout.\n' >&2; exit 1; }
[[ "$GITHUB_RUN_ID" =~ ^[1-9][0-9]*$ && "$GITHUB_RUN_ATTEMPT" =~ ^[1-9][0-9]*$ ]] \
  || { printf 'GitHub publication run ID and attempt must be positive decimal integers.\n' >&2; exit 2; }
[[ "$CORE_TAG" == "sdk/v$CORE_SDK_VERSION" ]] \
  || { printf 'Core tag and package version do not agree.\n' >&2; exit 2; }
[[ "$IS_PRERELEASE" == true || "$IS_PRERELEASE" == false ]] \
  || { printf 'IS_PRERELEASE must be true or false.\n' >&2; exit 2; }
[[ "$SUNDER_REGISTRY_PUBLISH_TOKEN" =~ ^sunder_pub_v1_[A-Za-z0-9_-]{43}$ ]] \
  || { printf 'SUNDER_REGISTRY_PUBLISH_TOKEN is not a valid scoped publish token.\n' >&2; exit 1; }
registry_api="${REGISTRY_API_URL%/}"
[[ "$registry_api" =~ ^https://[^/@?#]+$ ]] \
  || { printf 'SUNDER_REGISTRY_API_URL must be an absolute HTTPS origin.\n' >&2; exit 1; }

for required_command in cmp curl cut gh git grep jq mktemp python3 sort tr unzip wc; do
  command -v "$required_command" >/dev/null 2>&1 \
    || { printf '%s is required to publish the Agent family.\n' "$required_command" >&2; exit 127; }
done
if ! command -v sha256sum >/dev/null 2>&1 && ! command -v shasum >/dev/null 2>&1; then
  printf 'sha256sum or shasum is required to publish the Agent family.\n' >&2
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

jq -e '.packages | length == 15 and all(.[]; .artifactType == "sunderpkg") and ([.[].packageId] | unique | length) == 15' packages.json >/dev/null \
  || { printf 'packages.json must contain exactly 15 unique canonical package archives.\n' >&2; exit 1; }
[[ "$VERSION" == "$(jq -er '.releaseVersion' packages.json)" ]] \
  || { printf 'VERSION does not match packages.json releaseVersion.\n' >&2; exit 1; }

package_ids=()
while IFS= read -r package_id; do package_ids+=("$package_id"); done \
  < <(jq -r '.packages | map(select(.artifactType == "sunderpkg")) | sort_by(.packageId)[] | .packageId' packages.json)
runtime_names=()
for package_id in "${package_ids[@]}"; do
  runtime_names+=("$package_id.$VERSION.sunderpkg")
done
native_evidence_name='native-test-evidence.json'
output_audit_name='package-output-audit.json'
final_artifact_names=("${runtime_names[@]}")
primary_names=("${final_artifact_names[@]}" "$native_evidence_name" "$output_audit_name")
evidence_names=(SHA256SUMS release-provenance.json release-sbom.spdx.json toolchain.txt)
expected_release_names=("${primary_names[@]}" "${evidence_names[@]}")
expected_primary_json="$(printf '%s\n' "${primary_names[@]}" | jq -Rsc 'split("\n")[:-1] | sort')"

shopt -s nullglob
initial_files=("$asset_directory"/*)
[[ "${#initial_files[@]}" == "${#primary_names[@]}" ]] \
  || { printf 'The tested family input must contain exactly %s primary files.\n' "${#primary_names[@]}" >&2; exit 1; }
for name in "${primary_names[@]}"; do
  [[ -f "$asset_directory/$name" ]] \
    || { printf 'Missing exact tested release input: %s\n' "$name" >&2; exit 1; }
done

native_evidence="$asset_directory/$native_evidence_name"
bash scripts/release/verify-native-family-evidence.sh \
  "$native_evidence" "$asset_directory" packages.json \
  "$VERSION" "$GITHUB_SHA" "$CORE_TAG" "$CORE_COMMIT" "$CORE_SDK_VERSION" >/dev/null
(
  regenerated_audit="$(mktemp "${RUNNER_TEMP:-${TMPDIR:-/tmp}}/sunder-agent-output-audit.XXXXXX")"
  trap 'rm -f "$regenerated_audit"' EXIT
  bash scripts/release/generate-package-output-audit.sh \
    "$asset_directory" "$native_evidence" "$PWD" "$regenerated_audit" >/dev/null
  cmp -s "$asset_directory/$output_audit_name" "$regenerated_audit" \
    || { printf 'Tested package output audit is not the deterministic audit of the exact archives and native measurements.\n' >&2; exit 1; }
)

family_plan="$(python3 -B scripts/release/agent_release.py plan "$PWD" "$asset_directory" \
  "$registry_api" "$SUNDER_AGENT_RELEASE_OWNER_PRINCIPAL_ID")"
[[ "$(jq -r '.lockSetSha256' <<< "$family_plan")" == "$SUNDER_AGENT_LOCK_SET_SHA256" ]] \
  || { printf 'Production locks differ from the exact approved lock set.\n' >&2; exit 1; }

# This anonymous read-only gate must complete before a GitHub draft or immutable package is published.
bash scripts/release/verify-execution-runtime-availability.sh "$registry_api"

tag="agent/v$VERSION"
expected_ref="refs/tags/$tag"
expected_workflow_ref="$GH_REPO/.github/workflows/sunder-package-release.yml@$expected_ref"
[[ "$GITHUB_REF" == "$expected_ref" && "$GITHUB_REF_NAME" == "$tag" \
    && "$GITHUB_WORKFLOW" == 'Sunder Agent Family Release' \
    && "$GITHUB_WORKFLOW_REF" == "$expected_workflow_ref" ]] \
  || { printf 'Agent publication invocation does not match the mandated tag-push workflow.\n' >&2; exit 1; }
release_notes_base="$(printf '%s\n\nAgent source commit: %s\nCore source commit: %s\nCore SDK release: %s\nCore SDK package version: %s\n\n%s' \
  "Coordinated 15-package Agent native runtime family release for $VERSION." \
  "$GITHUB_SHA" "$CORE_COMMIT" "$CORE_TAG" "$CORE_SDK_VERSION" \
  'Immutable publication preserves and byte-verifies the native-tested artifacts without moving Registry dist tags or finalizing this draft.')"
created_draft=false
external_publication_started=false
temp_root="${RUNNER_TEMP:-${TMPDIR:-/tmp}}"
verification_root="$(mktemp -d "${temp_root%/}/sunder-agent-publication.XXXXXX")"
release_id=''
evidence_run_attempt=''
release_assets_sha256=''

cleanup() {
  exit_code=$?
  trap - EXIT
  rm -rf "$verification_root"
  if [[ $exit_code -ne 0 && "$created_draft" == true && "$external_publication_started" != true ]] \
    && gh release view "$tag" --repo "$GH_REPO" >/dev/null 2>&1 \
    && [[ "$(gh release view "$tag" --repo "$GH_REPO" --json isDraft --jq .isDraft)" == true ]]; then
    gh release delete "$tag" --repo "$GH_REPO" --yes || true
  elif [[ $exit_code -ne 0 && "$external_publication_started" == true ]]; then
    printf 'Preserving draft %s because immutable external publication may have started; rerun the same tag to recover.\n' "$tag" >&2
  fi
  exit "$exit_code"
}
trap cleanup EXIT

build_release_notes() {
  local directory="$1"
  local name
  printf '%s\n\nRelease evidence SHA-256:' "$release_notes_base"
  for name in "${evidence_names[@]}"; do
    printf '\n%s SHA-256: %s' "$name" "$(hash_file "$directory/$name")"
  done
}

verify_release_sources() {
  local release_json="$1"
  jq -e \
    --arg tag "$tag" \
    --argjson prerelease "$IS_PRERELEASE" \
    --arg agent_commit "$GITHUB_SHA" \
    --arg core_commit "$CORE_COMMIT" \
    --arg core_tag "$CORE_TAG" \
    --arg core_version "$CORE_SDK_VERSION" '
      .tagName == $tag
      and .isDraft == true
      and .isPrerelease == $prerelease
      and (.body | capture("(?m)^Agent source commit: (?<sha>[0-9a-f]{40})$").sha) == $agent_commit
      and (.body | capture("(?m)^Core source commit: (?<sha>[0-9a-f]{40})$").sha) == $core_commit
      and (.body | capture("(?m)^Core SDK release: (?<tag>sdk/v[^[:space:]]+)$").tag) == $core_tag
      and (.body | capture("(?m)^Core SDK package version: (?<version>[^[:space:]]+)$").version) == $core_version
    ' <<< "$release_json" >/dev/null \
    || { printf 'GitHub release is not the exact expected Agent draft and source provenance.\n' >&2; return 1; }
}

verify_release_inventory() {
  local release_json="$1"
  [[ "$(jq '.assets | length' <<< "$release_json")" == "${#expected_release_names[@]}" ]] \
    || { printf 'Draft asset inventory must contain exactly %s files.\n' "${#expected_release_names[@]}" >&2; return 1; }
  for expected_name in "${expected_release_names[@]}"; do
    [[ "$(jq --arg name "$expected_name" '[.assets[] | select(.name == $name)] | length' <<< "$release_json")" == 1 ]] \
      || { printf 'Draft must contain exactly one %s.\n' "$expected_name" >&2; return 1; }
  done
}

verify_evidence_directory() {
  local directory="$1"
  local provenance="$directory/release-provenance.json"
  local sums="$directory/SHA256SUMS"
  local sbom="$directory/release-sbom.spdx.json"
  local evidence="$directory/$native_evidence_name"
  local regenerated_audit

  python3 -B scripts/release/agent_release.py verify-provenance "$PWD" "$directory"

  jq -e \
    --arg repository "$GH_REPO" \
    --arg workflow "$GITHUB_WORKFLOW" \
    --arg workflow_ref "$expected_workflow_ref" \
    --arg run_id "$GITHUB_RUN_ID" \
    --argjson release_id "$release_id" \
    --arg ref "$expected_ref" \
    --arg ref_name "$tag" \
    --arg agent_commit "$GITHUB_SHA" \
    --arg core_commit "$CORE_COMMIT" \
    --arg core_tag "$CORE_TAG" \
    --arg core_version "$CORE_SDK_VERSION" \
    --arg registry_origin "$registry_api" \
    --arg owner "$SUNDER_AGENT_RELEASE_OWNER_PRINCIPAL_ID" \
    --arg lock_set "$SUNDER_AGENT_LOCK_SET_SHA256" \
    --arg repository_id "${GITHUB_REPOSITORY_ID:-}" \
    --argjson expected_names "$(printf '%s\n' "${runtime_names[@]}" | jq -Rsc 'split("\n")[:-1] | sort')" '
      .schemaVersion == 1
      and .publication == {registryOrigin:$registry_origin,ownerPrincipalId:$owner}
      and .lockSetSha256 == $lock_set
      and (.controlPlane.repositoryId | tostring) == $repository_id
      and .buildType == "https://sunderapp.io/provenance/github-actions/v1"
      and .invocation.repository == $repository
      and .invocation.workflow == $workflow
      and .invocation.workflowRef == $workflow_ref
      and .invocation.runId == $run_id
      and (.invocation.runAttempt | test("^[1-9][0-9]*$"))
      and .invocation.releaseId == $release_id
      and .invocation.ref == $ref
      and .invocation.refName == $ref_name
      and .invocation.sha == $agent_commit
      and ([.sources[] | {name, sha}] | sort_by(.name)) == ([
        {name: "sunder-agent-package", sha: $agent_commit},
        {name: "sunder-core", sha: $core_commit}
      ] | sort_by(.name))
      and .materials == [{name: "sunder-core-sdk", tag: $core_tag, sha: $core_commit, packageVersion: $core_version}]
      and ([.artifacts[].path] | sort) == $expected_names
      and ([.artifacts[].path] | length) == ([.artifacts[].path] | unique | length)
      and all(.artifacts[];
        (.sha256 | type == "string" and test("^[0-9a-f]{64}$"))
        and (.size | type == "number" and floor == . and . > 0))
    ' "$provenance" >/dev/null \
    || { printf 'Release provenance does not match the exact Agent/Core primary artifact set.\n' >&2; return 1; }

  bash scripts/release/verify-native-family-evidence.sh \
    "$evidence" "$directory" packages.json \
    "$VERSION" "$GITHUB_SHA" "$CORE_TAG" "$CORE_COMMIT" "$CORE_SDK_VERSION" >/dev/null
  regenerated_audit="$(mktemp "${RUNNER_TEMP:-${TMPDIR:-/tmp}}/sunder-agent-output-audit.XXXXXX")"
  if ! bash scripts/release/generate-package-output-audit.sh \
      "$directory" "$evidence" "$PWD" "$regenerated_audit" >/dev/null \
    || ! cmp -s "$directory/$output_audit_name" "$regenerated_audit"; then
    rm -f "$regenerated_audit"
    printf 'Downloaded package output audit does not match the exact archives and native measurements.\n' >&2
    return 1
  fi
  rm -f "$regenerated_audit"

  [[ "$(wc -l < "$sums" | tr -d '[:space:]')" == "${#primary_names[@]}" ]] \
    || { printf 'SHA256SUMS does not contain the exact primary inventory.\n' >&2; return 1; }
  jq -e --argjson expected_names "$expected_primary_json" '
    [.files[].fileName | sub("^\\./"; "")] | sort == $expected_names
  ' "$sbom" >/dev/null \
    || { printf 'Release SBOM does not contain the exact primary inventory.\n' >&2; return 1; }

  for name in "${primary_names[@]}"; do
    local path="$directory/$name"
    local sha
    local size
    sha="$(hash_file "$path")"
    size="$(file_size "$path")"
    if [[ "$name" == *.sunderpkg ]]; then
      jq -e --arg name "$name" --arg sha "$sha" --argjson size "$size" '
      [.artifacts[] | select(.path == $name and .sha256 == $sha and .size == $size)] | length == 1
    ' "$provenance" >/dev/null \
      || { printf 'Provenance hash or size differs for %s.\n' "$name" >&2; return 1; }
    fi
    grep -Fqx "$sha  $name" "$sums" \
      || { printf 'SHA256SUMS differs for %s.\n' "$name" >&2; return 1; }
    jq -e --arg name "./$name" --arg sha "$sha" '
      [.files[] | select(.fileName == $name and ([.checksums[] | select(.algorithm == "SHA256" and .checksumValue == $sha)] | length == 1))] | length == 1
    ' "$sbom" >/dev/null \
      || { printf 'Release SBOM checksum differs for %s.\n' "$name" >&2; return 1; }
  done
}

verify_remote_draft() {
  local compare_all_local="$1"
  local directory="$verification_root/assets-$RANDOM-$RANDOM"
  local release_json
  local downloaded_files
  local release_asset_records='[]'
  mkdir "$directory"
  release_json="$(gh release view "$tag" --repo "$GH_REPO" --json databaseId,tagName,isDraft,isPrerelease,body,assets)"
  release_id="$(jq -er '.databaseId | select(type == "number" and floor == . and . > 0)' <<< "$release_json")" \
    || { printf 'GitHub draft does not expose one positive numeric release database id.\n' >&2; return 1; }
  verify_release_sources "$release_json"
  verify_release_inventory "$release_json"
  gh release download "$tag" --repo "$GH_REPO" --dir "$directory"
  downloaded_files=("$directory"/*)
  [[ "${#downloaded_files[@]}" == "${#expected_release_names[@]}" ]] \
    || { printf 'Downloaded draft contains an unexpected file count.\n' >&2; return 1; }
  for name in "${expected_release_names[@]}"; do
    local remote="$directory/$name"
    [[ -f "$remote" ]] || { printf 'Downloaded draft is missing %s.\n' "$name" >&2; return 1; }
    local remote_size
    local remote_sha
    remote_size="$(file_size "$remote")"
    remote_sha="$(hash_file "$remote")"
    [[ "$(jq -r --arg name "$name" '.assets[] | select(.name == $name) | .size' <<< "$release_json")" == "$remote_size" ]] \
      || { printf 'GitHub asset metadata size differs for %s.\n' "$name" >&2; return 1; }
    if [[ "$compare_all_local" == true ]]; then
      [[ "$(hash_file "$asset_directory/$name")" == "$remote_sha" \
        && "$(file_size "$asset_directory/$name")" == "$remote_size" ]] \
        || { printf 'Downloaded GitHub draft asset differs byte-for-byte from publication input: %s\n' "$name" >&2; return 1; }
    fi
    release_asset_records="$(jq -cn \
      --argjson records "$release_asset_records" \
      --arg path "$name" \
      --arg sha256 "$remote_sha" \
      --argjson size "$remote_size" \
      '$records + [{path:$path,sha256:$sha256,size:$size}]')"
  done
  for name in "${primary_names[@]}"; do
    [[ "$(hash_file "$asset_directory/$name")" == "$(hash_file "$directory/$name")" ]] \
      || { printf 'Downloaded GitHub draft primary asset differs from tested bytes: %s\n' "$name" >&2; return 1; }
  done
  [[ "$(jq -r '.body' <<< "$release_json")" == "$(build_release_notes "$directory")" ]] \
    || { printf 'GitHub draft notes do not byte-bind all four downloaded release evidence assets.\n' >&2; return 1; }
  verify_evidence_directory "$directory"
  release_assets_sha256="$(jq -cS 'sort_by(.path)' <<< "$release_asset_records" | hash_stream)"
  evidence_run_attempt="$(jq -er '.invocation.runAttempt | select(test("^[1-9][0-9]*$"))' \
    "$directory/release-provenance.json")" \
    || { printf 'Release provenance does not contain a positive publication run attempt.\n' >&2; return 1; }
}

if gh release view "$tag" --repo "$GH_REPO" >/dev/null 2>&1; then
  verify_remote_draft false
else
  create_args=(release create "$tag" --repo "$GH_REPO" --title "Sunder Agent Family $VERSION" --notes "$release_notes_base" --latest=false --verify-tag --draft)
  [[ "$IS_PRERELEASE" != true ]] || create_args+=(--prerelease)
  gh "${create_args[@]}"
  created_draft=true
  release_identity="$(gh release view "$tag" --repo "$GH_REPO" --json databaseId,tagName,isDraft)"
  release_id="$(jq -er --arg tag "$tag" '
    select(.tagName == $tag and .isDraft == true)
    | .databaseId | select(type == "number" and floor == . and . > 0)
  ' <<< "$release_identity")" \
    || { printf 'Created Agent draft does not expose the expected numeric release id.\n' >&2; exit 1; }
  SUNDER_CORE_TAG="$CORE_TAG" \
  SUNDER_CORE_COMMIT="$CORE_COMMIT" \
  SUNDER_CORE_PACKAGE_VERSION="$CORE_SDK_VERSION" \
  GITHUB_RELEASE_ID="$release_id" \
    bash scripts/generate-release-evidence.sh "$asset_directory" \
      "sunder-agent-package=$GITHUB_SHA" "sunder-core=$CORE_COMMIT"
  verify_evidence_directory "$asset_directory"
  release_notes="$(build_release_notes "$asset_directory")"
  jq -n --arg body "$release_notes" '{body:$body}' > "$verification_root/release-notes.json"
  gh api --method PATCH "repos/$GH_REPO/releases/$release_id" \
    --input "$verification_root/release-notes.json" >/dev/null
  release_assets=("$asset_directory"/*)
  [[ "${#release_assets[@]}" == "${#expected_release_names[@]}" ]] \
    || { printf 'Generated local draft inventory is not exact.\n' >&2; exit 1; }
  gh release upload "$tag" --repo "$GH_REPO" "${release_assets[@]}"
  verify_remote_draft true
fi

external_publication_started=true
while IFS= read -r package_id; do
  expected_publication="$verification_root/$package_id.publication.json"
  jq --arg id "$package_id" '.publications[] | select(.package.packageId == $id)' \
    <<< "$family_plan" > "$expected_publication"
  bash scripts/release/publish-registry-package.sh \
    --initial-yanked "$expected_publication" \
    "$registry_api" "$asset_directory/$package_id.$VERSION.sunderpkg" "$package_id" "$VERSION"
  # Readback verified every downloaded canonical/projection byte. Reconciliation
  # compared the complete committed inventory; no restore may bind/change a lock.
  [[ "$(python3 -B scripts/release/agent_release.py lock-set "$PWD")" == "$SUNDER_AGENT_LOCK_SET_SHA256" ]] \
    || { printf 'Publication changed the approved lock set; retaining all accepted Yanked tuples.\n' >&2; exit 1; }
done < <(jq -r '.order[]' <<< "$family_plan")

verify_remote_draft false
[[ "$release_id" =~ ^[1-9][0-9]*$ && "$evidence_run_attempt" =~ ^[1-9][0-9]*$ \
    && "$release_assets_sha256" =~ ^[0-9a-f]{64}$ ]] \
  || { printf 'Verified publication did not retain exact release coordinates and byte inventory digest.\n' >&2; exit 1; }
if [[ -n "${GITHUB_OUTPUT:-}" ]]; then
  {
    printf 'release_id=%s\n' "$release_id"
    printf 'evidence_run_attempt=%s\n' "$evidence_run_attempt"
    printf 'release_assets_sha256=%s\n' "$release_assets_sha256"
  } >> "$GITHUB_OUTPUT"
fi
printf 'Published and verified immutable Agent %s artifacts; GitHub release %s remains a draft and no Registry dist tag moved.\n' \
  "$VERSION" "$tag"
trap - EXIT
rm -rf "$verification_root"
