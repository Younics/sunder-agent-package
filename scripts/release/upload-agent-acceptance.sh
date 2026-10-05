#!/usr/bin/env bash
set -euo pipefail

[[ $# -eq 8 ]] || {
  printf 'Usage: upload-agent-acceptance.sh <release-id> <agent-tag> <publication-run-id> <publication-run-attempt> <acceptance-run-id> <acceptance-run-attempt> <acceptance-workflow-sha> <acceptance-json>\n' >&2
  exit 2
}
release_id="$1"
tag="$2"
publication_run_id="$3"
publication_run_attempt="$4"
run_id="$5"
run_attempt="$6"
workflow_sha="$7"
acceptance="$8"
asset_name="agent-family-acceptance-$run_id-$run_attempt.json"

[[ "$release_id" =~ ^[1-9][0-9]*$ ]] \
  || { printf 'Release ID must be positive.\n' >&2; exit 2; }
[[ "$tag" =~ ^agent/v[A-Za-z0-9.-]+$ ]] \
  || { printf 'Unexpected Agent release tag: %s\n' "$tag" >&2; exit 2; }
[[ "$publication_run_id" =~ ^[1-9][0-9]*$ && "$publication_run_attempt" =~ ^[1-9][0-9]*$ \
    && "$run_id" =~ ^[1-9][0-9]*$ && "$run_attempt" =~ ^[1-9][0-9]*$ ]] \
  || { printf 'Agent publication and acceptance run coordinates must be positive.\n' >&2; exit 2; }
[[ "$workflow_sha" =~ ^[0-9a-f]{40}$ ]] \
  || { printf 'Agent acceptance workflow SHA must be a full lowercase Git SHA.\n' >&2; exit 2; }
[[ -f "$acceptance" && ! -L "$acceptance" && "$(basename "$acceptance")" == 'agent-family-acceptance.json' ]] \
  || { printf 'Agent acceptance upload must be a regular agent-family-acceptance.json file.\n' >&2; exit 1; }
: "${GH_REPO:?GH_REPO is required}"
: "${GH_TOKEN:?GH_TOKEN is required}"
[[ "$GH_REPO" == 'Younics/sunder-agent-package' ]] \
  || { printf 'Unexpected Agent repository: %s\n' "$GH_REPO" >&2; exit 2; }
for required_command in curl gh jq mktemp; do
  command -v "$required_command" >/dev/null 2>&1 \
    || { printf '%s is required to upload Agent acceptance.\n' "$required_command" >&2; exit 127; }
done
if ! command -v sha256sum >/dev/null 2>&1 && ! command -v shasum >/dev/null 2>&1; then
  printf 'sha256sum or shasum is required to upload Agent acceptance.\n' >&2
  exit 127
fi

hash_file() {
  if command -v sha256sum >/dev/null 2>&1; then sha256sum "$1" | cut -d ' ' -f 1
  else shasum -a 256 "$1" | cut -d ' ' -f 1; fi
}

jq -e \
  --arg run_id "$run_id" \
  --arg run_attempt "$run_attempt" \
  --arg publication_run_id "$publication_run_id" \
  --arg publication_run_attempt "$publication_run_attempt" \
  --arg workflow_sha "$workflow_sha" \
  --arg tag "$tag" \
  --argjson release_id "$release_id" '
    .schemaVersion == 4
    and (.phase == "pre-visibility" or .phase == "post-visibility")
    and .decision == "accepted"
    and .tag == $tag
    and .draftRelease.id == $release_id
    and .publicationRunId == $publication_run_id
    and .publicationRunAttempt == $publication_run_attempt
    and .acceptanceRunId == $run_id
    and .acceptanceRunAttempt == $run_attempt
    and .acceptanceWorkflow.workflowRef == "refs/heads/main"
    and .acceptanceWorkflow.workflowSha == $workflow_sha
  ' "$acceptance" >/dev/null \
  || { printf 'Agent acceptance document is not bound to the selected release attempt.\n' >&2; exit 1; }

release_json="$(gh api -H 'Accept: application/vnd.github+json' \
  -H 'X-GitHub-Api-Version: 2022-11-28' "repos/$GH_REPO/releases/$release_id")"
jq -e --argjson id "$release_id" --arg tag "$tag" '
  .id == $id and .tag_name == $tag and .draft == true
' <<< "$release_json" >/dev/null \
  || { printf 'Agent release changed identity or left draft state before acceptance upload.\n' >&2; exit 1; }
asset_count="$(jq --arg name "$asset_name" '[.assets[] | select(.name == $name)] | length' <<< "$release_json")"
if [[ "$asset_count" == 1 ]]; then
  asset_id="$(jq -r --arg name "$asset_name" '.assets[] | select(.name == $name) | .id' <<< "$release_json")"
  existing="$(mktemp "${RUNNER_TEMP:-${TMPDIR:-/tmp}}/sunder-agent-acceptance.XXXXXX")"
  trap 'rm -f "$existing"' EXIT
  gh api -H 'Accept: application/octet-stream' -H 'X-GitHub-Api-Version: 2022-11-28' \
    "repos/$GH_REPO/releases/assets/$asset_id" > "$existing"
  jq -e --arg name "$asset_name" --arg digest "sha256:$(hash_file "$acceptance")" \
    --argjson size "$(wc -c < "$acceptance" | tr -d '[:space:]')" '
      .assets[] | select(.name == $name)
      | .state == "uploaded" and .digest == $digest and .size == $size
    ' <<< "$release_json" >/dev/null \
    || { printf 'Existing durable Agent acceptance has null or mismatched metadata.\n' >&2; exit 1; }
  [[ "$(wc -c < "$existing" | tr -d '[:space:]')" == "$(wc -c < "$acceptance" | tr -d '[:space:]')" \
      && "$(hash_file "$existing")" == "$(hash_file "$acceptance")" ]] \
    || { printf 'Existing durable Agent acceptance differs and will not be replaced.\n' >&2; exit 1; }
  exit 0
fi
[[ "$asset_count" == 0 ]] \
  || { printf 'Agent release contains duplicate durable acceptance assets.\n' >&2; exit 1; }

encoded_name="$(jq -rn --arg value "$asset_name" '$value | @uri')"
response="$(curl --fail --silent --show-error --proto '=https' --tlsv1.2 --max-redirs 0 \
  --request POST -H 'Accept: application/vnd.github+json' -H 'X-GitHub-Api-Version: 2022-11-28' \
  -H 'Content-Type: application/octet-stream' -H "Authorization: Bearer $GH_TOKEN" \
  --data-binary "@$acceptance" \
  "https://uploads.github.com/repos/$GH_REPO/releases/$release_id/assets?name=$encoded_name")"
jq -e --arg name "$asset_name" --argjson size "$(wc -c < "$acceptance" | tr -d '[:space:]')" '
    (.id | type == "number" and . > 0) and .name == $name and .state == "uploaded"
    and .size == $size
  ' <<< "$response" >/dev/null \
  || { printf 'GitHub returned invalid durable Agent acceptance metadata.\n' >&2; exit 1; }

# GitHub may omit the digest from the upload response. The release representation
# must nevertheless expose the completed hash before this attempt can continue.
release_json="$(gh api -H 'Accept: application/vnd.github+json' \
  -H 'X-GitHub-Api-Version: 2022-11-28' "repos/$GH_REPO/releases/$release_id")"
jq -e \
  --arg name "$asset_name" \
  --arg digest "sha256:$(hash_file "$acceptance")" \
  --argjson id "$release_id" \
  --argjson size "$(wc -c < "$acceptance" | tr -d '[:space:]')" \
  --arg tag "$tag" '
    .id == $id and .tag_name == $tag and .draft == true
    and ([.assets[] | select(.name == $name)] | length) == 1
    and (.assets[] | select(.name == $name)
      | .state == "uploaded" and .digest == $digest and .size == $size)
  ' <<< "$release_json" >/dev/null \
  || { printf 'Uploaded durable Agent acceptance has null or mismatched release metadata.\n' >&2; exit 1; }
