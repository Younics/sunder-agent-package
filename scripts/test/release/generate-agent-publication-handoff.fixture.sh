#!/usr/bin/env bash
set -euo pipefail
export LC_ALL=C

fixture_scripts="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
script_dir="$(cd "$fixture_scripts/../../release" && pwd)"
generator="$script_dir/generate-agent-publication-handoff.sh"
publisher="$script_dir/publish-agent-family.sh"
workflow="$script_dir/../../.github/workflows/sunder-package-release.yml"
[[ -f "$generator" && -f "$publisher" ]] \
  || { printf 'Agent publication handoff generator or publisher was not found.\n' >&2; exit 1; }
[[ -f "$workflow" ]] || { printf 'Agent package release workflow was not found.\n' >&2; exit 1; }
for required_command in cmp cp cut grep jq mktemp sort tr wc; do
  command -v "$required_command" >/dev/null 2>&1 \
    || { printf '%s is required to run the publication handoff fixture.\n' "$required_command" >&2; exit 127; }
done
if ! command -v sha256sum >/dev/null 2>&1 && ! command -v shasum >/dev/null 2>&1; then
  printf 'sha256sum or shasum is required to run the publication handoff fixture.\n' >&2
  exit 127
fi
grep -Fq 'artifact_name="sunder-agent-publication-evidence-$RELEASE_ID-$GITHUB_RUN_ATTEMPT"' "$workflow"
grep -Fq 'bash scripts/release/generate-agent-publication-handoff.sh' "$workflow"
grep -Fq 'VERIFIED_RELEASE_ASSETS_SHA256: ${{ steps.publication.outputs.release_assets_sha256 }}' "$workflow"
! grep -Fq 'test "$EVIDENCE_RUN_ATTEMPT" = "$GITHUB_RUN_ATTEMPT"' "$workflow"
grep -Fq "jq -cS 'sort_by(.path)'" "$publisher"
grep -Fq "printf 'release_assets_sha256=%s" "$publisher"

hash_file() {
  if command -v sha256sum >/dev/null 2>&1; then sha256sum "$1" | cut -d ' ' -f 1
  else shasum -a 256 "$1" | cut -d ' ' -f 1; fi
}

hash_stream() {
  if command -v sha256sum >/dev/null 2>&1; then sha256sum | cut -d ' ' -f 1
  else shasum -a 256 | cut -d ' ' -f 1; fi
}

file_size() {
  wc -c < "$1" | tr -d '[:space:]'
}

fixture_root="$(mktemp -d "${TMPDIR:-/tmp}/sunder-agent-publication-handoff.XXXXXX")"
trap 'rm -rf "$fixture_root"' EXIT
baseline="$fixture_root/immutable-attempt-1"
source_directory="$fixture_root/release-source"
mock_bin="$fixture_root/mock-bin"
mkdir "$baseline" "$mock_bin"

repository=Younics/sunder-agent-package
release_id=424242
run_id=8001
tag=agent/v2.0.0
source_sha=1111111111111111111111111111111111111111
origin_attempt=1
for index in {1..15}; do
  printf -v name 'sunder.fixture.agent.%02d.2.0.0.sunderpkg' "$index"
  printf 'immutable fixture archive %s\n' "$index" > "$baseline/$name"
done
python3 -B "$fixture_scripts/agent-release.fixture.py" prepare "$source_directory" "$baseline"
# Fifteen archives, two primary evidence files and four release-evidence files.
# release-provenance.json is produced below; no NuGet artifact belongs here.
for name in native-test-evidence.json package-output-audit.json SHA256SUMS release-sbom.spdx.json toolchain.txt; do
  printf 'immutable fixture evidence for %s\n' "$name" > "$baseline/$name"
done
jq -nS \
  --arg repository "$repository" \
  --arg run_id "$run_id" \
  --arg run_attempt "$origin_attempt" \
  --arg tag "$tag" \
  --arg source "$source_sha" \
  --argjson release_id "$release_id" '
    {
      schemaVersion:1,
      buildType:"https://sunderapp.io/provenance/github-actions/v1",
      invocation:{
        repository:$repository,
        workflow:"Sunder Agent Family Release",
        workflowRef:($repository + "/.github/workflows/sunder-package-release.yml@refs/tags/" + $tag),
        runId:$run_id,
        runAttempt:$run_attempt,
        releaseId:$release_id,
        ref:("refs/tags/" + $tag),
        refName:$tag,
        sha:$source
      }
    }
  ' > "$baseline/release-provenance.json"
python3 -B "$fixture_scripts/agent-release.fixture.py" complete "$source_directory" "$baseline"
export MOCK_HANDOFF_RELEASE="$fixture_root/release.json"
python3 -B "$fixture_scripts/agent-release.fixture.py" release "$source_directory" "$baseline" \
  "$MOCK_HANDOFF_RELEASE" "$release_id" "$tag"
cat > "$mock_bin/gh" <<'MOCK_GH'
#!/usr/bin/env bash
set -euo pipefail
[[ "$*" == 'api repos/Younics/sunder-agent-package/releases/424242' ]] || exit 99
command cat "$MOCK_HANDOFF_RELEASE"
MOCK_GH
chmod +x "$mock_bin/gh"

