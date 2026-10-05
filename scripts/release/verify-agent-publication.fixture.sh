#!/usr/bin/env bash
set -euo pipefail
export LC_ALL=C

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
verifier="$script_dir/verify-agent-publication.sh"
generator="$script_dir/../generate-release-evidence.sh"
handoff_generator="$script_dir/generate-agent-publication-handoff.sh"
[[ -f "$verifier" && -f "$generator" && -f "$handoff_generator" ]] \
  || { printf 'Agent publication verifier or evidence generator was not found.\n' >&2; exit 1; }
for required_command in cp cut grep jq mktemp mv sort tr wc; do
  command -v "$required_command" >/dev/null 2>&1 \
    || { printf '%s is required to run the Agent publication fixture.\n' "$required_command" >&2; exit 127; }
done
if ! command -v sha256sum >/dev/null 2>&1 && ! command -v shasum >/dev/null 2>&1; then
  printf 'sha256sum or shasum is required to run the Agent publication fixture.\n' >&2
  exit 127
fi

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

fixture_root="$(mktemp -d "${TMPDIR:-/tmp}/sunder-agent-publication.XXXXXX")"
trap 'rm -rf "$fixture_root"' EXIT
source_directory="$fixture_root/release-source"
evidence_directory="$fixture_root/publication-evidence"
mock_bin="$fixture_root/mock-bin"
mkdir -p "$source_directory/scripts/release" "$evidence_directory" "$mock_bin"

export MOCK_REPOSITORY=Younics/sunder-agent-package
export MOCK_RELEASE_ID=424242
export MOCK_RUN_ID=8001
export MOCK_RUN_ATTEMPT=2
export MOCK_ORIGIN_ATTEMPT=1
export MOCK_SOURCE_SHA=1111111111111111111111111111111111111111
export MOCK_CORE_SHA=2222222222222222222222222222222222222222
export MOCK_TAG=agent/v2.0.0
export MOCK_ARTIFACT_ID=7001
export MOCK_ARTIFACT_DIGEST=sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
export MOCK_ARTIFACT_NAME="sunder-agent-publication-evidence-$MOCK_RELEASE_ID-$MOCK_RUN_ATTEMPT"
export MOCK_EVIDENCE_DIRECTORY="$evidence_directory"
export MOCK_RELEASE_JSON="$fixture_root/release.json"

# Exercise the real evidence writer without touching the developer's .NET outputs.
# Only the two read-only toolchain probes are permitted in this shell fixture.
cat > "$mock_bin/dotnet" <<'MOCK_DOTNET'
#!/usr/bin/env bash
set -euo pipefail
case "$*" in
  --version) printf '10.0.100\n' ;;
  --list-sdks) printf '10.0.100 [/fixture/dotnet/sdk]\n' ;;
  *) printf 'Unexpected dotnet command in publication fixture: %s\n' "$*" >&2; exit 99 ;;
esac
MOCK_DOTNET
chmod +x "$mock_bin/dotnet"

generator_directory="$fixture_root/generated-evidence"
mkdir "$generator_directory"
printf 'primary generator fixture\n' > "$generator_directory/fixture.bin"
PATH="$mock_bin:$PATH" \
SUNDER_CORE_TAG=sdk/v1.1.0 \
SUNDER_CORE_COMMIT="$MOCK_CORE_SHA" \
SUNDER_CORE_PACKAGE_VERSION=1.1.0 \
GITHUB_RELEASE_ID="$MOCK_RELEASE_ID" \
GITHUB_REPOSITORY="$MOCK_REPOSITORY" \
GITHUB_WORKFLOW='Generic Evidence Fixture' \
GITHUB_WORKFLOW_REF="$MOCK_REPOSITORY/.github/workflows/sunder-package-release.yml@refs/tags/$MOCK_TAG" \
GITHUB_RUN_ID="$MOCK_RUN_ID" \
GITHUB_RUN_ATTEMPT="$MOCK_RUN_ATTEMPT" \
GITHUB_REF="refs/tags/$MOCK_TAG" \
GITHUB_REF_NAME="$MOCK_TAG" \
GITHUB_SHA="$MOCK_SOURCE_SHA" \
  bash "$generator" "$generator_directory" \
    "sunder-agent-package=$MOCK_SOURCE_SHA" "sunder-core=$MOCK_CORE_SHA"
