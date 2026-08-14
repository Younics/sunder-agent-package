#!/usr/bin/env bash
set -euo pipefail

artifact_dir="${1:?Usage: generate-release-evidence.sh <artifact-directory> [name=sha ...]}"
shift
[[ -d "$artifact_dir" ]] || { printf 'Artifact directory not found: %s\n' "$artifact_dir" >&2; exit 1; }

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
mapfile -d '' -t artifacts < <(find "$artifact_dir" -type f -print0 | sort -z)
[[ ${#artifacts[@]} -gt 0 ]] || { printf 'No release artifacts found in %s.\n' "$artifact_dir" >&2; exit 1; }

: > "$artifact_dir/SHA256SUMS"
for file in "${artifacts[@]}"; do
  printf '%s  %s\n' "$(hash_file "$file")" "${file#"$artifact_dir"/}" >> "$artifact_dir/SHA256SUMS"
done
{
  printf 'dotnet-version: %s\n' "$(dotnet --version)"
  printf 'dotnet-sdk: %s\n' "$(dotnet --list-sdks | tr '\n' ';')"
  printf 'runner-os: %s\nrunner-arch: %s\nimage-os: %s\n' "${RUNNER_OS:-unknown}" "${RUNNER_ARCH:-unknown}" "${ImageOS:-unknown}"
} > "$artifact_dir/toolchain.txt"

{
  printf '{"schemaVersion":1,"buildType":"https://sunderapp.io/provenance/github-actions/v1","invocation":{"workflow":"%s","runId":"%s","runAttempt":"%s"},"sources":[' "${GITHUB_WORKFLOW:-local}" "${GITHUB_RUN_ID:-local}" "${GITHUB_RUN_ATTEMPT:-1}"
  separator=''
  for source in "$@"; do
    printf '%s{"name":"%s","sha":"%s"}' "$separator" "${source%%=*}" "${source#*=}"
    separator=','
  done
  printf '],"materials":['
  if [[ "$core_material" != null ]]; then
    printf '%s' "$core_material"
  fi
  printf '],"artifacts":['
  separator=''
  for file in "${artifacts[@]}"; do
    printf '%s{"path":"%s","sha256":"%s","size":%s}' "$separator" "${file#"$artifact_dir"/}" "$(hash_file "$file")" "$(wc -c < "$file" | tr -d ' ')"
    separator=','
  done
  printf ']}\n'
} > "$artifact_dir/release-provenance.json"

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
