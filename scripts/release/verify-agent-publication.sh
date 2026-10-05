#!/usr/bin/env bash
set -euo pipefail
export LC_ALL=C

if [[ $# -ne 7 ]]; then
  printf 'Usage: verify-agent-publication.sh <release-id> <tag> <source-sha> <publication-run-id> <publication-run-attempt> <release-source-directory> <publication-evidence-directory>\n' >&2
  exit 2
fi

release_id="$1"
tag="$2"
source_sha="$3"
publication_run_id="$4"
publication_run_attempt="$5"
release_source_directory="$6"
evidence_directory="$7"
script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
workflow_name='Sunder Agent Family Release'
workflow_path='.github/workflows/sunder-package-release.yml'
workflow_event='push'
expected_draft="${SUNDER_AGENT_PUBLICATION_EXPECTED_DRAFT:-true}"

[[ "$release_id" =~ ^[1-9][0-9]*$ \
    && "$publication_run_id" =~ ^[1-9][0-9]*$ \
    && "$publication_run_attempt" =~ ^[1-9][0-9]*$ ]] \
  || { printf 'Release, publication run, and publication attempt must be positive decimal integers.\n' >&2; exit 2; }
[[ "$source_sha" =~ ^[0-9a-f]{40}$ ]] \
  || { printf 'Publication source SHA must be a full lowercase Git commit.\n' >&2; exit 2; }
[[ "$expected_draft" == true || "$expected_draft" == false || "$expected_draft" == any ]] \
  || { printf 'SUNDER_AGENT_PUBLICATION_EXPECTED_DRAFT must be true, false, or any.\n' >&2; exit 2; }
if [[ ! "$tag" =~ ^agent/v((0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-([0-9A-Za-z-]+)(\.[0-9A-Za-z-]+)*)?)$ ]]; then
  printf 'Publication tag must use agent/v<strict-semver-without-build-metadata>.\n' >&2
  exit 2
fi
version="${BASH_REMATCH[1]}"
is_prerelease=false
if [[ "$version" == *-* ]]; then
  is_prerelease=true
  IFS='.' read -r -a identifiers <<< "${version#*-}"
  for identifier in "${identifiers[@]}"; do
    if [[ "$identifier" =~ ^[0-9]+$ && "$identifier" == 0* && "$identifier" != 0 ]]; then
      printf 'Numeric SemVer prerelease identifiers must not contain leading zeroes.\n' >&2
      exit 2
    fi
  done
fi
[[ -d "$release_source_directory" && ! -L "$release_source_directory" ]] \
  || { printf 'Immutable Agent release source must be a real directory.\n' >&2; exit 1; }
[[ -d "$evidence_directory" && ! -L "$evidence_directory" ]] \
  || { printf 'Publication evidence must be a real directory.\n' >&2; exit 1; }
: "${GH_REPO:?GH_REPO is required}"
[[ "$GH_REPO" == 'Younics/sunder-agent-package' ]] \
  || { printf 'Unexpected Agent repository: %s\n' "$GH_REPO" >&2; exit 2; }

for required_command in cmp cut find gh git grep jq mktemp python3 sort tr wc; do
  command -v "$required_command" >/dev/null 2>&1 \
    || { printf '%s is required to verify Agent publication.\n' "$required_command" >&2; exit 127; }
done
if ! command -v sha256sum >/dev/null 2>&1 && ! command -v shasum >/dev/null 2>&1; then
  printf 'sha256sum or shasum is required to verify Agent publication.\n' >&2
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

decimal_lte() {
  local left="$1"
  local right="$2"
  ((${#left} < ${#right})) \
    || { ((${#left} == ${#right})) && [[ "$left" == "$right" || "$left" < "$right" ]]; }
}

release_source_directory="$(cd "$release_source_directory" && pwd)"
evidence_directory="$(cd "$evidence_directory" && pwd)"
inventory="$release_source_directory/packages.json"
[[ -f "$inventory" && ! -L "$inventory" ]] \
  || { printf 'Immutable Agent release source does not contain a regular packages.json.\n' >&2; exit 1; }
[[ "$(git -C "$release_source_directory" rev-parse HEAD)" == "$source_sha" ]] \
  || { printf 'Immutable Agent release checkout does not match the selected source SHA.\n' >&2; exit 1; }
git -C "$release_source_directory" diff --quiet HEAD -- packages.json src \
  || { printf 'Immutable Agent release inputs have local changes.\n' >&2; exit 1; }
[[ "$(jq -er '.releaseVersion' "$inventory")" == "$version" ]] \
  || { printf 'Publication tag version does not match release-source packages.json.\n' >&2; exit 1; }

jq -e '.packages | length == 15 and all(.[]; .artifactType == "sunderpkg") and ([.[].packageId] | unique | length) == 15' "$inventory" >/dev/null \
  || { printf 'Immutable Agent release source must identify exactly 15 unique canonical package archives.\n' >&2; exit 1; }
package_ids=()
while IFS= read -r package_id; do package_ids+=("$package_id"); done \
  < <(jq -r '.packages | map(select(.artifactType == "sunderpkg")) | sort_by(.packageId)[] | .packageId' "$inventory")
expected_names=()
for package_id in "${package_ids[@]}"; do expected_names+=("$package_id.$version.sunderpkg"); done
expected_names+=(
  native-test-evidence.json
  package-output-audit.json
  SHA256SUMS
  release-provenance.json
  release-sbom.spdx.json
  toolchain.txt
)
handoff_name='publication-handoff-provenance.json'
expected_handoff_names=("${expected_names[@]}" "$handoff_name")
expected_primary_names=("${expected_names[@]:0:${#expected_names[@]}-4}")
expected_names_json="$(printf '%s\n' "${expected_names[@]}" | jq -Rsc 'split("\n")[:-1] | sort')"
expected_primary_names_json="$(printf '%s\n' "${expected_primary_names[@]}" | jq -Rsc 'split("\n")[:-1] | sort')"

unexpected_entry="$(find "$evidence_directory" -mindepth 1 -maxdepth 1 ! -type f -print -quit)"
[[ -z "$unexpected_entry" ]] \
  || { printf 'Publication handoff contains a non-regular entry.\n' >&2; exit 1; }
actual_count="$(find "$evidence_directory" -mindepth 1 -maxdepth 1 -type f | wc -l | tr -d '[:space:]')"
[[ "$actual_count" == "${#expected_handoff_names[@]}" ]] \
  || { printf 'Publication handoff has the wrong file count.\n' >&2; exit 1; }
for name in "${expected_handoff_names[@]}"; do
  [[ -f "$evidence_directory/$name" && ! -L "$evidence_directory/$name" ]] \
    || { printf 'Publication handoff is missing exact regular file %s.\n' "$name" >&2; exit 1; }
done

expected_ref="refs/tags/$tag"
expected_workflow_ref="$GH_REPO/$workflow_path@$expected_ref"
provenance="$evidence_directory/release-provenance.json"
python3 -B "$script_dir/agent_release.py" verify-provenance "$release_source_directory" "$evidence_directory"
repository_id="$(jq -r '.controlPlane.repositoryId' "$provenance")"
provenance_run_attempt="$(jq -er '.invocation.runAttempt | select(type == "string" and test("^[1-9][0-9]*$"))' "$provenance")" \
  || { printf 'Release provenance does not contain a valid immutable origin attempt.\n' >&2; exit 1; }
decimal_lte "$provenance_run_attempt" "$publication_run_attempt" \
  || { printf 'Release provenance cannot originate from a later attempt than the publication handoff.\n' >&2; exit 1; }
jq -e \
  --arg repository "$GH_REPO" \
  --arg workflow "$workflow_name" \
  --arg workflow_ref "$expected_workflow_ref" \
  --arg run_id "$publication_run_id" \
  --arg run_attempt "$provenance_run_attempt" \
  --argjson release_id "$release_id" \
  --arg ref "$expected_ref" \
  --arg tag "$tag" \
  --arg source "$source_sha" \
  --argjson expected_artifacts "$(printf '%s\n' "${expected_names[@]:0:15}" | jq -Rsc 'split("\n")[:-1] | sort')" '
    .schemaVersion == 1
    and .buildType == "https://sunderapp.io/provenance/github-actions/v1"
    and .invocation == {
      repository:$repository,workflow:$workflow,workflowRef:$workflow_ref,
      runId:$run_id,runAttempt:$run_attempt,releaseId:$release_id,
      ref:$ref,refName:$tag,sha:$source
    }
    and ([.sources[] | {name,sha}] | sort_by(.name)) == ([
      {name:"sunder-agent-package",sha:$source},
      {name:"sunder-core",sha:.materials[0].sha}
    ] | sort_by(.name))
    and (.sources | length) == 2
    and (.materials | type == "array" and length == 1)
    and .materials[0].name == "sunder-core-sdk"
    and .materials[0].tag == ("sdk/v" + .materials[0].packageVersion)
    and (.materials[0].sha | test("^[0-9a-f]{40}$"))
    and ([.artifacts[].path] | sort) == $expected_artifacts
    and ([.artifacts[].path] | length) == ([.artifacts[].path] | unique | length)
    and all(.artifacts[];
      (.sha256 | test("^[0-9a-f]{64}$"))
      and (.size | type == "number" and floor == . and . > 0))
  ' "$provenance" >/dev/null \
  || { printf 'Release provenance is not bound to the exact publication workflow invocation and primary artifacts.\n' >&2; exit 1; }

for name in "${expected_primary_names[@]}"; do
  [[ "$name" == *.sunderpkg ]] || continue
  sha="$(hash_file "$evidence_directory/$name")"
  size="$(file_size "$evidence_directory/$name")"
  jq -e --arg name "$name" --arg sha "$sha" --argjson size "$size" '
    [.artifacts[] | select(.path == $name and .sha256 == $sha and .size == $size)] | length == 1
  ' "$provenance" >/dev/null \
    || { printf 'Publication provenance differs from handoff file %s.\n' "$name" >&2; exit 1; }
done
handoff="$evidence_directory/$handoff_name"
provenance_sha256="$(hash_file "$provenance")"
release_assets_sha256="$(jq -cS \
  '[.immutableRelease.assets[] | {path,sha256,size}] | sort_by(.path)' \
  "$handoff" 2>/dev/null | hash_stream)" \
  || { printf 'Publication handoff does not contain a canonical immutable asset inventory.\n' >&2; exit 1; }
jq -e \
  --arg repository "$GH_REPO" \
  --arg workflow "$workflow_name" \
  --arg workflow_ref "$expected_workflow_ref" \
  --arg run_id "$publication_run_id" \
  --arg run_attempt "$publication_run_attempt" \
  --arg provenance_run_attempt "$provenance_run_attempt" \
  --arg provenance_sha256 "$provenance_sha256" \
  --arg release_assets_sha256 "$release_assets_sha256" \
  --argjson release_id "$release_id" \
  --arg ref "$expected_ref" \
  --arg tag "$tag" \
  --arg source "$source_sha" \
  --arg lock_set_sha256 "$(jq -r '.lockSetSha256' "$provenance")" \
  --argjson expected_assets "$expected_names_json" '
    type == "object"
    and keys == ["buildType","immutableRelease","invocation","schemaVersion"]
    and .schemaVersion == 1
    and .buildType == "https://sunderapp.io/provenance/github-actions-publication-handoff/v1"
    and (.invocation | type == "object" and keys == ["ref","refName","releaseId","repository","runAttempt","runId","sha","workflow","workflowRef"])
    and .invocation == {
      repository:$repository,workflow:$workflow,workflowRef:$workflow_ref,
      runId:$run_id,runAttempt:$run_attempt,releaseId:$release_id,
      ref:$ref,refName:$tag,sha:$source
    }
    and (.immutableRelease | type == "object" and keys == ["artifactSourceCommit","assets","assetsSha256","lockSetSha256","provenanceRunAttempt","provenanceSha256"])
    and .immutableRelease.artifactSourceCommit == $source
    and .immutableRelease.lockSetSha256 == $lock_set_sha256
    and .immutableRelease.assetsSha256 == $release_assets_sha256
    and .immutableRelease.provenanceRunAttempt == $provenance_run_attempt
    and .immutableRelease.provenanceSha256 == $provenance_sha256
    and .immutableRelease.assets == (.immutableRelease.assets | sort_by(.path))
    and ([.immutableRelease.assets[].path] | sort) == $expected_assets
    and ([.immutableRelease.assets[].path] | length) == ([.immutableRelease.assets[].path] | unique | length)
    and ([.immutableRelease.assets[].id] | length) == ([.immutableRelease.assets[].id] | unique | length)
    and all(.immutableRelease.assets[];
      type == "object" and keys == ["id","path","sha256","size"]
      and (.id | type == "number" and floor == . and . > 0)
      and (.sha256 | type == "string" and test("^[0-9a-f]{64}$"))
      and (.size | type == "number" and floor == . and . > 0))
  ' "$handoff" >/dev/null \
  || { printf 'Publication handoff provenance is not bound to the exact current attempt and immutable release.\n' >&2; exit 1; }
for name in "${expected_names[@]}"; do
  sha="$(hash_file "$evidence_directory/$name")"
  size="$(file_size "$evidence_directory/$name")"
  jq -e --arg name "$name" --arg sha "$sha" --argjson size "$size" '
    [.immutableRelease.assets[] | select(.path == $name and .sha256 == $sha and .size == $size)] | length == 1
  ' "$handoff" >/dev/null \
    || { printf 'Publication handoff provenance differs from immutable release file %s.\n' "$name" >&2; exit 1; }
done
sums="$evidence_directory/SHA256SUMS"
sbom="$evidence_directory/release-sbom.spdx.json"
[[ "$(wc -l < "$sums" | tr -d '[:space:]')" == "${#expected_primary_names[@]}" ]] \
  || { printf 'Publication SHA256SUMS has the wrong primary inventory size.\n' >&2; exit 1; }
jq -e --argjson expected "$expected_primary_names_json" '
  [.files[].fileName | sub("^\\./"; "")] | sort == $expected
' "$sbom" >/dev/null \
  || { printf 'Publication SBOM has the wrong primary inventory.\n' >&2; exit 1; }
for name in "${expected_primary_names[@]}"; do
  sha="$(hash_file "$evidence_directory/$name")"
  grep -Fqx "$sha  $name" "$sums" \
    || { printf 'Publication SHA256SUMS differs for %s.\n' "$name" >&2; exit 1; }
  jq -e --arg name "./$name" --arg sha "$sha" '
    [.files[] | select(
      .fileName == $name
      and ([.checksums[] | select(.algorithm == "SHA256" and .checksumValue == $sha)] | length == 1)
    )] | length == 1
  ' "$sbom" >/dev/null \
    || { printf 'Publication SBOM checksum differs for %s.\n' "$name" >&2; exit 1; }
done

encoded_tag="$(jq -rn --arg value "$tag" '$value | @uri')"
ref_json="$(gh api "repos/$GH_REPO/git/ref/tags/$encoded_tag")"
object_sha="$(jq -er --arg ref "$expected_ref" 'select(.ref == $ref) | .object.sha | select(test("^[0-9a-f]{40}$"))' <<< "$ref_json")" \
  || { printf 'GitHub did not return the exact Agent tag ref.\n' >&2; exit 1; }
object_type="$(jq -er '.object.type | select(. == "commit" or . == "tag")' <<< "$ref_json")" \
  || { printf 'Agent tag ref has an unsupported object type.\n' >&2; exit 1; }
for _ in {1..8}; do
  if [[ "$object_type" == commit ]]; then break; fi
  object_json="$(gh api "repos/$GH_REPO/git/tags/$object_sha")"
  object_sha="$(jq -er '.object.sha | select(test("^[0-9a-f]{40}$"))' <<< "$object_json")"
  object_type="$(jq -er '.object.type | select(. == "commit" or . == "tag")' <<< "$object_json")"
done
[[ "$object_type" == commit && "$object_sha" == "$source_sha" ]] \
  || { printf 'Exact Agent tag ref does not peel to the publication source SHA.\n' >&2; exit 1; }

run_json="$(gh api "repos/$GH_REPO/actions/runs/$publication_run_id")"
jq -e --argjson id "$repository_id" '.repository.id == $id' <<< "$run_json" >/dev/null \
  || { printf 'Publication repository numeric identity differs from provenance.\n' >&2; exit 1; }
jq -e \
  --argjson id "$publication_run_id" \
  --argjson attempt "$publication_run_attempt" \
  --arg repository "$GH_REPO" \
  --arg workflow "$workflow_name" \
  --arg path "$workflow_path" \
  --arg tag "$tag" \
  --arg source "$source_sha" '
    .id == $id
    and (.run_attempt | type == "number" and floor == . and . >= $attempt)
    and .repository.full_name == $repository
    and .name == $workflow
    and .path == $path
    and .event == "push"
    and .head_branch == $tag
    and .head_sha == $source
    and .status == "completed"
    and .conclusion == "success"
  ' <<< "$run_json" >/dev/null \
  || { printf 'Publication run is not the successful mandated Agent tag-push workflow for the exact source and tag.\n' >&2; exit 1; }

attempt_json="$(gh api "repos/$GH_REPO/actions/runs/$publication_run_id/attempts/$publication_run_attempt")"
jq -e \
  --argjson id "$publication_run_id" \
  --argjson attempt "$publication_run_attempt" \
  --arg repository "$GH_REPO" \
  --arg workflow "$workflow_name" \
  --arg path "$workflow_path" \
  --arg tag "$tag" \
  --arg source "$source_sha" '
    .id == $id
    and .run_attempt == $attempt
    and .repository.full_name == $repository
    and .name == $workflow
    and .path == $path
    and .event == "push"
    and .head_branch == $tag
    and .head_sha == $source
    and .status == "completed"
    and .conclusion == "success"
  ' <<< "$attempt_json" >/dev/null \
  || { printf 'Publication attempt is not the exact successful Agent tag-push workflow attempt.\n' >&2; exit 1; }

if [[ "$provenance_run_attempt" != "$publication_run_attempt" ]]; then
  provenance_attempt_json="$(gh api "repos/$GH_REPO/actions/runs/$publication_run_id/attempts/$provenance_run_attempt")"
  jq -e \
    --argjson id "$publication_run_id" \
    --argjson attempt "$provenance_run_attempt" \
    --arg repository "$GH_REPO" \
    --arg workflow "$workflow_name" \
    --arg path "$workflow_path" \
    --arg tag "$tag" \
    --arg source "$source_sha" '
      .id == $id
      and .run_attempt == $attempt
      and .repository.full_name == $repository
      and .name == $workflow
      and .path == $path
      and .event == "push"
      and .head_branch == $tag
      and .head_sha == $source
      and .status == "completed"
      and (.conclusion | type == "string" and . != "success")
    ' <<< "$provenance_attempt_json" >/dev/null \
    || { printf 'Recovered release provenance did not originate from an exact failed attempt of the same run.\n' >&2; exit 1; }
fi

artifact_name="sunder-agent-publication-evidence-$release_id-$publication_run_attempt"
encoded_artifact_name="$(jq -rn --arg value "$artifact_name" '$value | @uri')"
artifacts_json="$(gh api "repos/$GH_REPO/actions/runs/$publication_run_id/artifacts?name=$encoded_artifact_name")"
jq -e \
  --arg name "$artifact_name" \
  --argjson run_id "$publication_run_id" \
  --arg source "$source_sha" '
    (.artifacts | type == "array")
    and ([.artifacts[] | select(.expired == false)] | length) == 1
    and (.artifacts[] | select(.expired == false)
      | .name == $name
        and (.id | type == "number" and floor == . and . > 0)
        and (.size_in_bytes | type == "number" and floor == . and . > 0)
        and (.digest | test("^sha256:[0-9a-f]{64}$"))
        and .workflow_run.id == $run_id
        and .workflow_run.head_sha == $source)
  ' <<< "$artifacts_json" >/dev/null \
  || { printf 'Publication handoff artifact is not uniquely bound to the selected run and source.\n' >&2; exit 1; }
artifact_id="$(jq -er '.artifacts[] | select(.expired == false) | .id' <<< "$artifacts_json")"
artifact_json="$(gh api "repos/$GH_REPO/actions/artifacts/$artifact_id")"
jq -e \
  --arg digest "$(jq -er '.artifacts[] | select(.expired == false) | .digest' <<< "$artifacts_json")" \
  --arg name "$artifact_name" \
  --arg repository "$GH_REPO" \
  --arg source "$source_sha" \
  --argjson id "$artifact_id" \
  --argjson run_id "$publication_run_id" '
    .id == $id and .name == $name and .digest == $digest and .expired == false
    and .url == ("https://api.github.com/repos/" + $repository + "/actions/artifacts/" + ($id | tostring))
    and .archive_download_url == ("https://api.github.com/repos/" + $repository + "/actions/artifacts/" + ($id | tostring) + "/zip")
    and .workflow_run.id == $run_id and .workflow_run.head_sha == $source
    and (.created_at | type == "string")
  ' <<< "$artifact_json" >/dev/null \
  || { printf 'Publication handoff artifact metadata does not match its selected immutable identity.\n' >&2; exit 1; }
jobs_json="$(gh api --paginate --slurp \
  "repos/$GH_REPO/actions/runs/$publication_run_id/attempts/$publication_run_attempt/jobs?per_page=100")"
jq -e \
  --arg attempt "$publication_run_attempt" \
  --arg id "$publication_run_id" \
  --argjson artifact "$artifact_json" '
    type == "array" and length > 0
    and (.[0].total_count | type == "number" and floor == . and . > 0)
    and (.[0].total_count == ([.[].jobs[]] | length))
    and all(.[].jobs[]; (.run_id | tostring) == $id and (.run_attempt | tostring) == $attempt)
    and ([.[].jobs[] | select(.started_at != null and .completed_at != null)] | length) > 0
    and (($artifact.created_at | fromdateiso8601)
      >= ([.[].jobs[] | select(.started_at != null) | (.started_at | fromdateiso8601)] | min))
    and (($artifact.created_at | fromdateiso8601)
      <= ([.[].jobs[] | select(.completed_at != null) | (.completed_at | fromdateiso8601)] | max))
  ' <<< "$jobs_json" >/dev/null \
  || { printf 'Publication handoff artifact was not created by the selected run attempt.\n' >&2; exit 1; }

release_json="$(gh api "repos/$GH_REPO/releases/$release_id")"
acceptance_pattern='^(agent-family-acceptance-[1-9][0-9]*-[1-9][0-9]*|agent-family-cohort-[0-9a-f]{64})[.]json$'
jq -e \
  --argjson id "$release_id" \
  --arg tag "$tag" \
  --arg source "$source_sha" \
  --arg expected_draft "$expected_draft" \
  --argjson prerelease "$is_prerelease" \
  --arg acceptance_pattern "$acceptance_pattern" \
  --argjson expected_names "$expected_names_json" '
    .id == $id and .tag_name == $tag
    and (if $expected_draft == "any" then (.draft | type == "boolean") else (.draft | tostring) == $expected_draft end)
    and .prerelease == $prerelease
    and .name == ("Sunder Agent Family " + ($tag | sub("^agent/v"; "")))
    and (.body | capture("(?m)^Agent source commit: (?<sha>[0-9a-f]{40})$").sha) == $source
    and ([.assets[].name] | length) == ([.assets[].name] | unique | length)
    and ([.assets[].id] | length) == ([.assets[].id] | unique | length)
    and ([.assets[] | select((.name | test($acceptance_pattern)) | not) | .name] | sort) == $expected_names
    and all(.assets[];
      .state == "uploaded"
      and (.id | type == "number" and floor == . and . > 0)
      and (.size | type == "number" and floor == . and . > 0)
      and (.digest | test("^sha256:[0-9a-f]{64}$")))
    and all(.assets[] | select(.name | startswith("agent-family-acceptance-")); .name | test($acceptance_pattern))
  ' <<< "$release_json" >/dev/null \
  || { printf 'Numeric Agent draft or its publication asset inventory does not match the handoff.\n' >&2; exit 1; }

remote_directory="$(mktemp -d "${RUNNER_TEMP:-${TMPDIR:-/tmp}}/sunder-agent-publication-verify.XXXXXX")"
trap 'rm -rf "$remote_directory"' EXIT
for name in "${expected_names[@]}"; do
  local_path="$evidence_directory/$name"
  sha="$(hash_file "$local_path")"
  size="$(file_size "$local_path")"
  asset_record="$(jq -ce --arg name "$name" --arg digest "sha256:$sha" --argjson size "$size" '
    [.assets[] | select(.name == $name and .digest == $digest and .size == $size)]
    | if length == 1 then .[0] else error("asset mismatch") end
  ' <<< "$release_json")" \
    || { printf 'Numeric Agent draft metadata differs for publication file %s.\n' "$name" >&2; exit 1; }
  asset_id="$(jq -r '.id' <<< "$asset_record")"
  [[ "$asset_id" == "$(jq -r --arg name "$name" '.immutableRelease.assets[] | select(.path == $name) | .id' "$handoff")" ]] \
    || { printf 'Immutable publication asset id was replaced: %s.\n' "$name" >&2; exit 1; }
  expected_url="https://api.github.com/repos/$GH_REPO/releases/assets/$asset_id"
  [[ "$(jq -r '.url' <<< "$asset_record")" == "$expected_url" ]] \
    || { printf 'Numeric Agent draft asset URL is not identity-bound for %s.\n' "$name" >&2; exit 1; }
  gh api -H 'Accept: application/octet-stream' "$expected_url" > "$remote_directory/$name"
  cmp -s "$local_path" "$remote_directory/$name" \
    || { printf 'Numeric Agent draft bytes differ from publication handoff file %s.\n' "$name" >&2; exit 1; }
done

for name in SHA256SUMS release-provenance.json release-sbom.spdx.json toolchain.txt; do
  expected_line="$name SHA-256: $(hash_file "$evidence_directory/$name")"
  grep -Fqx "$expected_line" <<< "$(jq -r '.body' <<< "$release_json")" \
    || { printf 'Agent draft body does not bind evidence file %s.\n' "$name" >&2; exit 1; }
done

printf 'Verified Agent publication release %s, run %s successful attempt %s, immutable provenance attempt %s, exact handoff artifact, tag, draft, and bytes.\n' \
  "$release_id" "$publication_run_id" "$publication_run_attempt" "$provenance_run_attempt"
