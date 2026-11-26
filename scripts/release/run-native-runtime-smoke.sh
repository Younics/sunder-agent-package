#!/usr/bin/env bash
set -euo pipefail

if [[ $# -lt 2 || $# -gt 4 ]]; then
  printf 'Usage: run-native-runtime-smoke.sh <archive-directory> <new-metrics-directory> [development|published] [package-id]\n' >&2
  exit 2
fi

archive_directory="$(cd "$1" && pwd)"
metrics_directory="$2"
mode="${3:-development}"
unset SUNDER_AGENT_RELEASE_PACKAGE
subject="${4:-}"
if [[ -n "$subject" ]]; then export SUNDER_AGENT_RELEASE_PACKAGE="$subject"; fi
[[ "$mode" == development || "$mode" == published ]] \
  || { printf 'Select development or published native qualification explicitly.\n' >&2; exit 2; }
unset GH_TOKEN GITHUB_TOKEN SUNDER_REGISTRY_API_KEY SUNDER_REGISTRY_CLI_TOKEN SUNDER_REGISTRY_PUBLISH_TOKEN \
  SUNDER_REGISTRY_NATIVE_READ_TOKEN SUNDER_AGENT_NATIVE_REGISTRY_TOKEN
if [[ "$mode" == published ]]; then
  [[ -n "${SUNDER_AGENT_NATIVE_REGISTRY_ORIGIN:-}" ]] \
    || { printf 'Published installed acceptance requires an explicit HTTPS Registry origin.\n' >&2; exit 2; }
fi

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
runtime_policy="$script_dir/../../eng/ci/native-release.json"
dotnet_runtime_version="$(python3 -B - "$runtime_policy" <<'PY'
import json, re, sys
with open(sys.argv[1], encoding="utf-8") as stream:
    version = json.load(stream)["dotnetRuntimeVersion"]
if not isinstance(version, str) or not re.fullmatch(r"(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)", version):
    sys.exit("Native release tests require a pinned stable .NET Worker runtime.")
print(version)
PY
)"
test_source="$(cd "$script_dir/../../tests/fixtures" && pwd)/AgentNativeReleaseSmokeTests.cs"
test_project="$script_dir/../../tests/fixtures/NativeQualification/Sunder.Runtime.Host.Tests.csproj"
injection_targets="$script_dir/Sunder.Agent.NativeReleaseTests.targets"
[[ -f "$test_source" && -f "${test_source%.cs}.Maker.cs" && -f "${test_source%.cs}.Development.cs" \
    && -f "${test_source%.cs}.AppRpc.cs" && -f "$(dirname "$test_source")/NativeAppRpcStartupHook.cs" \
    && -f "$(dirname "$test_source")/McpAppSyntaxProbe.cs" && -f "$test_project" ]] \
  || { printf 'Native Agent release test project was not found.\n' >&2; exit 1; }
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

builder_sdk_version="${SUNDER_AGENT_BUILDER_SDK_VERSION:-$(dotnet --version)}"
builder_dotnet_path="$(command -v dotnet)"
[[ "$builder_sdk_version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] \
  || { printf 'The selected Builder SDK version must be an exact stable three-part version.\n' >&2; exit 1; }
[[ -f "$builder_dotnet_path" ]] \
  || { printf 'The selected Builder dotnet executable was not found.\n' >&2; exit 1; }
# The Maker job consumes the locked, signed public packages through its real
# local-feed selection. Do not rewrite the template's toolset pins.
# Keep the array nonempty under nounset on macOS system Bash (3.2).
preparation_args=("$(native_path "$metrics_directory/maker-inputs")")
if [[ "$mode" == published || ( -n "$subject" && "$subject" != younics.sunder.package.maker ) ]]; then preparation_args+=(--host-only); fi
python3 -B "$script_dir/prepare_maker_inputs.py" "${preparation_args[@]}"
distribution="$(native_path "$metrics_directory/maker-inputs/distribution")"
distribution_property() {
  python3 -B - "$distribution" "$1" <<'PY'
import json, sys
from pathlib import Path
root = Path(sys.argv[1])
print((root / json.loads((root / 'app-input.json').read_text())[sys.argv[2]]).resolve(strict=True))
PY
}
export SunderPublishedAppDirectory
export SunderPublishedHostDirectory
SunderPublishedAppDirectory="$(distribution_property app)"
SunderPublishedHostDirectory="$(distribution_property host)"
# Distributed files can share timestamps across runs. Never reuse an App
# path stamp, hook, or copied Host launcher from a previous qualification.
test_build="$(mktemp -d "$metrics_directory/../agent-native-test.XXXXXXXX")"
test_artifacts=(--artifacts-path "$(native_path "$test_build")")
dotnet restore "$test_project" --configfile "$script_dir/../../NuGet.Config" --locked-mode \
  "${test_artifacts[@]}" -p:Configuration=Release

test_fqn='Sunder.Runtime.Host.Tests.AgentNativeReleaseSmokeTests.CurrentRidArchives_ExerciseEveryNativeWorkerThroughRuntimeHost'
maker_fqn='Sunder.Runtime.Host.Tests.AgentNativeReleaseSmokeTests.MakerArchive_CurrentRidCreatesBuildsPacksAndInvokesWithoutAgent'
test_filter="FullyQualifiedName=$test_fqn|FullyQualifiedName=$maker_fqn"
if [[ -n "$subject" && "$subject" != younics.sunder.package.maker ]]; then test_filter="FullyQualifiedName=$test_fqn"; fi
if [[ "$mode" == published ]]; then
  test_fqn='Sunder.Runtime.Host.Tests.AgentNativeReleaseSmokeTests.PublishedArchives_InstallActivateAndExerciseEveryNativeWorkerThroughRuntimeHost'
  test_filter="FullyQualifiedName=$test_fqn"
fi
discovered="$(dotnet test "$test_project" "${test_artifacts[@]}" -c Release --no-restore --list-tests --filter "$test_filter" | sed $'s/\r$//')"
grep -Fqx "    $test_fqn" <<< "$discovered" \
  || { printf 'Missing native Agent release smoke.\n' >&2; exit 1; }
if [[ "$mode" == development && ( -z "$subject" || "$subject" == younics.sunder.package.maker ) ]]; then
  grep -Fqx "    $maker_fqn" <<< "$discovered" \
    || { printf 'Missing native standalone Maker journey.\n' >&2; exit 1; }
fi

SUNDER_AGENT_NATIVE_ARCHIVE_DIR="$(native_path "$archive_directory")" \
SUNDER_AGENT_REPOSITORY_ROOT="$(native_path "$script_dir/../..")" \
SUNDER_AGENT_NATIVE_METRICS_DIR="$(native_path "$metrics_directory")" \
SUNDER_AGENT_NATIVE_PLATFORM_CATALOG="$(native_path "$metrics_directory/maker-inputs/sunder-platform-contracts.v1.json")" \
SUNDER_AGENT_TEST_WORKER_DOTNET_VERSION="$dotnet_runtime_version" \
SUNDER_AGENT_BUILDER_SDK_VERSION="$builder_sdk_version" \
SUNDER_AGENT_BUILDER_DOTNET_PATH="$(native_path "$builder_dotnet_path")" \
SUNDER_AGENT_BUILDER_NUGET_FEED="$(native_path "$metrics_directory/maker-inputs/feed")" \
  dotnet test "$test_project" "${test_artifacts[@]}" -c Release --no-build --no-restore \
    --filter "$test_filter" \
    --logger 'trx;LogFileName=native-family.trx' --results-directory "$(native_path "$metrics_directory")"

record_args=("$mode" "$metrics_directory" "$archive_directory")
if [[ -n "$subject" ]]; then record_args+=(--package "$subject"); fi
python3 -B "$script_dir/native_runtime_evidence.py" "${record_args[@]}"
python3 -B - "$script_dir" "$metrics_directory" "$subject" <<'PY'
from pathlib import Path
import sys
sys.path.insert(0, sys.argv[1])
from native_runtime_evidence import execution_ids
from native_release_inputs import json_file, require
from agent_release import version
metrics = [json_file(path)["measurement"] for path in Path(sys.argv[2]).glob('*.json') if path.name != 'native-runtime-qualification.json']
expected = execution_ids(sys.argv[3] or None)
require(len(metrics) == len(expected), 'Missing/extra native package metrics')
observed = []
for item in metrics:
    matches = []
    for key in expected:
        if not item['archive'].startswith(key + '.') or not item['archive'].endswith('.sunderpkg'): continue
        try: version(item['archive'][len(key) + 1:-len('.sunderpkg')])
        except ValueError: continue
        matches.append(key)
    require(len(matches) == 1, 'Unknown/ambiguous native metric archive')
    observed.extend(matches)
require(set(observed) == expected, 'Native metrics must cover every selected package exactly once')
require(len({item['rid'] for item in metrics}) == 1, 'Mixed native metric platforms')
print(f'Exercised all {len(metrics)} native family workers; qualification scope is retained explicitly, not inferred as installed acceptance.')
PY
