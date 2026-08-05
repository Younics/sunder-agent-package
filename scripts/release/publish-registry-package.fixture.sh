#!/usr/bin/env bash
set -euo pipefail
export LC_ALL=C

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
publisher="$script_dir/publish-registry-package.sh"
[[ -f "$publisher" ]] || { printf 'Registry publisher not found: %s\n' "$publisher" >&2; exit 1; }

for required_command in cat cp cut grep jq mktemp mv tr unzip wc zip; do
  command -v "$required_command" >/dev/null 2>&1 \
    || { printf '%s is required to run the Registry publication fixture.\n' "$required_command" >&2; exit 127; }
done
if ! command -v sha256sum >/dev/null 2>&1 && ! command -v shasum >/dev/null 2>&1; then
  printf 'sha256sum or shasum is required to run the Registry publication fixture.\n' >&2
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

write_uint32_be() {
  local value="$1"
  local encoded
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
  {
    append_identity_value 'sunder-registry-projection-v1'
    append_identity_value '1'
    append_identity_value 'fixture.package'
    append_identity_value '1.2.3'
    append_identity_value "$canonical_sha"
    append_identity_value "$kind"
    if [[ -n "$rid" ]]; then
      append_identity_value "$rid"
    else
      printf '%b' '\0377\0377\0377\0377'
    fi
    append_identity_value "$manifest_sha"
    command cat "$content_index_path"
  } | hash_stream
}

temp_root="${TMPDIR:-/tmp}"
fixture_dir="$(mktemp -d "${temp_root%/}/sunder-agent-registry-fixture.XXXXXX")"
trap 'rm -rf "$fixture_dir"' EXIT
mock_bin="$fixture_dir/mock-bin"
mkdir -p "$mock_bin"

archive="$fixture_dir/fixture.package.1.2.3.sunderpkg"
canonical_root="$fixture_dir/canonical"
mkdir -p \
  "$canonical_root/manifest" \
  "$canonical_root/payload/shared" \
  "$canonical_root/payload/runtime/linux-x64/bin"
jq -cn '
  {
    archiveFormatVersion: 1,
    manifestVersion: 1,
    id: "fixture.package",
    name: "Fixture Package",
    summary: "Registry release fixture",
    version: "1.2.3",
    icon: null,
    dependsOn: [],
    targets: [{
      role: "runtime",
      rid: "linux-x64",
      kind: "worker",
      entryPoint: "bin/worker",
      targetFramework: "net10.0",
      sdkVersion: "1.0.0",
      requiredHostCapabilities: ["worker-protocol.v2"],
      views: []
    }],
    contractBundles: [],
    usesContracts: [],
    provides: []
  }
' > "$canonical_root/manifest/sunder-package.json"
printf 'shared projection bytes\n' > "$canonical_root/payload/shared/content.txt"
printf 'runtime projection bytes\n' > "$canonical_root/payload/runtime/linux-x64/bin/worker"
manifest_sha="$(hash_file "$canonical_root/manifest/sunder-package.json")"
manifest_size="$(file_size "$canonical_root/manifest/sunder-package.json")"
shared_payload_sha="$(hash_file "$canonical_root/payload/shared/content.txt")"
shared_payload_size="$(file_size "$canonical_root/payload/shared/content.txt")"
runtime_payload_sha="$(hash_file "$canonical_root/payload/runtime/linux-x64/bin/worker")"
runtime_payload_size="$(file_size "$canonical_root/payload/runtime/linux-x64/bin/worker")"
jq -cn \
  --arg manifest_sha "$manifest_sha" \
  --argjson manifest_size "$manifest_size" \
  --arg shared_sha "$shared_payload_sha" \
  --argjson shared_size "$shared_payload_size" \
  --arg runtime_sha "$runtime_payload_sha" \
  --argjson runtime_size "$runtime_payload_size" '
    {
      schemaVersion: 1,
      files: [
        {path: "manifest/sunder-package.json", sha256: $manifest_sha, size: $manifest_size},
        {path: "payload/shared/content.txt", sha256: $shared_sha, size: $shared_size},
        {path: "payload/runtime/linux-x64/bin/worker", sha256: $runtime_sha, size: $runtime_size}
      ] | sort_by(.path)
    }
  ' > "$canonical_root/manifest/content-index.json"