jq -e \
  --arg repository "$MOCK_REPOSITORY" --arg tag "$MOCK_TAG" --arg source "$MOCK_SOURCE_SHA" \
  --arg run_id "$MOCK_RUN_ID" --arg run_attempt "$MOCK_RUN_ATTEMPT" \
  --argjson release_id "$MOCK_RELEASE_ID" '
    .invocation == {
      repository:$repository,workflow:"Generic Evidence Fixture",
      workflowRef:($repository + "/.github/workflows/sunder-package-release.yml@refs/tags/" + $tag),
      runId:$run_id,runAttempt:$run_attempt,releaseId:$release_id,
      ref:("refs/tags/" + $tag),refName:$tag,sha:$source
    }
    and [.artifacts[].path] == ["fixture.bin"]
  ' "$generator_directory/release-provenance.json" >/dev/null

jq -n '
  {
    releaseVersion:"2.0.0",
    packages: [range(1;16) | {
      artifactType:"sunderpkg",
      packageId:("sunder.fixture.agent." + (. | tostring)),
      projectPath:("src/Fixture" + (. | tostring) + "/Fixture" + (. | tostring) + ".csproj")
    }]
  }
' > "$source_directory/packages.json"
cat > "$source_directory/scripts/release/verify-agent-publication.sh" <<'MALICIOUS_POLICY'
#!/usr/bin/env bash
printf 'MALICIOUS_TAG_POLICY_EXECUTED\n' >> "$MOCK_EVENT_LOG"
exit 99
MALICIOUS_POLICY
chmod +x "$source_directory/scripts/release/verify-agent-publication.sh"

primary_names=()
while IFS= read -r package_id; do
  primary_names+=("$package_id.2.0.0.sunderpkg")
done < <(jq -r '.packages[] | select(.artifactType == "sunderpkg") | .packageId' "$source_directory/packages.json" | sort)
[[ "${#primary_names[@]}" -eq 15 ]]
primary_names+=(native-test-evidence.json package-output-audit.json)
[[ "${#primary_names[@]}" -eq 17 ]]
for name in "${primary_names[@]}"; do
  printf 'immutable fixture bytes for %s\n' "$name" > "$evidence_directory/$name"
done
python3 -B "$script_dir/agent-release.fixture.py" prepare "$source_directory" "$evidence_directory"

artifact_records='[]'
sbom_files='[]'
: > "$evidence_directory/SHA256SUMS"
for name in "${primary_names[@]}"; do
  sha="$(hash_file "$evidence_directory/$name")"
  size="$(file_size "$evidence_directory/$name")"
  printf '%s  %s\n' "$sha" "$name" >> "$evidence_directory/SHA256SUMS"
  artifact_records="$(jq -cn --argjson records "$artifact_records" --arg path "$name" \
    --arg sha "$sha" --argjson size "$size" '$records + [{path:$path,sha256:$sha,size:$size}]')"
  sbom_files="$(jq -cn --argjson files "$sbom_files" --arg name "./$name" --arg sha "$sha" \
    '$files + [{fileName:$name,checksums:[{algorithm:"SHA256",checksumValue:$sha}]}]')"
