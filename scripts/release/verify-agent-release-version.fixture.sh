#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
verifier="$script_dir/verify-agent-release-version.sh"
sdk_version=1.1.0

for version in \
  2.0.0 \
  2.0.1 \
  2.0.1-preview.1 \
  2.1.0-rc.2+build.7 \
  2.9.99; do
  bash "$verifier" "$version" "$sdk_version" >/dev/null
done

for version in \
  0.0.0 \
  1.99.99 \
  2.0.0-preview.1 \
  2.0.0-preview.99+build.7 \
  3.0.0; do
  failure_log="$(mktemp "${TMPDIR:-/tmp}/sunder-release-version.XXXXXX")"
  if bash "$verifier" "$version" "$sdk_version" >"$failure_log" 2>&1; then
    printf 'Release version outside the family range was accepted: %s\n' "$version" >&2
    rm -f "$failure_log"
    exit 1
  fi
  grep -Fq "does not satisfy the family dependency range '>=2.0.0 <3.0.0'" "$failure_log"
  rm -f "$failure_log"
done

failure_log="$(mktemp "${TMPDIR:-/tmp}/sunder-release-version.XXXXXX")"
trap 'rm -f "$failure_log"' EXIT
if bash "$verifier" 2.0.01 "$sdk_version" >"$failure_log" 2>&1; then
  printf 'Malformed release SemVer was accepted.\n' >&2
  exit 1
fi
grep -Fq 'is not strict SemVer 2.0' "$failure_log"
