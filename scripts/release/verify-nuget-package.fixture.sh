#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
verifier="$script_dir/verify-nuget-package.sh"
[[ -f "$verifier" ]] || { printf 'Verifier not found: %s\n' "$verifier" >&2; exit 1; }

for required_command in grep mktemp unzip zip; do
  command -v "$required_command" >/dev/null 2>&1 \
    || { printf '%s is required to run the NuGet verification fixture.\n' "$required_command" >&2; exit 127; }
done

temp_root="${TMPDIR:-/tmp}"
fixture_dir="$(mktemp -d "${temp_root%/}/sunder-agent-nuget-fixture.XXXXXX")"
trap 'rm -rf "$fixture_dir"' EXIT
real_unzip="$(command -v unzip)"
mock_bin="$fixture_dir/mock-bin"
mkdir -p "$mock_bin" "$fixture_dir/expected" "$fixture_dir/published"

cat > "$mock_bin/dotnet" <<'EOF'
#!/usr/bin/env bash
exit 0
EOF
cat > "$mock_bin/unzip" <<'EOF'
#!/usr/bin/env bash
if [[ "${MOCK_UNZIP_EXTRACTION_FAILURE:-false}" == "true" && "${1:-}" == "-p" ]]; then
  exit 11
fi
exec "$REAL_UNZIP" "$@"
EOF
chmod +x "$mock_bin/dotnet" "$mock_bin/unzip"
export REAL_UNZIP="$real_unzip"

printf '<Types />\n' > "$fixture_dir/expected/[Content_Types].xml"
printf '<package />\n' > "$fixture_dir/expected/fixture.nuspec"
cp "$fixture_dir/expected/[Content_Types].xml" "$fixture_dir/published/[Content_Types].xml"
cp "$fixture_dir/expected/fixture.nuspec" "$fixture_dir/published/fixture.nuspec"
(
  cd "$fixture_dir/expected"
  zip -q "$fixture_dir/expected.nupkg" '[Content_Types].xml' fixture.nuspec
)
(
  cd "$fixture_dir/published"
  zip -q "$fixture_dir/published.nupkg" '[Content_Types].xml' fixture.nuspec
)

PATH="$mock_bin:$PATH" bash "$verifier" "$fixture_dir/expected.nupkg" "$fixture_dir/published.nupkg"

printf '<Types changed="true" />\n' > "$fixture_dir/published/[Content_Types].xml"
(
  cd "$fixture_dir/published"
  zip -q -u "$fixture_dir/published.nupkg" '[Content_Types].xml'
)
literal_failure_log="$fixture_dir/literal-member-failure.log"
if PATH="$mock_bin:$PATH" bash "$verifier" "$fixture_dir/expected.nupkg" "$fixture_dir/published.nupkg" > "$literal_failure_log" 2>&1; then
  printf 'Canonical comparison accepted changed [Content_Types].xml bytes.\n' >&2
  exit 1
fi
grep -Fq 'canonical content differs' "$literal_failure_log" \
  || { printf 'Literal ZIP member change did not fail canonical comparison as expected.\n' >&2; exit 1; }

extraction_failure_log="$fixture_dir/extraction-failure.log"
if MOCK_UNZIP_EXTRACTION_FAILURE=true PATH="$mock_bin:$PATH" \
  bash "$verifier" "$fixture_dir/expected.nupkg" "$fixture_dir/expected.nupkg" > "$extraction_failure_log" 2>&1; then
  printf 'NuGet verifier masked an unzip extraction failure.\n' >&2
  exit 1
fi
grep -Fq "Failed to extract NuGet package entry '[Content_Types].xml'" "$extraction_failure_log" \
  || { printf 'NuGet verifier did not report the failed literal member extraction.\n' >&2; exit 1; }

printf 'NuGet verification fixture passed: literal member names are hashed and extraction failures are fatal.\n'
