#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
verifier="$script_dir/verify-core-sdk-release.sh"
[[ -f "$verifier" ]] || { printf 'Core SDK release verifier not found: %s\n' "$verifier" >&2; exit 1; }

for required_command in grep jq mktemp unzip zip; do
  command -v "$required_command" >/dev/null 2>&1 \
    || { printf '%s is required to run the Core SDK release fixture.\n' "$required_command" >&2; exit 127; }
done
if ! command -v sha256sum >/dev/null 2>&1 && ! command -v shasum >/dev/null 2>&1; then
  printf 'sha256sum or shasum is required to run the Core SDK release fixture.\n' >&2
  exit 127
fi

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

temp_root="${TMPDIR:-/tmp}"
fixture_dir="$(mktemp -d "${temp_root%/}/sunder-core-sdk-release-fixture.XXXXXX")"
trap 'rm -rf "$fixture_dir"' EXIT
assets="$fixture_dir/assets"
published="$fixture_dir/published"
package_root="$fixture_dir/package"
mock_bin="$fixture_dir/mock-bin"
mkdir -p "$assets" "$published" "$package_root" "$mock_bin"

version=1.1.0
core_commit=1111111111111111111111111111111111111111
package_ids=(
  Sunder.Sdk
  Sunder.Sdk.Avalonia
  Sunder.Sdk.Stacks
  Sunder.Sdk.Worker
  Sunder.Package.Build
)
artifacts='[]'
release_assets='[{"name":"release-provenance.json","browser_download_url":"https://fixture.invalid/assets/release-provenance.json"}]'
for package_id in "${package_ids[@]}"; do
  package_name="$package_id.$version.nupkg"
  printf '<Types />\n' > "$package_root/[Content_Types].xml"
  printf '<package><metadata><id>%s</id><repository type="git" url="https://github.com/Younics/sunder-core.git" commit="%s" /></metadata></package>\n' \
    "$package_id" "$core_commit" > "$package_root/package.nuspec"
  (
    cd "$package_root"
    zip -q "$assets/$package_name" '[Content_Types].xml' package.nuspec
  )
  package_id_lower="$(printf '%s' "$package_id" | tr '[:upper:]' '[:lower:]')"
  cp "$assets/$package_name" "$published/$package_id_lower.$version.nupkg"
  artifacts="$(jq -cn \
    --argjson artifacts "$artifacts" \
    --arg path "$package_name" \
    --arg sha256 "$(hash_file "$assets/$package_name")" \
    --argjson size "$(file_size "$assets/$package_name")" \
    '$artifacts + [{path: $path, sha256: $sha256, size: $size}]')"
  release_assets="$(jq -cn \
    --argjson assets "$release_assets" \
    --arg name "$package_name" \
    --arg url "https://fixture.invalid/assets/$package_name" \
    '$assets + [{name: $name, browser_download_url: $url}]')"
done

jq -n \
  --arg core_commit "$core_commit" \
  --argjson artifacts "$artifacts" '
  {
    schemaVersion: 1,
    buildType: "https://sunderapp.io/provenance/github-actions/v1",
    sources: [{name: "sunder-core", sha: $core_commit}],
    artifacts: $artifacts
  }
' > "$assets/release-provenance.json"
cp "$assets/release-provenance.json" "$fixture_dir/release-provenance.original.json"
jq -n --arg tag "sdk/v$version" --argjson assets "$release_assets" \
  '{tag_name: $tag, draft: false, assets: $assets}' > "$fixture_dir/release.json"

cat > "$mock_bin/curl" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
output=''
url=''
while [[ $# -gt 0 ]]; do
  case "$1" in
    --output|-o)
      output="$2"
      shift 2
      ;;
    --retry|--header|-H|--write-out)
      shift 2
      ;;
    --fail|--silent|--show-error|--location|--retry-all-errors)
      shift
      ;;
    *)
      url="$1"
      shift
      ;;
  esac
