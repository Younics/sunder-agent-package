#!/usr/bin/env bash
set -euo pipefail

fixture_scripts="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
script_dir="$(cd "$fixture_scripts/../../release" && pwd)"
repository_root="$(cd "$script_dir/../.." && pwd)"
lock_verifier="$script_dir/verify-release-locks.sh"
test_targets="$script_dir/Sunder.Agent.NativeReleaseTests.targets"
native_builder="$script_dir/build-native-family.ps1"
native_helper="$script_dir/native-family.py"
output_auditor="$script_dir/generate-package-output-audit.sh"
native_smoke="$repository_root/tests/fixtures/AgentNativeReleaseSmokeTests.cs"

lock_report="$(bash "$lock_verifier" "$repository_root")"
printf '%s\n' "$lock_report"
grep -Fq 'Verified 35 release locks and 15 primary package projects' <<< "$lock_report" \
  || { printf 'Canonical release fixture requires the current 35-project lock inventory.\n' >&2; exit 1; }
test ! -e "$script_dir/Sunder.Agent.ReleaseSigning.targets"
test ! -e "$script_dir/sign-worker-apphost.ps1"
grep -Fq 'expectedEntryPoint' "$native_builder"
grep -Fq '.runtimeconfig.json' "$native_builder"
grep -Fq 'SunderPackagePrebuiltRoleInputsFile=' "$native_builder"
grep -Fq 'identity_keys(expected) + " nativeRuns finalArtifacts"' "$native_helper"
grep -Fq 'Selective native builds still require all fifteen Worker tests' "$native_helper"
grep -Fq 'Expected the complete unique runtime family' "$native_helper"
grep -Fq 'maximumBytes: 360003313' "$output_auditor"
grep -Fq 'meetsInstalledProjectionGate: true' "$output_auditor"
grep -Fq 'universalIndexedPayload' "$output_auditor"
grep -Fq 'OfficialRuntimeFixture executionRuntimes' "$native_smoke"
grep -Fq 'EnsureDotNetAsync("10.0.12")' "$native_smoke"
grep -Fq 'AssertBindingMatches' "$native_smoke"
if grep -Eq 'SignedCurrentRidExecutionRuntimeFixture|ObserveSupportedAsync|packagingRevision|LifecyclePolicies|\.sunderruntime|Sunder\.ExecutionRuntime\.Build' "$native_smoke"; then
  printf 'Native release smoke must use official runtime acquisition and exact bindings, without retired runtime artifacts or publication tooling.\n' >&2
  exit 1
fi
grep -Fq 'AssertRuntimeLaunch' "$native_smoke"
grep -Fq 'VerifyBuilderProjectLifecycleAsync' "$native_smoke"
grep -Fq 'SUNDER_AGENT_BUILDER_SDK_VERSION' "$native_smoke"
grep -Fq 'kind = "add-path-entry"' "$native_smoke"
grep -Fq '"new", "console"' "$native_smoke"
grep -Fq '"build", projectRoot' "$native_smoke"
grep -Fq '"publish", projectRoot' "$native_smoke"
grep -Fq 'SunderAgentNativeReleaseTestSource' "$test_targets"
grep -Fq '$(MSBuildProjectName)' "$test_targets"
grep -Fq 'Sunder.Runtime.Host.Tests' "$test_targets"

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
jq '.dependencies["net10.0"]["Sunder.Sdk.Worker"].resolved = "0.0.0"' "$worker_lock" > "$worker_lock.changed"
mv "$worker_lock.changed" "$worker_lock"
failure_log="$fixture_root/lock-failure.log"
if bash "$lock_verifier" "$fixture_root" > "$failure_log" 2>&1; then
  printf 'Release lock verifier accepted a worker outside its declared Core dependency range.\n' >&2
  exit 1
fi
grep -Fq 'Release lock does not resolve Sunder.Sdk.Worker within declared' "$failure_log" \
  || { printf 'Release lock verifier did not explain the dependency-range mismatch.\n' >&2; exit 1; }

python3 -B "$fixture_scripts/native-family.fixture.py"

printf 'Native release source fixture passed: 35 release locks, 15 canonical packages, Core version/lock metadata, framework-dependent module policy, qualified-input transport and mocked no-rebuild fan-in; no remote provenance or native execution was qualified.\n'
