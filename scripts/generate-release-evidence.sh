#!/usr/bin/env bash
set -euo pipefail

artifact_dir="${1:?Usage: generate-release-evidence.sh <artifact-directory> [name=sha ...]}"
shift
[[ -d "$artifact_dir" ]] || { printf 'Artifact directory not found: %s\n' "$artifact_dir" >&2; exit 1; }

hash_file() { sha256sum "$1" | cut -d ' ' -f 1; }
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
