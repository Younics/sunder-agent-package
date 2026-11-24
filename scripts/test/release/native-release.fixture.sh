#!/usr/bin/env bash
set -euo pipefail

fixture_scripts="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
script_dir="$(cd "$fixture_scripts/../../release" && pwd)"
repository_root="$(cd "$script_dir/../.." && pwd)"
lock_verifier="$script_dir/verify-release-locks.sh"
bash "$lock_verifier" "$repository_root"

temp_root="${TMPDIR:-/tmp}"
fixture_root="$(mktemp -d "${temp_root%/}/sunder-native-release-fixture.XXXXXX")"
trap 'rm -rf "$fixture_root"' EXIT
cp "$repository_root/packages.json" "$fixture_root/packages.json"
cp "$repository_root/Sunder.AgentPackage.slnx" "$fixture_root/Sunder.AgentPackage.slnx"
cp "$repository_root/Directory.Packages.props" "$fixture_root/Directory.Packages.props"
mkdir -p "$fixture_root/compatibility"
cp "$repository_root/compatibility/agent-current-inventory.json" "$fixture_root/compatibility/agent-current-inventory.json"
find "$repository_root/src" "$repository_root/tests" \
  -type d \( -name bin -o -name obj \) -prune -o \
  -type f -name packages.release.lock.json -print > "$fixture_root/release-locks.txt"
while IFS= read -r lock_path; do
  relative_path="${lock_path#"$repository_root"/}"
  mkdir -p "$fixture_root/$(dirname "$relative_path")"
  cp "$lock_path" "$fixture_root/$relative_path"
done < "$fixture_root/release-locks.txt"

worker_lock_relative="$(jq -r '.packages[] | select(.artifactType == "sunderpkg") | .projectPath' "$fixture_root/packages.json" | sed -n '1p')"
worker_lock="$fixture_root/$(dirname "$worker_lock_relative")/packages.release.lock.json"
# A plain NuGet version is a minimum, so compatible minor/patch upgrades are valid.
# Mutate below the first supported SDK instead of rejecting an allowed upgrade.
jq '(.dependencies[] | select(has("Sunder.Sdk.Worker")) | .["Sunder.Sdk.Worker"].resolved) = "0.0.0"' \
  "$worker_lock" > "$worker_lock.changed"
mv "$worker_lock.changed" "$worker_lock"
failure_log="$fixture_root/lock-failure.log"
if bash "$lock_verifier" "$fixture_root" > "$failure_log" 2>&1; then
  printf 'Release lock verifier accepted a worker outside its declared Core dependency range.\n' >&2
  exit 1
fi
grep -Fq 'Release lock does not resolve Sunder.Sdk.Worker within declared' "$failure_log" \
  || { printf 'Release lock verifier did not explain the dependency-range mismatch.\n' >&2; exit 1; }

python3 -B "$fixture_scripts/native-family.fixture.py"

printf 'Release lock and native transport fixtures passed. No remote provenance or native execution was qualified.\n'