(
  cd "$canonical_root"
  zip -q -X "$archive" \
    manifest/content-index.json \
    manifest/sunder-package.json \
    payload/runtime/linux-x64/bin/worker \
    payload/shared/content.txt
)
canonical_sha="$(hash_file "$archive")"

projection_sequence=0
write_projection() {
  local archive_path="$1"
  local kind="$2"
  local rid="$3"
  local actual_content="$4"
  local indexed_content="${5:-$actual_content}"
  local forced_identity="${6:-}"
  local root
  local payload_relative
  local indexed_payload="$fixture_dir/indexed-payload"
  local payload_sha
  local payload_size
  local identity

  projection_sequence=$((projection_sequence + 1))
  root="$fixture_dir/projection-root-$projection_sequence"
  case "$kind" in
    shared)
      payload_relative='payload/shared/content.txt'
      ;;
    runtime)
      payload_relative="payload/runtime/$rid/bin/worker"
      ;;
    *)
      printf 'Unsupported fixture projection kind: %s\n' "$kind" >&2
      return 1
      ;;
  esac
  mkdir -p "$root/manifest" "$(dirname "$root/$payload_relative")"
  cp "$canonical_root/manifest/sunder-package.json" "$root/manifest/sunder-package.json"
  printf '%s\n' "$actual_content" > "$root/$payload_relative"
  printf '%s\n' "$indexed_content" > "$indexed_payload"
  payload_sha="$(hash_file "$indexed_payload")"
  payload_size="$(file_size "$indexed_payload")"
  jq -cn \
    --arg path "$payload_relative" \
    --arg sha "$payload_sha" \
    --argjson size "$payload_size" \
    '{schemaVersion: 1, files: [{path: $path, sha256: $sha, size: $size}]}' \
    > "$root/manifest/content-index.json"
  identity="$(compute_projection_identity "$kind" "$rid" "$root/manifest/content-index.json")"
  [[ -z "$forced_identity" ]] || identity="$forced_identity"
  jq -cn \
    --arg package_id fixture.package \
    --arg version 1.2.3 \
    --arg source_sha "$canonical_sha" \
    --arg kind "$kind" \
    --arg rid "$rid" \
    --arg manifest_sha "$manifest_sha" \
    --arg identity "$identity" '
      {
        projectionFormatVersion: 1,
        packageId: $package_id,
        packageVersion: $version,
        sourceArchiveSha256: $source_sha,
        kind: $kind,
        rid: (if $rid == "" then null else $rid end),
        manifestSha256: $manifest_sha,
        projectionSha256: $identity
      }
    ' > "$root/manifest/sunder-projection.json"
  (
    cd "$root"
    zip -q -X "$archive_path" \
      manifest/content-index.json \
      manifest/sunder-package.json \
      manifest/sunder-projection.json \
      "$payload_relative"
  )
}

projection_shared="$fixture_dir/projection-shared.sunderpkg"
projection_runtime="$fixture_dir/projection-runtime.sunderpkg"
projection_wrong_content="$fixture_dir/projection-shared-wrong-content.sunderpkg"
projection_noncanonical_content="$fixture_dir/projection-shared-noncanonical-content.sunderpkg"
projection_wrong_identity="$fixture_dir/projection-shared-wrong-identity.sunderpkg"
projection_malformed="$fixture_dir/projection-shared-malformed.sunderpkg"
write_projection "$projection_shared" shared '' 'shared projection bytes'
write_projection "$projection_runtime" runtime linux-x64 'runtime projection bytes'
write_projection \
  "$projection_wrong_content" shared '' \
  'tampered shared projection bytes' 'shared projection bytes'
write_projection \
  "$projection_noncanonical_content" shared '' \
  'different indexed projection bytes'
write_projection \
  "$projection_wrong_identity" shared '' \
  'shared projection bytes' 'shared projection bytes' "$(printf '%064d' 8)"
printf 'not a projection archive\n' > "$projection_malformed"
shared_identity="$(unzip -p "$projection_shared" manifest/sunder-projection.json | jq -r '.projectionSha256')"
runtime_identity="$(unzip -p "$projection_runtime" manifest/sunder-projection.json | jq -r '.projectionSha256')"
wrong_content_identity="$(unzip -p "$projection_wrong_content" manifest/sunder-projection.json | jq -r '.projectionSha256')"
noncanonical_content_identity="$(unzip -p "$projection_noncanonical_content" manifest/sunder-projection.json | jq -r '.projectionSha256')"
[[ "$wrong_content_identity" == "$shared_identity" ]]

