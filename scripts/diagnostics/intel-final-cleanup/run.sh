#!/usr/bin/env bash
# Diagnostic-only runner. Never export release evidence or publish here.
set -euo pipefail
source_root="$(cd "$1" && pwd -P)"
core="$(cd "$2" && pwd -P)"
inputs="$(cd "$3" && pwd -P)"
output="$4"
diagnostic="$(cd "$(dirname "$0")" && pwd -P)"
unset GH_TOKEN GITHUB_TOKEN SUNDER_REGISTRY_API_KEY SUNDER_REGISTRY_CLI_TOKEN SUNDER_REGISTRY_PUBLISH_TOKEN \
    SUNDER_REGISTRY_NATIVE_READ_TOKEN SUNDER_AGENT_NATIVE_REGISTRY_TOKEN SUNDER_AGENT_NATIVE_REGISTRY_ORIGIN
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_CLI_USE_MSBUILD_SERVER=0 SUNDER_HOME="$RUNNER_TEMP/intel-final-home"
cd "$core"
test "$(dotnet --version)" = 10.0.403
test "$(git rev-parse HEAD)" = "$CORE_COMMIT"
test "$(git -C "$source_root" rev-parse HEAD)" = "$AGENT_COMMIT"
test -z "$(git status --porcelain=v1)"
test -z "$(git -C "$source_root" status --porcelain=v1)"
mkdir "$output"
git -C "$diagnostic" rev-parse HEAD > "$output/diagnostic-commit.txt"
cp "$inputs/expected-scope.json" "$output/expected-scope.json"
cp "$diagnostic/inputs.json" "$output/input-pins.json"
sw_vers > "$output/platform.txt"
uname -m >> "$output/platform.txt"
dotnet --info > "$output/dotnet-info.txt"

app=languages/csharp/dotnet/host/Sunder.App/Sunder.App.csproj
test_project=languages/csharp/dotnet/tests/Sunder.Runtime.Host.Tests/Sunder.Runtime.Host.Tests.csproj
build() {
    dotnet restore "$app" --locked-mode
    dotnet build "$app" -c Release --no-restore -p:GeneratePackageOnBuild=false
    app_target="$(dotnet msbuild "$app" -p:Configuration=Release -getProperty:TargetPath)"
    test -f "$app_target"
    test -z "$(git status --porcelain=v1)"

    git apply --check "$diagnostic/core-stop-trace.patch"
    git apply "$diagnostic/core-stop-trace.patch"
    git diff --check
    test -z "$(git ls-files --others --exclude-standard)"
    git diff --name-only | LC_ALL=C sort > "$output/traced-files.txt"
    prefix=languages/csharp/dotnet/host/Sunder.Runtime.Host/Services
    printf '%s\n' "$prefix/ExecutionRuntimeProcessContainment.cs" "$prefix/PackageStageCleanupService.cs" \
        "$prefix/ProcessRuntimeWorker.PackageData.cs" "$prefix/ProcessRuntimeWorker.cs" "$prefix/RuntimeOperationGate.cs" |
        LC_ALL=C sort | diff - "$output/traced-files.txt"
    git diff --binary > "$output/applied-trace.diff"
    properties=(
        "-p:CustomAfterMicrosoftCommonTargets=$source_root/scripts/release/Sunder.Agent.NativeReleaseTests.targets"
        "-p:SunderAgentNativeReleaseTestSource=$source_root/tests/fixtures/AgentNativeReleaseSmokeTests.cs"
        "-p:SunderAgentNativeAppTargetPath=$app_target"
    )
    dotnet restore "$test_project" --locked-mode "${properties[@]}"
    dotnet build "$test_project" -c Release --no-restore -p:GeneratePackageOnBuild=false "${properties[@]}"
    git diff --binary | cmp - "$output/applied-trace.diff"
}
trap 'tail -80 "$output/build.log" >&2' ERR
build > "$output/build.log" 2>&1
trap - ERR

export SUNDER_AGENT_NATIVE_ARCHIVE_DIR="$inputs/archives"
export SUNDER_AGENT_NATIVE_PLATFORM_CATALOG="$inputs/sunder-platform-contracts.v1.json"
export SUNDER_OFFICIAL_RUNTIME_ARCHIVE_CACHE="$RUNNER_TEMP/intel-final-official-runtimes"
name='Sunder.Runtime.Host.Tests.AgentNativeReleaseSmokeTests.CurrentRidArchives_ExerciseEveryNativeWorkerThroughRuntimeHost'

run_case() {
    local label="$1" forced="$2" metrics="$output/$1" result=0
    mkdir "$metrics"
    # Fresh short physical roots for both socket addresses; no shared Worker
    # directories across the natural and forced-order trials.
    SUNDER_AGENT_NATIVE_METRICS_DIR="$metrics"
    local short_root="${TMPDIR:-/tmp}"
    TMPDIR="$(mktemp -d "${short_root%/}/sf.XXXXXXXX")"
    export SUNDER_AGENT_NATIVE_METRICS_DIR TMPDIR
    printf '%s\n' "$TMPDIR" > "$metrics/test-temp-root.txt"
    SUNDER_DIAGNOSTIC_FORCE_STAGE_SWEEP="$forced" \
        dotnet test "$test_project" -c Release --no-build --no-restore --filter "FullyQualifiedName=$name" \
        --logger 'console;verbosity=normal' --logger 'trx;LogFileName=native-family.trx' --results-directory "$metrics" \
        > "$metrics/test.log" 2>&1 || result=$?
    python3 -B "$diagnostic/report.py" "$metrics" "$inputs/expected-scope.json" "$result"
    echo "$label test exit status: $result"
    tail -22 "$metrics/test.log"
    return "$result"
}

# Stop at the first genuine teardown failure. If natural timing passes, put
# the original sweeper on the known shutdown edge once and capture the result.
(run_case natural 0)
(run_case forced-stage-sweep 1)
