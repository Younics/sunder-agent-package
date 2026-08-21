#!/usr/bin/env bash
set -euo pipefail

if [[ $# -lt 1 || $# -gt 2 ]]; then
  printf 'Usage: verify-release-locks.sh <core-sdk-version> [repository-root]\n' >&2
  exit 2
fi

version="$1"
repository_root="${2:-.}"
repository_root="$(cd "$repository_root" && pwd)"
[[ "$version" =~ ^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?$ ]] \
  || { printf 'Core SDK version is not valid SemVer: %s\n' "$version" >&2; exit 2; }

for command_name in jq find sort; do
  command -v "$command_name" >/dev/null 2>&1 \
    || { printf '%s is required to verify release locks.\n' "$command_name" >&2; exit 127; }
done

inventory="$repository_root/packages.json"
[[ -f "$inventory" ]] || { printf 'Package inventory was not found: %s\n' "$inventory" >&2; exit 1; }
primary_count="$(jq -er '[.packages[] | select(.artifactType == "sunderpkg")] | length' "$inventory")"
[[ "$primary_count" == 15 ]] || { printf 'Expected exactly 15 primary package projects, found %s.\n' "$primary_count" >&2; exit 1; }

core_packages='["Sunder.Sdk","Sunder.Sdk.Avalonia","Sunder.Sdk.Stacks","Sunder.Sdk.Worker","Sunder.Package.Build"]'
lock_count=0
while IFS= read -r lock_path; do
  lock_count=$((lock_count + 1))
  jq -e --arg version "$version" --argjson core_packages "$core_packages" '
    .version == 2
    and (.dependencies | type == "object" and length > 0)
    and all(.dependencies[];
      . as $framework |
      all($core_packages[];
        . as $package |
        ($framework[$package] == null)
        or ($framework[$package].type != "Project"
            and $framework[$package].resolved == $version
            and ($framework[$package].contentHash | type == "string" and length > 0))))
  ' "$lock_path" >/dev/null || {
    printf 'Release lock does not resolve every referenced Core package to %s: %s\n' "$version" "$lock_path" >&2
    exit 1
  }
done < <(find "$repository_root/src" "$repository_root/tests" -name packages.release.lock.json -type f -print | sort)
[[ "$lock_count" -gt 0 ]] || { printf 'No release locks were found.\n' >&2; exit 1; }

while IFS=$'\t' read -r package_id primary_project; do
  primary_directory="$(dirname "$repository_root/$primary_project")"
  primary_lock="$primary_directory/packages.release.lock.json"
  [[ -f "$primary_lock" ]] || { printf 'Primary package project %s has no release lock: %s\n' "$package_id" "$primary_lock" >&2; exit 1; }
  jq -e --arg version "$version" '
    .dependencies["net10.0"]["Sunder.Sdk"].resolved == $version
    and .dependencies["net10.0"]["Sunder.Sdk.Worker"].resolved == $version
    and .dependencies["net10.0"]["Sunder.Package.Build"].resolved == $version
  ' "$primary_lock" >/dev/null || {
    printf 'Primary package project %s is not locked to the final Core SDK, Worker, and Build packages at %s.\n' "$package_id" "$version" >&2
    exit 1
  }
done < <(jq -r '.packages | map(select(.artifactType == "sunderpkg")) | sort_by(.key)[] | [.packageId, .projectPath] | @tsv' "$inventory")

printf 'Verified %s release locks and exactly 15 primary package projects against final Core packages %s.\n' "$lock_count" "$version"
