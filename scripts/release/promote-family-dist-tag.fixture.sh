#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
promoter="$script_dir/promote-family-dist-tag.sh"
[[ -f "$promoter" ]] || { printf 'Promotion script not found: %s\n' "$promoter" >&2; exit 1; }

for required_command in grep jq mktemp; do
  command -v "$required_command" >/dev/null 2>&1 \
    || { printf '%s is required to run the dist-tag promotion fixture.\n' "$required_command" >&2; exit 127; }
done

temp_root="${TMPDIR:-/tmp}"
fixture_dir="$(mktemp -d "${temp_root%/}/sunder-agent-promotion-fixture.XXXXXX")"
trap 'rm -rf "$fixture_dir"' EXIT
mock_bin="$fixture_dir/mock-bin"
mkdir -p "$mock_bin"

cat > "$mock_bin/curl" <<'MOCK_CURL'
#!/usr/bin/env bash
set -euo pipefail

method=GET
payload=''
url=''
while [[ $# -gt 0 ]]; do
  case "$1" in
    -X)
      method="$2"
      shift 2
      ;;
    --data)
      payload="$2"
      shift 2
      ;;
    -H)
      shift 2
      ;;
    --fail-with-body|-sS)
      shift
      ;;
    http://*|https://*)
      url="$1"
      shift
      ;;
    *)
      printf 'Mock curl does not support argument: %s\n' "$1" >&2
      exit 2
      ;;
  esac
done

[[ -n "$url" ]] || { printf 'Mock curl did not receive a URL.\n' >&2; exit 2; }
relative="${url#*/api/v1/packages/}"
package_id="${relative%%/*}"
operation="${relative#*/}"
printf '%s\t%s\t%s\n' "$method" "$package_id" "$operation" >> "$MOCK_REGISTRY_CALLS"