snapshot_release() {
  local directory="$1"
  local asset
  local name
  for asset in "$directory"/*; do
    name="$(basename "$asset")"
    [[ "$name" == publication-handoff-provenance.json ]] && continue
    printf '%s  %s\n' "$(hash_file "$asset")" "$name"
  done | sort
}

release_assets_digest() {
  local directory="$1"
  local asset
  local assets='[]'
  local name
  for asset in "$directory"/*; do
    name="$(basename "$asset")"
    [[ "$name" == publication-handoff-provenance.json ]] && continue
    assets="$(jq -cn \
      --argjson assets "$assets" \
      --arg path "$name" \
      --arg sha256 "$(hash_file "$asset")" \
      --argjson size "$(file_size "$asset")" \
      '$assets + [{path:$path,sha256:$sha256,size:$size}]')"
  done
  jq -cS 'sort_by(.path)' <<< "$assets" | hash_stream
}

run_generator() {
  local directory="$1"
  local current_attempt="$2"
  local claimed_origin="${3:-$origin_attempt}"
  local verified_assets_sha256="${4:-$(release_assets_digest "$directory")}"
  PATH="$mock_bin:$PATH" \
  SUNDER_AGENT_RELEASE_SOURCE_DIRECTORY="$source_directory" \
  GITHUB_REPOSITORY="$repository" \
  GITHUB_WORKFLOW='Sunder Agent Family Release' \
  GITHUB_WORKFLOW_REF="$repository/.github/workflows/sunder-package-release.yml@refs/tags/$tag" \
  GITHUB_RUN_ID="${MOCK_RUN_ID:-$run_id}" \
  GITHUB_RUN_ATTEMPT="$current_attempt" \
  GITHUB_REF="refs/tags/$tag" \
  GITHUB_REF_NAME="$tag" \
  GITHUB_SHA="$source_sha" \
    bash "$generator" "$directory" "$release_id" "$tag" "$source_sha" "$claimed_origin" \
      "$verified_assets_sha256"
}

recovery="$fixture_root/recovery-attempt-2"
deterministic="$fixture_root/deterministic-attempt-2"
fresh="$fixture_root/fresh-attempt-1"
cp -R "$baseline" "$recovery"
cp -R "$baseline" "$deterministic"
cp -R "$baseline" "$fresh"
snapshot_release "$recovery" > "$fixture_root/before.sha256"
run_generator "$recovery" 2 > "$fixture_root/recovery.log"
snapshot_release "$recovery" > "$fixture_root/after.sha256"
cmp -s "$fixture_root/before.sha256" "$fixture_root/after.sha256" \
  || { printf 'Recovery mutated an immutable attempt-1 release asset.\n' >&2; exit 1; }
jq -e \
  --arg run_id "$run_id" \
  --arg source "$source_sha" '
    .invocation.runId == $run_id
    and .invocation.runAttempt == "2"
    and .invocation.sha == $source
    and .immutableRelease.provenanceRunAttempt == "1"
    and (.immutableRelease.assetsSha256 | test("^[0-9a-f]{64}$"))
    and (.immutableRelease.assets | length) == 21
    and ([.immutableRelease.assets[] | select(.path | endswith(".sunderpkg"))] | length) == 15
    and all(.immutableRelease.assets[]; (.path | endswith(".nupkg")) | not)
    and ([.immutableRelease.assets[].path] | index("publication-handoff-provenance.json") == null)
  ' "$recovery/publication-handoff-provenance.json" >/dev/null

run_generator "$deterministic" 2 > /dev/null
cmp -s \
  "$recovery/publication-handoff-provenance.json" \
  "$deterministic/publication-handoff-provenance.json" \
  || { printf 'Equivalent publication recovery handoffs are not deterministic.\n' >&2; exit 1; }
run_generator "$fresh" 1 > /dev/null
jq -e '.invocation.runAttempt == "1" and .immutableRelease.provenanceRunAttempt == "1"' \
  "$fresh/publication-handoff-provenance.json" >/dev/null

expect_rejected() {
  local name="$1"
  local directory="$fixture_root/rejected-$name"
  shift
  cp -R "$baseline" "$directory"
  if "$@" > "$fixture_root/$name.log" 2>&1; then
    printf 'Publication handoff fixture unexpectedly accepted %s.\n' "$name" >&2
    exit 1
  fi
}

export MOCK_RUN_ID=8002
expect_rejected cross-run run_generator "$fixture_root/rejected-cross-run" 2
unset MOCK_RUN_ID
expect_rejected mismatched-origin run_generator "$fixture_root/rejected-mismatched-origin" 2 2
expect_rejected mismatched-assets-digest run_generator "$fixture_root/rejected-mismatched-assets-digest" 2 1 \
  bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb
if run_generator "$recovery" 2 > "$fixture_root/existing-handoff.log" 2>&1; then
  printf 'Publication handoff fixture overwrote an existing handoff.\n' >&2
  exit 1
fi

for inventory_case in missing-archive extra-archive; do
  directory="$fixture_root/$inventory_case"
  cp -R "$baseline" "$directory"
  if [[ "$inventory_case" == missing-archive ]]; then
    rm "$directory/sunder.fixture.agent.01.2.0.0.sunderpkg"
  else
    printf 'unexpected sixteenth fixture archive\n' > "$directory/sunder.fixture.agent.16.2.0.0.sunderpkg"
  fi
  snapshot_release "$directory" > "$fixture_root/$inventory_case.before"
  if run_generator "$directory" 2 > "$fixture_root/$inventory_case.log" 2>&1; then
    printf 'Publication handoff fixture accepted %s.\n' "$inventory_case" >&2
    exit 1
  fi
  grep -Fq 'exactly 21 immutable release assets' "$fixture_root/$inventory_case.log"
  [[ ! -e "$directory/publication-handoff-provenance.json" && ! -e "$directory/publication-handoff-provenance.json.next" ]]
  snapshot_release "$directory" > "$fixture_root/$inventory_case.after"
  cmp -s "$fixture_root/$inventory_case.before" "$fixture_root/$inventory_case.after"
done

printf 'Agent publication handoff fixture passed: exactly 15 archives and 21 release assets, immutable failed-attempt recovery, deterministic evidence, and cross-run, origin, digest, overwrite, and inventory rejection.\n'
