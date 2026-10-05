#!/usr/bin/env bash
set -euo pipefail
export LC_ALL=C

if [[ $# -ne 4 && $# -ne 6 ]]; then
  printf 'Usage: verify-acceptance-artifact.sh <repository> <workflow-sha> <run-id> <run-attempt> [<artifact-id> <artifact-digest>]\n' >&2
  exit 2
fi

repository="$1"
workflow_sha="$2"
run_id="$3"
run_attempt="$4"
artifact_id="${5:-}"
artifact_digest="${6:-}"
verify_actions_artifact=false
[[ $# -ne 6 ]] || verify_actions_artifact=true
artifact_name="sunder-agent-production-acceptance-$run_id-$run_attempt"
workflow_event='workflow_dispatch'
workflow_name='Sunder Agent Production Acceptance'
workflow_path='.github/workflows/sunder-agent-production-acceptance.yml'
workflow_branch='main'

[[ "$repository" =~ ^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$ ]] \
  || { printf 'Acceptance repository must be an owner/repository name.\n' >&2; exit 2; }
[[ "$workflow_sha" =~ ^[0-9a-f]{40}$ ]] \
  || { printf 'Acceptance workflow SHA must be a full lowercase Git SHA.\n' >&2; exit 2; }
[[ "$run_id" =~ ^[1-9][0-9]*$ ]] \
  || { printf 'Acceptance run id must be positive.\n' >&2; exit 2; }
[[ "$run_attempt" =~ ^[1-9][0-9]*$ ]] \
  || { printf 'Acceptance run attempt must be positive.\n' >&2; exit 2; }
if [[ "$verify_actions_artifact" == true ]]; then
  [[ "$artifact_id" =~ ^[1-9][0-9]*$ ]] \
    || { printf 'Acceptance artifact id must be positive.\n' >&2; exit 2; }
  [[ "$artifact_digest" =~ ^sha256:[0-9a-f]{64}$ ]] \
    || { printf 'Acceptance artifact digest must use GitHub sha256:<lowercase-digest> form.\n' >&2; exit 2; }
fi

for required_command in gh jq; do
  command -v "$required_command" >/dev/null 2>&1 \
    || { printf '%s is required to verify acceptance artifact identity.\n' "$required_command" >&2; exit 127; }
done

run_json="$(gh api "repos/$repository/actions/runs/$run_id")"
jq -e \
  --arg attempt "$run_attempt" \
  --arg branch "$workflow_branch" \
  --arg event "$workflow_event" \
  --arg id "$run_id" \
  --arg name "$workflow_name" \
  --arg path "$workflow_path" \
  --arg repository "$repository" \
  --arg workflow_sha "$workflow_sha" '
    (.id | tostring) == $id
    and (.run_attempt | type == "number" and floor == . and . >= ($attempt | tonumber))
    and .name == $name
    and .path == $path
    and .event == $event
    and .repository.full_name == $repository
    and .head_branch == $branch
    and .head_sha == $workflow_sha
    and .status == "completed"
    and .conclusion == "success"
    and (.repository.id | type == "number" and floor == . and . > 0)
  ' <<< "$run_json" >/dev/null \
  || { printf 'Acceptance run is not the successful protected-main policy workflow dispatch.\n' >&2; exit 1; }

attempt_json="$(gh api "repos/$repository/actions/runs/$run_id/attempts/$run_attempt")"
jq -e \
  --arg attempt "$run_attempt" \
  --arg branch "$workflow_branch" \
  --arg event "$workflow_event" \
  --arg id "$run_id" \
  --arg name "$workflow_name" \
  --arg path "$workflow_path" \
  --arg repository "$repository" \
  --arg workflow_sha "$workflow_sha" '
    (.id | tostring) == $id
    and (.run_attempt | tostring) == $attempt
    and .name == $name
    and .path == $path
    and .event == $event
    and .repository.full_name == $repository
    and .head_branch == $branch
    and .head_sha == $workflow_sha
    and .status == "completed"
    and .conclusion == "success"
    and (.repository.id | type == "number" and floor == . and . > 0)
  ' <<< "$attempt_json" >/dev/null \
  || { printf 'Acceptance attempt is not the exact trusted successful workflow dispatch.\n' >&2; exit 1; }

artifact_json=''
if [[ "$verify_actions_artifact" == true ]]; then
  artifact_json="$(gh api "repos/$repository/actions/artifacts/$artifact_id")"
  jq -e \
    --arg digest "$artifact_digest" \
    --arg id "$artifact_id" \
    --arg name "$artifact_name" \
    --arg repository "$repository" \
    --arg run_id "$run_id" \
    --arg workflow_sha "$workflow_sha" \
    --argjson repository_id "$(jq '.repository.id' <<< "$attempt_json")" '
      (.id | tostring) == $id
      and .name == $name
      and .digest == $digest
      and .expired == false
      and (.size_in_bytes | type == "number" and floor == . and . > 0)
      and .url == ("https://api.github.com/repos/" + $repository + "/actions/artifacts/" + $id)
      and .archive_download_url == ("https://api.github.com/repos/" + $repository + "/actions/artifacts/" + $id + "/zip")
      and (.workflow_run.id | tostring) == $run_id
      and .workflow_run.repository_id == $repository_id
      and .workflow_run.head_sha == $workflow_sha
      and (.created_at | type == "string")
    ' <<< "$artifact_json" >/dev/null \
    || { printf 'Acceptance artifact id, GitHub digest, run, repository, or source does not match selection.\n' >&2; exit 1; }
fi

jobs_json="$(gh api --paginate --slurp "repos/$repository/actions/runs/$run_id/attempts/$run_attempt/jobs?per_page=100")"
if [[ "$verify_actions_artifact" == true ]]; then
  jq -e \
    --arg attempt "$run_attempt" \
    --arg id "$run_id" \
    --argjson artifact "$artifact_json" '
      type == "array" and length > 0
      and (.[0].total_count | type == "number" and floor == . and . > 0)
      and (.[0].total_count == ([.[].jobs[]] | length))
      and all(.[].jobs[];
        (.run_id | tostring) == $id
        and (.run_attempt | tostring) == $attempt)
      and ([.[].jobs[] | select(.started_at != null and .completed_at != null)] | length) > 0
      and (($artifact.created_at | fromdateiso8601)
        >= ([.[].jobs[] | select(.started_at != null) | (.started_at | fromdateiso8601)] | min))
      and (($artifact.created_at | fromdateiso8601)
        <= ([.[].jobs[] | select(.completed_at != null) | (.completed_at | fromdateiso8601)] | max))
    ' <<< "$jobs_json" >/dev/null \
    || { printf 'Acceptance artifact creation is not bounded by jobs from the selected run attempt.\n' >&2; exit 1; }
  printf 'Verified trusted acceptance artifact %s digest %s belongs to run %s attempt %s.\n' \
    "$artifact_id" "$artifact_digest" "$run_id" "$run_attempt"
else
  jq -e \
    --arg attempt "$run_attempt" \
    --arg id "$run_id" '
      type == "array" and length > 0
      and (.[0].total_count | type == "number" and floor == . and . > 0)
      and (.[0].total_count == ([.[].jobs[]] | length))
      and all(.[].jobs[];
        (.run_id | tostring) == $id
        and (.run_attempt | tostring) == $attempt)
      and ([.[].jobs[] | select(.started_at != null and .completed_at != null)] | length) > 0
    ' <<< "$jobs_json" >/dev/null \
    || { printf 'Acceptance jobs do not belong to the selected run attempt.\n' >&2; exit 1; }
  printf 'Verified trusted acceptance run %s attempt %s and its completed jobs.\n' "$run_id" "$run_attempt"
fi
