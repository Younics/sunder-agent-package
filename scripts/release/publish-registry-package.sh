#!/usr/bin/env bash
set -euo pipefail
export LC_ALL=C

usage() {
  printf 'Usage: publish-registry-package.sh <registry-api-url> <archive> <package-id> <version>\n' >&2
}

if [[ $# -ne 4 ]]; then
  usage
  exit 2
fi

registry_api="${1%/}"
archive="$2"
package_id="$3"
version="$4"

[[ -n "$registry_api" && -n "$package_id" && -n "$version" ]] || { usage; exit 2; }
[[ "$registry_api" =~ ^https://[^/@?#]+$ ]] \
  || { printf 'Registry API URL must be an absolute HTTPS origin without user info, path, query, or fragment.\n' >&2; exit 2; }
[[ -f "$archive" ]] || { printf 'Registry package archive not found: %s\n' "$archive" >&2; exit 1; }
[[ -n "${SUNDER_REGISTRY_PUBLISH_TOKEN:-}" ]] \
  || { printf 'SUNDER_REGISTRY_PUBLISH_TOKEN is not configured for Registry publication.\n' >&2; exit 1; }
[[ "$SUNDER_REGISTRY_PUBLISH_TOKEN" =~ ^sunder_pub_v1_[A-Za-z0-9_-]{43}$ ]] \
  || { printf 'SUNDER_REGISTRY_PUBLISH_TOKEN must be a valid scoped sunder_pub_v1 publish token.\n' >&2; exit 1; }

for required_command in cat cmp curl cut grep jq mktemp sed sort tr unzip wc; do
  command -v "$required_command" >/dev/null 2>&1 \
    || { printf '%s is required to publish and verify Registry packages.\n' "$required_command" >&2; exit 127; }
done
if ! command -v sha256sum >/dev/null 2>&1 && ! command -v shasum >/dev/null 2>&1; then
  printf 'sha256sum or shasum is required to verify Registry packages.\n' >&2
  exit 127
fi

hash_file() {
  local path="$1"
  if command -v sha256sum >/dev/null 2>&1; then
    sha256sum "$path" | cut -d ' ' -f 1
  else
    shasum -a 256 "$path" | cut -d ' ' -f 1
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

write_uint32_be() {
  local value="$1"
  local encoded
  [[ "$value" =~ ^[0-9]+$ && "$value" -le 4294967295 ]] || return 1
  printf -v encoded '\\0%03o\\0%03o\\0%03o\\0%03o' \
    "$((value / 16777216 % 256))" \
    "$((value / 65536 % 256))" \
    "$((value / 256 % 256))" \
    "$((value % 256))"
  printf '%b' "$encoded"
}

append_identity_value() {
  local value="$1"
  write_uint32_be "${#value}"
  printf '%s' "$value"
}

compute_projection_identity() {
  local kind="$1"
  local rid="$2"
  local content_index_path="$3"
  # Mirrors SunderPackageProjectionIdentity v1: 32-bit big-endian byte lengths and a -1 null marker.
  {
    append_identity_value 'sunder-registry-projection-v1'
    append_identity_value '1'
    append_identity_value "$package_id"
    append_identity_value "$version"
    append_identity_value "$expected_sha"
    append_identity_value "$kind"
    if [[ -n "$rid" ]]; then
      append_identity_value "$rid"
    else
      printf '%b' '\0377\0377\0377\0377'
    fi
    append_identity_value "$expected_manifest_sha"
    command cat "$content_index_path"
  } | hash_stream
}

extract_archive_entry() {
  local archive_path="$1"
  local entry="$2"
  local entry_pattern
  entry_pattern="$(printf '%s' "$entry" | sed -e 's/\\/\\\\/g' -e 's/\[/[[]/g' -e 's/\*/\\*/g' -e 's/?/\\?/g')"
  unzip -p "$archive_path" "$entry_pattern"
}

projection_directory_allowed() {
  local path="$1"
  local kind="$2"
  local rid="$3"
  case "$path" in
    manifest|payload)
      return 0
      ;;
  esac
  if [[ "$kind" == 'shared' ]]; then
    [[ "$path" == 'payload/shared' || "$path" == payload/shared/* ]]
    return
  fi
  [[ "$path" == "payload/$kind" \
    || "$path" == "payload/$kind/shared" \
    || "$path" == "payload/$kind/shared/"* \
    || "$path" == "payload/$kind/$rid" \
    || "$path" == "payload/$kind/$rid/"* ]]
}

resolve_download_url() {
  local advertised_url="$1"
  local advertised_origin
  case "$advertised_url" in
    https://*)
      [[ "$advertised_url" =~ ^(https://[^/@?#]+)(/[^?#]*)$ ]] || return 1
      advertised_origin="${BASH_REMATCH[1]}"
      jq -e --arg origin "$advertised_origin" '.trustedArtifactOrigins | index($origin) != null' \
        "$details_path" >/dev/null || return 1
      printf '%s' "$advertised_url"
      ;;
    /*)
      printf '%s%s' "$registry_api" "$advertised_url"
      ;;
    *)
      return 1
      ;;
  esac
}

encoded_id="$(jq -rn --arg value "$package_id" '$value | @uri')"
encoded_version="$(jq -rn --arg value "$version" '$value | @uri')"
details_url="$registry_api/api/v1/packages/$encoded_id/versions/$encoded_version"
expected_sha="$(hash_file "$archive")"
expected_size="$(file_size "$archive")"

temp_root="${TMPDIR:-/tmp}"
temp_dir="$(mktemp -d "${temp_root%/}/sunder-agent-registry-publish.XXXXXX")"
trap 'rm -rf "$temp_dir"' EXIT
details_path="$temp_dir/version-details.json"
details_status=''
local_entries_path="$temp_dir/local-archive-entries.txt"
local_manifest_path="$temp_dir/local-sunder-package.json"
local_content_index_path="$temp_dir/local-content-index.json"
expected_projection_keys_path="$temp_dir/expected-projection-keys.json"

if ! unzip -tqq "$archive" >/dev/null 2>&1 \
  || ! unzip -Z1 "$archive" > "$local_entries_path"; then
  printf 'Local Registry package is not a readable canonical .sunderpkg archive: %s\n' "$archive" >&2
  exit 1
fi
manifest_entry_count="$(grep -Fxc 'manifest/sunder-package.json' "$local_entries_path" || true)"
content_index_entry_count="$(grep -Fxc 'manifest/content-index.json' "$local_entries_path" || true)"
if [[ "$manifest_entry_count" -ne 1 || "$content_index_entry_count" -ne 1 ]]; then
  printf 'Local Registry package must contain exactly one canonical manifest and content index: %s\n' "$archive" >&2
  exit 1
fi
if ! extract_archive_entry "$archive" 'manifest/sunder-package.json' > "$local_manifest_path" \
  || ! extract_archive_entry "$archive" 'manifest/content-index.json' > "$local_content_index_path"; then
  printf 'Could not read canonical package metadata from %s.\n' "$archive" >&2
  exit 1
fi
if ! jq -e --arg package_id "$package_id" --arg version "$version" '
  type == "object"
  and .id == $package_id
  and .version == $version
  and (.targets | type == "array")
  and all(.targets[];
    type == "object"
    and ((.role == "runtime") or (.role == "app"))
    and (.rid | type == "string" and test("^(win|linux|osx)-(x64|arm64)$")))
  and ([.targets[] | "\(.role)/\(.rid)"] | length == (unique | length))
' "$local_manifest_path" >/dev/null; then
  printf "Local canonical manifest does not match package '%s' %s or has invalid target keys.\n" \
    "$package_id" "$version" >&2
  exit 1
fi
expected_manifest_sha="$(hash_file "$local_manifest_path")"
expected_manifest_size="$(file_size "$local_manifest_path")"
if ! jq -e \
  --arg manifest_sha "$expected_manifest_sha" \
  --argjson manifest_size "$expected_manifest_size" '
    def portable_path:
      type == "string"
      and length > 0 and length <= 240
      and test("^[ -~]+$")
      and (startswith("/") | not)
      and (contains("\\") | not)
      and (split("/") | length <= 32 and all(.[];
        length > 0 and . != "." and . != ".."
        and (endswith(" ") | not) and (endswith(".") | not)
        and (test("[<>:\"|?*]") | not)));
    type == "object"
    and keys == ["files", "schemaVersion"]
    and .schemaVersion == 1
    and (.files | type == "array" and length <= 4096)
    and all(.files[];
      type == "object"
      and keys == ["path", "sha256", "size"]
      and (.path | portable_path)
      and (.sha256 | type == "string" and test("^[0-9a-f]{64}$"))
      and (.size | type == "number" and floor == . and . >= 0))
    and ([.files[].path] | length == (unique | length))
    and ([.files[] | select(.path == "manifest/sunder-package.json")]
      == [{path: "manifest/sunder-package.json", sha256: $manifest_sha, size: $manifest_size}])
  ' "$local_content_index_path" >/dev/null; then
  printf "Local canonical content index does not bind the manifest for package '%s' %s.\n" \
    "$package_id" "$version" >&2
  exit 1
fi
jq -c '
  [{kind: "shared", rid: null}]
  + ([.targets[] | {kind: .role, rid: .rid}]
    | unique_by([.kind, .rid])
    | sort_by([(if .kind == "runtime" then 1 else 2 end), .rid]))
' "$local_manifest_path" > "$expected_projection_keys_path"

fetch_version_details() {
  if ! details_status="$(curl -sS -o "$details_path" -w '%{http_code}' "$details_url")"; then
    printf "Could not fetch Registry version details for package '%s' %s.\n" "$package_id" "$version" >&2
    return 1
  fi
  [[ "$details_status" =~ ^[0-9]{3}$ ]] \
    || { printf "Registry returned an invalid HTTP status for package '%s' %s.\n" "$package_id" "$version" >&2; return 1; }
}

inspect_projection_archive() {
  local projection_download="$1"
  local index="$2"
  local kind="$3"
  local rid="$4"
  local advertised_projection_identity="$5"
  local projection_label="$kind${rid:+/$rid}"
  local entries_path="$temp_dir/projection-$index.entries"
  local actual_files_path="$temp_dir/projection-$index.actual-files"
  local expected_files_path="$temp_dir/projection-$index.expected-files"
  local descriptor_path="$temp_dir/projection-$index-descriptor.json"
  local manifest_path="$temp_dir/projection-$index-manifest.json"
  local content_index_path="$temp_dir/projection-$index-content-index.json"
  local payload_path="$temp_dir/projection-$index-payload"
  local entry
  local directory
  local entry_hash
  local entry_size
  local indexed_hash
  local indexed_size
  local descriptor_identity
  local computed_identity

  if ! unzip -tqq "$projection_download" >/dev/null 2>&1 \
    || ! unzip -Z1 "$projection_download" > "$entries_path"; then
    printf "Registry projection '%s' is not a readable projection archive for package '%s' %s.\n" \
      "$projection_label" "$package_id" "$version" >&2
    return 1
  fi
  if [[ "$(grep -Fxc 'manifest/sunder-projection.json' "$entries_path" || true)" -ne 1 \
    || "$(grep -Fxc 'manifest/sunder-package.json' "$entries_path" || true)" -ne 1 \
    || "$(grep -Fxc 'manifest/content-index.json' "$entries_path" || true)" -ne 1 ]]; then
    printf "Registry projection '%s' does not contain exactly one copy of each projection metadata file for package '%s' %s.\n" \
      "$projection_label" "$package_id" "$version" >&2
    return 1
  fi
  if ! extract_archive_entry "$projection_download" 'manifest/sunder-projection.json' > "$descriptor_path" \
    || ! extract_archive_entry "$projection_download" 'manifest/sunder-package.json' > "$manifest_path" \
    || ! extract_archive_entry "$projection_download" 'manifest/content-index.json' > "$content_index_path"; then
    printf "Registry projection '%s' has unreadable projection metadata for package '%s' %s.\n" \
      "$projection_label" "$package_id" "$version" >&2
    return 1
  fi
  if ! cmp -s "$local_manifest_path" "$manifest_path"; then
    printf "Registry projection '%s' does not contain the exact local canonical manifest for package '%s' %s.\n" \
      "$projection_label" "$package_id" "$version" >&2
    return 1
  fi
  if ! jq -e \
    --arg package_id "$package_id" \
    --arg version "$version" \
    --arg kind "$kind" \
    --arg rid "$rid" \
    --arg source_archive_sha "$expected_sha" \
    --arg manifest_sha "$expected_manifest_sha" '
      type == "object"
      and keys == [
        "kind", "manifestSha256", "packageId", "packageVersion",
        "projectionFormatVersion", "projectionSha256", "rid", "sourceArchiveSha256"
      ]
      and .projectionFormatVersion == 1
      and .packageId == $package_id
      and .packageVersion == $version
      and .sourceArchiveSha256 == $source_archive_sha
      and .kind == $kind
      and (if $rid == "" then .rid == null else .rid == $rid end)
      and .manifestSha256 == $manifest_sha
      and (.projectionSha256 | type == "string" and test("^[0-9a-f]{64}$"))
    ' "$descriptor_path" >/dev/null; then
    printf "Registry projection '%s' descriptor does not match the local release identity for package '%s' %s.\n" \
      "$projection_label" "$package_id" "$version" >&2
    return 1
  fi
  if ! jq -e --arg kind "$kind" --arg rid "$rid" '
    def portable_path:
      type == "string"
      and length > 0 and length <= 240
      and test("^[ -~]+$")
      and (startswith("/") | not)
      and (contains("\\") | not)
      and (split("/") | length <= 32 and all(.[];
        length > 0 and . != "." and . != ".."
        and (endswith(" ") | not) and (endswith(".") | not)
        and (test("[<>:\"|?*]") | not)));
    def belongs:
      if $kind == "shared" then
        startswith("payload/shared/")
      else
        startswith("payload/" + $kind + "/shared/")
        or startswith("payload/" + $kind + "/" + $rid + "/")
      end;
    type == "object"
    and keys == ["files", "schemaVersion"]
    and .schemaVersion == 1
    and (.files | type == "array" and length <= 4096)
    and all(.files[];
      type == "object"
      and keys == ["path", "sha256", "size"]
      and (.path | portable_path and belongs)
      and (.sha256 | type == "string" and test("^[0-9a-f]{64}$"))
      and (.size | type == "number" and floor == . and . >= 0))
    and ([.files[].path] as $paths
      | $paths == ($paths | sort) and ($paths | length) == ($paths | unique | length))
  ' "$content_index_path" >/dev/null; then
    printf "Registry projection '%s' has an invalid projection content index for package '%s' %s.\n" \
      "$projection_label" "$package_id" "$version" >&2
    return 1
  fi
  if ! jq -e \
    --arg kind "$kind" \
    --arg rid "$rid" \
    --slurpfile canonical "$local_content_index_path" '
      ($canonical[0].files
        | map(select(
          if $kind == "shared" then
            .path | startswith("payload/shared/")
          else
            (.path | startswith("payload/" + $kind + "/shared/"))
            or (.path | startswith("payload/" + $kind + "/" + $rid + "/"))
          end))
        | sort_by(.path)) as $expected
      | .schemaVersion == 1 and .files == $expected
    ' "$content_index_path" >/dev/null; then
    printf "Registry projection '%s' content index is not the exact local canonical projection for package '%s' %s.\n" \
      "$projection_label" "$package_id" "$version" >&2
    return 1
  fi

  computed_identity="$(compute_projection_identity "$kind" "$rid" "$content_index_path")"
  descriptor_identity="$(jq -r '.projectionSha256' "$descriptor_path")"
  if [[ "$computed_identity" != "$descriptor_identity" ]]; then
    printf "Registry projection '%s' content identity does not match its descriptor for package '%s' %s.\n" \
      "$projection_label" "$package_id" "$version" >&2
    return 1
  fi
  if [[ "$computed_identity" != "$advertised_projection_identity" ]]; then
    printf "Registry projection '%s' content identity differs from Registry metadata for package '%s' %s.\n" \
      "$projection_label" "$package_id" "$version" >&2
    return 1
  fi

  : > "$actual_files_path"
  while IFS= read -r entry || [[ -n "$entry" ]]; do
    [[ -n "$entry" && ${#entry} -le 241 && "$entry" =~ ^[[:print:]]+$ ]] || {
      printf "Registry projection '%s' contains an unsafe archive entry for package '%s' %s.\n" \
        "$projection_label" "$package_id" "$version" >&2
      return 1
    }
    if [[ "$entry" == */ ]]; then
      directory="${entry%/}"
      projection_directory_allowed "$directory" "$kind" "$rid" || {
        printf "Registry projection '%s' contains directory '%s' outside its projection roots for package '%s' %s.\n" \
          "$projection_label" "$directory" "$package_id" "$version" >&2
        return 1
      }
      continue
    fi
    printf '%s\n' "$entry" >> "$actual_files_path"
  done < "$entries_path"
  {
    printf '%s\n' \
      'manifest/content-index.json' \
      'manifest/sunder-package.json' \
      'manifest/sunder-projection.json'
    jq -r '.files[].path' "$content_index_path"
  } > "$expected_files_path"
  sort -o "$actual_files_path" "$actual_files_path"
  sort -o "$expected_files_path" "$expected_files_path"
  if ! cmp -s "$actual_files_path" "$expected_files_path"; then
    printf "Registry projection '%s' archive inventory differs from its content index for package '%s' %s.\n" \
      "$projection_label" "$package_id" "$version" >&2
    return 1
  fi

  while IFS=$'\t' read -r entry indexed_hash indexed_size; do
    if ! extract_archive_entry "$projection_download" "$entry" > "$payload_path"; then
      printf "Registry projection '%s' payload '%s' is unreadable for package '%s' %s.\n" \
        "$projection_label" "$entry" "$package_id" "$version" >&2
      return 1
    fi
    entry_hash="$(hash_file "$payload_path")"
    entry_size="$(file_size "$payload_path")"
    if [[ "$entry_hash" != "$indexed_hash" || "$entry_size" != "$indexed_size" ]]; then
      printf "Registry projection '%s' payload '%s' does not match its content index for package '%s' %s.\n" \
        "$projection_label" "$entry" "$package_id" "$version" >&2
      return 1
    fi
  done < <(jq -r '.files[] | [.path, .sha256, (.size | tostring)] | @tsv' "$content_index_path")
}

verify_version_details() {
  local canonical_url
  local canonical_download="$temp_dir/canonical.sunderpkg"
  local canonical_sha
  local canonical_size
  local advertised_canonical_sha
  local advertised_canonical_size
  local projection_count
  local index
  local kind
  local rid
  local projection_label
  local projection_url
  local projection_download
  local projection_sha
  local projection_size
  local advertised_projection_sha
  local advertised_projection_size
  local source_archive_sha
  local manifest_sha
  local projection_identity
  local projection_format

  if ! jq -e --arg package_id "$package_id" --arg version "$version" '
    type == "object"
    and .packageId == $package_id
    and .version == $version
    and (.canonicalArtifact | type == "object")
    and (.canonicalArtifact.sha256 | type == "string" and test("^[0-9a-f]{64}$"))
    and (.canonicalArtifact.size | type == "number" and floor == . and . >= 0)
    and (.canonicalArtifact.downloadUrl | type == "string" and length > 0)
    and (.trustedArtifactOrigins | type == "array" and all(.[];
      type == "string" and test("^https://[^/@?#]+$")))
    and (.projections | type == "array" and length > 0)
    and ([.projections[] | "\(.kind)/\(.rid // "")"] | length == (unique | length))
    and (([.projections[] | [
      (if .kind == "shared" then 0 elif .kind == "runtime" then 1 elif .kind == "app" then 2 else 3 end),
      (.rid // "")
    ]]) as $keys | $keys == ($keys | sort))
    and all(.projections[];
      type == "object"
      and has("rid")
      and ((.kind == "shared" and .rid == null)
        or ((.kind == "runtime" or .kind == "app")
          and (.rid | type == "string" and test("^(win|linux|osx)-(x64|arm64)$"))))
      and (.sha256 | type == "string" and test("^[0-9a-f]{64}$"))
      and (.size | type == "number" and floor == . and . >= 0)
      and (.downloadUrl | type == "string" and length > 0)
      and (.sourceArchiveSha256 | type == "string" and test("^[0-9a-f]{64}$"))
      and (.manifestSha256 | type == "string" and test("^[0-9a-f]{64}$"))
      and (.projectionContentIdentity | type == "string" and test("^[0-9a-f]{64}$"))
      and .projectionFormatVersion == 1)
  ' "$details_path" >/dev/null; then
    printf "Registry returned invalid version details for package '%s' %s.\n" "$package_id" "$version" >&2
    return 1
  fi

  if ! jq -e --slurpfile expected "$expected_projection_keys_path" '
    [.projections[] | {kind: .kind, rid: .rid}] == $expected[0]
  ' "$details_path" >/dev/null; then
    printf "Registry projection inventory differs from the local canonical targets for package '%s' %s.\n" \
      "$package_id" "$version" >&2
    return 1
  fi
  if ! jq -e --arg manifest_sha "$expected_manifest_sha" \
    'all(.projections[]; .manifestSha256 == $manifest_sha)' "$details_path" >/dev/null; then
    printf "Registry projection manifestSha256 differs from the local canonical manifest for package '%s' %s.\n" \
      "$package_id" "$version" >&2
    return 1
  fi

  advertised_canonical_sha="$(jq -r '.canonicalArtifact.sha256' "$details_path")"
  [[ "$advertised_canonical_sha" == "$expected_sha" ]] || {
    printf "Registry canonical SHA-256 differs from the local archive for package '%s' %s.\n" "$package_id" "$version" >&2
    return 1
  }
  advertised_canonical_size="$(jq -r '.canonicalArtifact.size' "$details_path")"
  [[ "$advertised_canonical_size" == "$expected_size" ]] || {
    printf "Registry canonical size differs from the local archive for package '%s' %s.\n" "$package_id" "$version" >&2
    return 1
  }

  if ! canonical_url="$(resolve_download_url "$(jq -r '.canonicalArtifact.downloadUrl' "$details_path")")"; then
    printf "Registry advertised an invalid canonical download URL for package '%s' %s.\n" "$package_id" "$version" >&2
    return 1
  fi
  if ! curl --fail-with-body -sS "$canonical_url" -o "$canonical_download"; then
    printf "Could not download the Registry canonical archive for package '%s' %s.\n" "$package_id" "$version" >&2
    return 1
  fi
  canonical_sha="$(hash_file "$canonical_download")"
  canonical_size="$(file_size "$canonical_download")"
  [[ "$canonical_sha" == "$advertised_canonical_sha" && "$canonical_size" == "$advertised_canonical_size" ]] || {
    printf "Registry canonical download checksum or size differs for package '%s' %s.\n" "$package_id" "$version" >&2
    return 1
  }

  projection_count="$(jq '.projections | length' "$details_path")"
  for ((index = 0; index < projection_count; index++)); do
    kind="$(jq -r --argjson index "$index" '.projections[$index].kind' "$details_path")"
    rid="$(jq -r --argjson index "$index" '.projections[$index].rid // empty' "$details_path")"
    projection_label="$kind${rid:+/$rid}"
    source_archive_sha="$(jq -r --argjson index "$index" '.projections[$index].sourceArchiveSha256' "$details_path")"
    manifest_sha="$(jq -r --argjson index "$index" '.projections[$index].manifestSha256' "$details_path")"
    projection_identity="$(jq -r --argjson index "$index" '.projections[$index].projectionContentIdentity' "$details_path")"
    projection_format="$(jq -r --argjson index "$index" '.projections[$index].projectionFormatVersion' "$details_path")"
    [[ "$source_archive_sha" == "$expected_sha" ]] || {
      printf "Registry projection '%s' has the wrong sourceArchiveSha256 for package '%s' %s.\n" \
        "$projection_label" "$package_id" "$version" >&2
      return 1
    }
    [[ "$manifest_sha" == "$expected_manifest_sha" ]] || {
      printf "Registry projection '%s' has the wrong manifestSha256 for package '%s' %s.\n" \
        "$projection_label" "$package_id" "$version" >&2
      return 1
    }

    if ! projection_url="$(resolve_download_url "$(jq -r --argjson index "$index" '.projections[$index].downloadUrl' "$details_path")")"; then
      printf "Registry projection '%s' has an invalid download URL for package '%s' %s.\n" \
        "$projection_label" "$package_id" "$version" >&2
      return 1
    fi
    projection_download="$temp_dir/projection-$index.sunderpkg"
    if ! curl --fail-with-body -sS "$projection_url" -o "$projection_download"; then
      printf "Could not download Registry projection '%s' for package '%s' %s.\n" \
        "$projection_label" "$package_id" "$version" >&2
      return 1
    fi
    advertised_projection_sha="$(jq -r --argjson index "$index" '.projections[$index].sha256' "$details_path")"
    advertised_projection_size="$(jq -r --argjson index "$index" '.projections[$index].size' "$details_path")"
    projection_sha="$(hash_file "$projection_download")"
    projection_size="$(file_size "$projection_download")"
    [[ "$projection_sha" == "$advertised_projection_sha" && "$projection_size" == "$advertised_projection_size" ]] || {
      printf "Registry projection '%s' checksum or size differs for package '%s' %s.\n" \
        "$projection_label" "$package_id" "$version" >&2
      return 1
    }
    [[ "$projection_format" == '1' ]] || {
      printf "Registry projection '%s' has an unsupported projection format for package '%s' %s.\n" \
        "$projection_label" "$package_id" "$version" >&2
      return 1
    }
    inspect_projection_archive \
      "$projection_download" "$index" "$kind" "$rid" "$projection_identity"
  done
}

fetch_version_details
case "$details_status" in
  200)
    verify_version_details
    printf "Registry package '%s' %s already exists and matches the verified release archive and projections.\n" \
      "$package_id" "$version"
    exit 0
    ;;
  404)
    ;;
  *)
    printf "Registry version preflight for package '%s' %s returned HTTP %s.\n" \
      "$package_id" "$version" "$details_status" >&2
    exit 1
    ;;
esac

if ! publish_response="$(curl --fail-with-body -sS \
  -H "Authorization: Bearer $SUNDER_REGISTRY_PUBLISH_TOKEN" \
  -H "X-Sunder-Expected-Resource-Id: $package_id" \
  -H 'X-Sunder-Set-Latest: false' \
  -F "package=@$archive" \
  -F 'setLatest=false' \
  "$registry_api/api/v1/packages/publish")"; then
  printf "Registry publication failed for package '%s' %s.\n" "$package_id" "$version" >&2
  exit 1
fi
if ! jq -e --arg package_id "$package_id" --arg version "$version" \
  '.success == true and .packageId == $package_id and .version == $version' \
  <<< "$publish_response" >/dev/null; then
  printf "Registry rejected publication for package '%s' %s.\n" "$package_id" "$version" >&2
  exit 1
fi

fetch_version_details
[[ "$details_status" == "200" ]] || {
  printf "Registry version details for package '%s' %s returned HTTP %s after publication.\n" \
    "$package_id" "$version" "$details_status" >&2
  exit 1
}
verify_version_details
printf "Published and verified Registry package '%s' %s canonical archive and projections.\n" \
  "$package_id" "$version"