if [[ "$method" == "GET" && "$operation" == "dist-tags" ]]; then
  current_target="$(jq -r --arg package_id "$package_id" --arg tag "$MOCK_DIST_TAG" \
    '.packages[$package_id][$tag] // empty' "$MOCK_REGISTRY_STATE")"
  if [[ "${MOCK_FAIL_VERIFY_PACKAGE:-}" == "$package_id" \
    && "$current_target" == "${MOCK_TARGET_VERSION:-}" \
    && ! -e "$MOCK_VERIFY_FAILURE_MARKER" ]]; then
    : > "$MOCK_VERIFY_FAILURE_MARKER"
    exit 22
  fi
  jq -c --arg package_id "$package_id" '
    (.packages[$package_id] // {}) as $tags
    | {
        packageId: $package_id,
        distTags: [$tags | to_entries[] | {tag: .key, version: .value, updatedAtUtc: "2026-01-01T00:00:00Z"}]
      }
  ' "$MOCK_REGISTRY_STATE"
  exit 0
fi

tag="${operation#dist-tags/}"
[[ "$operation" == "dist-tags/$tag" ]] || { printf 'Unsupported mock Registry URL: %s\n' "$url" >&2; exit 2; }
state_temp="${MOCK_REGISTRY_STATE}.tmp"
if [[ "$method" == "PUT" ]]; then
  target_version="$(jq -er '.version' <<< "$payload")"
  jq --arg package_id "$package_id" --arg tag "$tag" --arg version "$target_version" \
    '.packages[$package_id] = (.packages[$package_id] // {}) | .packages[$package_id][$tag] = $version' \
    "$MOCK_REGISTRY_STATE" > "$state_temp"
  mv "$state_temp" "$MOCK_REGISTRY_STATE"
  if [[ "${MOCK_FAIL_UPDATE_AFTER_WRITE_PACKAGE:-}" == "$package_id" \
    && "$target_version" == "${MOCK_TARGET_VERSION:-}" \
    && ! -e "$MOCK_UPDATE_FAILURE_MARKER" ]]; then
    : > "$MOCK_UPDATE_FAILURE_MARKER"
    exit 22
  fi
  printf '{"success":true}\n'
  exit 0
fi

if [[ "$method" == "DELETE" ]]; then
  jq --arg package_id "$package_id" --arg tag "$tag" \
    'del(.packages[$package_id][$tag])' "$MOCK_REGISTRY_STATE" > "$state_temp"
  mv "$state_temp" "$MOCK_REGISTRY_STATE"
  printf '{"success":true}\n'
  exit 0
fi

printf 'Unsupported mock Registry method: %s\n' "$method" >&2
exit 2
MOCK_CURL
chmod +x "$mock_bin/curl"

export MOCK_REGISTRY_STATE="$fixture_dir/registry-state.json"
export MOCK_REGISTRY_CALLS="$fixture_dir/registry-calls.tsv"
export MOCK_UPDATE_FAILURE_MARKER="$fixture_dir/update-failed"
export MOCK_VERIFY_FAILURE_MARKER="$fixture_dir/verify-failed"
export REGISTRY_TOKEN=fixture-token

reset_fixture() {
  printf '%s\n' "$1" > "$MOCK_REGISTRY_STATE"
  : > "$MOCK_REGISTRY_CALLS"
  rm -f "$MOCK_UPDATE_FAILURE_MARKER" "$MOCK_VERIFY_FAILURE_MARKER"
  unset MOCK_FAIL_UPDATE_AFTER_WRITE_PACKAGE MOCK_FAIL_VERIFY_PACKAGE MOCK_TARGET_VERSION
}

run_promotion() {
  PATH="$mock_bin:$PATH" bash "$promoter" 'https://registry.test' "$@"
}

assert_no_mutations() {
  if grep -Eq '^(PUT|DELETE)[[:space:]]' "$MOCK_REGISTRY_CALLS"; then
    printf 'Promotion fixture observed an unexpected Registry mutation.\n' >&2
    exit 1
  fi
}

reset_fixture '{"packages":{"pkg.a":{"latest":"1.0.0"},"pkg.b":{"latest":"1.0.0"}}}'
export MOCK_DIST_TAG=latest
if ! run_promotion latest 1.1.0 pkg.a pkg.b > "$fixture_dir/success.log" 2>&1; then
  printf 'Expected family promotion failed:\n' >&2
  while IFS= read -r line; do printf '%s\n' "$line" >&2; done < "$fixture_dir/success.log"
  exit 1
fi
jq -e '.packages["pkg.a"].latest == "1.1.0" and .packages["pkg.b"].latest == "1.1.0"' \
  "$MOCK_REGISTRY_STATE" >/dev/null

reset_fixture '{"packages":{"pkg.a":{},"pkg.b":{"latest":"1.0.0"},"pkg.c":{"latest":"1.0.0"}}}'
export MOCK_DIST_TAG=latest
export MOCK_TARGET_VERSION=1.1.0
export MOCK_FAIL_UPDATE_AFTER_WRITE_PACKAGE=pkg.b
if run_promotion latest 1.1.0 pkg.a pkg.b pkg.c > "$fixture_dir/update-failure.log" 2>&1; then
  printf 'Promotion unexpectedly accepted an ambiguous update failure.\n' >&2
  exit 1
fi
jq -e '(.packages["pkg.a"].latest == null) and .packages["pkg.b"].latest == "1.0.0" and .packages["pkg.c"].latest == "1.0.0"' \
  "$MOCK_REGISTRY_STATE" >/dev/null
grep -Fq 'rollback restored the complete pre-promotion snapshot' "$fixture_dir/update-failure.log"

reset_fixture '{"packages":{"pkg.a":{"latest":"1.0.0"},"pkg.b":{"latest":"1.0.0"}}}'
export MOCK_DIST_TAG=latest
export MOCK_TARGET_VERSION=1.1.0
export MOCK_FAIL_VERIFY_PACKAGE=pkg.a
if run_promotion latest 1.1.0 pkg.a pkg.b > "$fixture_dir/verification-failure.log" 2>&1; then
  printf 'Promotion unexpectedly accepted a verification failure.\n' >&2
  exit 1
fi
jq -e '.packages["pkg.a"].latest == "1.0.0" and .packages["pkg.b"].latest == "1.0.0"' \
  "$MOCK_REGISTRY_STATE" >/dev/null
grep -Fq 'rollback restored the complete pre-promotion snapshot' "$fixture_dir/verification-failure.log"

reset_fixture '{"packages":{"pkg.a":{"latest":"1.2.0"},"pkg.b":{"latest":"1.0.0"}}}'
export MOCK_DIST_TAG=latest
if run_promotion latest 1.1.0 pkg.a pkg.b > "$fixture_dir/latest-regression.log" 2>&1; then
  printf "Promotion regressed the 'latest' dist tag.\n" >&2
  exit 1
fi
grep -Fq 'Refusing to regress' "$fixture_dir/latest-regression.log"
assert_no_mutations

reset_fixture '{"packages":{"pkg.a":{"preview":"1.2.0-beta.10"},"pkg.b":{"preview":"1.2.0-beta.1"}}}'
export MOCK_DIST_TAG=preview
if run_promotion preview 1.2.0-beta.2 pkg.a pkg.b > "$fixture_dir/preview-regression.log" 2>&1; then
  printf "Promotion regressed the 'preview' dist tag.\n" >&2
  exit 1
fi
grep -Fq 'Refusing to regress' "$fixture_dir/preview-regression.log"
assert_no_mutations

reset_fixture '{"packages":{"pkg.a":{"latest":"1.1.0"},"pkg.b":{"latest":"1.1.0"}}}'
export MOCK_DIST_TAG=latest
run_promotion latest 1.1.0 pkg.a pkg.b > "$fixture_dir/idempotent.log"
grep -Fq '0 changed, 2 already current' "$fixture_dir/idempotent.log"
assert_no_mutations

printf 'Dist-tag promotion fixture passed: success, rollback, monotonicity, and idempotent reruns verified.\n'
