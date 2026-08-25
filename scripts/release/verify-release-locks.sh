#!/usr/bin/env bash
set -euo pipefail

if [[ $# -lt 1 || $# -gt 3 ]]; then
  printf 'Usage: verify-release-locks.sh <core-sdk-version> [repository-root] [core-package-feed]\n' >&2
  exit 2
fi

version="$1"
repository_root="${2:-.}"
repository_root="$(cd "$repository_root" && pwd)"
core_package_feed="${3:-}"
[[ "$version" =~ ^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?$ ]] \
  || { printf 'Core SDK version is not valid SemVer: %s\n' "$version" >&2; exit 2; }

for command_name in dotnet jq find python3 sort; do
  command -v "$command_name" >/dev/null 2>&1 \
    || { printf '%s is required to verify release locks.\n' "$command_name" >&2; exit 127; }
done

inventory="$repository_root/packages.json"
[[ -f "$inventory" ]] || { printf 'Package inventory was not found: %s\n' "$inventory" >&2; exit 1; }
primary_count="$(jq -er '[.packages[] | select(.artifactType == "sunderpkg")] | length' "$inventory")"
[[ "$primary_count" == 15 ]] || { printf 'Expected exactly 15 primary package projects, found %s.\n' "$primary_count" >&2; exit 1; }

core_packages='["Sunder.Sdk","Sunder.Sdk.Avalonia","Sunder.Sdk.Stacks","Sunder.Sdk.Worker","Sunder.Package.Build"]'
expected_hashes='{}'
if [[ -n "$core_package_feed" ]]; then
  core_package_feed="$(cd "$core_package_feed" && pwd -P)"
  while IFS= read -r package_id; do
    package_path="$core_package_feed/$package_id.$version.nupkg"
    [[ -f "$package_path" ]] || { printf 'Core candidate package was not found: %s\n' "$package_path" >&2; exit 1; }
    content_hash="$(python3 -c 'import base64, hashlib, pathlib, sys; print(base64.b64encode(hashlib.sha512(pathlib.Path(sys.argv[1]).read_bytes()).digest()).decode("ascii"))' "$package_path")"
    expected_hashes="$(jq -cn \
      --argjson current "$expected_hashes" \
      --arg package_id "$package_id" \
      --arg content_hash "$content_hash" \
      '$current + {($package_id): $content_hash}')"
    printf 'Core package %s contentHash %s\n' "$package_id" "$content_hash"
  done < <(jq -r '.[]' <<< "$core_packages")
fi

expected_locks="$({
  while IFS= read -r project_path; do
    if [[ "$project_path" == *.csproj \
          && ( "$project_path" == src/* || "$project_path" == tests/* ) ]]; then
      printf '%s/%s/packages.release.lock.json\n' "$repository_root" "$(dirname "$project_path")"
    fi
  done < <(dotnet sln "$repository_root/Sunder.AgentPackage.slnx" list)
} | sort)"
actual_locks="$(find "$repository_root/src" "$repository_root/tests" -name packages.release.lock.json -type f -print | sort)"
if [[ "$expected_locks" != "$actual_locks" ]]; then
  printf 'Release lock inventory does not exactly match the solution project graph.\nExpected:\n%s\nActual:\n%s\n' \
    "$expected_locks" "$actual_locks" >&2
  exit 1
fi

lock_count=0
while IFS= read -r lock_path; do
  lock_count=$((lock_count + 1))
  jq -e \
    --arg version "$version" \
    --argjson core_packages "$core_packages" \
    --argjson expected_hashes "$expected_hashes" '
    .version == 2
    and (.dependencies | type == "object" and length > 0)
    and all(.dependencies[];
      . as $framework |
      all($core_packages[];
        . as $package |
        ($framework[$package] == null)
        or ($framework[$package].type != "Project"
            and $framework[$package].resolved == $version
            and ($framework[$package].contentHash | type == "string" and length > 0)
            and (($expected_hashes | length) == 0
                 or $framework[$package].contentHash == $expected_hashes[$package]))))
  ' "$lock_path" >/dev/null || {
    printf 'Release lock does not resolve every referenced Core package to the expected %s bytes: %s\n' "$version" "$lock_path" >&2
    exit 1
  }
done <<< "$actual_locks"

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

if [[ -n "$core_package_feed" ]]; then
  printf 'Verified %s release locks and exactly 15 primary package projects against exact Core %s package bytes in %s.\n' "$lock_count" "$version" "$core_package_feed"
else
  printf 'Verified %s release locks and exactly 15 primary package projects against final Core packages %s.\n' "$lock_count" "$version"
fi