done
printf 'dotnet-version: 10.0.100\nrunner-os: fixture\n' > "$evidence_directory/toolchain.txt"
jq -n \
  --arg repository "$MOCK_REPOSITORY" \
  --arg tag "$MOCK_TAG" \
  --arg source "$MOCK_SOURCE_SHA" \
  --arg core "$MOCK_CORE_SHA" \
  --arg run_id "$MOCK_RUN_ID" \
  --arg run_attempt "$MOCK_ORIGIN_ATTEMPT" \
  --argjson release_id "$MOCK_RELEASE_ID" \
  --argjson artifacts "$artifact_records" '
    {
      schemaVersion:1,
      buildType:"https://sunderapp.io/provenance/github-actions/v1",
      invocation:{
        repository:$repository,
        workflow:"Sunder Agent Family Release",
        workflowRef:($repository + "/.github/workflows/sunder-package-release.yml@refs/tags/" + $tag),
        runId:$run_id,runAttempt:$run_attempt,releaseId:$release_id,
        ref:("refs/tags/" + $tag),refName:$tag,sha:$source
      },
      sources:[{name:"sunder-agent-package",sha:$source},{name:"sunder-core",sha:$core}],
      materials:[{name:"sunder-core-sdk",tag:"sdk/v1.1.0",sha:$core,packageVersion:"1.1.0"}],
      artifacts:$artifacts
    }
  ' > "$evidence_directory/release-provenance.json"
python3 -B "$script_dir/agent-release.fixture.py" complete "$source_directory" "$evidence_directory"
jq -n --argjson files "$sbom_files" '{spdxVersion:"SPDX-2.3",files:$files}' \
  > "$evidence_directory/release-sbom.spdx.json"

all_names=("${primary_names[@]}" SHA256SUMS release-provenance.json release-sbom.spdx.json toolchain.txt)
[[ "${#all_names[@]}" -eq 21 ]]
release_assets='[]'
asset_id=1000
for name in "${all_names[@]}"; do
  asset_id=$((asset_id + 1))
  release_assets="$(jq -cn \
    --argjson assets "$release_assets" \
    --argjson id "$asset_id" \
    --arg name "$name" \
    --arg digest "sha256:$(hash_file "$evidence_directory/$name")" \
    --arg url "https://api.github.com/repos/$MOCK_REPOSITORY/releases/assets/$asset_id" \
    --argjson size "$(file_size "$evidence_directory/$name")" \
    '$assets + [{id:$id,name:$name,state:"uploaded",digest:$digest,size:$size,url:$url}]')"
done
release_body="$(printf 'Fixture Agent release\n\nAgent source commit: %s\n\nRelease evidence SHA-256:' "$MOCK_SOURCE_SHA")"
for name in SHA256SUMS release-provenance.json release-sbom.spdx.json toolchain.txt; do
  release_body+="$(printf '\n%s SHA-256: %s' "$name" "$(hash_file "$evidence_directory/$name")")"
done
jq -n \
  --arg body "$release_body" \
  --arg tag "$MOCK_TAG" \
  --argjson assets "$release_assets" \
  --argjson id "$MOCK_RELEASE_ID" '
    {id:$id,tag_name:$tag,draft:true,prerelease:false,name:"Sunder Agent Family 2.0.0",body:$body,assets:$assets}
  ' > "$MOCK_RELEASE_JSON"
cp "$MOCK_RELEASE_JSON" "$fixture_root/release-template.json"
verified_release_assets_sha256="$(jq -cS \
  '[.[] | {path:.name,sha256:(.digest | sub("^sha256:";"")),size}] | sort_by(.path)' \
  <<< "$release_assets" | hash_stream)"
