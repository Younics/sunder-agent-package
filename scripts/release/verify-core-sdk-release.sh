#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 2 ]]; then
  printf 'Usage: verify-core-sdk-release.sh <version> <expected-core-commit|resolve>\n' >&2
  exit 2
fi

version="$1"
expected_core_commit="$2"
[[ "$version" =~ ^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?$ ]] \
  || { printf 'Core SDK version is not valid SemVer: %s\n' "$version" >&2; exit 2; }
resolve_commit=false
if [[ "$expected_core_commit" == resolve ]]; then
  resolve_commit=true
elif [[ ! "$expected_core_commit" =~ ^[0-9a-f]{40}$ ]]; then
  printf 'Expected Core commit must be a full lowercase Git SHA or resolve.\n' >&2
  exit 2
fi

for required_command in curl git grep jq tr unzip wc; do
  command -v "$required_command" >/dev/null 2>&1 \
    || { printf '%s is required to verify the Core SDK release.\n' "$required_command" >&2; exit 127; }
done
if ! command -v sha256sum >/dev/null 2>&1 && ! command -v shasum >/dev/null 2>&1; then
  printf 'sha256sum or shasum is required to verify the Core SDK release.\n' >&2
  exit 127
fi

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
nuget_verifier="$script_dir/verify-nuget-package.sh"
[[ -f "$nuget_verifier" ]] || { printf 'NuGet verifier not found: %s\n' "$nuget_verifier" >&2; exit 1; }

github_api="${SUNDER_CORE_GITHUB_API_URL:-https://api.github.com/repos/Younics/sunder-core}"
github_git_url="${SUNDER_CORE_GIT_URL:-https://github.com/Younics/sunder-core.git}"
nuget_flat_container="${NUGET_FLAT_CONTAINER:-https://api.nuget.org/v3-flatcontainer}"
temp_root="${TMPDIR:-/tmp}"
temp_dir="$(mktemp -d "${temp_root%/}/sunder-core-sdk-release.XXXXXX")"
trap 'rm -rf "$temp_dir"' EXIT

hash_file() {
  if command -v sha256sum >/dev/null 2>&1; then
    sha256sum "$1" | cut -d ' ' -f 1
  else
    shasum -a 256 "$1" | cut -d ' ' -f 1
  fi
}

download() {
  local url="$1"
  local output="$2"
  curl --fail --silent --show-error --location --retry 3 --output "$output" "$url"
}

tag="sdk/v$version"
encoded_tag="$(jq -rn --arg value "$tag" '$value | @uri')"
release_json="$temp_dir/release.json"
download "$github_api/releases/tags/$encoded_tag" "$release_json"
jq -e --arg tag "$tag" '
  .tag_name == $tag
  and .draft == false
  and (.assets | type == "array")
' "$release_json" >/dev/null || {
  printf 'Core SDK release %s is missing, draft, or malformed.\n' "$tag" >&2
  exit 1
}

asset_url() {
  local name="$1"
  jq -er --arg name "$name" '
    [.assets[] | select(.name == $name)]
    | if length == 1 and (.[0].browser_download_url | type == "string")
      then .[0].browser_download_url
      else error("release asset must exist exactly once")
      end
  ' "$release_json"
}

provenance="$temp_dir/release-provenance.json"
download "$(asset_url release-provenance.json)" "$provenance"
provenance_core_commit="$(jq -er '
  if .schemaVersion == 1
     and .buildType == "https://sunderapp.io/provenance/github-actions/v1"
     and (.sources | type == "array" and length == 1)
     and (.sources[0].name == "sunder-core")
     and (.sources[0].sha | type == "string" and test("^[0-9a-f]{40}$"))
     and (.artifacts | type == "array")
  then .sources[0].sha
  else error("invalid Core SDK provenance")
  end
' "$provenance")" || {
  printf 'Core SDK release provenance is malformed.\n' >&2
  exit 1
}
if [[ "$resolve_commit" == true ]]; then
  expected_core_commit="$provenance_core_commit"
