#!/usr/bin/env bash
set -euo pipefail

if [[ $# -lt 4 || $# -gt 5 ]]; then
  printf 'Usage: run-native-runtime-smoke.sh <core-repository> <archive-directory> <new-metrics-directory> <expected-core-commit> [development|published]\n' >&2
  exit 2
fi

core_repository="$(cd "$1" && pwd)"
archive_directory="$(cd "$2" && pwd)"
metrics_directory="$3"
expected_core_commit="$4"
mode="${5:-development}"
[[ "$mode" == development || "$mode" == published ]] \
  || { printf 'Select development or published native qualification explicitly.\n' >&2; exit 2; }
unset GH_TOKEN GITHUB_TOKEN SUNDER_REGISTRY_API_KEY SUNDER_REGISTRY_CLI_TOKEN SUNDER_REGISTRY_PUBLISH_TOKEN \
  SUNDER_REGISTRY_NATIVE_READ_TOKEN SUNDER_AGENT_NATIVE_REGISTRY_TOKEN
if [[ "$mode" == published ]]; then
  [[ -n "${SUNDER_AGENT_NATIVE_REGISTRY_ORIGIN:-}" ]] \
    || { printf 'Published installed acceptance requires an explicit HTTPS Registry origin.\n' >&2; exit 2; }
fi
[[ "$expected_core_commit" =~ ^[0-9a-f]{40}$ ]] \
  || { printf 'Expected Core commit must be a full lowercase Git SHA.\n' >&2; exit 2; }
[[ "$(git -C "$core_repository" rev-parse HEAD)" == "$expected_core_commit" ]] \
  || { printf 'Core test checkout does not match release provenance.\n' >&2; exit 1; }

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
test_source="$(cd "$script_dir/../../tests/fixtures" && pwd)/AgentNativeReleaseSmokeTests.cs"
test_project_directory="$core_repository/languages/csharp/dotnet/tests/Sunder.Runtime.Host.Tests"
test_project="$test_project_directory/Sunder.Runtime.Host.Tests.csproj"
injection_targets="$script_dir/Sunder.Agent.NativeReleaseTests.targets"
[[ -f "$test_source" && -f "${test_source%.cs}.Maker.cs" && -f "${test_source%.cs}.Development.cs" \
    && -f "${test_source%.cs}.AppRpc.cs" && -f "$(dirname "$test_source")/NativeAppRpcStartupHook.cs" && -f "$test_project" ]] \
  || { printf 'Native release smoke source or Core test project was not found.\n' >&2; exit 1; }
[[ -f "$injection_targets" ]] || { printf 'Native release test injection targets were not found.\n' >&2; exit 1; }
[[ ! -e "$metrics_directory" ]] || { printf 'Native result directory must be new; retain failed evidence separately.\n' >&2; exit 1; }
mkdir -p "$metrics_directory"
metrics_directory="$(cd "$metrics_directory" && pwd)"

native_path() {
  if command -v cygpath >/dev/null 2>&1; then
    cygpath -w "$1"
  else
    printf '%s\n' "$(cd "$(dirname "$1")" && pwd)/$(basename "$1")"
  fi
}

test_source_native="$(native_path "$test_source")"
injection_targets_native="$(native_path "$injection_targets")"
builder_sdk_version="${SUNDER_AGENT_BUILDER_SDK_VERSION:-$(dotnet --version)}"
builder_dotnet_path="$(command -v dotnet)"
[[ "$builder_sdk_version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] \
  || { printf 'The selected Builder SDK version must be an exact stable three-part version.\n' >&2; exit 1; }
[[ -f "$builder_dotnet_path" ]] \
  || { printf 'The selected Builder dotnet executable was not found.\n' >&2; exit 1; }
# The Maker job driver consumes these retained exact archives through its real
# local-feed selection. Do not fall back to a published template/SDK or alter pins.
preparation_options=()
if [[ "$mode" == published ]]; then preparation_options+=(--host-only); fi
python3 -B "$script_dir/prepare_maker_inputs.py" "$core_repository" "$metrics_directory/maker-inputs" "$expected_core_commit" "${preparation_options[@]}"
app_target="$(python3 -B - "$metrics_directory/maker-inputs" <<'PY'
import json, sys
from pathlib import Path
root = Path(sys.argv[1])
print((root / json.loads((root / 'inputs.json').read_text())['app']['targetPath']).resolve(strict=True))
PY
)"
injection_properties=(
  "-p:CustomAfterMicrosoftCommonTargets=$injection_targets_native"
  "-p:SunderAgentNativeReleaseTestSource=$test_source_native"
  "-p:SunderAgentNativeAppTargetPath=$(native_path "$app_target")"
)

dotnet restore "$test_project" --locked-mode "${injection_properties[@]}"
test_fqn='Sunder.Runtime.Host.Tests.AgentNativeReleaseSmokeTests.CurrentRidArchives_ExerciseEveryNativeWorkerThroughRuntimeHost'
maker_fqn='Sunder.Runtime.Host.Tests.AgentNativeReleaseSmokeTests.MakerArchive_CurrentRidCreatesBuildsPacksAndInvokesWithoutAgent'
test_filter="FullyQualifiedName=$test_fqn|FullyQualifiedName=$maker_fqn"
if [[ "$mode" == published ]]; then
  test_fqn='Sunder.Runtime.Host.Tests.AgentNativeReleaseSmokeTests.PublishedArchives_InstallActivateAndExerciseEveryNativeWorkerThroughRuntimeHost'
  test_filter="FullyQualifiedName=$test_fqn"
fi
discovered="$(dotnet test "$test_project" -c Release --no-restore -p:GeneratePackageOnBuild=false \
  "${injection_properties[@]}" --list-tests --filter "$test_filter")"
grep -Fqx "    $test_fqn" <<< "$discovered" \
  || { printf 'Core did not discover the injected native Agent release smoke.\n' >&2; exit 1; }
if [[ "$mode" == development ]]; then
  grep -Fqx "    $maker_fqn" <<< "$discovered" \
    || { printf 'Core did not discover the native standalone Maker journey.\n' >&2; exit 1; }
fi

SUNDER_AGENT_NATIVE_ARCHIVE_DIR="$(native_path "$archive_directory")" \
SUNDER_AGENT_NATIVE_METRICS_DIR="$(native_path "$metrics_directory")" \
SUNDER_AGENT_NATIVE_PLATFORM_CATALOG="$(native_path "$metrics_directory/maker-inputs/sunder-platform-contracts.v1.json")" \
SUNDER_AGENT_BUILDER_SDK_VERSION="$builder_sdk_version" \
SUNDER_AGENT_BUILDER_DOTNET_PATH="$(native_path "$builder_dotnet_path")" \
SUNDER_AGENT_BUILDER_NUGET_FEED="$(native_path "$metrics_directory/maker-inputs/feed")" \
  dotnet test "$test_project" -c Release --no-build --no-restore -p:GeneratePackageOnBuild=false \
    "${injection_properties[@]}" \
    --filter "$test_filter" \
    --logger 'trx;LogFileName=native-family.trx' --results-directory "$(native_path "$metrics_directory")"

python3 -B "$script_dir/native_runtime_evidence.py" "$mode" "$metrics_directory" "$archive_directory" "$expected_core_commit"
python3 -B - "$script_dir" "$metrics_directory" <<'PY'
from pathlib import Path
import sys
sys.path.insert(0, sys.argv[1])
from family_policy import FAMILY_PACKAGE_IDS
from native_release_inputs import json_file, require
from agent_release import version
metrics = [json_file(path)["measurement"] for path in Path(sys.argv[2]).glob('*.json') if path.name != 'native-runtime-qualification.json']
require(len(metrics) == len(FAMILY_PACKAGE_IDS), 'Missing/extra native family metrics')
observed = []
for item in metrics:
    matches = []
    for key in FAMILY_PACKAGE_IDS:
        if not item['archive'].startswith(key + '.') or not item['archive'].endswith('.sunderpkg'): continue
        try: version(item['archive'][len(key) + 1:-len('.sunderpkg')])
        except ValueError: continue
        matches.append(key)
    require(len(matches) == 1, 'Unknown/ambiguous native metric archive')
    observed.extend(matches)
require(set(observed) == set(FAMILY_PACKAGE_IDS), 'Native metrics must cover every reviewed package exactly once')
require(len({item['rid'] for item in metrics}) == 1, 'Mixed native metric platforms')
print(f'Exercised all {len(metrics)} native family workers; qualification scope is retained explicitly, not inferred as installed acceptance.')
PY