done
[[ -n "$output" && -n "$url" ]] || exit 2
case "$url" in
  */releases/tags/*)
    source_path="$MOCK_ROOT/release.json"
    ;;
  https://fixture.invalid/assets/*)
    source_path="$MOCK_ROOT/assets/${url##*/}"
    ;;
  */v3-flatcontainer/*)
    source_path="$MOCK_ROOT/published/${url##*/}"
    ;;
  *)
    exit 22
    ;;
esac
cp "$source_path" "$output"
EOF
cat > "$mock_bin/dotnet" <<'EOF'
#!/usr/bin/env bash
exit 0
EOF
cat > "$mock_bin/git" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
[[ "$1" == ls-remote ]] || exit 2
printf '%s\trefs/tags/sdk/v%s\n' "${MOCK_CORE_TAG_COMMIT:?}" "${MOCK_CORE_VERSION:?}"
EOF
chmod +x "$mock_bin/curl" "$mock_bin/dotnet" "$mock_bin/git"

run_verifier() {
  MOCK_ROOT="$fixture_dir" \
  PATH="$mock_bin:$PATH" \
  MOCK_CORE_TAG_COMMIT="${MOCK_CORE_TAG_COMMIT:-$core_commit}" \
  MOCK_CORE_VERSION="$version" \
  SUNDER_CORE_GITHUB_API_URL="https://fixture.invalid/api" \
  SUNDER_CORE_GIT_URL="https://fixture.invalid/core.git" \
  NUGET_FLAT_CONTAINER="https://fixture.invalid/v3-flatcontainer" \
    bash "$verifier" "$version" "$core_commit"
}

run_verifier
resolved="$(
  MOCK_ROOT="$fixture_dir" \
  PATH="$mock_bin:$PATH" \
  MOCK_CORE_TAG_COMMIT="$core_commit" \
  MOCK_CORE_VERSION="$version" \
  SUNDER_CORE_GITHUB_API_URL="https://fixture.invalid/api" \
  SUNDER_CORE_GIT_URL="https://fixture.invalid/core.git" \
  NUGET_FLAT_CONTAINER="https://fixture.invalid/v3-flatcontainer" \
    bash "$verifier" "$version" resolve
)"
[[ "$resolved" == "$core_commit" ]] \
  || { printf 'Core SDK verifier did not resolve the provenance commit.\n' >&2; exit 1; }

tag_failure_log="$fixture_dir/tag-failure.log"
if MOCK_CORE_TAG_COMMIT=3333333333333333333333333333333333333333 run_verifier > "$tag_failure_log" 2>&1; then
  printf 'Core SDK verifier accepted a tag outside the provenance commit.\n' >&2
  exit 1
fi
grep -Fq 'does not resolve to provenance commit' "$tag_failure_log" \
  || { printf 'Core SDK verifier did not report tag provenance failure.\n' >&2; exit 1; }

jq '.sources[0].sha = "2222222222222222222222222222222222222222"' \
  "$fixture_dir/release-provenance.original.json" > "$assets/release-provenance.json"
source_failure_log="$fixture_dir/source-failure.log"
if run_verifier > "$source_failure_log" 2>&1; then
  printf 'Core SDK verifier accepted mismatched source provenance.\n' >&2
  exit 1
fi
grep -Fq 'does not identify exact Core commit' "$source_failure_log" \
  || { printf 'Core SDK verifier did not report source provenance failure.\n' >&2; exit 1; }

cp "$fixture_dir/release-provenance.original.json" "$assets/release-provenance.json"
printf 'tampered' >> "$assets/Sunder.Sdk.$version.nupkg"
hash_failure_log="$fixture_dir/hash-failure.log"
if run_verifier > "$hash_failure_log" 2>&1; then
  printf 'Core SDK verifier accepted an asset that differed from release provenance.\n' >&2
  exit 1
fi
grep -Fq 'does not match Core release provenance' "$hash_failure_log" \
  || { printf 'Core SDK verifier did not report release asset hash failure.\n' >&2; exit 1; }

printf 'Core SDK release fixture passed: exact tag/source provenance, release hashes, and published package verification are enforced.\n'