cat > "$mock_bin/gh" <<'MOCK_HANDOFF_GH'
#!/usr/bin/env bash
set -euo pipefail
[[ "$*" == "api repos/$MOCK_REPOSITORY/releases/$MOCK_RELEASE_ID" ]] || exit 99
command cat "$MOCK_RELEASE_JSON"
MOCK_HANDOFF_GH
chmod +x "$mock_bin/gh"
PATH="$mock_bin:$PATH" \
SUNDER_AGENT_RELEASE_SOURCE_DIRECTORY="$source_directory" \
GITHUB_REPOSITORY="$MOCK_REPOSITORY" \
GITHUB_WORKFLOW='Sunder Agent Family Release' \
GITHUB_WORKFLOW_REF="$MOCK_REPOSITORY/.github/workflows/sunder-package-release.yml@refs/tags/$MOCK_TAG" \
GITHUB_RUN_ID="$MOCK_RUN_ID" \
GITHUB_RUN_ATTEMPT="$MOCK_RUN_ATTEMPT" \
GITHUB_REF="refs/tags/$MOCK_TAG" \
GITHUB_REF_NAME="$MOCK_TAG" \
GITHUB_SHA="$MOCK_SOURCE_SHA" \
  bash "$handoff_generator" "$evidence_directory" "$MOCK_RELEASE_ID" "$MOCK_TAG" \
    "$MOCK_SOURCE_SHA" "$MOCK_ORIGIN_ATTEMPT" "$verified_release_assets_sha256" >/dev/null
cp "$evidence_directory/publication-handoff-provenance.json" "$fixture_root/handoff-template.json"
export MOCK_EVENT_LOG="$fixture_root/events.log"

cat > "$mock_bin/git" <<'MOCK_GIT'
#!/usr/bin/env bash
set -euo pipefail
if [[ "$1" == -C && "$3" == diff && "$4" == --quiet ]]; then
  [[ "${MOCK_DIRTY_SOURCE:-false}" == false ]]
  exit
fi
[[ "$1" == -C && "$3" == rev-parse && "$4" == HEAD ]] || exit 2
printf '%s\n' "${MOCK_GIT_SHA:-$MOCK_SOURCE_SHA}"
MOCK_GIT

cat > "$mock_bin/gh" <<'MOCK_GH'
#!/usr/bin/env bash
set -euo pipefail
[[ "${1:-}" == api ]] || exit 2
shift
endpoint=''
while (($#)); do
  case "$1" in
    -H) shift 2 ;;
    --paginate|--slurp) shift ;;
    *) endpoint="$1"; shift ;;
  esac
