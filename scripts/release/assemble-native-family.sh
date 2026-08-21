#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 8 ]]; then
  printf 'Usage: assemble-native-family.sh <native-input-directory> <output-directory> <version> <agent-commit> <core-tag> <core-commit> <core-package-version> <repository-root>\n' >&2
  exit 2
fi

native_input="$(cd "$1" && pwd)"
output_directory="$2"
version="$3"
agent_commit="$4"
core_tag="$5"
core_commit="$6"
core_package_version="$7"
repository_root="$(cd "$8" && pwd)"
mkdir -p "$output_directory"
output_directory="$(cd "$output_directory" && pwd)"
rids=(win-x64 win-arm64 linux-x64 linux-arm64 osx-x64 osx-arm64)

hash_file() {
  sha256sum "$1" | cut -d ' ' -f 1
}

file_size() {
  wc -c < "$1" | tr -d '[:space:]'
}

expected_package_ids="$(jq -c '[.packages[] | select(.artifactType == "sunderpkg") | .packageId] | sort' "$repository_root/packages.json")"
native_runs='[]'
for rid in "${rids[@]}"; do
  evidence="$native_input/native-leaf-evidence-$rid.json"
  bundle="$native_input/native-leaves-$rid.tar.gz"
  [[ -f "$evidence" && -f "$bundle" ]] \
    || { printf 'Native fan-in input is incomplete for %s.\n' "$rid" >&2; exit 1; }
  jq -e \
    --arg rid "$rid" \
    --arg version "$version" \
    --arg agent_commit "$agent_commit" \
    --arg core_tag "$core_tag" \
    --arg core_commit "$core_commit" \
    --arg core_package_version "$core_package_version" \
    --argjson expected_packages "$expected_package_ids" '
      .schemaVersion == 1
      and .rid == $rid
      and .version == $version
      and .agentCommit == $agent_commit
      and .core == {tag: $core_tag, commit: $core_commit, packageVersion: $core_package_version}
      and (.packages | length == 15)
      and ([.packages[].packageId] | sort) == $expected_packages
      and all(.packages[];
        .metrics.measurement.rid == $rid
        and .metrics.measurement.workerProcessCount == 1
        and .metrics.measurement.currentRidInstalledBytes > 0
        and .metrics.measurement.startupMilliseconds > 0
        and .metrics.measurement.workerRssBytes > 0)
    ' "$evidence" >/dev/null || {
      printf 'Native evidence identity or inventory is invalid for %s.\n' "$rid" >&2
      exit 1
    }
  [[ "$(jq -r '.bundle.path' "$evidence")" == "$(basename "$bundle")" \
     && "$(jq -r '.bundle.sha256' "$evidence")" == "$(hash_file "$bundle")" \
     && "$(jq -r '.bundle.size' "$evidence")" == "$(file_size "$bundle")" ]] \
    || { printf 'Native leaf bundle differs from evidence for %s.\n' "$rid" >&2; exit 1; }

  while IFS= read -r entry; do
    [[ -n "$entry" && "$entry" != /* && "$entry" != ../* && "$entry" != *'/../'* ]] \
      || { printf 'Unsafe native leaf bundle entry for %s: %s\n' "$rid" "$entry" >&2; exit 1; }
  done < <(tar -tzf "$bundle")
  tar -xzf "$bundle" -C "$repository_root"

  while IFS=$'\t' read -r leaf_path relative_path expected_sha expected_size; do
    file_path="$repository_root/$leaf_path/$relative_path"
    [[ -f "$file_path" \
       && "$(hash_file "$file_path")" == "$expected_sha" \
       && "$(file_size "$file_path")" == "$expected_size" ]] \
      || { printf 'Extracted native leaf file differs from evidence: %s\n' "$file_path" >&2; exit 1; }
  done < <(jq -r '.packages[] | .leaf.path as $leaf | .leaf.files[] | [$leaf, .path, .sha256, (.size | tostring)] | @tsv' "$evidence")
  while IFS=$'\t' read -r marker_path marker_sha; do
    marker="$repository_root/$marker_path"
    [[ -f "$marker" && "$(hash_file "$marker")" == "$marker_sha" ]] \
      || { printf 'Extracted native leaf marker differs from evidence: %s\n' "$marker" >&2; exit 1; }
  done < <(jq -r '.packages[] | [.leaf.markerPath, .leaf.markerSha256] | @tsv' "$evidence")
  native_runs="$(jq -cn --argjson runs "$native_runs" --slurpfile run "$evidence" '$runs + [$run[0]]')"
done

common_properties=(
  -p:SunderUseSourceSdk=false
  -p:SunderAgentVersion="$version"
  -p:ContinuousIntegrationBuild=true
  -p:RestoreLockedMode=true
)
mapfile -t app_projects < <(jq -r '.packages | map(select(.artifactType == "sunderpkg" and .appProjectPath != null)) | sort_by(.key)[] | .appProjectPath' "$repository_root/packages.json")
for app_project in "${app_projects[@]}"; do
  app_project="$repository_root/$app_project"
  dotnet restore "$app_project" --locked-mode --source "$NUGET_SOURCE" -p:SunderUseSourceSdk=false -p:RestoreLockedMode=true
  dotnet build "$app_project" -c Release --no-restore -m:1 "${common_properties[@]}"
done

protocol_project="$repository_root/src/Sunder.Package.Agent.Contracts/Sunder.Package.Agent.Contracts.csproj"
dotnet restore "$protocol_project" --locked-mode --source "$NUGET_SOURCE" -p:SunderUseSourceSdk=false -p:RestoreLockedMode=true
dotnet build "$protocol_project" -c Release --no-restore -m:1 "${common_properties[@]}"
dotnet pack "$protocol_project" -c Release --no-build --no-restore -o "$output_directory" "${common_properties[@]}"

while IFS=$'\t' read -r package_id project_relative; do
  project="$repository_root/$project_relative"
  archive="$output_directory/$package_id.$version.sunderpkg"
  dotnet restore "$project" --locked-mode --source "$NUGET_SOURCE" -p:SunderUseSourceSdk=false -p:RestoreLockedMode=true
  dotnet msbuild "$project" -t:PackSunderPackage -m:1 \
    -p:Configuration=Release \
    -p:BuildProjectReferences=false \
    -p:SunderPackageUsePrebuiltLeaves=true \
    -p:SunderUseSourceSdk=false \
    -p:SunderAgentVersion="$version" \
    -p:ContinuousIntegrationBuild=true \
    -p:RestoreLockedMode=true \
    -p:SunderPackageOutputPath="$archive"
done < <(jq -r '.packages | map(select(.artifactType == "sunderpkg")) | sort_by(.key)[] | [.packageId, .projectPath] | @tsv' "$repository_root/packages.json")

runtime_count="$(find "$output_directory" -maxdepth 1 -name '*.sunderpkg' -type f | wc -l | tr -d '[:space:]')"
[[ "$runtime_count" == 15 ]] || { printf 'Expected 15 immutable family archives, found %s.\n' "$runtime_count" >&2; exit 1; }
protocol="$output_directory/Sunder.Package.Agent.Protocol.$version.nupkg"
[[ -f "$protocol" ]] || { printf 'Agent Protocol archive is missing after native fan-in.\n' >&2; exit 1; }

artifacts='[]'
while IFS= read -r artifact; do
  artifact_json="$(jq -n \
    --arg path "$(basename "$artifact")" \
    --arg sha256 "$(hash_file "$artifact")" \
    --argjson size "$(file_size "$artifact")" \
    '{path: $path, sha256: $sha256, size: $size}')"
  artifacts="$(jq -cn --argjson artifacts "$artifacts" --argjson artifact "$artifact_json" '$artifacts + [$artifact]')"
done < <(find "$output_directory" -maxdepth 1 -type f \( -name '*.sunderpkg' -o -name '*.nupkg' \) -print | sort)
[[ "$(jq 'length' <<< "$artifacts")" == 16 ]] || { printf 'Native fan-in artifact evidence is incomplete.\n' >&2; exit 1; }

jq -n \
  --arg version "$version" \
  --arg agentCommit "$agent_commit" \
  --arg coreTag "$core_tag" \
  --arg coreCommit "$core_commit" \
  --arg corePackageVersion "$core_package_version" \
  --argjson nativeRuns "$native_runs" \
  --argjson artifacts "$artifacts" '
    {
      schemaVersion: 1,
      version: $version,
      agentCommit: $agentCommit,
      core: {tag: $coreTag, commit: $coreCommit, packageVersion: $corePackageVersion},
      nativeRuns: $nativeRuns,
      finalArtifacts: $artifacts
    }
  ' > "$output_directory/native-test-evidence.json"

printf 'Composed exact signed leaves from six native jobs into 15 immutable archives without rebuilding them.\n'
