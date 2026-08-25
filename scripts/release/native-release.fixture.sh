#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repository_root="$(cd "$script_dir/../.." && pwd)"
lock_verifier="$script_dir/verify-release-locks.sh"
signing_targets="$script_dir/Sunder.Agent.ReleaseSigning.targets"
test_targets="$script_dir/Sunder.Agent.NativeReleaseTests.targets"
signer="$script_dir/sign-worker-apphost.ps1"

bash "$lock_verifier" 1.1.0 "$repository_root"
grep -Fq 'SignSunderAgentReleaseWorkerAppHost' "$signing_targets"
grep -Fq '@(SunderPackageWorkerSigningFile)' "$signing_targets"
grep -Fq '$(SunderPackageWorkerEntryPointRelativePath)' "$signing_targets"
grep -Fq 'Get-AuthenticodeSignature' "$signer"
grep -Fq 'codesign --verify --strict' "$signer"
grep -Fq 'SunderAgentNativeReleaseTestSource' "$test_targets"
grep -Fq '$(MSBuildProjectName)' "$test_targets"
grep -Fq 'Sunder.Runtime.Host.Tests' "$test_targets"

temp_root="${TMPDIR:-/tmp}"
fixture_root="$(mktemp -d "${temp_root%/}/sunder-native-release-fixture.XXXXXX")"
trap 'rm -rf "$fixture_root"' EXIT
cp "$repository_root/packages.json" "$fixture_root/packages.json"
cp "$repository_root/Sunder.AgentPackage.slnx" "$fixture_root/Sunder.AgentPackage.slnx"
while IFS= read -r lock_path; do
  relative_path="${lock_path#"$repository_root"/}"
  mkdir -p "$fixture_root/$(dirname "$relative_path")"
  cp "$lock_path" "$fixture_root/$relative_path"
done < <(find "$repository_root/src" "$repository_root/tests" -name packages.release.lock.json -type f -print)

worker_lock_relative="$(jq -r '.packages[] | select(.artifactType == "sunderpkg") | .projectPath' "$fixture_root/packages.json" | sed -n '1p')"
worker_lock="$fixture_root/$(dirname "$worker_lock_relative")/packages.release.lock.json"
jq '.dependencies["net10.0"]["Sunder.Sdk.Worker"].resolved = "1.1.1"' "$worker_lock" > "$worker_lock.changed"
mv "$worker_lock.changed" "$worker_lock"
failure_log="$fixture_root/lock-failure.log"
if bash "$lock_verifier" 1.1.0 "$fixture_root" > "$failure_log" 2>&1; then
  printf 'Release lock verifier accepted a worker restored against a different Core package.\n' >&2
  exit 1
fi
grep -Eq 'Release lock does not resolve|not locked to the final Core' "$failure_log" \
  || { printf 'Release lock verifier did not explain the final-Core mismatch.\n' >&2; exit 1; }

printf 'Native release fixture passed: exact worker inventory, final remote Core locks, and platform signing hooks are enforced.\n'