fi
jq -e --arg core_commit "$expected_core_commit" '
  .schemaVersion == 1
  and .buildType == "https://sunderapp.io/provenance/github-actions/v1"
  and (.sources | type == "array" and length == 1)
  and (.sources[0].name == "sunder-core" and .sources[0].sha == $core_commit)
  and (.artifacts | type == "array")
' "$provenance" >/dev/null || {
  printf 'Core SDK release provenance does not identify exact Core commit %s.\n' "$expected_core_commit" >&2
  exit 1
}

tag_commit=''
tag_object=''
while IFS=$'\t' read -r sha ref; do
  case "$ref" in
    "refs/tags/$tag") tag_object="$sha" ;;
    "refs/tags/$tag^{}") tag_commit="$sha" ;;
    *) printf 'Core SDK tag lookup returned unexpected ref %s.\n' "$ref" >&2; exit 1 ;;
  esac
done < <(git ls-remote --exit-code "$github_git_url" "refs/tags/$tag" "refs/tags/$tag^{}")
[[ -n "$tag_commit" ]] || tag_commit="$tag_object"
[[ "$tag_commit" == "$expected_core_commit" ]] || {
  printf 'Core SDK tag %s does not resolve to provenance commit %s.\n' "$tag" "$expected_core_commit" >&2
  exit 1
}

package_ids=(
  Sunder.Sdk
  Sunder.Sdk.Avalonia
  Sunder.Sdk.Stacks
  Sunder.Sdk.Worker
  Sunder.Package.Build
)
for package_id in "${package_ids[@]}"; do
  package_name="$package_id.$version.nupkg"
  expected_package="$temp_dir/expected-$package_name"
  published_package="$temp_dir/published-$package_name"
  download "$(asset_url "$package_name")" "$expected_package"

  evidence_record="$(jq -er --arg name "$package_name" '
    [.artifacts[] | select(.path == $name)]
    | if length == 1
      and (.[0].sha256 | type == "string" and test("^[0-9a-f]{64}$"))
      and (.[0].size | type == "number" and . >= 0 and floor == .)
      then "\(.[0].sha256)\t\(.[0].size)"
      else error("artifact provenance must be unique and complete")
      end
  ' "$provenance")"
  IFS=$'\t' read -r expected_sha expected_size <<< "$evidence_record"
  actual_sha="$(hash_file "$expected_package" | tr '[:upper:]' '[:lower:]')"
  actual_size="$(wc -c < "$expected_package" | tr -d '[:space:]')"
  [[ "$actual_sha" == "$expected_sha" && "$actual_size" == "$expected_size" ]] || {
    printf 'Core release asset %s does not match Core release provenance.\n' "$package_name" >&2
    exit 1
  }
  nuspec="$(unzip -p "$expected_package" '*.nuspec')"
  grep -Eq "<repository[^>]*commit=\"$expected_core_commit\"" <<< "$nuspec" \
    || { printf 'Core release asset %s does not record provenance commit %s.\n' "$package_name" "$expected_core_commit" >&2; exit 1; }
  grep -Eq '<repository[^>]*url="https://github\.com/Younics/sunder-core(\.git)?"' <<< "$nuspec" \
    || { printf 'Core release asset %s does not record the canonical Core repository.\n' "$package_name" >&2; exit 1; }

  package_id_lower="$(printf '%s' "$package_id" | tr '[:upper:]' '[:lower:]')"
  version_lower="$(printf '%s' "$version" | tr '[:upper:]' '[:lower:]')"
  download \
    "$nuget_flat_container/$package_id_lower/$version_lower/$package_id_lower.$version_lower.nupkg" \
    "$published_package"
  bash "$nuget_verifier" "$expected_package" "$published_package" || {
    printf 'Published Core package %s failed canonical content or repository-signature verification.\n' "$package_name" >&2
    exit 1
  }
done

printf 'Verified Core SDK tag %s, commit provenance, release hashes, canonical NuGet content, and repository signatures.\n' "$tag" >&2
if [[ "$resolve_commit" == true ]]; then
  printf '%s\n' "$expected_core_commit"
fi
