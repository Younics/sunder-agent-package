#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 4 ]]; then
  printf 'Usage: run-native-runtime-smoke.sh <core-repository> <archive-directory> <metrics-directory> <expected-core-commit>\n' >&2
  exit 2
fi

core_repository="$(cd "$1" && pwd)"
archive_directory="$(cd "$2" && pwd)"
metrics_directory="$3"
expected_core_commit="$4"
[[ "$expected_core_commit" =~ ^[0-9a-f]{40}$ ]] \
  || { printf 'Expected Core commit must be a full lowercase Git SHA.\n' >&2; exit 2; }
[[ "$(git -C "$core_repository" rev-parse HEAD)" == "$expected_core_commit" ]] \
  || { printf 'Core test checkout does not match release provenance.\n' >&2; exit 1; }

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
test_source="$(cd "$script_dir/../../tests/fixtures" && pwd)/AgentNativeReleaseSmokeTests.cs"
test_project_directory="$core_repository/languages/csharp/dotnet/tests/Sunder.Runtime.Host.Tests"
test_project="$test_project_directory/Sunder.Runtime.Host.Tests.csproj"
injection_targets="$script_dir/Sunder.Agent.NativeReleaseTests.targets"
[[ -f "$test_source" && -f "$test_project" ]] \
  || { printf 'Native release smoke source or Core test project was not found.\n' >&2; exit 1; }
[[ -f "$injection_targets" ]] || { printf 'Native release test injection targets were not found.\n' >&2; exit 1; }
mkdir -p "$metrics_directory"

native_path() {
  if command -v cygpath >/dev/null 2>&1; then
    cygpath -w "$1"
  else
    printf '%s\n' "$(cd "$(dirname "$1")" && pwd)/$(basename "$1")"
  fi
}

test_source_native="$(native_path "$test_source")"
injection_targets_native="$(native_path "$injection_targets")"
injection_properties=(
  "-p:CustomAfterMicrosoftCommonTargets=$injection_targets_native"
  "-p:SunderAgentNativeReleaseTestSource=$test_source_native"
)

dotnet restore "$test_project" --locked-mode
test_fqn='Sunder.Runtime.Host.Tests.AgentNativeReleaseSmokeTests.CurrentRidArchives_ExerciseEveryNativeWorkerThroughRuntimeHost'
discovered="$(dotnet test "$test_project" -c Release --no-restore -p:GeneratePackageOnBuild=false \
  "${injection_properties[@]}" --list-tests --filter "FullyQualifiedName=$test_fqn")"
grep -Fqx "    $test_fqn" <<< "$discovered" \
  || { printf 'Core did not discover the injected native Agent release smoke.\n' >&2; exit 1; }

SUNDER_AGENT_NATIVE_ARCHIVE_DIR="$(native_path "$archive_directory")" \
SUNDER_AGENT_NATIVE_METRICS_DIR="$(native_path "$metrics_directory")" \
  dotnet test "$test_project" -c Release --no-restore -p:GeneratePackageOnBuild=false \
    "${injection_properties[@]}" \
    --filter "FullyQualifiedName=$test_fqn"

metric_count="$(find "$metrics_directory" -maxdepth 1 -name '*.json' -type f | wc -l | tr -d '[:space:]')"
[[ "$metric_count" == 15 ]] \
  || { printf 'Expected Core metrics for exactly 15 native workers, found %s.\n' "$metric_count" >&2; exit 1; }
printf 'Exercised exactly 15 native workers through the real Runtime Host release lifecycle.\n'