export MOCK_REGISTRY_STATE="$fixture_dir/registry-state"
export MOCK_REGISTRY_CALLS="$fixture_dir/registry-calls"
export MOCK_REGISTRY_DETAILS="$fixture_dir/version-details.json"
export MOCK_CANONICAL_ARCHIVE="$archive"
export MOCK_SHARED_PROJECTION="$projection_shared"
export MOCK_RUNTIME_PROJECTION="$projection_runtime"
export MOCK_EXPECTED_ARCHIVE="$archive"
export MOCK_EXPECTED_PACKAGE_ID=fixture.package
export MOCK_EXPECTED_VERSION=1.2.3
export MOCK_EXPECTED_PUBLISH_TOKEN=sunder_pub_v1_0123456789012345678901234567890123456789012
export SUNDER_REGISTRY_PUBLISH_TOKEN="$MOCK_EXPECTED_PUBLISH_TOKEN"
export SUNDER_REGISTRY_CLI_TOKEN=sunder_cli_0123456789012345678901234567890123456789012

write_details() {
  local advertised_canonical_sha="${1:-$canonical_sha}"
  local advertised_shared_sha="${2:-$(hash_file "$MOCK_SHARED_PROJECTION")}"
  local advertised_source_sha="${3:-$canonical_sha}"
  local advertised_manifest_sha="${4:-$manifest_sha}"
  local advertised_shared_identity="${5:-$shared_identity}"
  local runtime_sha
  runtime_sha="$(hash_file "$MOCK_RUNTIME_PROJECTION")"
  jq -n \
    --arg package_id "$MOCK_EXPECTED_PACKAGE_ID" \
    --arg version "$MOCK_EXPECTED_VERSION" \
    --arg canonical_sha "$advertised_canonical_sha" \
    --argjson canonical_size "$(file_size "$archive")" \
    --arg shared_sha "$advertised_shared_sha" \
    --argjson shared_size "$(file_size "$MOCK_SHARED_PROJECTION")" \
    --arg runtime_sha "$runtime_sha" \
    --argjson runtime_size "$(file_size "$MOCK_RUNTIME_PROJECTION")" \
    --arg source_sha "$advertised_source_sha" \
    --arg manifest_sha "$advertised_manifest_sha" \
    --arg shared_identity "$advertised_shared_identity" \
    --arg runtime_identity "$runtime_identity" '
      {
        packageId: $package_id,
        version: $version,
        canonicalArtifact: {
          sha256: $canonical_sha,
          size: $canonical_size,
          downloadUrl: "/api/v1/packages/fixture.package/versions/1.2.3/canonical"
        },
        projections: [
          {
            kind: "shared",
            rid: null,
            sha256: $shared_sha,
            size: $shared_size,
            downloadUrl: "/api/v1/packages/fixture.package/versions/1.2.3/projections/shared/download",
            sourceArchiveSha256: $source_sha,
            manifestSha256: $manifest_sha,
            projectionContentIdentity: $shared_identity,
            projectionFormatVersion: 1
          },
          {
            kind: "runtime",
            rid: "linux-x64",
            sha256: $runtime_sha,
            size: $runtime_size,
            downloadUrl: "https://artifacts.test/runtime-linux-x64.sunderpkg",
            sourceArchiveSha256: $source_sha,
            manifestSha256: $manifest_sha,
            projectionContentIdentity: $runtime_identity,
            projectionFormatVersion: 1
          }
        ],
        trustedArtifactOrigins: ["https://registry.test", "https://artifacts.test"]
      }
    ' > "$MOCK_REGISTRY_DETAILS"
}

cat > "$mock_bin/curl" <<'MOCK_CURL'
#!/usr/bin/env bash
set -euo pipefail

