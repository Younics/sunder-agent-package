#!/usr/bin/env bash
set -euo pipefail
export LC_ALL=C

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../release" && pwd)"
uploader="$script_dir/upload-agent-acceptance.sh"
downloader="$script_dir/download-agent-acceptance.sh"
[[ -f "$uploader" && -f "$downloader" ]] \
  || { printf 'Agent acceptance upload/download scripts were not found.\n' >&2; exit 1; }
for required_command in cmp cp cut jq mktemp tr wc; do
  command -v "$required_command" >/dev/null 2>&1 \
    || { printf '%s is required to run the Agent acceptance upload fixture.\n' "$required_command" >&2; exit 127; }
done
if ! command -v sha256sum >/dev/null 2>&1 && ! command -v shasum >/dev/null 2>&1; then
  printf 'sha256sum or shasum is required to run the Agent acceptance upload fixture.\n' >&2
  exit 127
fi

hash_file() {
  if command -v sha256sum >/dev/null 2>&1; then sha256sum "$1" | cut -d ' ' -f 1
  else shasum -a 256 "$1" | cut -d ' ' -f 1; fi
}

fixture_root="$(mktemp -d "${TMPDIR:-/tmp}/sunder-agent-acceptance-upload.XXXXXX")"
trap 'rm -rf "$fixture_root"' EXIT
mock_bin="$fixture_root/mock-bin"
asset_state="$fixture_root/assets"
acceptance="$fixture_root/agent-family-acceptance.json"
mkdir -p "$mock_bin" "$asset_state"

release_id=4242
tag='agent/v2.0.0'
run_id=9001
publication_run_id=8001
publication_run_attempt=1
source_sha=1111111111111111111111111111111111111111
workflow_sha=2222222222222222222222222222222222222222

set_attempt() {
  local attempt="$1"
  jq -n \
    --arg attempt "$attempt" \
    --arg run_id "$run_id" \
    --arg source "$source_sha" \
    --arg tag "$tag" \
    --argjson release_id "$release_id" '
      {
        schemaVersion:4,
        phase:"pre-visibility",
        decision:"accepted",
        repository:"Younics/sunder-agent-package",
        tag:$tag,
        version:"2.0.0",
        sourceCommit:$source,
        publicationRunId:"8001",
        publicationRunAttempt:"1",
        publicationWorkflow:{
          name:"Sunder Agent Family Release",
          path:".github/workflows/sunder-package-release.yml",
          event:"push",
          artifactName:"sunder-agent-publication-evidence-4242-1"
        },
        acceptanceRunId:$run_id,
        acceptanceRunAttempt:$attempt,
        acceptanceWorkflow:{
          name:"Sunder Agent Production Acceptance",
          path:".github/workflows/sunder-agent-production-acceptance.yml",
          event:"workflow_dispatch",
          artifactName:("sunder-agent-production-acceptance-" + $run_id + "-" + $attempt),
          coordinatorSha256:("c" * 64),
          workflowRef:"refs/heads/main",
          workflowSha:("2" * 40)
        },
        draftRelease:{id:$release_id}
      }
    ' > "$acceptance"
}