done
case "$endpoint" in
  "repos/$MOCK_REPOSITORY/git/ref/tags/"*)
    jq -n --arg ref "refs/tags/$MOCK_TAG" --arg sha "${MOCK_TAG_SHA:-$MOCK_SOURCE_SHA}" \
      '{ref:$ref,object:{type:"commit",sha:$sha}}'
    ;;
  "repos/$MOCK_REPOSITORY/actions/runs/$MOCK_RUN_ID")
    jq -n \
      --arg conclusion "${MOCK_RUN_CONCLUSION:-success}" \
      --arg head "${MOCK_RUN_HEAD_SHA:-$MOCK_SOURCE_SHA}" \
      --arg name "${MOCK_RUN_WORKFLOW:-Sunder Agent Family Release}" \
      --arg path "${MOCK_RUN_PATH:-.github/workflows/sunder-package-release.yml}" \
      --arg tag "$MOCK_TAG" \
      --argjson attempt "${MOCK_CURRENT_RUN_ATTEMPT:-$MOCK_RUN_ATTEMPT}" \
      --argjson id "$MOCK_RUN_ID" '
        {id:$id,run_attempt:$attempt,repository:{id:1234,full_name:"Younics/sunder-agent-package"},
         name:$name,path:$path,event:"push",head_branch:$tag,head_sha:$head,
         status:"completed",conclusion:$conclusion}
      '
    ;;
  "repos/$MOCK_REPOSITORY/actions/runs/$MOCK_RUN_ID/attempts/$MOCK_RUN_ATTEMPT")
    jq -n \
      --arg head "${MOCK_RUN_HEAD_SHA:-$MOCK_SOURCE_SHA}" \
      --arg name "${MOCK_RUN_WORKFLOW:-Sunder Agent Family Release}" \
      --arg path "${MOCK_RUN_PATH:-.github/workflows/sunder-package-release.yml}" \
      --arg tag "$MOCK_TAG" \
      --argjson attempt "${MOCK_ATTEMPT_RESULT:-$MOCK_RUN_ATTEMPT}" \
      --argjson id "$MOCK_RUN_ID" '
        {id:$id,run_attempt:$attempt,repository:{full_name:"Younics/sunder-agent-package"},
         name:$name,path:$path,event:"push",head_branch:$tag,head_sha:$head,
         status:"completed",conclusion:"success"}
      '
    ;;
  "repos/$MOCK_REPOSITORY/actions/runs/$MOCK_RUN_ID/attempts/$MOCK_ORIGIN_ATTEMPT")
    jq -n \
      --arg conclusion "${MOCK_ORIGIN_CONCLUSION:-failure}" \
      --arg head "${MOCK_RUN_HEAD_SHA:-$MOCK_SOURCE_SHA}" \
      --arg name "${MOCK_RUN_WORKFLOW:-Sunder Agent Family Release}" \
      --arg path "${MOCK_RUN_PATH:-.github/workflows/sunder-package-release.yml}" \
      --arg tag "$MOCK_TAG" \
      --argjson attempt "$MOCK_ORIGIN_ATTEMPT" \
      --argjson id "$MOCK_RUN_ID" '
        {id:$id,run_attempt:$attempt,repository:{full_name:"Younics/sunder-agent-package"},
         name:$name,path:$path,event:"push",head_branch:$tag,head_sha:$head,
         status:"completed",conclusion:$conclusion}
      '
    ;;
  "repos/$MOCK_REPOSITORY/actions/runs/$MOCK_RUN_ID/artifacts?name="*)
    jq -n \
      --arg digest "$MOCK_ARTIFACT_DIGEST" \
      --arg head "${MOCK_ARTIFACT_HEAD_SHA:-$MOCK_SOURCE_SHA}" \
      --arg name "${MOCK_ARTIFACT_NAME_RESULT:-$MOCK_ARTIFACT_NAME}" \
      --argjson id "$MOCK_ARTIFACT_ID" \
      --argjson run_id "$MOCK_RUN_ID" '
        {artifacts:[{id:$id,name:$name,digest:$digest,expired:false,size_in_bytes:4096,
          workflow_run:{id:$run_id,head_sha:$head}}]}
      '
    ;;
  "repos/$MOCK_REPOSITORY/actions/artifacts/$MOCK_ARTIFACT_ID")
    jq -n \
      --arg created "${MOCK_ARTIFACT_CREATED_AT:-2026-08-24T12:05:00Z}" \
      --arg digest "$MOCK_ARTIFACT_DIGEST" \
      --arg head "${MOCK_ARTIFACT_HEAD_SHA:-$MOCK_SOURCE_SHA}" \
      --arg name "${MOCK_ARTIFACT_NAME_RESULT:-$MOCK_ARTIFACT_NAME}" \
      --arg repository "$MOCK_REPOSITORY" \
      --argjson id "$MOCK_ARTIFACT_ID" \
      --argjson run_id "$MOCK_RUN_ID" '
        {id:$id,name:$name,digest:$digest,expired:false,size_in_bytes:4096,created_at:$created,
         url:("https://api.github.com/repos/"+$repository+"/actions/artifacts/"+($id|tostring)),
         archive_download_url:("https://api.github.com/repos/"+$repository+"/actions/artifacts/"+($id|tostring)+"/zip"),
         workflow_run:{id:$run_id,head_sha:$head}}
      '
    ;;
  "repos/$MOCK_REPOSITORY/actions/runs/$MOCK_RUN_ID/attempts/$MOCK_RUN_ATTEMPT/jobs?per_page=100")
    jq -n --argjson attempt "$MOCK_RUN_ATTEMPT" --argjson id "$MOCK_RUN_ID" \
      '[{total_count:1,jobs:[{run_id:$id,run_attempt:$attempt,started_at:"2026-08-24T12:00:00Z",completed_at:"2026-08-24T12:10:00Z"}]}]'
    ;;
  "repos/$MOCK_REPOSITORY/releases/$MOCK_RELEASE_ID")
    command cat "$MOCK_RELEASE_JSON"
    ;;
  "https://api.github.com/repos/$MOCK_REPOSITORY/releases/assets/"*)
    id="${endpoint##*/}"
    name="$(jq -er --argjson id "$id" '.assets[] | select(.id == $id) | .name' "$MOCK_RELEASE_JSON")"
    command cat "$MOCK_EVIDENCE_DIRECTORY/$name"
    ;;
  *)
    printf 'Unsupported mock GitHub endpoint: %s\n' "$endpoint" >&2
    exit 2
    ;;