method=GET
output=''
write_out=''
url=''
authorization=''
expected_resource=''
set_latest_header=''
package_form=''
set_latest_form=''
while [[ $# -gt 0 ]]; do
  case "$1" in
    -o)
      output="$2"
      shift 2
      ;;
    -w)
      write_out="$2"
      shift 2
      ;;
    -H)
      case "$2" in
        Authorization:\ Bearer\ *) authorization="${2#Authorization: Bearer }" ;;
        X-Sunder-Expected-Resource-Id:\ *) expected_resource="${2#X-Sunder-Expected-Resource-Id: }" ;;
        X-Sunder-Set-Latest:\ *) set_latest_header="${2#X-Sunder-Set-Latest: }" ;;
      esac
      shift 2
      ;;
    -F)
      method=POST
      case "$2" in
        package=*) package_form="$2" ;;
        setLatest=*) set_latest_form="$2" ;;
      esac
      shift 2
      ;;
    --fail-with-body|-sS)
      shift
      ;;
    http://*|https://*)
      url="$1"
      shift
      ;;
    *)
      printf 'Mock curl does not support argument: %s\n' "$1" >&2
      exit 2
      ;;
  esac
done

[[ -n "$url" ]] || { printf 'Mock curl did not receive a URL.\n' >&2; exit 2; }
details_suffix="/api/v1/packages/$MOCK_EXPECTED_PACKAGE_ID/versions/$MOCK_EXPECTED_VERSION"
if [[ "$method" == GET && "$url" == *"$details_suffix" ]]; then
  printf 'DETAILS\n' >> "$MOCK_REGISTRY_CALLS"
  if [[ "$(< "$MOCK_REGISTRY_STATE")" == published ]]; then
    cp "$MOCK_REGISTRY_DETAILS" "$output"
    status=200
  else
    : > "$output"
    status=404
  fi
  [[ -z "$write_out" || "$write_out" == '%{http_code}' ]] \
    || { printf 'Unexpected curl write-out format: %s\n' "$write_out" >&2; exit 2; }
  printf '%s' "$status"
  exit 0
fi

if [[ "$method" == POST && "$url" == */api/v1/packages/publish ]]; then
  printf 'PUBLISH\n' >> "$MOCK_REGISTRY_CALLS"
  [[ "$authorization" == "$MOCK_EXPECTED_PUBLISH_TOKEN" ]] \
    || { printf 'Publish request did not use the scoped publish token.\n' >&2; exit 2; }
  [[ "$expected_resource" == "$MOCK_EXPECTED_PACKAGE_ID" ]] \
    || { printf 'Publish request omitted the matching expected-resource header.\n' >&2; exit 2; }
  [[ "$set_latest_header" == false ]] \
    || { printf 'Publish request omitted X-Sunder-Set-Latest:false.\n' >&2; exit 2; }
  [[ "$package_form" == "package=@$MOCK_EXPECTED_ARCHIVE" ]] \
    || { printf 'Publish request omitted the multipart package.\n' >&2; exit 2; }
  [[ "$set_latest_form" == setLatest=false ]] \
    || { printf 'Publish request omitted multipart setLatest=false.\n' >&2; exit 2; }
  printf 'published\n' > "$MOCK_REGISTRY_STATE"
  jq -cn --arg package_id "$MOCK_EXPECTED_PACKAGE_ID" --arg version "$MOCK_EXPECTED_VERSION" \
    '{success: true, packageId: $package_id, version: $version}'
  exit 0
fi

[[ -z "$authorization" ]] || { printf 'Download unexpectedly received an authorization token.\n' >&2; exit 2; }
case "$url" in
  *"$details_suffix/canonical")
    printf 'CANONICAL\n' >> "$MOCK_REGISTRY_CALLS"
    cp "$MOCK_CANONICAL_ARCHIVE" "$output"
    ;;
  *"$details_suffix/projections/shared/download")
    printf 'PROJECTION shared\n' >> "$MOCK_REGISTRY_CALLS"
    cp "$MOCK_SHARED_PROJECTION" "$output"
    ;;
  https://artifacts.test/runtime-linux-x64.sunderpkg)
    printf 'PROJECTION runtime/linux-x64\n' >> "$MOCK_REGISTRY_CALLS"
    cp "$MOCK_RUNTIME_PROJECTION" "$output"
    ;;
  *)
    printf 'Unsupported mock Registry URL: %s\n' "$url" >&2
    exit 2
    ;;
esac
MOCK_CURL
chmod +x "$mock_bin/curl"