cat > "$mock_bin/gh" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
[[ "${1:-}" == api ]] || exit 80
hash_file() {
  if command -v sha256sum >/dev/null 2>&1; then sha256sum "$1" | cut -d ' ' -f 1
  else shasum -a 256 "$1" | cut -d ' ' -f 1; fi
}
endpoint="${*: -1}"
case "$endpoint" in
  */releases/4242)
    draft=true
    [[ "${MOCK_PUBLIC:-false}" != true ]] || draft=false
    assets='[]'
    id=1000
    for path in "$MOCK_ASSET_STATE"/*; do
      [[ -f "$path" ]] || continue
      id=$((id + 1))
      digest="sha256:$(hash_file "$path")"
      case "${MOCK_METADATA_MODE:-valid}" in
        null) digest=null ;;
        mismatch) digest="sha256:$(printf '0%.0s' {1..64})" ;;
      esac
      if [[ "$digest" == null ]]; then
        assets="$(jq -cn --argjson assets "$assets" --argjson id "$id" --arg name "$(basename "$path")" --argjson size "$(wc -c < "$path" | tr -d '[:space:]')" '$assets + [{id:$id,name:$name,state:"uploaded",digest:null,size:$size}]')"
      else
        assets="$(jq -cn --argjson assets "$assets" --argjson id "$id" --arg name "$(basename "$path")" --arg digest "$digest" --argjson size "$(wc -c < "$path" | tr -d '[:space:]')" '$assets + [{id:$id,name:$name,state:"uploaded",digest:$digest,size:$size}]')"
      fi
    done
    jq -n --argjson draft "$draft" --argjson assets "$assets" \
      '{id:4242,tag_name:"agent/v2.0.0",draft:$draft,assets:$assets}'
    ;;
  */releases/assets/*)
    requested_id="${endpoint##*/}"
    id=1000
    for path in "$MOCK_ASSET_STATE"/*; do
      [[ -f "$path" ]] || continue
      id=$((id + 1))
      if [[ "$id" == "$requested_id" ]]; then
        command cat "$path"
        exit 0
      fi
    done
    exit 82
    ;;
  *) exit 81 ;;
esac
EOF

cat > "$mock_bin/curl" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
url=''
source_path=''
while (($#)); do
  case "$1" in
    --data-binary)
      shift
      source_path="${1#@}"
      ;;
    https://*) url="$1" ;;
  esac
  shift
done
asset_name="${url##*name=}"
[[ "$asset_name" =~ ^agent-family-acceptance-[1-9][0-9]*-[1-9][0-9]*[.]json$ && -f "$source_path" ]] \
  || exit 83
cp "$source_path" "$MOCK_ASSET_STATE/$asset_name"
printf '%s\n' "$asset_name" >> "$MOCK_UPLOAD_LOG"
[[ "${MOCK_FAIL_AFTER_MUTATION:-false}" != true ]] || exit 75
jq -n \
  --arg name "$asset_name" \
  --argjson size "$(wc -c < "$source_path" | tr -d '[:space:]')" \
  '{id:9999,name:$name,state:"uploaded",size:$size,digest:null}'
EOF
chmod +x "$mock_bin/gh" "$mock_bin/curl"

export GH_REPO='Younics/sunder-agent-package'
export GH_TOKEN='fixture-token'
export MOCK_ASSET_STATE="$asset_state"
export MOCK_UPLOAD_LOG="$fixture_root/uploads.log"
touch "$MOCK_UPLOAD_LOG"

set_attempt 1
PATH="$mock_bin:$PATH" bash "$uploader" "$release_id" "$tag" \
  "$publication_run_id" "$publication_run_attempt" "$run_id" 1 "$workflow_sha" "$acceptance"
[[ "$(wc -l < "$MOCK_UPLOAD_LOG" | tr -d '[:space:]')" == 1 ]] \
  || { printf 'First Agent acceptance upload did not mutate exactly once.\n' >&2; exit 1; }

PATH="$mock_bin:$PATH" bash "$uploader" "$release_id" "$tag" \
  "$publication_run_id" "$publication_run_attempt" "$run_id" 1 "$workflow_sha" "$acceptance"
[[ "$(wc -l < "$MOCK_UPLOAD_LOG" | tr -d '[:space:]')" == 1 ]] \
  || { printf 'Equal Agent acceptance retry uploaded a duplicate.\n' >&2; exit 1; }

if MOCK_METADATA_MODE=null PATH="$mock_bin:$PATH" bash "$uploader" \
  "$release_id" "$tag" "$publication_run_id" "$publication_run_attempt" \
  "$run_id" 1 "$workflow_sha" "$acceptance" > "$fixture_root/null.log" 2>&1; then
  printf 'Agent acceptance uploader accepted null digest metadata.\n' >&2
  exit 1
fi
grep -Fq 'null or mismatched metadata' "$fixture_root/null.log"

if MOCK_METADATA_MODE=mismatch PATH="$mock_bin:$PATH" bash "$uploader" \
  "$release_id" "$tag" "$publication_run_id" "$publication_run_attempt" \
  "$run_id" 1 "$workflow_sha" "$acceptance" > "$fixture_root/digest.log" 2>&1; then
  printf 'Agent acceptance uploader accepted mismatched digest metadata.\n' >&2
  exit 1
fi
grep -Fq 'null or mismatched metadata' "$fixture_root/digest.log"

asset_one="$asset_state/agent-family-acceptance-$run_id-1.json"
cp "$asset_one" "$fixture_root/asset-one.original"
printf 'different acceptance\n' > "$asset_one"
if PATH="$mock_bin:$PATH" bash "$uploader" \
  "$release_id" "$tag" "$publication_run_id" "$publication_run_attempt" \
  "$run_id" 1 "$workflow_sha" "$acceptance" > "$fixture_root/bytes.log" 2>&1; then
  printf 'Agent acceptance uploader accepted differing existing bytes.\n' >&2
  exit 1
fi
cp "$fixture_root/asset-one.original" "$asset_one"

if MOCK_PUBLIC=true PATH="$mock_bin:$PATH" bash "$uploader" \
  "$release_id" "$tag" "$publication_run_id" "$publication_run_attempt" \
  "$run_id" 1 "$workflow_sha" "$acceptance" > "$fixture_root/public.log" 2>&1; then
  printf 'Agent acceptance uploader mutated a finalized release.\n' >&2
  exit 1
fi
grep -Fq 'left draft state' "$fixture_root/public.log"

rm -rf "$asset_state"
mkdir "$asset_state"
: > "$MOCK_UPLOAD_LOG"
if MOCK_FAIL_AFTER_MUTATION=true PATH="$mock_bin:$PATH" bash "$uploader" \
  "$release_id" "$tag" "$publication_run_id" "$publication_run_attempt" \
  "$run_id" 1 "$workflow_sha" "$acceptance" > "$fixture_root/runner-loss.log" 2>&1; then
  printf 'Agent acceptance upload did not surface simulated runner loss.\n' >&2
  exit 1
fi
[[ -f "$asset_one" ]] || { printf 'Simulated runner loss did not occur after upload mutation.\n' >&2; exit 1; }
PATH="$mock_bin:$PATH" bash "$uploader" "$release_id" "$tag" \
  "$publication_run_id" "$publication_run_attempt" "$run_id" 1 "$workflow_sha" "$acceptance"
[[ "$(wc -l < "$MOCK_UPLOAD_LOG" | tr -d '[:space:]')" == 1 ]] \
  || { printf 'Same-attempt Agent acceptance retry did not converge.\n' >&2; exit 1; }

set_attempt 2
PATH="$mock_bin:$PATH" bash "$uploader" "$release_id" "$tag" \
  "$publication_run_id" "$publication_run_attempt" "$run_id" 2 "$workflow_sha" "$acceptance"
[[ -f "$asset_state/agent-family-acceptance-$run_id-1.json" \
    && -f "$asset_state/agent-family-acceptance-$run_id-2.json" ]] \
  || { printf 'New Agent acceptance attempt did not preserve and recover from prior assets.\n' >&2; exit 1; }
acceptance_sha="$(hash_file "$acceptance")"
SUNDER_AGENT_ACCEPTANCE_CANDIDATE_ONLY=true PATH="$mock_bin:$PATH" bash "$downloader" \
  "$release_id" "$tag" "$source_sha" "$publication_run_id" "$publication_run_attempt" \
  "$run_id" 2 "$workflow_sha" "$acceptance_sha" "$fixture_root/downloaded.json"
cmp "$acceptance" "$fixture_root/downloaded.json"

printf 'Agent acceptance upload fixture passed: first upload, equal and runner-loss retry, immutable mismatch, null and mismatched metadata, finalized-release refusal, and new-attempt recovery are enforced.\n'
