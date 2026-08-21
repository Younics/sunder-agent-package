#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 10 ]]; then
  printf 'Usage: record-native-leaf-evidence.sh <rid> <version> <agent-commit> <core-tag> <core-commit> <core-package-version> <archive-directory> <metrics-directory> <output-directory> <repository-root>\n' >&2
  exit 2
fi

rid="$1"
version="$2"
agent_commit="$3"
core_tag="$4"
core_commit="$5"
core_package_version="$6"
archive_directory="$(cd "$7" && pwd)"
metrics_directory="$(cd "$8" && pwd)"
output_directory="$9"
repository_root="$(cd "${10}" && pwd)"

case "$rid" in
  win-x64|win-arm64|linux-x64|linux-arm64|osx-x64|osx-arm64) ;;
  *) printf 'Unsupported release RID: %s\n' "$rid" >&2; exit 2 ;;
esac
[[ "$agent_commit" =~ ^[0-9a-f]{40}$ && "$core_commit" =~ ^[0-9a-f]{40}$ ]] \
  || { printf 'Agent and Core commits must be full lowercase Git SHAs.\n' >&2; exit 2; }
[[ "$core_tag" == "sdk/v$core_package_version" ]] \
  || { printf 'Core tag and package version do not agree.\n' >&2; exit 2; }
mkdir -p "$output_directory" "$output_directory/metrics"
output_directory="$(cd "$output_directory" && pwd)"

hash_file() {
  if command -v sha256sum >/dev/null 2>&1; then
    sha256sum "$1" | cut -d ' ' -f 1
  else
    shasum -a 256 "$1" | cut -d ' ' -f 1
  fi
}

file_size() {
  wc -c < "$1" | tr -d '[:space:]'
}

relative_to_repository() {
  local path="$1"
  printf '%s\n' "${path#"$repository_root"/}"
}