reset_fixture() {
  printf '%s\n' "$1" > "$MOCK_REGISTRY_STATE"
  : > "$MOCK_REGISTRY_CALLS"
  export MOCK_SHARED_PROJECTION="$projection_shared"
  export MOCK_RUNTIME_PROJECTION="$projection_runtime"
  write_details
}

run_publisher() {
  PATH="$mock_bin:$PATH" bash "$publisher" \
    https://registry.test "$archive" "$MOCK_EXPECTED_PACKAGE_ID" "$MOCK_EXPECTED_VERSION"
}

reset_fixture absent
run_publisher > "$fixture_dir/publish.log"
[[ "$(grep -c '^DETAILS$' "$MOCK_REGISTRY_CALLS")" -eq 2 ]]
[[ "$(grep -c '^PUBLISH$' "$MOCK_REGISTRY_CALLS")" -eq 1 ]]
[[ "$(grep -c '^CANONICAL$' "$MOCK_REGISTRY_CALLS")" -eq 1 ]]
[[ "$(grep -c '^PROJECTION ' "$MOCK_REGISTRY_CALLS")" -eq 2 ]]

reset_fixture published
run_publisher > "$fixture_dir/existing.log"
[[ "$(grep -c '^DETAILS$' "$MOCK_REGISTRY_CALLS")" -eq 1 ]]
if grep -q '^PUBLISH$' "$MOCK_REGISTRY_CALLS"; then
  printf 'Existing-version verification unexpectedly republished the package.\n' >&2
  exit 1
fi
[[ "$(grep -c '^CANONICAL$' "$MOCK_REGISTRY_CALLS")" -eq 1 ]]
[[ "$(grep -c '^PROJECTION ' "$MOCK_REGISTRY_CALLS")" -eq 2 ]]

reset_fixture published
write_details "$(printf '%064d' 0)"
if run_publisher > "$fixture_dir/canonical-mismatch.log" 2>&1; then
  printf 'Registry verification accepted a mismatched canonical SHA-256.\n' >&2
  exit 1
fi
grep -Fq 'canonical SHA-256 differs' "$fixture_dir/canonical-mismatch.log"

reset_fixture published
write_details "$canonical_sha" "$(printf '%064d' 0)"
if run_publisher > "$fixture_dir/projection-mismatch.log" 2>&1; then
  printf 'Registry verification accepted a mismatched projection checksum.\n' >&2
  exit 1
fi
grep -Fq "projection 'shared' checksum or size differs" "$fixture_dir/projection-mismatch.log"

reset_fixture published
write_details "$canonical_sha" "$(hash_file "$projection_shared")" "$(printf '%064d' 0)"
if run_publisher > "$fixture_dir/source-mismatch.log" 2>&1; then
  printf 'Registry verification accepted a mismatched projection sourceArchiveSha256.\n' >&2
  exit 1
fi
grep -Fq 'wrong sourceArchiveSha256' "$fixture_dir/source-mismatch.log"

reset_fixture published
jq '.projections |= map(select(.kind != "runtime"))' "$MOCK_REGISTRY_DETAILS" \
  > "$fixture_dir/missing-projection-details.json"
mv "$fixture_dir/missing-projection-details.json" "$MOCK_REGISTRY_DETAILS"
if run_publisher > "$fixture_dir/missing-projection.log" 2>&1; then
  printf 'Registry verification accepted a missing canonical target projection.\n' >&2
  exit 1
fi
grep -Fq 'projection inventory differs from the local canonical targets' "$fixture_dir/missing-projection.log"

reset_fixture published
jq '.projections += [(.projections[1]
  | .kind = "app"
  | .rid = "win-x64"
  | .downloadUrl = "/api/v1/packages/fixture.package/versions/1.2.3/projections/app/win-x64/download")]' \
  "$MOCK_REGISTRY_DETAILS" > "$fixture_dir/extra-projection-details.json"
mv "$fixture_dir/extra-projection-details.json" "$MOCK_REGISTRY_DETAILS"
if run_publisher > "$fixture_dir/extra-projection.log" 2>&1; then
  printf 'Registry verification accepted an extra projection.\n' >&2
  exit 1
fi
grep -Fq 'projection inventory differs from the local canonical targets' "$fixture_dir/extra-projection.log"

reset_fixture published
write_details \
  "$canonical_sha" \
  "$(hash_file "$projection_shared")" \
  "$canonical_sha" \
  "$(printf '%064d' 7)"