esac
MOCK_GH
chmod +x "$mock_bin/git" "$mock_bin/gh"

reset_fixture() {
  : > "$MOCK_EVENT_LOG"
  cp "$fixture_root/release-template.json" "$MOCK_RELEASE_JSON"
  cp "$fixture_root/handoff-template.json" "$evidence_directory/publication-handoff-provenance.json"
  unset MOCK_ARTIFACT_CREATED_AT MOCK_ARTIFACT_HEAD_SHA MOCK_ARTIFACT_NAME_RESULT \
    MOCK_ATTEMPT_RESULT MOCK_CURRENT_RUN_ATTEMPT MOCK_GIT_SHA MOCK_RUN_CONCLUSION \
    MOCK_ORIGIN_CONCLUSION MOCK_RUN_HEAD_SHA MOCK_RUN_PATH MOCK_RUN_WORKFLOW MOCK_TAG_SHA
}

run_verifier() {
  local run_id="${1:-$MOCK_RUN_ID}"
  local run_attempt="${2:-$MOCK_RUN_ATTEMPT}"
  local source_sha="${3:-$MOCK_SOURCE_SHA}"
  PATH="$mock_bin:$PATH" GH_REPO="$MOCK_REPOSITORY" GH_TOKEN=fixture-token \
    bash "$verifier" "$MOCK_RELEASE_ID" "$MOCK_TAG" "$source_sha" "$run_id" "$run_attempt" \
      "$source_directory" "$evidence_directory"
}

expect_rejected() {
  local name="$1"
  shift
  if run_verifier "$@" > "$fixture_root/$name.log" 2>&1; then
    printf 'Agent publication fixture unexpectedly accepted %s.\n' "$name" >&2
    exit 1
  fi
  ! grep -Fxq MALICIOUS_TAG_POLICY_EXECUTED "$MOCK_EVENT_LOG"
}

reset_fixture
jq '.invocation.runAttempt = "1"' "$evidence_directory/publication-handoff-provenance.json" \
  > "$evidence_directory/publication-handoff-provenance.json.next"
mv "$evidence_directory/publication-handoff-provenance.json.next" \
  "$evidence_directory/publication-handoff-provenance.json"
expect_rejected failed-first-attempt "$MOCK_RUN_ID" "$MOCK_ORIGIN_ATTEMPT"

reset_fixture
run_verifier > "$fixture_root/valid.log"
grep -Fq 'successful attempt 2, immutable provenance attempt 1' "$fixture_root/valid.log"
grep -Fq 'Verified Agent publication release' "$fixture_root/valid.log"
! grep -Fxq MALICIOUS_TAG_POLICY_EXECUTED "$MOCK_EVENT_LOG"

reset_fixture
cp "$source_directory/packages.json" "$fixture_root/inventory-template.json"
jq '.packages += [{artifactType:"nuget",packageId:"Sunder.Package.Agent.Protocol",projectPath:"src/Protocol/Protocol.csproj"}]' \
  "$fixture_root/inventory-template.json" > "$source_directory/packages.json"
expect_rejected obsolete-sixteenth-artifact
grep -Fq 'exactly 15 unique canonical package archives' "$fixture_root/obsolete-sixteenth-artifact.log"
cp "$fixture_root/inventory-template.json" "$source_directory/packages.json"

reset_fixture
export MOCK_ORIGIN_CONCLUSION=success
expect_rejected successful-origin-cannot-be-recovered