packages_json='[]'
tar_list="$output_directory/native-leaves-$rid.list"
: > "$tar_list"
while IFS=$'\t' read -r package_id worker_project; do
  worker_directory="$(dirname "$repository_root/$worker_project")"
  leaf="$worker_directory/bin/Release/net10.0/$rid/sunder-dev"
  marker="$leaf.sunder-generated-output"
  manifest="$leaf/manifest/sunder-package.json"
  content_index="$leaf/manifest/content-index.json"
  archive="$archive_directory/$package_id.$version.sunderpkg"
  metric="$metrics_directory/$package_id.$rid.json"
  for required_path in "$leaf" "$marker" "$manifest" "$content_index" "$archive" "$metric"; do
    [[ -e "$required_path" ]] || { printf 'Native evidence input is missing: %s\n' "$required_path" >&2; exit 1; }
  done

  jq -e --arg package_id "$package_id" --arg rid "$rid" --arg version "$version" '
    .id == $package_id and .version == $version
    and ([.targets[] | select(.role == "runtime") | .rid] == [$rid])
    and all(.targets[] | select(.role == "runtime"); .kind == "worker")
  ' "$manifest" >/dev/null
  entry_point="$(jq -er --arg rid "$rid" '.targets[] | select(.role == "runtime" and .rid == $rid) | .entryPoint' "$manifest")"
  apphost="$leaf/payload/runtime/$rid/$entry_point"
  [[ -f "$apphost" ]] || { printf 'Native worker apphost is missing: %s\n' "$apphost" >&2; exit 1; }

  metric_copy="$output_directory/metrics/$package_id.$rid.json"
  cp "$metric" "$metric_copy"
  jq -e --arg archive "$(basename "$archive")" --arg rid "$rid" '
    .measurement.formatVersion == 1
    and .measurement.profile == "dotnetWorker"
    and .measurement.rid == $rid
    and .measurement.archive == $archive
    and .measurement.currentRidInstalledBytes > 0
    and .measurement.startupMilliseconds > 0
    and .measurement.workerProcessCount == 1
    and .measurement.workerRssBytes > 0
    and .thresholds.expectedWorkerProcessCount == 1
  ' "$metric_copy" >/dev/null || {
    printf 'Core metric evidence is invalid for %s/%s.\n' "$package_id" "$rid" >&2
    exit 1
  }

  files_tsv="$output_directory/$package_id.$rid.files.tsv"
  : > "$files_tsv"
  while IFS= read -r file_path; do
    relative_path="${file_path#"$leaf"/}"
    printf '%s\t%s\t%s\n' "$relative_path" "$(hash_file "$file_path")" "$(file_size "$file_path")" >> "$files_tsv"
  done < <(find "$leaf" -type f -print | LC_ALL=C sort)
  files_json="$(jq -Rn '
    [inputs | split("\t") | {path: .[0], sha256: .[1], size: (.[2] | tonumber)}]
  ' < "$files_tsv")"
  rm -f "$files_tsv"

  leaf_relative="$(relative_to_repository "$leaf")"
  marker_relative="$(relative_to_repository "$marker")"
  printf '%s\n%s\n' "$leaf_relative" "$marker_relative" >> "$tar_list"
  signature_kind=none
  [[ "$rid" != win-* ]] || signature_kind=authenticode
  [[ "$rid" != osx-* ]] || signature_kind=apple
  package_json="$(jq -n \
    --arg packageId "$package_id" \
    --arg workerProject "$worker_project" \
    --arg leafPath "$leaf_relative" \
    --arg markerPath "$marker_relative" \
    --arg markerSha256 "$(hash_file "$marker")" \
    --arg appHostPath "payload/runtime/$rid/$entry_point" \
    --arg appHostSha256 "$(hash_file "$apphost")" \
    --arg signatureKind "$signature_kind" \
    --arg archive "$(basename "$archive")" \
    --arg archiveSha256 "$(hash_file "$archive")" \
    --argjson archiveSize "$(file_size "$archive")" \
    --arg metricSha256 "$(hash_file "$metric_copy")" \
    --argjson files "$files_json" \
    --slurpfile metrics "$metric_copy" '
      {
        packageId: $packageId,
        workerProject: $workerProject,
        leaf: {
          path: $leafPath,
          markerPath: $markerPath,
          markerSha256: $markerSha256,
          files: $files,
          appHostPath: $appHostPath,
          appHostSha256: $appHostSha256,
          signatureKind: $signatureKind
        },
        testedArchive: {path: $archive, sha256: $archiveSha256, size: $archiveSize},
        metricsSha256: $metricSha256,
        metrics: $metrics[0]
      }
    ')"
  packages_json="$(jq -cn --argjson packages "$packages_json" --argjson package "$package_json" '$packages + [$package]')"
done < <(jq -r '.packages | map(select(.artifactType == "sunderpkg")) | sort_by(.key)[] | [.packageId, .projectPath] | @tsv' "$repository_root/packages.json")

[[ "$(jq 'length' <<< "$packages_json")" == 15 ]] \
  || { printf 'Native evidence must contain exactly 15 worker leaves.\n' >&2; exit 1; }
bundle="$output_directory/native-leaves-$rid.tar.gz"
tar -czf "$bundle" -C "$repository_root" -T "$tar_list"
rm -f "$tar_list"
evidence="$output_directory/native-leaf-evidence-$rid.json"
jq -n \
  --arg rid "$rid" \
  --arg version "$version" \
  --arg agentCommit "$agent_commit" \
  --arg coreTag "$core_tag" \
  --arg coreCommit "$core_commit" \
  --arg corePackageVersion "$core_package_version" \
  --arg bundle "$(basename "$bundle")" \
  --arg bundleSha256 "$(hash_file "$bundle")" \
  --argjson bundleSize "$(file_size "$bundle")" \
  --argjson packages "$packages_json" '
  {
    schemaVersion: 1,
    rid: $rid,
    version: $version,
    agentCommit: $agentCommit,
    core: {tag: $coreTag, commit: $coreCommit, packageVersion: $corePackageVersion},
    bundle: {path: $bundle, sha256: $bundleSha256, size: $bundleSize},
    packages: $packages
  }
  ' > "$evidence"

printf 'Recorded signed native leaf and Core metric evidence for exactly 15 %s workers.\n' "$rid"