if run_publisher > "$fixture_dir/manifest-mismatch.log" 2>&1; then
  printf 'Registry verification accepted a projection manifest hash that differs from the local manifest.\n' >&2
  exit 1
fi
grep -Fq 'manifestSha256 differs from the local canonical manifest' "$fixture_dir/manifest-mismatch.log"

reset_fixture published
export MOCK_SHARED_PROJECTION="$projection_malformed"
write_details
if run_publisher > "$fixture_dir/malformed-projection.log" 2>&1; then
  printf 'Registry verification accepted a malformed projection archive with matching advertised outer metadata.\n' >&2
  exit 1
fi
grep -Fq 'is not a readable projection archive' "$fixture_dir/malformed-projection.log"

reset_fixture published
export MOCK_SHARED_PROJECTION="$projection_wrong_content"
write_details
if run_publisher > "$fixture_dir/wrong-projection-content.log" 2>&1; then
  printf 'Registry verification accepted projection payload bytes that differ from the signed content index.\n' >&2
  exit 1
fi
grep -Fq "payload 'payload/shared/content.txt' does not match its content index" \
  "$fixture_dir/wrong-projection-content.log"

reset_fixture published
export MOCK_SHARED_PROJECTION="$projection_noncanonical_content"
write_details \
  "$canonical_sha" \
  "$(hash_file "$projection_noncanonical_content")" \
  "$canonical_sha" \
  "$manifest_sha" \
  "$noncanonical_content_identity"
if run_publisher > "$fixture_dir/noncanonical-projection-content.log" 2>&1; then
  printf 'Registry verification accepted internally consistent projection content not selected from the local canonical archive.\n' >&2
  exit 1
fi
grep -Fq 'content index is not the exact local canonical projection' \
  "$fixture_dir/noncanonical-projection-content.log"

reset_fixture published
export MOCK_SHARED_PROJECTION="$projection_wrong_identity"
write_details \
  "$canonical_sha" \
  "$(hash_file "$projection_wrong_identity")" \
  "$canonical_sha" \
  "$manifest_sha" \
  "$(printf '%064d' 8)"
if run_publisher > "$fixture_dir/wrong-projection-identity.log" 2>&1; then
  printf 'Registry verification trusted a coordinated wrong projection content identity.\n' >&2
  exit 1
fi
grep -Fq 'content identity does not match its descriptor' "$fixture_dir/wrong-projection-identity.log"

reset_fixture absent
if env -u SUNDER_REGISTRY_PUBLISH_TOKEN \
  SUNDER_REGISTRY_CLI_TOKEN="$SUNDER_REGISTRY_CLI_TOKEN" \
  PATH="$mock_bin:$PATH" bash "$publisher" \
  https://registry.test "$archive" "$MOCK_EXPECTED_PACKAGE_ID" "$MOCK_EXPECTED_VERSION" \
  > "$fixture_dir/missing-token.log" 2>&1; then
  printf 'Registry publication accepted a missing publish token.\n' >&2
  exit 1
fi
grep -Fq 'SUNDER_REGISTRY_PUBLISH_TOKEN is not configured' "$fixture_dir/missing-token.log"

if PATH="$mock_bin:$PATH" bash "$publisher" \
  http://registry.test "$archive" "$MOCK_EXPECTED_PACKAGE_ID" "$MOCK_EXPECTED_VERSION" \
  > "$fixture_dir/insecure-origin.log" 2>&1; then
  printf 'Registry publication accepted an insecure Registry origin.\n' >&2
  exit 1
fi
grep -Fq 'absolute HTTPS origin' "$fixture_dir/insecure-origin.log"

if SUNDER_REGISTRY_PUBLISH_TOKEN=sunder_pub_v1_invalid \
  PATH="$mock_bin:$PATH" bash "$publisher" \
  https://registry.test "$archive" "$MOCK_EXPECTED_PACKAGE_ID" "$MOCK_EXPECTED_VERSION" \
  > "$fixture_dir/invalid-token.log" 2>&1; then
  printf 'Registry publication accepted a malformed publish token.\n' >&2
  exit 1
fi
grep -Fq 'valid scoped sunder_pub_v1' "$fixture_dir/invalid-token.log"

printf 'Registry publication fixture passed: canonical manifest-derived inventory, content identities, payloads, scoped headers, and reruns verified.\n'
