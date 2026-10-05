#!/usr/bin/env bash
set -euo pipefail
export LC_ALL=C

if [[ $# -ne 4 ]]; then
  printf 'Usage: generate-package-output-audit.sh <archive-directory> <native-test-evidence.json> <repository-root> <output.json>\n' >&2
  exit 2
fi

archive_directory="$(cd "$1" && pwd)"
native_evidence="$(cd "$(dirname "$2")" && pwd)/$(basename "$2")"
repository_root="$(cd "$3" && pwd)"
output="$4"
script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

[[ -f "$native_evidence" ]] \
  || { printf 'Native evidence was not found: %s\n' "$native_evidence" >&2; exit 1; }
[[ -f "$repository_root/packages.json" ]] \
  || { printf 'Agent package inventory was not found beneath %s.\n' "$repository_root" >&2; exit 1; }

for required_command in basename cut dirname jq mkdir mktemp mv rm tr unzip wc; do
  command -v "$required_command" >/dev/null 2>&1 \
    || { printf '%s is required to generate the package output audit.\n' "$required_command" >&2; exit 127; }
done
if ! command -v sha256sum >/dev/null 2>&1 && ! command -v shasum >/dev/null 2>&1; then
  printf 'sha256sum or shasum is required to generate the package output audit.\n' >&2
  exit 127
fi

hash_file() {
  if command -v sha256sum >/dev/null 2>&1; then
    sha256sum "$1" | cut -d ' ' -f 1
  else
    shasum -a 256 "$1" | cut -d ' ' -f 1
  fi
}

file_size() {
  wc -c < "$1" | tr -d '[:space:]'
}

temp_root="${TMPDIR:-/tmp}"
work="$(mktemp -d "${temp_root%/}/sunder-agent-output-audit.XXXXXX")"
trap 'rm -rf "$work"' EXIT

rids=(win-x64 win-arm64 linux-x64 linux-arm64 osx-x64 osx-arm64)
version="$(jq -er '.version' "$native_evidence")"
agent_commit="$(jq -er '.agentCommit' "$native_evidence")"
core_tag="$(jq -er '.core.tag' "$native_evidence")"
core_commit="$(jq -er '.core.commit' "$native_evidence")"
core_package_version="$(jq -er '.core.packageVersion' "$native_evidence")"
bash "$script_dir/verify-native-family-evidence.sh" \
  "$native_evidence" "$archive_directory" "$repository_root/packages.json" \
  "$version" "$agent_commit" "$core_tag" "$core_commit" "$core_package_version" >/dev/null

packages='[]'
while IFS= read -r package_id; do
  archive="$archive_directory/$package_id.$version.sunderpkg"
  [[ -f "$archive" ]] \
    || { printf 'Package output audit input is missing: %s\n' "$archive" >&2; exit 1; }

  manifest="$work/$package_id.manifest.json"
  index="$work/$package_id.content-index.json"
  unzip -p "$archive" manifest/sunder-package.json > "$manifest"
  unzip -p "$archive" manifest/content-index.json > "$index"
  jq -e --arg package_id "$package_id" '
    .archiveFormatVersion == 1
    and .manifestVersion == 1
    and .id == $package_id
    and (.version | type == "string" and length > 0)
  ' "$manifest" >/dev/null \
    || { printf 'Package output audit found an invalid manifest for %s.\n' "$package_id" >&2; exit 1; }
  jq -e '
    .schemaVersion == 1
    and keys == ["files","schemaVersion"]
    and (.files | type == "array" and length > 0)
    and ([.files[].path] | length) == ([.files[].path] | unique | length)
    and all(.files[];
      type == "object" and keys == ["path","sha256","size"]
      and (.path | type == "string" and length > 0)
      and (.sha256 | type == "string" and test("^[0-9a-f]{64}$"))
      and (.size | type == "number" and floor == . and . >= 0))
  ' "$index" >/dev/null \
    || { printf 'Package output audit found an invalid content index for %s.\n' "$package_id" >&2; exit 1; }

  archive_json="$(jq -n \
    --arg path "$(basename "$archive")" \
    --arg sha256 "$(hash_file "$archive")" \
    --argjson bytes "$(file_size "$archive")" \
    --argjson files "$(( $(jq '.files | length' "$index") + 1 ))" \
    '{path: $path, sha256: $sha256, files: $files, bytes: $bytes}')"
  rid_audits='[]'
  for rid in "${rids[@]}"; do
    runtime_target="$(jq -ce --arg rid "$rid" '
      [.targets[] | select(.role == "runtime" and .rid == $rid)]
      | if length == 1 then .[0] else error("expected one Runtime target") end
    ' "$manifest")" || {
      printf 'Package output audit requires one Runtime target for %s/%s.\n' "$package_id" "$rid" >&2
      exit 1
    }
    jq -e --arg rid "$rid" '
      ([.targets[] | select(.role == "runtime") | .rid] | sort)
        == ["linux-arm64","linux-x64","osx-arm64","osx-x64","win-arm64","win-x64"]
      and all(.targets[] | select(.role == "runtime");
        .kind == "worker"
        and .targetFramework == "net10.0"
        and (.sdkVersion | type == "string" and test("^1[.]1[.]0([+][0-9A-Za-z-]+([.][0-9A-Za-z-]+)*)?$"))
        and .executionRuntime == {
          id: "sunder.runtime.dotnet",
          versionRange: ">=10.0.0 <11.0.0",
          moduleAbi: "sunder.dotnet-worker-module.v1"
        }
        and (.requiredHostCapabilities | index("execution-runtime.v1") != null)
        and (.requiredHostCapabilities | index("rpc.v1") != null)
        and (.requiredHostCapabilities | index("sdk-baseline-1-1.v1") != null)
        and (.requiredHostCapabilities | index("worker-protocol.v1") != null)
        and all(.requiredHostCapabilities[];
          . != "worker-protocol.v2" and . != "worker-protocol.v3" and . != "rpc.v2" and . != "rpc.v3"))
      and (([.targets[] | select(.role == "app") | .rid] | sort) as $app_rids
        | ($app_rids == [] or $app_rids == ["linux-arm64","linux-x64","osx-arm64","osx-x64","win-arm64","win-x64"]))
    ' "$manifest" >/dev/null || {
      printf 'Package output audit found an invalid V1 target matrix for %s/%s.\n' "$package_id" "$rid" >&2
      exit 1
    }
    has_app="$(jq -r --arg rid "$rid" '[.targets[] | select(.role == "app" and .rid == $rid)] | length == 1' "$manifest")"
    [[ "$has_app" == true || "$(jq --arg rid "$rid" '[.targets[] | select(.role == "app" and .rid == $rid)] | length' "$manifest")" == 0 ]] \
      || { printf 'Package output audit found duplicate App targets for %s/%s.\n' "$package_id" "$rid" >&2; exit 1; }

    entry_point="$(jq -er '.entryPoint' <<< "$runtime_target")"
    [[ "$entry_point" == lib/*.dll ]] \
      || { printf 'Package output audit found an invalid Worker entry point for %s/%s.\n' "$package_id" "$rid" >&2; exit 1; }
    runtime_config_logical="${entry_point%.dll}.runtimeconfig.json"
    runtime_config_path="$(jq -er \
      --arg rid "$rid" \
      --arg exact "payload/runtime/$rid/$runtime_config_logical" \
      --arg role_shared "payload/runtime/shared/$runtime_config_logical" \
      --arg package_shared "payload/shared/$runtime_config_logical" '
        [.files[].path | select(. == $exact or . == $role_shared or . == $package_shared)]
        | if length == 1 then .[0] else error("runtimeconfig projection is ambiguous or missing") end
      ' "$index")" || {
      printf 'Package output audit could not resolve the runtimeconfig for %s/%s.\n' "$package_id" "$rid" >&2
      exit 1
    }
    runtime_config="$work/$package_id.$rid.runtimeconfig.json"
    unzip -p "$archive" "$runtime_config_path" > "$runtime_config"
    framework="$(jq -ce '
      .runtimeOptions as $options
      | select($options.tfm == "net10.0")
      | select(($options.includedFrameworks // null) == null and ($options.frameworks // null) == null)
      | $options.framework
      | select(.name == "Microsoft.NETCore.App" and (.version | startswith("10.")))
      | {tfm: $options.tfm, name, version}
    ' "$runtime_config")" || {
      printf 'Package output audit found a runtimeconfig/profile mismatch for %s/%s.\n' "$package_id" "$rid" >&2
      exit 1
    }

    layers="$(jq -c --arg rid "$rid" --argjson has_app "$has_app" '
      def kind:
        .path as $path
        | if ($path | test("(^|/)(runtimes/[^/]+/native/|[^/]*e_sqlite3\\.dll$|libe_sqlite3\\.(so|dylib)$|libonigwrap\\.(dll|so|dylib)$|Microsoft\\.DiaSymReader\\.Native\\.[^.]+\\.dll$|[^/]+\\.node$|[^/]+\\.so(\\.[0-9]+)*$|[^/]+\\.dylib$)"; "i"))
          then "native"
          elif ($path | endswith(".dll")) then "managed"
          else "other"
          end;
      def layer($rid):
        if (.path | startswith("payload/shared/")) then "packageShared"
        elif (.path | startswith("payload/runtime/shared/")) then "runtimeShared"
        elif (.path | startswith("payload/runtime/" + $rid + "/")) then "runtimeRid"
        elif (.path | startswith("payload/app/shared/")) then "appShared"
        elif (.path | startswith("payload/app/" + $rid + "/")) then "appRid"
        else "unknown"
        end;
      def summary:
        . as $files
        | {
            files: ($files | length),
            bytes: ([$files[].size] | add // 0),
            breakdown: {
              managed: ([$files[] | select(kind == "managed")] | {files: length, bytes: ([.[].size] | add // 0)}),
              native: ([$files[] | select(kind == "native")] | {files: length, bytes: ([.[].size] | add // 0)}),
              other: ([$files[] | select(kind == "other")] | {files: length, bytes: ([.[].size] | add // 0)})
            }
          };
      [.files[] | select(.path | startswith("payload/"))] as $payload
      | [$payload[] | select(.path | startswith("payload/shared/"))] as $package_shared
      | [$payload[] | select(.path | startswith("payload/runtime/shared/"))] as $runtime_shared
      | [$payload[] | select(.path | startswith("payload/runtime/" + $rid + "/"))] as $runtime_rid
      | [$payload[] | select(.path | startswith("payload/app/shared/"))] as $app_shared
      | [$payload[] | select(.path | startswith("payload/app/" + $rid + "/"))] as $app_rid
      | ($package_shared + $runtime_shared + $runtime_rid
          + (if $has_app then $app_shared + $app_rid else [] end)) as $projected
      | {
          packageShared: ($package_shared | summary),
          runtimeShared: ($runtime_shared | summary),
          runtimeRid: ($runtime_rid | summary),
          appShared: ($app_shared | summary),
          appRid: ($app_rid | summary),
          projected: ($projected | summary),
          largest25: ($projected
            | map(. + {kind: kind, layer: layer($rid)})
            | sort_by([-.size, .path])[:25]
            | map({path, bytes: .size, kind, layer}))
        }
    ' "$index")"
    installed_bytes="$(jq -er --arg rid "$rid" --arg package_id "$package_id" '
      [.nativeRuns[] | select(.rid == $rid) | .packages[] | select(.packageId == $package_id)
        | .metrics.measurement.currentRidInstalledBytes]
      | if length == 1 and .[0] > 0 then .[0] else error("missing installed measurement") end
    ' "$native_evidence")" || {
      printf 'Package output audit is missing measured installed bytes for %s/%s.\n' "$package_id" "$rid" >&2
      exit 1
    }
    projected_files="$(jq -r '.projected.files' <<< "$layers")"
    rid_json="$(jq -n \
      --arg rid "$rid" \
      --arg entryPoint "$entry_point" \
      --arg sdkVersion "$(jq -er '.sdkVersion' <<< "$runtime_target")" \
      --argjson executionRuntime "$(jq -c '.executionRuntime' <<< "$runtime_target")" \
      --argjson requiredHostCapabilities "$(jq -c '.requiredHostCapabilities | sort' <<< "$runtime_target")" \
      --argjson framework "$framework" \
      --argjson layers "$layers" \
      --argjson installedFiles "$((projected_files + 2))" \
      --argjson installedBytes "$installed_bytes" '
        {
          rid: $rid,
          entryPoint: $entryPoint,
          sdkVersion: $sdkVersion,
          runtimeConfigFramework: $framework,
          executionRuntime: $executionRuntime,
          requiredHostCapabilities: $requiredHostCapabilities,
          layers: ($layers | del(.largest25)),
          installedProjection: {files: $installedFiles, bytes: $installedBytes},
          largest25: $layers.largest25
        }
      ')"
    rid_audits="$(jq -cn --argjson current "$rid_audits" --argjson item "$rid_json" '$current + [$item]')"
  done

  package_json="$(jq -n \
    --arg packageId "$package_id" \
    --argjson archive "$archive_json" \
    --argjson contractBundles "$(jq -c '[.contractBundles[]? | {contractId, version, descriptorPath, descriptorSha256, abiSha256}] | sort_by(.contractId, .version)' "$manifest")" \
    --argjson rids "$rid_audits" '
      {packageId: $packageId, archive: $archive, contractBundles: $contractBundles, rids: $rids}
    ')"
  packages="$(jq -cn --argjson current "$packages" --argjson item "$package_json" '$current + [$item]')"
done < <(jq -r '.packages | map(select(.artifactType == "sunderpkg")) | sort_by(.packageId)[] | .packageId' "$repository_root/packages.json")

[[ "$(jq 'length' <<< "$packages")" == 15 ]] \
  || { printf 'Package output audit requires exactly 15 package archives.\n' >&2; exit 1; }

family_rids='[]'
for rid in "${rids[@]}"; do
  rid_family="$(jq -cn --arg rid "$rid" --argjson packages "$packages" '
    [$packages[].rids[] | select(.rid == $rid)] as $selected
    | {
        rid: $rid,
        projectedPayload: {
          files: ([$selected[].layers.projected.files] | add),
          bytes: ([$selected[].layers.projected.bytes] | add)
        },
        installedProjection: {
          files: ([$selected[].installedProjection.files] | add),
          bytes: ([$selected[].installedProjection.bytes] | add)
        }
      }
  ')"
  family_rids="$(jq -cn --argjson current "$family_rids" --argjson item "$rid_family" '$current + [$item]')"
done

core="$(jq -c '.core' "$native_evidence")"
archive_files="$(jq '[.[].archive.files] | add' <<< "$packages")"
archive_bytes="$(jq '[.[].archive.bytes] | add' <<< "$packages")"
indexed_payload_files="$(jq '[.[].archive.files - 1] | add' <<< "$packages")"
# The universal indexed total is retained for archive diagnostics, never used as the installed-footprint gate.
indexed_payload_bytes="$(for package_id in $(jq -r '.[].packageId' <<< "$packages"); do
  unzip -p "$archive_directory/$package_id.$version.sunderpkg" manifest/content-index.json \
    | jq '[.files[].size] | add // 0'
done | jq -s 'add')"

footprint_ceiling=360003313
failing_rids="$(jq -r --argjson ceiling "$footprint_ceiling" '
  .[] | select(.installedProjection.bytes > $ceiling)
  | "\(.rid)=\(.installedProjection.bytes)"
' <<< "$family_rids")"
[[ -z "$failing_rids" ]] || {
  printf 'Selected-current-RID installed projection exceeds the 360003313-byte Phase 5 ceiling: %s\n' \
    "$(tr '\n' ' ' <<< "$failing_rids" | tr -d '[:space:]')" >&2
  exit 1
}

output_parent="$(dirname "$output")"
mkdir -p "$output_parent"
next="$output.next"
jq -n \
  --arg version "$version" \
  --arg agentCommit "$agent_commit" \
  --argjson core "$core" \
  --argjson archiveFiles "$archive_files" \
  --argjson archiveBytes "$archive_bytes" \
  --argjson indexedPayloadFiles "$indexed_payload_files" \
  --argjson indexedPayloadBytes "$indexed_payload_bytes" \
  --argjson familyRids "$family_rids" \
  --argjson packages "$packages" '
    {
      schemaVersion: 1,
      kind: "https://sunderapp.io/evidence/agent-package-output-audit/v1",
      version: $version,
      agentCommit: $agentCommit,
      core: $core,
      installedProjectionGate: {
        phase0BaselineBytes: 1440013254,
        requiredReductionPercent: 75,
        maximumBytes: 360003313
      },
      classification: {
        managed: "Projected .dll files except paths classified as native.",
        native: "RID-native directories and known native .dll, .so, .dylib, and .node names.",
        other: "Descriptors, manifests, runtime configuration, symbols, assets, and other package-owned content."
      },
      family: {
        universalArchives: {files: $archiveFiles, bytes: $archiveBytes},
        universalIndexedPayload: {files: $indexedPayloadFiles, bytes: $indexedPayloadBytes},
        rids: ($familyRids | map(. + {meetsInstalledProjectionGate: true}))
      },
      packages: $packages
    }
  ' > "$next"
mv "$next" "$output"

printf 'Generated deterministic output audit for 15 packages across six RIDs.\n'
