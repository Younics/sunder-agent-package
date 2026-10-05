#!/usr/bin/env bash
set -euo pipefail

[[ $# -eq 10 ]] || {
  printf 'Usage: download-agent-acceptance.sh <release-id> <tag> <source-sha> <publication-run-id> <publication-run-attempt> <acceptance-run-id> <acceptance-run-attempt> <acceptance-workflow-sha> <sha256> <destination>\n' >&2
  exit 2
}
release_id="$1"
tag="$2"
source_sha="$3"
publication_run_id="$4"
publication_run_attempt="$5"
run_id="$6"
run_attempt="$7"
workflow_sha="$8"
expected_sha="$9"
destination="${10}"
asset_name="agent-family-acceptance-$run_id-$run_attempt.json"
script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
candidate_only="${SUNDER_AGENT_ACCEPTANCE_CANDIDATE_ONLY:-false}"

[[ "$release_id" =~ ^[1-9][0-9]*$ && "$tag" =~ ^agent/v[A-Za-z0-9.-]+$ ]] \
  || { printf 'Durable Agent acceptance release coordinates are invalid.\n' >&2; exit 2; }
[[ "$source_sha" =~ ^[0-9a-f]{40}$ \
    && "$publication_run_id" =~ ^[1-9][0-9]*$ \
    && "$publication_run_attempt" =~ ^[1-9][0-9]*$ \
    && "$run_id" =~ ^[1-9][0-9]*$ \
    && "$run_attempt" =~ ^[1-9][0-9]*$ ]] \
  || { printf 'Durable Agent acceptance source, publication, or acceptance coordinate is invalid.\n' >&2; exit 2; }
[[ "$workflow_sha" =~ ^[0-9a-f]{40}$ ]] \
  || { printf 'Durable Agent acceptance workflow SHA is invalid.\n' >&2; exit 2; }
[[ "$expected_sha" =~ ^[0-9a-f]{64}$ ]] \
  || { printf 'Durable Agent acceptance SHA-256 is invalid.\n' >&2; exit 2; }
[[ "$candidate_only" == true || "$candidate_only" == false ]] \
  || { printf 'SUNDER_AGENT_ACCEPTANCE_CANDIDATE_ONLY must be true or false.\n' >&2; exit 2; }
[[ ! -e "$destination" && ! -L "$destination" ]] \
  || { printf 'Durable Agent acceptance destination already exists.\n' >&2; exit 1; }
: "${GH_REPO:?GH_REPO is required}"
: "${GH_TOKEN:?GH_TOKEN is required}"
[[ "$GH_REPO" == 'Younics/sunder-agent-package' ]] \
  || { printf 'Unexpected Agent repository: %s\n' "$GH_REPO" >&2; exit 2; }
for required_command in gh jq mktemp; do
  command -v "$required_command" >/dev/null 2>&1 \
    || { printf '%s is required to download Agent acceptance.\n' "$required_command" >&2; exit 127; }
done
if ! command -v sha256sum >/dev/null 2>&1 && ! command -v shasum >/dev/null 2>&1; then
  printf 'sha256sum or shasum is required to download Agent acceptance.\n' >&2
  exit 127
fi
hash_file() {
  if command -v sha256sum >/dev/null 2>&1; then sha256sum "$1" | cut -d ' ' -f 1
  else shasum -a 256 "$1" | cut -d ' ' -f 1; fi
}

if [[ "$candidate_only" != true ]]; then
  bash "$script_dir/verify-acceptance-artifact.sh" "$GH_REPO" "$workflow_sha" "$run_id" "$run_attempt"
fi
release_json=''
for _ in {1..30}; do
  if release_json="$(gh api -H 'Accept: application/vnd.github+json' \
      -H 'X-GitHub-Api-Version: 2022-11-28' "repos/$GH_REPO/releases/$release_id")" \
    && jq -e --argjson id "$release_id" --arg tag "$tag" --arg name "$asset_name" \
      --arg digest "sha256:$expected_sha" '
        .id == $id and .tag_name == $tag and (.draft | type == "boolean")
        and ([.assets[].name] | length) == ([.assets[].name] | unique | length)
        and all(.assets[] | select(.name | startswith("agent-family-acceptance-"));
          (.name | test("^agent-family-acceptance-[1-9][0-9]*-[1-9][0-9]*[.]json$"))
          and .state == "uploaded"
          and (.digest | type == "string" and test("^sha256:[0-9a-f]{64}$"))
          and (.size | type == "number" and floor == . and . > 0))
        and ([.assets[] | select(.name == $name)] | length) == 1
        and (.assets[] | select(.name == $name)
          | .state == "uploaded" and .digest == $digest
            and (.id | type == "number" and floor == . and . > 0)
            and (.size | type == "number" and floor == . and . > 0))
      ' <<< "$release_json" >/dev/null; then
    break
  fi
  release_json=''
  sleep 2
done
[[ -n "$release_json" ]] \
  || { printf 'Numeric release does not contain the selected durable Agent acceptance.\n' >&2; exit 1; }
asset_id="$(jq -r --arg name "$asset_name" '.assets[] | select(.name == $name) | .id' <<< "$release_json")"
expected_size="$(jq -r --arg name "$asset_name" '.assets[] | select(.name == $name) | .size' <<< "$release_json")"
temporary="$(mktemp "${RUNNER_TEMP:-${TMPDIR:-/tmp}}/sunder-agent-acceptance-download.XXXXXX")"
trap 'rm -f "$temporary"' EXIT
gh api -H 'Accept: application/octet-stream' -H 'X-GitHub-Api-Version: 2022-11-28' \
  "repos/$GH_REPO/releases/assets/$asset_id" > "$temporary"
[[ "$(wc -c < "$temporary" | tr -d '[:space:]')" == "$expected_size" \
    && "$(hash_file "$temporary")" == "$expected_sha" ]] \
  || { printf 'Downloaded durable Agent acceptance differs from GitHub metadata or selected SHA-256.\n' >&2; exit 1; }
jq -e \
  --arg repository "$GH_REPO" \
  --arg source "$source_sha" \
  --arg tag "$tag" \
  --arg publication_run_id "$publication_run_id" \
  --arg publication_run_attempt "$publication_run_attempt" \
  --arg run_id "$run_id" \
  --arg run_attempt "$run_attempt" \
  --arg workflow_sha "$workflow_sha" \
  --argjson release_id "$release_id" '
    .schemaVersion == 4
    and (.phase == "pre-visibility" or .phase == "post-visibility")
    and .decision == "accepted"
    and .repository == $repository
    and .tag == $tag
    and .sourceCommit == $source
    and .draftRelease.id == $release_id
    and .publicationRunId == $publication_run_id
    and .publicationRunAttempt == $publication_run_attempt
    and .publicationWorkflow == {
      name:"Sunder Agent Family Release",
      path:".github/workflows/sunder-package-release.yml",
      event:"push",
      artifactName:("sunder-agent-publication-evidence-" + ($release_id | tostring) + "-" + $publication_run_attempt)
    }
    and .acceptanceRunId == $run_id
    and .acceptanceRunAttempt == $run_attempt
    and .acceptanceWorkflow.name == "Sunder Agent Production Acceptance"
    and .acceptanceWorkflow.path == ".github/workflows/sunder-agent-production-acceptance.yml"
    and .acceptanceWorkflow.event == "workflow_dispatch"
    and .acceptanceWorkflow.artifactName == ("sunder-agent-production-acceptance-" + $run_id + "-" + $run_attempt)
    and .acceptanceWorkflow.workflowRef == "refs/heads/main"
    and .acceptanceWorkflow.workflowSha == $workflow_sha
    and (.acceptanceWorkflow.coordinatorSha256 | test("^[0-9a-f]{64}$"))
  ' "$temporary" >/dev/null \
  || { printf 'Downloaded durable Agent acceptance has different release, publication, or protected-policy coordinates.\n' >&2; exit 1; }
mv "$temporary" "$destination"
trap - EXIT
printf 'Downloaded durable Agent acceptance from numeric release %s.\n' "$release_id"
