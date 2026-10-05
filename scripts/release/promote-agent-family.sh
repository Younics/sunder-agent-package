#!/usr/bin/env bash
set -euo pipefail
export LC_ALL=C

usage() {
  printf 'Usage: promote-agent-family.sh <agent/v-version> <source-sha> <registry-api-url> <acceptance-evidence.json> <verification-directory>\n' >&2
}

if [[ $# -ne 5 ]]; then
  usage
  exit 2
fi

tag="$1"
source_sha="$2"
registry_api="${3%/}"
acceptance_evidence="$4"
verification_directory="$5"
guard_only="${SUNDER_AGENT_PROMOTION_GUARD_ONLY:-false}"
candidate_only="${SUNDER_AGENT_ACCEPTANCE_CANDIDATE_ONLY:-false}"
script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
policy_root="$(cd "$script_dir/../.." && pwd)"
release_source_directory="${SUNDER_AGENT_RELEASE_SOURCE_DIRECTORY:-$policy_root}"
publication_evidence_directory="${PUBLICATION_EVIDENCE_DIRECTORY:-}"

: "${ACCEPTANCE_RUN_ATTEMPT:?ACCEPTANCE_RUN_ATTEMPT is required}"
: "${ACCEPTANCE_RUN_ID:?ACCEPTANCE_RUN_ID is required}"
: "${ACCEPTANCE_WORKFLOW_SHA:?ACCEPTANCE_WORKFLOW_SHA is required}"
: "${GH_REPO:?GH_REPO is required}"
: "${GH_TOKEN:?GH_TOKEN is required}"
: "${PUBLICATION_RUN_ATTEMPT:?PUBLICATION_RUN_ATTEMPT is required}"
: "${PUBLICATION_RUN_ID:?PUBLICATION_RUN_ID is required}"
: "${SUNDER_AGENT_POLICY_SHA:?SUNDER_AGENT_POLICY_SHA is required}"
: "${SUNDER_AGENT_POLICY_WORKFLOW_PATH:?SUNDER_AGENT_POLICY_WORKFLOW_PATH is required}"
if [[ "$candidate_only" != true ]]; then
  : "${ACCEPTANCE_EVIDENCE_SHA256:?ACCEPTANCE_EVIDENCE_SHA256 is required}"
else
  ACCEPTANCE_EVIDENCE_SHA256="${ACCEPTANCE_EVIDENCE_SHA256:-}"
fi
registry_cli_token="${SUNDER_REGISTRY_CLI_TOKEN:-}"
unset SUNDER_REGISTRY_CLI_TOKEN

acceptance_artifact_name="sunder-agent-production-acceptance-$ACCEPTANCE_RUN_ID-$ACCEPTANCE_RUN_ATTEMPT"
acceptance_release_asset_name="agent-family-acceptance-$ACCEPTANCE_RUN_ID-$ACCEPTANCE_RUN_ATTEMPT.json"
acceptance_release_asset_pattern='^(agent-family-acceptance-[1-9][0-9]*-[1-9][0-9]*|agent-family-cohort-[0-9a-f]{64})[.]json$'
acceptance_workflow_event='workflow_dispatch'
acceptance_workflow_name='Sunder Agent Production Acceptance'
acceptance_workflow_path='.github/workflows/sunder-agent-production-acceptance.yml'
acceptance_workflow_ref='refs/heads/main'
publication_artifact_name=''
publication_workflow_event='push'
publication_workflow_name='Sunder Agent Family Release'
publication_workflow_path='.github/workflows/sunder-package-release.yml'
policy_ref='refs/heads/main'

[[ "$tag" =~ ^agent/v((0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?)$ ]] \
  || { printf 'Promotion tag must use agent/v<strict-semver-without-build-metadata>.\n' >&2; exit 2; }
version="${BASH_REMATCH[1]}"
if [[ "$version" == *-* ]]; then
  prerelease="${version#*-}"
  IFS='.' read -r -a prerelease_identifiers <<< "$prerelease"
  for identifier in "${prerelease_identifiers[@]}"; do
    if [[ "$identifier" =~ ^[0-9]+$ && "$identifier" == 0* && "$identifier" != 0 ]]; then
      printf 'Numeric SemVer prerelease identifiers must not contain leading zeroes.\n' >&2
      exit 2
    fi
  done
fi
[[ "$source_sha" =~ ^[0-9a-f]{40}$ ]] \
  || { printf 'Promotion source SHA must be a full lowercase Git SHA.\n' >&2; exit 2; }
[[ "$ACCEPTANCE_WORKFLOW_SHA" =~ ^[0-9a-f]{40}$ && "$SUNDER_AGENT_POLICY_SHA" =~ ^[0-9a-f]{40}$ ]] \
  || { printf 'Acceptance and current policy SHAs must be full lowercase Git SHAs.\n' >&2; exit 2; }
if [[ "$candidate_only" != true ]]; then
  [[ "$ACCEPTANCE_EVIDENCE_SHA256" =~ ^[0-9a-f]{64}$ ]] \
    || { printf 'ACCEPTANCE_EVIDENCE_SHA256 must be a lowercase SHA-256 digest.\n' >&2; exit 2; }
fi
[[ "$ACCEPTANCE_RUN_ATTEMPT" =~ ^[1-9][0-9]*$ ]] \
  || { printf 'ACCEPTANCE_RUN_ATTEMPT must be a positive run attempt.\n' >&2; exit 2; }
[[ "$ACCEPTANCE_RUN_ID" =~ ^[1-9][0-9]*$ ]] \
  || { printf 'ACCEPTANCE_RUN_ID must be a positive workflow run id.\n' >&2; exit 2; }
[[ "$PUBLICATION_RUN_ATTEMPT" =~ ^[1-9][0-9]*$ && "$PUBLICATION_RUN_ID" =~ ^[1-9][0-9]*$ ]] \
  || { printf 'Publication run id and attempt must be positive.\n' >&2; exit 2; }
[[ "$registry_api" =~ ^https://[^/@?#]+$ ]] \
  || { printf 'Registry API URL must be an absolute HTTPS origin without user info, path, query, or fragment.\n' >&2; exit 2; }
[[ "$guard_only" == true || "$guard_only" == false ]] \
  || { printf 'SUNDER_AGENT_PROMOTION_GUARD_ONLY must be true or false.\n' >&2; exit 2; }
[[ "$candidate_only" == true || "$candidate_only" == false ]] \
  || { printf 'SUNDER_AGENT_ACCEPTANCE_CANDIDATE_ONLY must be true or false.\n' >&2; exit 2; }
[[ "$guard_only" != true || "$candidate_only" != true ]] \
  || { printf 'Acceptance candidate and promotion guard modes cannot be combined.\n' >&2; exit 2; }
if [[ "$candidate_only" == true ]]; then
  [[ "$SUNDER_AGENT_POLICY_WORKFLOW_PATH" == '.github/workflows/sunder-agent-production-acceptance.yml' ]] \
    || { printf 'Acceptance candidate policy must use the protected acceptance workflow.\n' >&2; exit 2; }
else
  [[ "$SUNDER_AGENT_POLICY_WORKFLOW_PATH" == '.github/workflows/sunder-agent-promotion.yml' ]] \
    || { printf 'Promotion policy must use the protected promotion workflow.\n' >&2; exit 2; }
fi
[[ -f "$acceptance_evidence" ]] \
  || { printf 'Acceptance evidence was not found: %s\n' "$acceptance_evidence" >&2; exit 1; }
[[ -d "$release_source_directory" && ! -L "$release_source_directory" \
    && -f "$release_source_directory/packages.json" && ! -L "$release_source_directory/packages.json" ]] \
  || { printf 'Exact Agent release source directory is unavailable.\n' >&2; exit 1; }
[[ -d "$publication_evidence_directory" && ! -L "$publication_evidence_directory" ]] \
  || { printf 'Exact Agent publication evidence directory is unavailable.\n' >&2; exit 1; }
[[ "$guard_only" == true || ! -e "$verification_directory" ]] \
  || { printf 'Verification directory must not already exist: %s\n' "$verification_directory" >&2; exit 1; }

for required_command in cmp cut gh git grep jq mkdir mktemp python3 sort tr unzip wc; do
  command -v "$required_command" >/dev/null 2>&1 \
    || { printf '%s is required to promote the Agent family.\n' "$required_command" >&2; exit 127; }
done
if ! command -v sha256sum >/dev/null 2>&1 && ! command -v shasum >/dev/null 2>&1; then
  printf 'sha256sum or shasum is required to promote the Agent family.\n' >&2
  exit 127
fi

hash_file() {
  if command -v sha256sum >/dev/null 2>&1; then
    sha256sum "$1" | cut -d ' ' -f 1
  else
    shasum -a 256 "$1" | cut -d ' ' -f 1
  fi
}

hash_stream() {
  if command -v sha256sum >/dev/null 2>&1; then
    sha256sum | cut -d ' ' -f 1
  else
    shasum -a 256 | cut -d ' ' -f 1
  fi
}

file_size() {
  wc -c < "$1" | tr -d '[:space:]'
}

[[ "$(git -C "$policy_root" rev-parse HEAD)" == "$SUNDER_AGENT_POLICY_SHA" ]] \
  || { printf 'Agent release policy was not checked out from the selected protected-main policy SHA.\n' >&2; exit 1; }
[[ "$(git -C "$release_source_directory" rev-parse HEAD)" == "$source_sha" ]] \
  || { printf 'Agent release source was not checked out from the accepted source SHA.\n' >&2; exit 1; }
git -C "$release_source_directory" diff --quiet HEAD -- packages.json src \
  || { printf 'Accepted source inputs have local changes.\n' >&2; exit 1; }
[[ "$version" == "$(jq -er '.releaseVersion' "$release_source_directory/packages.json")" ]] \
  || { printf 'Promotion version does not match the accepted source packages.json.\n' >&2; exit 1; }
python3 -B "$script_dir/agent_release.py" check-json "$acceptance_evidence"
acceptance_phase="$(jq -er '.phase | select(. == "pre-visibility" or . == "post-visibility")' "$acceptance_evidence")" \
  || { printf 'Acceptance must explicitly select pre-visibility or post-visibility.\n' >&2; exit 1; }
cohort_work="$(mktemp -d "${RUNNER_TEMP:-${TMPDIR:-/tmp}}/sunder-agent-cohort.XXXXXX")"
trap 'rm -rf "$cohort_work"' EXIT
cohort_request="$cohort_work/request.json"
cohort_result="$cohort_work/result.json"
jq '.cohort.request' "$acceptance_evidence" > "$cohort_request"
if [[ "$acceptance_phase" == post-visibility ]]; then
  jq '.cohort.result' "$acceptance_evidence" > "$cohort_result"
fi

verify_publication_handoff() {
  local expected_draft="${1:-true}"
  SUNDER_AGENT_PUBLICATION_EXPECTED_DRAFT="$expected_draft" \
    bash "$script_dir/verify-agent-publication.sh" \
    "$accepted_release_id" "$tag" "$source_sha" \
    "$PUBLICATION_RUN_ID" "$PUBLICATION_RUN_ATTEMPT" \
    "$release_source_directory" "$publication_evidence_directory"
}

verify_acceptance_handoff() {
  local expected_draft="${1:-true}"
  verify_publication_handoff "$expected_draft"
  [[ "$(hash_file "$acceptance_evidence")" == "$ACCEPTANCE_EVIDENCE_SHA256" ]] \
    || { printf 'Durable acceptance evidence does not match the explicitly accepted SHA-256.\n' >&2; return 1; }
  bash "$script_dir/verify-acceptance-artifact.sh" \
    "$GH_REPO" "$ACCEPTANCE_WORKFLOW_SHA" "$ACCEPTANCE_RUN_ID" "$ACCEPTANCE_RUN_ATTEMPT"
}

accepted_release_id="$(jq -er '.draftRelease.id | select(type == "number" and floor == . and . > 0)' "$acceptance_evidence")" \
  || { printf 'Acceptance evidence does not contain a positive numeric GitHub release database id.\n' >&2; exit 1; }
publication_artifact_name="sunder-agent-publication-evidence-$accepted_release_id-$PUBLICATION_RUN_ATTEMPT"
if [[ "$candidate_only" == true ]]; then
  verify_publication_handoff true
else
  verify_acceptance_handoff any
fi
publication_handoff="$publication_evidence_directory/publication-handoff-provenance.json"
release_provenance_run_attempt="$(jq -er \
  '.immutableRelease.provenanceRunAttempt | select(type == "string" and test("^[1-9][0-9]*$"))' \
  "$publication_handoff")" \
  || { printf 'Publication handoff does not identify the immutable release provenance attempt.\n' >&2; exit 1; }

jq -e '.packages | length == 15 and all(.[]; .artifactType == "sunderpkg") and ([.[].packageId] | unique | length) == 15' "$release_source_directory/packages.json" >/dev/null \
  || { printf 'Accepted source must contain exactly 15 unique canonical package archives.\n' >&2; exit 1; }

package_ids=()
while IFS= read -r package_id; do package_ids+=("$package_id"); done \
  < <(jq -r '.packages | map(select(.artifactType == "sunderpkg")) | sort_by(.packageId)[] | .packageId' "$release_source_directory/packages.json")
runtime_names=()
for package_id in "${package_ids[@]}"; do runtime_names+=("$package_id.$version.sunderpkg"); done
native_evidence_name='native-test-evidence.json'
output_audit_name='package-output-audit.json'
primary_names=("${runtime_names[@]}" "$native_evidence_name" "$output_audit_name")
final_artifact_names=("${runtime_names[@]}")
evidence_names=(SHA256SUMS release-provenance.json release-sbom.spdx.json toolchain.txt)
expected_release_names=("${primary_names[@]}" "${evidence_names[@]}")
expected_runtime_ids_json="$(printf '%s\n' "${package_ids[@]}" | jq -Rsc 'split("\n")[:-1] | sort')"
expected_primary_names_json="$(printf '%s\n' "${primary_names[@]}" | jq -Rsc 'split("\n")[:-1] | sort')"
expected_release_names_json="$(printf '%s\n' "${expected_release_names[@]}" | jq -Rsc 'split("\n")[:-1] | sort')"

is_prerelease=false
dist_tag=latest
if [[ "$version" == *-* ]]; then
  is_prerelease=true
  dist_tag=preview
fi

accepted_body_sha256="$(jq -er '.draftRelease.bodySha256 | select(type == "string" and test("^[0-9a-f]{64}$"))' "$acceptance_evidence")" \
  || { printf 'Acceptance evidence does not contain a valid original release body SHA-256.\n' >&2; exit 1; }
promotion_receipt_prefix='<!-- sunder-agent-promotion-receipt:v2 '
promotion_receipt_suffix=' -->'
promotion_receipt_json="$(jq -cnS \
  --arg artifact_name "$acceptance_artifact_name" \
  --arg attempt "$ACCEPTANCE_RUN_ATTEMPT" \
  --arg body_sha "$accepted_body_sha256" \
  --arg dist_tag "$dist_tag" \
  --arg event "$acceptance_workflow_event" \
  --arg evidence_sha "$ACCEPTANCE_EVIDENCE_SHA256" \
  --arg name "$acceptance_workflow_name" \
  --arg path "$acceptance_workflow_path" \
  --arg acceptance_workflow_ref "$acceptance_workflow_ref" \
  --arg acceptance_workflow_sha "$ACCEPTANCE_WORKFLOW_SHA" \
  --arg policy_path "$SUNDER_AGENT_POLICY_WORKFLOW_PATH" \
  --arg policy_ref "$policy_ref" \
  --arg policy_sha "$SUNDER_AGENT_POLICY_SHA" \
  --arg publication_artifact "$publication_artifact_name" \
  --arg publication_attempt "$PUBLICATION_RUN_ATTEMPT" \
  --arg publication_event "$publication_workflow_event" \
  --arg publication_name "$publication_workflow_name" \
  --arg publication_path "$publication_workflow_path" \
  --arg publication_run_id "$PUBLICATION_RUN_ID" \
  --arg release_asset_name "$acceptance_release_asset_name" \
  --arg repository "$GH_REPO" \
  --arg run_id "$ACCEPTANCE_RUN_ID" \
  --arg source "$source_sha" \
  --arg tag "$tag" \
  --arg version "$version" \
  --argjson release_id "$accepted_release_id" '
    {
      schemaVersion: 2,
      releaseId: $release_id,
      tag: $tag,
      sourceCommit: $source,
      version: $version,
      distTag: $dist_tag,
      acceptedBodySha256: $body_sha,
      publication: {
        workflow: {
          repository: $repository,
          name: $publication_name,
          path: $publication_path,
          event: $publication_event,
          artifactName: $publication_artifact
        },
        runId: $publication_run_id,
        runAttempt: $publication_attempt
      },
      acceptance: {
        workflow: {
          repository: $repository,
          name: $name,
          path: $path,
          event: $event,
          artifactName: $artifact_name,
          workflowRef: $acceptance_workflow_ref,
          workflowSha: $acceptance_workflow_sha
        },
        runId: $run_id,
        runAttempt: $attempt,
        releaseAssetName: $release_asset_name,
        evidenceSha256: $evidence_sha
      },
      promotionPolicy: {
        workflowPath: $policy_path,
        workflowRef: $policy_ref,
        workflowSha: $policy_sha
      }
    }
  ')"

encoded_tag="$(jq -rn --arg value "$tag" '$value | @uri')"
verify_source_tag_ref() {
  local ref_json
  local object_json
  local object_sha
  local object_type
  local depth

  ref_json="$(gh api "repos/$GH_REPO/git/ref/tags/$encoded_tag")"
  jq -e --arg ref "refs/tags/$tag" '
    .ref == $ref
    and (.object.sha | type == "string" and test("^[0-9a-f]{40}$"))
    and (.object.type == "commit" or .object.type == "tag")
  ' <<< "$ref_json" >/dev/null \
    || { printf 'GitHub did not return the exact expected refs/tags source ref.\n' >&2; return 1; }
  object_sha="$(jq -r '.object.sha' <<< "$ref_json")"
  object_type="$(jq -r '.object.type' <<< "$ref_json")"
  for depth in {1..8}; do
    if [[ "$object_type" == commit ]]; then
      [[ "$object_sha" == "$source_sha" ]] \
        || { printf 'Exact Agent tag ref does not peel to the accepted source SHA.\n' >&2; return 1; }
      return 0
    fi
    [[ "$object_type" == tag ]] \
      || { printf 'Agent tag ref contains an unsupported Git object type.\n' >&2; return 1; }
    object_json="$(gh api "repos/$GH_REPO/git/tags/$object_sha")"
    object_sha="$(jq -er '.object.sha | select(type == "string" and test("^[0-9a-f]{40}$"))' <<< "$object_json")" \
      || { printf 'Annotated Agent tag has an invalid target SHA.\n' >&2; return 1; }
    object_type="$(jq -er '.object.type | select(. == "commit" or . == "tag")' <<< "$object_json")" \
      || { printf 'Annotated Agent tag has an invalid target type.\n' >&2; return 1; }
  done
  printf 'Agent tag ref exceeded the maximum annotated-tag depth.\n' >&2
  return 1
}

release_json=''
release_draft=''
load_accepted_release() {
  local requested_draft="$1"
  local acceptance_asset_count
  local expected_draft
  local expected_release_asset_count="${#expected_release_names[@]}"
  release_json="$(gh api "repos/$GH_REPO/releases/$accepted_release_id")"
  release_draft="$(jq -er 'if (.draft | type) == "boolean" then (.draft | tostring) else error("invalid draft state") end' <<< "$release_json")" \
    || { printf 'Numeric GitHub release has an invalid draft state.\n' >&2; return 1; }
  if [[ "$requested_draft" == any ]]; then
    expected_draft="$release_draft"
  else
    expected_draft="$requested_draft"
  fi
  if ! jq -e \
    --arg prefix "$promotion_receipt_prefix" \
    --arg receipt "$promotion_receipt_json" \
    --arg source "$source_sha" \
    --arg suffix "$promotion_receipt_suffix" \
    --arg tag "$tag" \
    --argjson id "$accepted_release_id" \
    --argjson expected_draft "$expected_draft" \
    --argjson prerelease "$is_prerelease" \
    --slurpfile acceptance "$acceptance_evidence" '
      .id == $id
      and .tag_name == $tag
      and .draft == $expected_draft
      and .prerelease == $prerelease
      and .name == $acceptance[0].draftRelease.title
      and .body == (
        $acceptance[0].draftRelease.body
        + (if $expected_draft then "" else "\n\n" + $prefix + $receipt + $suffix end))
      and (.body | capture("(?m)^Agent source commit: (?<sha>[0-9a-f]{40})$").sha) == $source
    ' <<< "$release_json" >/dev/null \
  ; then
    if [[ "$expected_draft" == false ]]; then
      printf 'Finalized release does not contain the exact deterministic promotion receipt and accepted metadata.\n' >&2
    else
      printf 'Numeric GitHub release id, draft state, tag, title, body, version kind, or source does not match acceptance.\n' >&2
    fi
    return 1
  fi
  jq -e --arg pattern "$acceptance_release_asset_pattern" '
    all(.assets[] | select(.name | startswith("agent-family-acceptance-"));
      (.name | test($pattern))
      and .state == "uploaded"
      and (.digest | type == "string" and test("^sha256:[0-9a-f]{64}$"))
      and (.size | type == "number" and floor == . and . > 0))
    and ([.assets[].name] | length) == ([.assets[].name] | unique | length)
    and ([.assets[].id] | length) == ([.assets[].id] | unique | length)
  ' <<< "$release_json" >/dev/null \
    || { printf 'GitHub release contains malformed, duplicate, or unhashed Agent acceptance assets.\n' >&2; return 1; }
  acceptance_asset_count="$(jq --arg pattern "$acceptance_release_asset_pattern" \
    '[.assets[] | select(.name | test($pattern))] | length' <<< "$release_json")"
  expected_release_asset_count=$((expected_release_asset_count + acceptance_asset_count))
  [[ "$(jq '.assets | length' <<< "$release_json")" == "$expected_release_asset_count" ]] \
    || { printf 'GitHub release asset inventory has the wrong size.\n' >&2; return 1; }
  for name in "${expected_release_names[@]}"; do
    [[ "$(jq --arg name "$name" '[.assets[] | select(.name == $name)] | length' <<< "$release_json")" == 1 ]] \
      || { printf 'GitHub release must contain exactly one %s.\n' "$name" >&2; return 1; }
  done
  if [[ "$candidate_only" != true ]]; then
    local acceptance_asset_id
    local acceptance_asset_url
    local acceptance_download
    local acceptance_sha
    local acceptance_size
    acceptance_sha="$(hash_file "$acceptance_evidence")"
    acceptance_size="$(file_size "$acceptance_evidence")"
    jq -e \
      --arg name "$acceptance_release_asset_name" \
      --arg digest "sha256:$acceptance_sha" \
      --arg repository "$GH_REPO" \
      --argjson size "$acceptance_size" '
        ([.assets[] | select(.name == $name)] | length) == 1
        and (.assets[] | select(.name == $name)
          | .state == "uploaded" and .digest == $digest and .size == $size
            and (.id | type == "number" and floor == . and . > 0)
            and .url == ("https://api.github.com/repos/" + $repository + "/releases/assets/" + (.id | tostring)))
      ' <<< "$release_json" >/dev/null \
      || { printf 'Durable Agent acceptance asset metadata differs from the selected evidence.\n' >&2; return 1; }
    acceptance_asset_id="$(jq -r --arg name "$acceptance_release_asset_name" '.assets[] | select(.name == $name) | .id' <<< "$release_json")"
    acceptance_asset_url="https://api.github.com/repos/$GH_REPO/releases/assets/$acceptance_asset_id"
    acceptance_download="$(mktemp "${RUNNER_TEMP:-${TMPDIR:-/tmp}}/sunder-agent-acceptance.XXXXXX")"
    if ! gh api -H 'Accept: application/octet-stream' "$acceptance_asset_url" > "$acceptance_download" \
      || ! cmp -s "$acceptance_evidence" "$acceptance_download"; then
      rm -f "$acceptance_download"
      printf 'Durable Agent acceptance asset bytes differ from the selected evidence.\n' >&2
      return 1
    fi
    rm -f "$acceptance_download"
  fi
  jq -e \
    --arg acceptance_pattern "$acceptance_release_asset_pattern" \
    --arg repository "$GH_REPO" \
    --slurpfile acceptance "$acceptance_evidence" '
      ([.assets[] | select((.name | test($acceptance_pattern)) | not) | {id, name, size, digest, url}] | sort_by(.name))
      == ([$acceptance[0].draftRelease.assets[] | {
        id,
        name,
        size,
        digest: ("sha256:" + .sha256),
        url: ("https://api.github.com/repos/" + $repository + "/releases/assets/" + (.id | tostring))
      }] | sort_by(.name))
    ' <<< "$release_json" >/dev/null \
    || { printf 'GitHub release asset ids, names, sizes, digests, or URLs differ from acceptance.\n' >&2; return 1; }
}

verify_acceptance_document() {
  python3 -B "$script_dir/agent_acceptance.py" "$release_source_directory" \
    "$publication_evidence_directory" "$acceptance_evidence" "$registry_api"
  jq -e \
    --arg artifact "$acceptance_artifact_name" \
    --arg attempt "$ACCEPTANCE_RUN_ATTEMPT" \
    --arg acceptance_workflow_ref "$acceptance_workflow_ref" \
    --arg acceptance_workflow_sha "$ACCEPTANCE_WORKFLOW_SHA" \
    --arg event "$acceptance_workflow_event" \
    --arg name "$acceptance_workflow_name" \
    --arg path "$acceptance_workflow_path" \
    --arg repository "$GH_REPO" \
    --arg run_id "$ACCEPTANCE_RUN_ID" \
    --arg source "$source_sha" \
    --arg tag "$tag" \
    --arg version "$version" \
    --arg publication_artifact "$publication_artifact_name" \
    --arg publication_attempt "$PUBLICATION_RUN_ATTEMPT" \
    --arg publication_event "$publication_workflow_event" \
    --arg publication_name "$publication_workflow_name" \
    --arg publication_path "$publication_workflow_path" \
    --arg publication_run_id "$PUBLICATION_RUN_ID" \
    --argjson release_id "$accepted_release_id" \
    --argjson expected_assets "$expected_release_names_json" \
    --argjson expected_packages "$expected_runtime_ids_json" '
      type == "object"
      and keys == ["acceptanceRunAttempt","acceptanceRunId","acceptanceWorkflow","acceptedAtUtc","cohort","decision","draftRelease","nativeReceipts","phase","publicationRunAttempt","publicationRunId","publicationWorkflow","registryPackages","releaseEvidence","repository","schemaVersion","sourceCommit","tag","version"]
      and .schemaVersion == 4
      and .decision == "accepted"
      and .repository == $repository
      and .tag == $tag
      and .version == $version
      and .sourceCommit == $source
      and .acceptanceRunId == $run_id
      and .acceptanceRunAttempt == $attempt
      and .publicationRunId == $publication_run_id
      and .publicationRunAttempt == $publication_attempt
      and .publicationWorkflow == {
        name:$publication_name,path:$publication_path,event:$publication_event,artifactName:$publication_artifact
      }
      and (.acceptanceWorkflow | keys) == ["artifactName","coordinatorSha256","event","name","path","workflowRef","workflowSha"]
      and .acceptanceWorkflow.name == $name
      and .acceptanceWorkflow.path == $path
      and .acceptanceWorkflow.event == $event
      and .acceptanceWorkflow.artifactName == $artifact
      and .acceptanceWorkflow.workflowRef == $acceptance_workflow_ref
      and .acceptanceWorkflow.workflowSha == $acceptance_workflow_sha
      and (.acceptanceWorkflow.coordinatorSha256 | test("^[0-9a-f]{64}$"))
      and ((.acceptedAtUtc | fromdateiso8601) | type == "number")
      and (.draftRelease | type == "object" and keys == ["assets","body","bodySha256","draft","id","provenanceSha256","title"])
      and .draftRelease.id == $release_id
      and .draftRelease.draft == true
      and .draftRelease.title == ("Sunder Agent Family " + $version)
      and (.draftRelease.body | type == "string" and length > 0)
      and (.draftRelease.bodySha256 | type == "string" and test("^[0-9a-f]{64}$"))
      and (.draftRelease.provenanceSha256 | type == "string" and test("^[0-9a-f]{64}$"))
      and (.draftRelease.assets | type == "array")
      and ([.draftRelease.assets[].name] | sort) == $expected_assets
      and ([.draftRelease.assets[].name] | length) == ([.draftRelease.assets[].name] | unique | length)
      and ([.draftRelease.assets[].id] | length) == ([.draftRelease.assets[].id] | unique | length)
      and all(.draftRelease.assets[];
        type == "object" and keys == ["id","name","sha256","size"]
        and (.id | type == "number" and floor == . and . > 0)
        and (.sha256 | type == "string" and test("^[0-9a-f]{64}$"))
        and (.size | type == "number" and floor == . and . > 0))
      and (.nativeReceipts | type == "array" and length == 6)
      and ([.nativeReceipts[].rid] | sort) == ["linux-arm64","linux-x64","osx-arm64","osx-x64","win-arm64","win-x64"]
      and all(.nativeReceipts[];
        type == "object" and keys == ["driverSha256","rid","sha256"]
        and (.driverSha256 | test("^[0-9a-f]{64}$"))
        and (.sha256 | test("^[0-9a-f]{64}$")))
      and (.registryPackages | type == "array")
      and ([.registryPackages[].packageId] | sort) == $expected_packages
      and ([.registryPackages[].packageId] | length) == ([.registryPackages[].packageId] | unique | length)
      and all(.registryPackages[];
        type == "object" and keys == ["canonicalSha256","packageId","version"]
        and .version == $version
        and (.canonicalSha256 | type == "string" and test("^[0-9a-f]{64}$")))
    ' "$acceptance_evidence" >/dev/null \
    || { printf 'Archived acceptance evidence has the wrong trusted workflow, run, release metadata, source, or inventory binding.\n' >&2; return 1; }
  [[ "$(jq -j '.draftRelease.body' "$acceptance_evidence" | hash_stream)" == "$(jq -r '.draftRelease.bodySha256' "$acceptance_evidence")" ]] \
    || { printf 'Archived acceptance release body bytes do not match their SHA-256.\n' >&2; return 1; }
}

verify_release_directory() {
  local directory="$1"
  local provenance="$directory/release-provenance.json"
  local native_evidence="$directory/$native_evidence_name"
  local sums="$directory/SHA256SUMS"
  local sbom="$directory/release-sbom.spdx.json"
  local downloaded_files
  local core_tag
  local core_commit
  local core_version
  local regenerated_audit

  python3 -B "$script_dir/agent_release.py" verify-provenance "$release_source_directory" "$directory"

  shopt -s nullglob
  downloaded_files=("$directory"/*)
  [[ "${#downloaded_files[@]}" == "${#expected_release_names[@]}" ]] \
    || { printf 'Downloaded GitHub release has the wrong file count.\n' >&2; return 1; }
  for name in "${expected_release_names[@]}"; do
    local file="$directory/$name"
    local sha
    local size
    [[ -f "$file" ]] || { printf 'Downloaded GitHub release is missing %s.\n' "$name" >&2; return 1; }
    sha="$(hash_file "$file")"
    size="$(file_size "$file")"
    [[ "$(jq -r --arg name "$name" '.assets[] | select(.name == $name) | .size' <<< "$release_json")" == "$size" ]] \
      || { printf 'GitHub asset metadata size differs for %s.\n' "$name" >&2; return 1; }
    jq -e --arg name "$name" --arg sha "$sha" --argjson size "$size" '
      [.draftRelease.assets[] | select(.name == $name and .sha256 == $sha and .size == $size)] | length == 1
    ' "$acceptance_evidence" >/dev/null \
      || { printf 'Downloaded GitHub release asset differs from archived acceptance: %s\n' "$name" >&2; return 1; }
  done

  [[ "$(hash_file "$provenance")" == "$(jq -r '.draftRelease.provenanceSha256' "$acceptance_evidence")" ]] \
    || { printf 'Release provenance bytes differ from archived acceptance.\n' >&2; return 1; }
  [[ "$(hash_file "$provenance")" == "$(jq -r '.immutableRelease.provenanceSha256' "$publication_handoff")" ]] \
    || { printf 'Release provenance bytes differ from the verified publication handoff.\n' >&2; return 1; }
  jq -e \
    --arg source "$source_sha" \
    --arg publication_attempt "$release_provenance_run_attempt" \
    --arg publication_ref "refs/tags/$tag" \
    --arg publication_run_id "$PUBLICATION_RUN_ID" \
    --arg publication_workflow "$publication_workflow_name" \
    --arg publication_workflow_ref "$GH_REPO/$publication_workflow_path@refs/tags/$tag" \
    --arg repository "$GH_REPO" \
    --arg tag "$tag" \
    --argjson release_id "$accepted_release_id" \
    --argjson expected_names "$(printf '%s\n' "${runtime_names[@]}" | jq -Rsc 'split("\n")[:-1] | sort')" '
      .schemaVersion == 1
      and .buildType == "https://sunderapp.io/provenance/github-actions/v1"
      and .invocation == {
        repository:$repository,workflow:$publication_workflow,workflowRef:$publication_workflow_ref,
        runId:$publication_run_id,runAttempt:$publication_attempt,releaseId:$release_id,
        ref:$publication_ref,refName:$tag,sha:$source
      }
      and ([.sources[] | select(.name == "sunder-agent-package" and .sha == $source)] | length == 1)
      and ([.sources[] | select(.name == "sunder-core" and (.sha | test("^[0-9a-f]{40}$")))] | length == 1)
      and (.sources | length == 2)
      and (.materials | length == 1)
      and .materials[0].name == "sunder-core-sdk"
      and .materials[0].tag == ("sdk/v" + .materials[0].packageVersion)
      and .materials[0].sha == ([.sources[] | select(.name == "sunder-core")][0].sha)
      and ([.artifacts[].path] | sort) == $expected_names
      and ([.artifacts[].path] | length) == ([.artifacts[].path] | unique | length)
    ' "$provenance" >/dev/null \
    || { printf 'Release provenance source, material, or primary inventory is invalid.\n' >&2; return 1; }
  core_tag="$(jq -r '.materials[0].tag' "$provenance")"
  core_commit="$(jq -r '.materials[0].sha' "$provenance")"
  core_version="$(jq -r '.materials[0].packageVersion' "$provenance")"
  bash "$script_dir/verify-native-family-evidence.sh" \
    "$native_evidence" "$directory" "$release_source_directory/packages.json" \
    "$version" "$source_sha" "$core_tag" "$core_commit" "$core_version" >/dev/null \
    || { printf 'Native evidence does not bind the complete accepted Agent/Core/version schema and exact final artifacts.\n' >&2; return 1; }
  regenerated_audit="$(mktemp "${RUNNER_TEMP:-${TMPDIR:-/tmp}}/sunder-agent-output-audit.XXXXXX")"
  if ! bash "$script_dir/generate-package-output-audit.sh" \
      "$directory" "$native_evidence" "$release_source_directory" "$regenerated_audit" >/dev/null \
    || ! cmp -s "$directory/$output_audit_name" "$regenerated_audit"; then
    rm -f "$regenerated_audit"
    printf 'Accepted package output audit does not match the exact archives and native measurements.\n' >&2
    return 1
  fi
  rm -f "$regenerated_audit"
  jq -e \
    --arg agent "$source_sha" \
    --arg core_commit "$core_commit" \
    --arg core_tag "$core_tag" \
    --arg core_version "$core_version" '
      (.body | capture("(?m)^Agent source commit: (?<sha>[0-9a-f]{40})$").sha) == $agent
      and (.body | capture("(?m)^Core source commit: (?<sha>[0-9a-f]{40})$").sha) == $core_commit
      and (.body | capture("(?m)^Core SDK release: (?<tag>sdk/v[^[:space:]]+)$").tag) == $core_tag
      and (.body | capture("(?m)^Core SDK package version: (?<version>[^[:space:]]+)$").version) == $core_version
    ' <<< "$release_json" >/dev/null \
    || { printf 'Release notes differ from accepted release provenance.\n' >&2; return 1; }

  [[ "$(wc -l < "$sums" | tr -d '[:space:]')" == "${#primary_names[@]}" ]] \
    || { printf 'SHA256SUMS has the wrong primary inventory size.\n' >&2; return 1; }
  jq -e --argjson expected_names "$expected_primary_names_json" '
    [.files[].fileName | sub("^\\./"; "")] | sort == $expected_names
  ' "$sbom" >/dev/null \
    || { printf 'Release SBOM has the wrong primary inventory.\n' >&2; return 1; }

  for name in "${primary_names[@]}"; do
    local file="$directory/$name"
    local sha
    local size
    sha="$(hash_file "$file")"
    size="$(file_size "$file")"
    if [[ "$name" == *.sunderpkg ]]; then
      jq -e --arg name "$name" --arg sha "$sha" --argjson size "$size" '
      [.artifacts[] | select(.path == $name and .sha256 == $sha and .size == $size)] | length == 1
    ' "$provenance" >/dev/null \
      || { printf 'Release provenance hash or size differs for %s.\n' "$name" >&2; return 1; }
    fi
    grep -Fqx "$sha  $name" "$sums" \
      || { printf 'SHA256SUMS differs for %s.\n' "$name" >&2; return 1; }
    jq -e --arg name "./$name" --arg sha "$sha" '
      [.files[] | select(.fileName == $name and ([.checksums[] | select(.algorithm == "SHA256" and .checksumValue == $sha)] | length == 1))] | length == 1
    ' "$sbom" >/dev/null \
      || { printf 'Release SBOM checksum differs for %s.\n' "$name" >&2; return 1; }
  done
  for name in "${final_artifact_names[@]}"; do
    local sha
    local size
    sha="$(hash_file "$directory/$name")"
    size="$(file_size "$directory/$name")"
    jq -e --arg name "$name" --arg sha "$sha" --argjson size "$size" '
      [.finalArtifacts[] | select(.path == $name and .sha256 == $sha and .size == $size)] | length == 1
    ' "$native_evidence" >/dev/null \
      || { printf 'Native fan-in evidence differs for %s.\n' "$name" >&2; return 1; }
  done
  for package_id in "${package_ids[@]}"; do
    local archive_name="$package_id.$version.sunderpkg"
    local archive_sha
    archive_sha="$(hash_file "$directory/$archive_name")"
    jq -e --arg package_id "$package_id" --arg version "$version" --arg sha "$archive_sha" '
      [.registryPackages[] | select(.packageId == $package_id and .version == $version and .canonicalSha256 == $sha)] | length == 1
    ' "$acceptance_evidence" >/dev/null \
      || { printf 'Accepted Registry canonical hash differs for %s.\n' "$package_id" >&2; return 1; }
  done
}

download_and_verify_release() {
  local directory="$1"
  local asset_id
  local asset_name
  local asset_url

  mkdir "$directory"
  while IFS=$'\t' read -r asset_id asset_name asset_url; do
    [[ "$asset_id" =~ ^[1-9][0-9]*$ ]] \
      || { printf 'GitHub release asset has an invalid numeric id.\n' >&2; return 1; }
    [[ "$asset_url" == "https://api.github.com/repos/$GH_REPO/releases/assets/$asset_id" ]] \
      || { printf 'GitHub release asset URL is not bound to the accepted repository and asset id.\n' >&2; return 1; }
    gh api -H 'Accept: application/octet-stream' "$asset_url" > "$directory/$asset_name"
  done < <(jq -r --arg acceptance_pattern "$acceptance_release_asset_pattern" '
    .assets[] | select((.name | test($acceptance_pattern)) | not) | [(.id | tostring), .name, .url] | @tsv
  ' <<< "$release_json")
  verify_release_directory "$directory"
}

verify_registry_packages() {
  local directory="$1"
  local availability=Listed
  local package_id
  local publication_time
  local verification_args
  if [[ "$acceptance_phase" == pre-visibility ]]; then
    availability=Either # An exact retry may already have committed; the cohort key proves it.
    [[ "$candidate_only" != true ]] || availability=Yanked
  fi
  for package_id in "${package_ids[@]}"; do
    publication_time="$(python3 -B "$script_dir/agent_release.py" publication-time "$release_source_directory" \
      "$(jq -r '.releaseEvidence.publicationEvidenceCommit' "$acceptance_evidence")" "$package_id")"
    verification_args=(--verify-only --initial-yanked "$cohort_work/$package_id.publication.json" --current-availability "$availability")
    [[ -z "$publication_time" ]] || verification_args+=(--published-at-utc "$publication_time")
    python3 -B "$script_dir/agent_release.py" expected-publication \
      "$directory/release-provenance.json" "$package_id" > "$cohort_work/$package_id.publication.json"
    env -u SUNDER_REGISTRY_PUBLISH_TOKEN -u SUNDER_REGISTRY_CLI_TOKEN \
      bash "$script_dir/publish-registry-package.sh" "${verification_args[@]}" \
        "$registry_api" "$directory/$package_id.$version.sunderpkg" "$package_id" "$version"
  done
}

verify_promoted_family() {
  env -u SUNDER_REGISTRY_CLI_TOKEN \
    python3 -B "$script_dir/agent_cohort.py" verify "$registry_api" "$cohort_request" "$cohort_result"
}

finalize_accepted_release() {
  local payload
  local response
  payload="$(jq -cn \
    --arg prefix "$promotion_receipt_prefix" \
    --arg receipt "$promotion_receipt_json" \
    --arg suffix "$promotion_receipt_suffix" \
    --slurpfile acceptance "$acceptance_evidence" '
      {
        draft: false,
        body: ($acceptance[0].draftRelease.body + "\n\n" + $prefix + $receipt + $suffix)
      }
    ')"
  response="$(gh api --method PATCH "repos/$GH_REPO/releases/$accepted_release_id" --input - <<< "$payload")"
  jq -e \
    --arg prefix "$promotion_receipt_prefix" \
    --arg receipt "$promotion_receipt_json" \
    --arg suffix "$promotion_receipt_suffix" \
    --arg tag "$tag" \
    --argjson id "$accepted_release_id" \
    --slurpfile acceptance "$acceptance_evidence" '
    .id == $id
    and .tag_name == $tag
    and .draft == false
    and .name == $acceptance[0].draftRelease.title
    and .body == ($acceptance[0].draftRelease.body + "\n\n" + $prefix + $receipt + $suffix)
  ' <<< "$response" >/dev/null \
    || { printf 'Numeric GitHub release finalization returned the wrong release identity, receipt, or state.\n' >&2; return 1; }
}

if [[ "$candidate_only" == true ]]; then
  mkdir "$verification_directory"
  verify_source_tag_ref
  load_accepted_release true
  verify_acceptance_document
  download_and_verify_release "$verification_directory/acceptance-assets"
  verify_registry_packages "$verification_directory/acceptance-assets"
  [[ "$acceptance_phase" != post-visibility ]] || verify_promoted_family
  printf 'Verified production acceptance candidate against the exact tag, draft bytes, provenance, and Registry package bytes.\n'
  exit 0
fi

if [[ "$guard_only" == true ]]; then
  [[ "$acceptance_phase" == pre-visibility ]] \
    || { printf 'A cohort mutation requires pre-visibility acceptance.\n' >&2; exit 1; }
  verify_source_tag_ref
  load_accepted_release true
  verify_acceptance_document
  printf 'Revalidated exact acceptance, tag, numeric draft metadata, publication assets, and the selected durable acceptance attempt immediately before Registry mutation.\n'
  exit 0
fi

mkdir "$verification_directory"
verify_source_tag_ref
load_accepted_release any
verify_acceptance_document
if [[ "$release_draft" == false ]]; then
  [[ "$acceptance_phase" == post-visibility ]] \
    || { printf 'Announcement requires post-visibility acceptance.\n' >&2; exit 1; }
  download_and_verify_release "$verification_directory/finalized-retry-assets"
  verify_registry_packages "$verification_directory/finalized-retry-assets"
  verify_promoted_family

  # Runner-loss recovery succeeds only while the exact receipt, release, handoff, and family still agree.
  verify_acceptance_handoff false
  verify_source_tag_ref
  load_accepted_release false
  verify_acceptance_document
  verify_promoted_family
  printf "Verified durable promotion receipt for Agent family '%s' at %s and numeric release %s after finalized retry.\n" \
    "$dist_tag" "$version" "$accepted_release_id"
  exit 0
fi
download_and_verify_release "$verification_directory/pre-promotion-assets"
verify_registry_packages "$verification_directory/pre-promotion-assets"

if [[ "$acceptance_phase" == pre-visibility ]]; then
  [[ "$registry_cli_token" =~ ^sunder_cli_[A-Za-z0-9_-]{43}$ ]] \
    || { printf 'A valid owner SUNDER_REGISTRY_CLI_TOKEN is required after preflight.\n' >&2; exit 1; }
  pre_mutation_guard_json="$(jq -cn \
  --arg orchestrator "$script_dir/promote-agent-family.sh" \
  --arg acceptance "$acceptance_evidence" \
  --arg directory "$verification_directory" \
  --arg registry "$registry_api" \
  --arg source "$source_sha" \
  --arg tag "$tag" \
  '["bash", $orchestrator, $tag, $source, $registry, $acceptance, $directory]')"
  SUNDER_AGENT_PROMOTION_GUARD_ONLY=true \
  SUNDER_REGISTRY_PRE_MUTATION_GUARD_JSON="$pre_mutation_guard_json" \
  SUNDER_REGISTRY_CLI_TOKEN="$registry_cli_token" \
    python3 -B "$script_dir/agent_cohort.py" apply "$registry_api" "$cohort_request" "$cohort_result"
  registry_cli_token=''
  verify_promoted_family
  python3 -B "$script_dir/agent_cohort_receipt.py" upload "$GH_REPO" "$accepted_release_id" "$tag" \
    "$registry_api" "$acceptance_evidence" "$cohort_result"
  printf 'Atomically listed all 15 retained identities; exact result retained. Run post-visibility acceptance before announcement.\n'
  exit 0
fi
SUNDER_REGISTRY_CLI_TOKEN="$registry_cli_token" \
  python3 -B "$script_dir/agent_cohort.py" confirm "$registry_api" "$cohort_request" "$cohort_result" \
    "$verification_directory/pre-promotion-assets"
registry_cli_token=''

# Perform the complete, potentially long pre-finalization verification.
verify_acceptance_handoff true
verify_source_tag_ref
load_accepted_release true
verify_acceptance_document
download_and_verify_release "$verification_directory/pre-finalization-assets"
verify_registry_packages "$verification_directory/pre-finalization-assets"
verify_promoted_family

# Re-fetch handoff, tag, and numeric draft metadata, then make the promoted family the last remote check before PATCH.
verify_acceptance_handoff true
verify_source_tag_ref
load_accepted_release true
verify_acceptance_document
verify_promoted_family
finalize_accepted_release

verify_source_tag_ref
verify_acceptance_handoff false
load_accepted_release false
verify_acceptance_document
download_and_verify_release "$verification_directory/post-promotion-assets"
verify_registry_packages "$verification_directory/post-promotion-assets"
verify_promoted_family
printf "Promoted Agent family '%s' to %s, finalized numeric release %s, and reverified every accepted identity.\n" \
  "$dist_tag" "$version" "$accepted_release_id"