reset_fixture
jq '.invocation.runAttempt = "3"' "$evidence_directory/publication-handoff-provenance.json" \
  > "$evidence_directory/publication-handoff-provenance.json.next"
mv "$evidence_directory/publication-handoff-provenance.json.next" \
  "$evidence_directory/publication-handoff-provenance.json"
expect_rejected forged-current-handoff-attempt

reset_fixture
jq '.immutableRelease.provenanceRunAttempt = "2"' "$evidence_directory/publication-handoff-provenance.json" \
  > "$evidence_directory/publication-handoff-provenance.json.next"
mv "$evidence_directory/publication-handoff-provenance.json.next" \
  "$evidence_directory/publication-handoff-provenance.json"
expect_rejected forged-origin-handoff-attempt

reset_fixture
jq '.invocation.runId = "8002"' "$evidence_directory/publication-handoff-provenance.json" \
  > "$evidence_directory/publication-handoff-provenance.json.next"
mv "$evidence_directory/publication-handoff-provenance.json.next" \
  "$evidence_directory/publication-handoff-provenance.json"
expect_rejected forged-handoff-run

reset_fixture
jq '.immutableRelease.provenanceSha256 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"' \
  "$evidence_directory/publication-handoff-provenance.json" \
  > "$evidence_directory/publication-handoff-provenance.json.next"
mv "$evidence_directory/publication-handoff-provenance.json.next" \
  "$evidence_directory/publication-handoff-provenance.json"
expect_rejected forged-provenance-digest

reset_fixture
jq '.immutableRelease.assetsSha256 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"' \
  "$evidence_directory/publication-handoff-provenance.json" \
  > "$evidence_directory/publication-handoff-provenance.json.next"
mv "$evidence_directory/publication-handoff-provenance.json.next" \
  "$evidence_directory/publication-handoff-provenance.json"
expect_rejected forged-release-assets-digest

reset_fixture
jq '.draft = false' "$MOCK_RELEASE_JSON" > "$MOCK_RELEASE_JSON.next"
mv "$MOCK_RELEASE_JSON.next" "$MOCK_RELEASE_JSON"
expect_rejected finalized-release-when-draft-required
SUNDER_AGENT_PUBLICATION_EXPECTED_DRAFT=false run_verifier > "$fixture_root/finalized-valid.log"

reset_fixture
export MOCK_RUN_WORKFLOW='Untrusted Agent Release'
expect_rejected wrong-workflow

reset_fixture
export MOCK_RUN_HEAD_SHA=3333333333333333333333333333333333333333
expect_rejected wrong-workflow-source

reset_fixture
expect_rejected wrong-publication-run 8002

reset_fixture
expect_rejected wrong-publication-attempt "$MOCK_RUN_ID" 3

reset_fixture
export MOCK_ATTEMPT_RESULT=3
expect_rejected wrong-attempt-metadata

reset_fixture
export MOCK_CURRENT_RUN_ATTEMPT=3
export MOCK_ARTIFACT_CREATED_AT=2026-08-24T12:20:00Z
expect_rejected rerun-artifact-from-different-attempt

reset_fixture
export MOCK_ARTIFACT_NAME_RESULT=sunder-agent-publication-evidence-424242-3
expect_rejected wrong-artifact-name

reset_fixture
export MOCK_TAG_SHA=3333333333333333333333333333333333333333
expect_rejected tag-source-mismatch

reset_fixture
export MOCK_GIT_SHA=3333333333333333333333333333333333333333
expect_rejected stale-release-source-checkout

reset_fixture
tampered="$evidence_directory/${primary_names[0]}"
cp "$tampered" "$fixture_root/original-primary"
printf 'tampered bytes\n' > "$tampered"
expect_rejected handoff-byte-tamper
cp "$fixture_root/original-primary" "$tampered"

printf 'Agent publication fixture passed: failed attempt 1 recovers through successful same-run attempt 2 with trusted-policy isolation, exact artifact binding, forged handoff rejection, tag identity, and byte integrity.\n'
