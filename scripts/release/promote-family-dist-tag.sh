#!/usr/bin/env bash
set -euo pipefail

usage() {
  printf 'Usage: promote-family-dist-tag.sh <registry-api-url> <latest|preview> <version> <package-id>...\n' >&2
}

if [[ $# -lt 4 ]]; then
  usage
  exit 2
fi

registry_api="${1%/}"
dist_tag="$2"
version="$3"
shift 3
package_ids=("$@")

[[ -n "$registry_api" ]] || { usage; exit 2; }
[[ "$dist_tag" == "latest" || "$dist_tag" == "preview" ]] \
  || { printf "Family promotion only supports the 'latest' and 'preview' dist tags.\n" >&2; exit 2; }
[[ -n "${REGISTRY_TOKEN:-}" ]] || { printf 'REGISTRY_TOKEN is not configured.\n' >&2; exit 1; }

for required_command in curl jq; do
  command -v "$required_command" >/dev/null 2>&1 \
    || { printf '%s is required to promote Registry dist tags.\n' "$required_command" >&2; exit 127; }
done

export LC_ALL=C
semver_comparison=0

is_strict_semver() {
  local candidate="$1"
  local precedence
  local core
  local prerelease=''
  local component
  local -a core_parts
  local -a prerelease_parts

  [[ ${#candidate} -le 256 ]] || return 1
  [[ "$candidate" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?(\+[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?$ ]] || return 1

  precedence="${candidate%%+*}"
  core="${precedence%%-*}"
  IFS='.' read -r -a core_parts <<< "$core"
  [[ ${#core_parts[@]} -eq 3 ]] || return 1
  for component in "${core_parts[@]}"; do
    [[ "$component" == "0" || "$component" != 0* ]] || return 1
  done

  if [[ "$precedence" == *-* ]]; then
    prerelease="${precedence#*-}"
    IFS='.' read -r -a prerelease_parts <<< "$prerelease"
    for component in "${prerelease_parts[@]}"; do
      if [[ "$component" =~ ^[0-9]+$ ]]; then
        [[ "$component" == "0" || "$component" != 0* ]] || return 1
      fi
    done
  fi
  return 0
}

compare_numeric_identifier() {
  local left="$1"
  local right="$2"

  if [[ ${#left} -lt ${#right} ]]; then
    semver_comparison=-1
  elif [[ ${#left} -gt ${#right} ]]; then
    semver_comparison=1
  elif [[ "$left" == "$right" ]]; then
    semver_comparison=0
  elif [[ "$left" < "$right" ]]; then
    semver_comparison=-1
  else
    semver_comparison=1
  fi
}

compare_semver_precedence() {
  local left="$1"
  local right="$2"
  local left_precedence="${left%%+*}"
  local right_precedence="${right%%+*}"
  local left_core="${left_precedence%%-*}"
  local right_core="${right_precedence%%-*}"
  local left_prerelease=''
  local right_prerelease=''
  local left_identifier
  local right_identifier
  local index
  local limit
  local -a left_core_parts
  local -a right_core_parts
  local -a left_prerelease_parts
  local -a right_prerelease_parts

  IFS='.' read -r -a left_core_parts <<< "$left_core"
  IFS='.' read -r -a right_core_parts <<< "$right_core"
  for index in 0 1 2; do
    compare_numeric_identifier "${left_core_parts[$index]}" "${right_core_parts[$index]}"
    [[ $semver_comparison -eq 0 ]] || return 0
  done

  [[ "$left_precedence" != *-* ]] || left_prerelease="${left_precedence#*-}"
  [[ "$right_precedence" != *-* ]] || right_prerelease="${right_precedence#*-}"
  if [[ -z "$left_prerelease" ]]; then
    [[ -z "$right_prerelease" ]] && semver_comparison=0 || semver_comparison=1
    return
  fi
  if [[ -z "$right_prerelease" ]]; then
    semver_comparison=-1
    return
  fi

  IFS='.' read -r -a left_prerelease_parts <<< "$left_prerelease"
  IFS='.' read -r -a right_prerelease_parts <<< "$right_prerelease"
  limit=${#left_prerelease_parts[@]}
  [[ ${#right_prerelease_parts[@]} -ge $limit ]] || limit=${#right_prerelease_parts[@]}
  for ((index = 0; index < limit; index++)); do
    left_identifier="${left_prerelease_parts[$index]}"
    right_identifier="${right_prerelease_parts[$index]}"
    if [[ "$left_identifier" =~ ^[0-9]+$ && "$right_identifier" =~ ^[0-9]+$ ]]; then
      compare_numeric_identifier "$left_identifier" "$right_identifier"
    elif [[ "$left_identifier" =~ ^[0-9]+$ ]]; then
      semver_comparison=-1
    elif [[ "$right_identifier" =~ ^[0-9]+$ ]]; then
      semver_comparison=1
    elif [[ "$left_identifier" == "$right_identifier" ]]; then
      semver_comparison=0
    elif [[ "$left_identifier" < "$right_identifier" ]]; then
      semver_comparison=-1
    else
      semver_comparison=1
    fi
    [[ $semver_comparison -eq 0 ]] || return 0
  done

  if [[ ${#left_prerelease_parts[@]} -lt ${#right_prerelease_parts[@]} ]]; then
    semver_comparison=-1
  elif [[ ${#left_prerelease_parts[@]} -gt ${#right_prerelease_parts[@]} ]]; then
    semver_comparison=1
  else
    semver_comparison=0
  fi
}

is_strict_semver "$version" \
  || { printf "Promotion target '%s' is not strict SemVer 2.0.\n" "$version" >&2; exit 2; }
target_precedence="${version%%+*}"
if [[ "$dist_tag" == "latest" && "$target_precedence" == *-* ]]; then
  printf "The 'latest' dist tag cannot be promoted to prerelease version '%s'.\n" "$version" >&2
  exit 2
fi
if [[ "$dist_tag" == "preview" && "$target_precedence" != *-* ]]; then
  printf "The 'preview' dist tag requires a prerelease version, not '%s'.\n" "$version" >&2
  exit 2
fi

for ((index = 0; index < ${#package_ids[@]}; index++)); do
  [[ -n "${package_ids[$index]}" ]] || { printf 'Package ids cannot be empty.\n' >&2; exit 2; }
  for ((other = 0; other < index; other++)); do
    if [[ "${package_ids[$other]}" == "${package_ids[$index]}" ]]; then
      printf "Duplicate package id '%s' in family promotion.\n" "${package_ids[$index]}" >&2
      exit 2
    fi
  done
done

dist_tag_url() {
  local package_id="$1"
  local encoded_id
  local encoded_tag

  encoded_id="$(jq -rn --arg value "$package_id" '$value | @uri')"
  encoded_tag="$(jq -rn --arg value "$dist_tag" '$value | @uri')"
  printf '%s/api/v1/packages/%s/dist-tags/%s' "$registry_api" "$encoded_id" "$encoded_tag"
}

dist_tags_url() {
  local package_id="$1"
  local encoded_id

  encoded_id="$(jq -rn --arg value "$package_id" '$value | @uri')"
  printf '%s/api/v1/packages/%s/dist-tags' "$registry_api" "$encoded_id"
}

fetch_dist_tags() {
  local package_id="$1"
  local url

  url="$(dist_tags_url "$package_id")"
  curl --fail-with-body -sS "$url"
}

validate_dist_tags_response() {
  local package_id="$1"
  local response="$2"

  jq -e --arg package_id "$package_id" '
    type == "object"
    and .packageId == $package_id
    and (.distTags | type == "array")
    and all(.distTags[]; (.tag | type == "string") and (.version | type == "string"))
  ' <<< "$response" >/dev/null
}

read_tag_target() {
  local response="$1"
  local count

  count="$(jq --arg tag "$dist_tag" '[.distTags[] | select(.tag == $tag)] | length' <<< "$response")"
  [[ "$count" -le 1 ]] || return 1
  if [[ "$count" -eq 1 ]]; then
    jq -r --arg tag "$dist_tag" '.distTags[] | select(.tag == $tag) | .version' <<< "$response"
  fi
}

set_dist_tag() {
  local package_id="$1"
  local target_version="$2"
  local payload
  local response
  local url

  payload="$(jq -cn --arg version "$target_version" '{version: $version}')"
  url="$(dist_tag_url "$package_id")"
  if ! response="$(curl --fail-with-body -sS -X PUT \
    -H "Authorization: Bearer $REGISTRY_TOKEN" \
    -H 'Content-Type: application/json' \
    --data "$payload" \
    "$url")"; then
    printf "Registry failed to set package '%s' dist tag '%s' to %s.\n" "$package_id" "$dist_tag" "$target_version" >&2
    return 1
  fi
  if ! jq -e '.success == true' <<< "$response" >/dev/null; then
    printf "Registry rejected package '%s' dist tag '%s' target %s.\n" "$package_id" "$dist_tag" "$target_version" >&2
    return 1
  fi
}

delete_dist_tag() {
  local package_id="$1"
  local response
  local url

  url="$(dist_tag_url "$package_id")"
  if ! response="$(curl --fail-with-body -sS -X DELETE \
    -H "Authorization: Bearer $REGISTRY_TOKEN" \
    "$url")"; then
    printf "Registry failed to delete package '%s' dist tag '%s'.\n" "$package_id" "$dist_tag" >&2
    return 1
  fi
  if ! jq -e '.success == true' <<< "$response" >/dev/null; then
    printf "Registry rejected deletion of package '%s' dist tag '%s'.\n" "$package_id" "$dist_tag" >&2
    return 1
  fi
}

verify_tag_target() {
  local package_id="$1"
  local expected_version="$2"
  local response
  local actual_version

  if ! response="$(fetch_dist_tags "$package_id")"; then
    printf "Could not verify package '%s' dist tag '%s'.\n" "$package_id" "$dist_tag" >&2
    return 1
  fi
  if ! validate_dist_tags_response "$package_id" "$response"; then
    printf "Registry returned an invalid dist-tag document for package '%s'.\n" "$package_id" >&2
    return 1
  fi
  if ! actual_version="$(read_tag_target "$response")"; then
    printf "Registry returned duplicate '%s' dist tags for package '%s'.\n" "$dist_tag" "$package_id" >&2
    return 1
  fi
  if [[ "$actual_version" != "$expected_version" ]]; then
    printf "Package '%s' dist tag '%s' resolved to '%s', expected '%s'.\n" \
      "$package_id" "$dist_tag" "${actual_version:-<absent>}" "$expected_version" >&2
    return 1
  fi
}

verify_tag_absent() {
  local package_id="$1"
  local response
  local actual_version

  if ! response="$(fetch_dist_tags "$package_id")"; then
    printf "Could not verify deletion of package '%s' dist tag '%s'.\n" "$package_id" "$dist_tag" >&2
    return 1
  fi
  if ! validate_dist_tags_response "$package_id" "$response"; then
    printf "Registry returned an invalid dist-tag document for package '%s'.\n" "$package_id" >&2
    return 1
  fi
  if ! actual_version="$(read_tag_target "$response")"; then
    printf "Registry returned duplicate '%s' dist tags for package '%s'.\n" "$dist_tag" "$package_id" >&2
    return 1
  fi
  if [[ -n "$actual_version" ]]; then
    printf "Package '%s' dist tag '%s' still resolves to '%s' after deletion.\n" "$package_id" "$dist_tag" "$actual_version" >&2
    return 1
  fi
}

previous_versions=()
previous_exists=()
changed_indices=()
rollback_needed=false

rollback_changed_tags() {
  local changed_position
  local package_index
  local package_id
  local rollback_failed=false

  printf 'Promotion failed; rolling back %s attempted dist-tag change(s).\n' "${#changed_indices[@]}" >&2
  for ((changed_position = ${#changed_indices[@]} - 1; changed_position >= 0; changed_position--)); do
    package_index="${changed_indices[$changed_position]}"
    package_id="${package_ids[$package_index]}"
    if [[ "${previous_exists[$package_index]}" == "true" ]]; then
      if ! set_dist_tag "$package_id" "${previous_versions[$package_index]}" \
        || ! verify_tag_target "$package_id" "${previous_versions[$package_index]}"; then
        printf "Rollback failed for package '%s' dist tag '%s'; expected prior target '%s'.\n" \
          "$package_id" "$dist_tag" "${previous_versions[$package_index]}" >&2
        rollback_failed=true
      fi
    elif ! delete_dist_tag "$package_id" || ! verify_tag_absent "$package_id"; then
      printf "Rollback failed for newly created package '%s' dist tag '%s'.\n" "$package_id" "$dist_tag" >&2
      rollback_failed=true
    fi
  done

  if [[ "$rollback_failed" == "true" ]]; then
    printf 'Registry dist-tag rollback was incomplete; manual repair is required.\n' >&2
  else
    printf 'Registry dist-tag rollback restored the complete pre-promotion snapshot.\n' >&2
  fi
}

on_exit() {
  local exit_code=$?
  trap - EXIT INT TERM
  if [[ $exit_code -ne 0 && "$rollback_needed" == "true" ]]; then
    set +e
    rollback_changed_tags
  fi
  exit "$exit_code"
}

trap on_exit EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

# Snapshot and validate the entire family before the first mutation.
for ((index = 0; index < ${#package_ids[@]}; index++)); do
  package_id="${package_ids[$index]}"
  if ! tags="$(fetch_dist_tags "$package_id")"; then
    printf "Could not capture package '%s' dist tags before promotion.\n" "$package_id" >&2
    exit 1
  fi
  if ! validate_dist_tags_response "$package_id" "$tags"; then
    printf "Registry returned an invalid dist-tag document for package '%s'.\n" "$package_id" >&2
    exit 1
  fi
  if ! previous_version="$(read_tag_target "$tags")"; then
    printf "Registry returned duplicate '%s' dist tags for package '%s'.\n" "$dist_tag" "$package_id" >&2
    exit 1
  fi

  if [[ -n "$previous_version" ]]; then
    is_strict_semver "$previous_version" \
      || { printf "Existing package '%s' dist tag '%s' target '%s' is not strict SemVer.\n" "$package_id" "$dist_tag" "$previous_version" >&2; exit 1; }
    compare_semver_precedence "$version" "$previous_version"
    if [[ $semver_comparison -lt 0 ]]; then
      printf "Refusing to regress package '%s' dist tag '%s' from %s to older SemVer %s.\n" \
        "$package_id" "$dist_tag" "$previous_version" "$version" >&2
      exit 1
    fi
    previous_exists+=(true)
    previous_versions+=("$previous_version")
  else
    previous_exists+=(false)
    previous_versions+=('')
  fi
done

changed_count=0
idempotent_count=0
for ((index = 0; index < ${#package_ids[@]}; index++)); do
  if [[ "${previous_exists[$index]}" == "true" && "${previous_versions[$index]}" == "$version" ]]; then
    idempotent_count=$((idempotent_count + 1))
    continue
  fi

  # Record the attempt before PUT so an ambiguous network failure is also compensated.
  changed_indices+=("$index")
  rollback_needed=true
  package_id="${package_ids[$index]}"
  set_dist_tag "$package_id" "$version"
  verify_tag_target "$package_id" "$version"
  changed_count=$((changed_count + 1))
done

# Re-read the complete family before committing the workflow-level transaction.
for package_id in "${package_ids[@]}"; do
  verify_tag_target "$package_id" "$version"
done

rollback_needed=false
printf "Promoted package family dist tag '%s' to %s: %s changed, %s already current.\n" \
  "$dist_tag" "$version" "$changed_count" "$idempotent_count"
