#!/usr/bin/env bash
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repository_root"

allowed_categories='["Unit","Contract","Conformance","Component","UI","Artifact","System","Release","Stress"]'
allowed_capabilities='["None","AvaloniaHeadless","Docker","Filesystem","HTTP","Native","PackageArchive","Process","SQLite","Worker"]'
allowed_lanes='["PackageCompatibility"]'
allowed_os='["Any","Linux","macOS","Windows"]'
allowed_sizes='["Small","Medium","Large"]'
package_ids="$(jq -c '[.packages[] | select(.artifactType == "sunderpkg") | .packageId]' packages.json)"
projects='[]'

while IFS= read -r project_path; do
  case "$project_path" in
    tests/*.Tests/*.csproj) ;;
    *) continue ;;
  esac

  metadata="$(dotnet msbuild "$project_path" -nologo \
    -getProperty:IsTestProject,SunderTestOwner,SunderTestCategory,SunderTestCapabilities,SunderTestOS,SunderTestSize,SunderTestSourceMode,SunderTestPackageMode,SunderTestLanes,SunderTestSourceOnlyTests)"
  is_test_project="$(jq -er '.Properties.IsTestProject' <<< "$metadata")"
  owner="$(jq -er '.Properties.SunderTestOwner | select(length > 0)' <<< "$metadata")"
  category="$(jq -er '.Properties.SunderTestCategory | select(length > 0)' <<< "$metadata")"
  capabilities_value="$(jq -er '.Properties.SunderTestCapabilities | select(length > 0)' <<< "$metadata")"
  os_value="$(jq -er '.Properties.SunderTestOS | select(length > 0)' <<< "$metadata")"
  size="$(jq -er '.Properties.SunderTestSize | select(length > 0)' <<< "$metadata")"
  source_mode="$(jq -er '.Properties.SunderTestSourceMode' <<< "$metadata")"
  package_mode="$(jq -er '.Properties.SunderTestPackageMode' <<< "$metadata")"
  lanes_value="$(jq -er '.Properties.SunderTestLanes' <<< "$metadata")"
  source_only_value="$(jq -er '.Properties.SunderTestSourceOnlyTests' <<< "$metadata")"

  [[ "$is_test_project" == "true" ]]
  jq -en --arg owner "$owner" --argjson package_ids "$package_ids" \
    '$owner == "agent-family" or ($package_ids | index($owner) != null)' >/dev/null
  jq -en --arg value "$category" --argjson allowed "$allowed_categories" \
    '$allowed | index($value) != null' >/dev/null
  jq -en --arg value "$size" --argjson allowed "$allowed_sizes" \
    '$allowed | index($value) != null' >/dev/null
  [[ "$source_mode" == "true" || "$source_mode" == "false" ]]
  [[ "$package_mode" == "true" || "$package_mode" == "false" ]]
  [[ "$source_mode" == "true" || "$package_mode" == "true" ]]

  capabilities="$(jq -cn --arg value "$capabilities_value" '$value | split(";") | map(select(length > 0)) | unique')"
  lanes="$(jq -cn --arg value "$lanes_value" '$value | split(";") | map(select(length > 0)) | unique')"
  operating_systems="$(jq -cn --arg value "$os_value" '$value | split(";") | map(select(length > 0)) | unique')"
  source_only_tests="$(jq -cn --arg value "$source_only_value" '$value | split(";") | map(select(length > 0)) | unique')"
  jq -en --argjson values "$capabilities" --argjson allowed "$allowed_capabilities" \
    '($values - $allowed) | length == 0' >/dev/null
  jq -en --argjson values "$lanes" --argjson allowed "$allowed_lanes" \
    '($values - $allowed) | length == 0' >/dev/null
  jq -en --argjson lanes "$lanes" --argjson package "$package_mode" \
    '($lanes | length) == 0 or $package' >/dev/null
  jq -en --argjson values "$operating_systems" --argjson allowed "$allowed_os" \
    '($values - $allowed) | length == 0' >/dev/null

  modes="$(jq -cn \
    --argjson source "$source_mode" \
    --argjson package "$package_mode" \
    '[if $source then "source" else empty end, if $package then "package" else empty end]')"
  entry="$(jq -cn \
    --arg path "$project_path" \
    --arg owner "$owner" \
    --arg category "$category" \
    --argjson capabilities "$capabilities" \
    --argjson lanes "$lanes" \
    --argjson operatingSystems "$operating_systems" \
    --arg size "$size" \
    --argjson modes "$modes" \
    --argjson sourceOnlyTests "$source_only_tests" \
    '{path: $path, owner: $owner, category: $category, capabilities: $capabilities, operatingSystems: $operatingSystems, size: $size, modes: $modes, lanes: $lanes, sourceOnlyTests: $sourceOnlyTests}')"
  projects="$(jq -cn --argjson projects "$projects" --argjson entry "$entry" '$projects + [$entry]')"
done < <(dotnet sln Sunder.AgentPackage.slnx list)

jq -n \
  --argjson schemaVersion 2 \
  --arg solution "Sunder.AgentPackage.slnx" \
  --argjson projects "$projects" \
  '{schemaVersion: $schemaVersion, solution: $solution, projects: $projects}'
