#!/usr/bin/env bash
set -euo pipefail

artifact_dir="${1:?Usage: generate-release-evidence.sh <artifact-directory> [name=sha ...]}"
shift
[[ -d "$artifact_dir" ]] || { printf 'Artifact directory not found: %s\n' "$artifact_dir" >&2; exit 1; }
command -v jq >/dev/null 2>&1 || { printf 'jq is required to generate release evidence.\n' >&2; exit 127; }

core_material='null'
if [[ -n "${SUNDER_CORE_TAG:-}${SUNDER_CORE_COMMIT:-}${SUNDER_CORE_PACKAGE_VERSION:-}" ]]; then
  [[ "${SUNDER_CORE_COMMIT:-}" =~ ^[0-9a-f]{40}$ ]] \
    || { printf 'SUNDER_CORE_COMMIT must be a full lowercase Git SHA.\n' >&2; exit 2; }
  [[ "${SUNDER_CORE_TAG:-}" == "sdk/v${SUNDER_CORE_PACKAGE_VERSION:-}" ]] \
    || { printf 'SUNDER_CORE_TAG must identify SUNDER_CORE_PACKAGE_VERSION.\n' >&2; exit 2; }
  core_material="$(jq -cn \
    --arg tag "$SUNDER_CORE_TAG" \
    --arg sha "$SUNDER_CORE_COMMIT" \
    --arg version "$SUNDER_CORE_PACKAGE_VERSION" \
    '{name: "sunder-core-sdk", tag: $tag, sha: $sha, packageVersion: $version}')"
fi

hash_file() {
  if command -v sha256sum >/dev/null 2>&1; then
    sha256sum "$1" | cut -d ' ' -f 1
  else
    shasum -a 256 "$1" | cut -d ' ' -f 1
  fi
}
artifacts=()
while IFS= read -r -d '' file; do
  case "$(basename "$file")" in
    SHA256SUMS|release-provenance.json|release-sbom.spdx.json|toolchain.txt) ;;
    *) artifacts+=("$file") ;;
  esac
done < <(find "$artifact_dir" -type f -print0 | sort -z)
[[ ${#artifacts[@]} -gt 0 ]] || { printf 'No release artifacts found in %s.\n' "$artifact_dir" >&2; exit 1; }

sources='[]'
source_names=()
for source in "$@"; do
  [[ "$source" =~ ^([A-Za-z0-9][A-Za-z0-9._-]*)=([0-9a-f]{40})$ ]] \
    || { printf 'Source must use name=<lowercase-40-character-Git-SHA>: %s\n' "$source" >&2; exit 2; }
  source_name="${BASH_REMATCH[1]}"
  source_sha="${BASH_REMATCH[2]}"
  for existing in "${source_names[@]-}"; do
    [[ "$existing" != "$source_name" ]] \
      || { printf 'Duplicate release evidence source name: %s\n' "$source_name" >&2; exit 2; }
  done
  source_names+=("$source_name")
  sources="$(jq -cn --argjson sources "$sources" --arg name "$source_name" --arg sha "$source_sha" \
    '$sources + [{name:$name,sha:$sha}]')"
done

artifact_records='[]'
for file in "${artifacts[@]}"; do
  name="${file#"$artifact_dir"/}"
  [[ "$name" =~ ^[A-Za-z0-9][A-Za-z0-9._+-]*$ ]] \
    || { printf 'Release artifact name is not safe for a flat asset manifest: %s\n' "$name" >&2; exit 1; }
  artifact_records="$(jq -cn \
    --argjson artifacts "$artifact_records" \
    --arg path "$name" \
    --arg sha256 "$(hash_file "$file")" \
    --argjson size "$(wc -c < "$file" | tr -d '[:space:]')" \
    '$artifacts + [{path:$path,sha256:$sha256,size:$size}]')"
done

: > "$artifact_dir/SHA256SUMS"
for file in "${artifacts[@]}"; do
  printf '%s  %s\n' "$(hash_file "$file")" "${file#"$artifact_dir"/}" >> "$artifact_dir/SHA256SUMS"
done
{
  printf 'dotnet-version: %s\n' "$(dotnet --version)"
  printf 'dotnet-sdk: %s\n' "$(dotnet --list-sdks | tr '\n' ';')"
  printf 'runner-os: %s\nrunner-arch: %s\nimage-os: %s\n' "${RUNNER_OS:-unknown}" "${RUNNER_ARCH:-unknown}" "${ImageOS:-unknown}"
} > "$artifact_dir/toolchain.txt"

release_id="${GITHUB_RELEASE_ID:-local}"
[[ "$release_id" == local || "$release_id" =~ ^[1-9][0-9]*$ ]] \
  || { printf 'GITHUB_RELEASE_ID must be a positive decimal integer when supplied.\n' >&2; exit 2; }
jq -n \
  --arg repository "${GITHUB_REPOSITORY:-local}" \
  --arg workflow "${GITHUB_WORKFLOW:-local}" \
  --arg workflow_ref "${GITHUB_WORKFLOW_REF:-local}" \
  --arg run_id "${GITHUB_RUN_ID:-local}" \
  --arg run_attempt "${GITHUB_RUN_ATTEMPT:-1}" \
  --arg release_id "$release_id" \
  --arg ref "${GITHUB_REF:-local}" \
  --arg ref_name "${GITHUB_REF_NAME:-local}" \
  --arg sha "${GITHUB_SHA:-local}" \
  --argjson sources "$sources" \
  --argjson core_material "$core_material" \
  --argjson artifacts "$artifact_records" '
    {
      schemaVersion: 1,
      buildType: "https://sunderapp.io/provenance/github-actions/v1",
      invocation: {
        repository: $repository,
        workflow: $workflow,
        workflowRef: $workflow_ref,
        runId: $run_id,
        runAttempt: $run_attempt,
        releaseId: (if $release_id == "local" then "local" else ($release_id | tonumber) end),
        ref: $ref,
        refName: $ref_name,
        sha: $sha
      },
      sources: $sources,
      materials: (if $core_material == null then [] else [$core_material] end),
      artifacts: $artifacts
    }
  ' > "$artifact_dir/release-provenance.json"

if [[ "${GITHUB_WORKFLOW:-}" == 'Sunder Agent Family Release' ]]; then
  script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
  python3 -B "$script_dir/agent_release.py" complete-provenance \
    "${SUNDER_AGENT_RELEASE_SOURCE_DIRECTORY:-$PWD}" "$artifact_dir"
fi

{
  printf '{"spdxVersion":"SPDX-2.3","dataLicense":"CC0-1.0","SPDXID":"SPDXRef-DOCUMENT","name":"Sunder Agent release artifacts","documentNamespace":"https://sunderapp.io/spdx/%s","creationInfo":{"created":"%s","creators":["Tool: sunder-release-evidence-1"]},"files":[' "$(hash_file "$artifact_dir/SHA256SUMS")" "$(date -u +'%Y-%m-%dT%H:%M:%SZ')"
  separator=''
  index=0
  for file in "${artifacts[@]}"; do
    printf '%s{"fileName":"./%s","SPDXID":"SPDXRef-File-%s","checksums":[{"algorithm":"SHA256","checksumValue":"%s"}]}' "$separator" "${file#"$artifact_dir"/}" "$index" "$(hash_file "$file")"
    separator=','
    index=$((index + 1))
  done
  printf ']}\n'
} > "$artifact_dir/release-sbom.spdx.json"
