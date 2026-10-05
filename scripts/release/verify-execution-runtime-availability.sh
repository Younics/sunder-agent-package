#!/usr/bin/env bash
set -euo pipefail
export LC_ALL=C

if [[ $# -lt 1 || $# -gt 2 ]]; then
  printf 'Usage: verify-execution-runtime-availability.sh <registry-api-url> [production-lock]\n' >&2
  exit 2
fi

registry_api="${1%/}"
production_lock="${2:-}"
[[ "$registry_api" =~ ^https://[^/@?#]+$ ]] \
  || { printf 'Registry API URL must be an absolute HTTPS origin without user info, path, query, or fragment.\n' >&2; exit 2; }

for required_command in curl jq; do
  command -v "$required_command" >/dev/null 2>&1 \
    || { printf '%s is required to verify execution runtime availability.\n' "$required_command" >&2; exit 127; }
done

if [[ -n "$production_lock" ]]; then
  [[ -f "$production_lock" && ! -L "$production_lock" && "$(basename "$production_lock")" == production.lock.json ]] \
    || { printf 'Runtime availability requires the real production.lock.json when a lock is supplied.\n' >&2; exit 2; }
  jq -e '
    .schemaVersion == 1
    and .lockKind == "production"
    and (.entries | type == "array" and length == 12)
    and ([.entries[].id] | unique | sort) == ["sunder.runtime.dotnet", "sunder.runtime.node"]
    and ([.entries[].rid] | unique | sort) == ["linux-arm64", "linux-x64", "osx-arm64", "osx-x64", "win-arm64", "win-x64"]
    and ([
      ["sunder.runtime.dotnet", "sunder.runtime.node"][] as $id
      | ["linux-arm64", "linux-x64", "osx-arm64", "osx-x64", "win-arm64", "win-x64"][] as $rid
      | ([.entries[] | select(.id == $id and .rid == $rid)] | length) == 1
    ] | all)
  ' "$production_lock" >/dev/null \
    || { printf 'Production execution-runtime lock does not contain the exact two-family six-RID set.\n' >&2; exit 1; }
fi

runtime_id='sunder.runtime.dotnet'
version_range='>=10.0.0 <11.0.0'
module_abi='sunder.dotnet-worker-module.v1'
trust_profile='sunder.runtime-trust.v1'
rids=(win-x64 win-arm64 linux-x64 linux-arm64 osx-x64 osx-arm64)

for rid in "${rids[@]}"; do
  expected_runtime='null'
  if [[ -n "$production_lock" ]]; then
    expected_runtime="$(jq -ce --arg rid "$rid" '
      [.entries[] | select(.id == "sunder.runtime.dotnet" and .rid == $rid)]
      | if length == 1 then .[0] else error("missing exact .NET runtime lock entry") end
      | {
          runtimeId:.id,
          version,
          packagingRevision,
          rid,
          moduleAbi:.profile.moduleAbi,
          workerProtocol:.profile.workerProtocol,
          minimumHostTrustGeneration,
          releaseSigningKeyId:.trust.releaseSigningKeyId,
          nativeAttestationKeyId:.trust.attestationSigningKeyId
        }
    ' "$production_lock")"
  fi
  trust_generation="$(jq -nr --argjson expected "$expected_runtime" '
    if $expected == null then 1 else $expected.minimumHostTrustGeneration end
  ')"
  request="$(jq -cn \
    --arg runtime_id "$runtime_id" \
    --arg version_range "$version_range" \
    --arg module_abi "$module_abi" \
    --arg rid "$rid" \
    --arg trust_profile "$trust_profile" \
    --argjson trust_generation "$trust_generation" '
      {
        requirement: {
          runtimeId: $runtime_id,
          versionRange: $version_range,
          moduleAbi: $module_abi
        },
        rid: $rid,
        runtimeTrust: {profileId: $trust_profile, generation: $trust_generation},
        currentRuntime: null,
        expectedResolutionFingerprint: null
      }
    ')"

  if ! response="$(curl --fail-with-body --silent --show-error \
    --proto '=https' --tlsv1.2 --max-redirs 0 \
    -H 'Content-Type: application/json' \
    --data-binary "$request" \
    "$registry_api/api/v1/execution-runtimes/resolve")"; then
    printf "Registry execution runtime resolution request failed for RID '%s'.\n" "$rid" >&2
    exit 1
  fi
  jq -e '.success == true and (.runtimeArtifact | type == "object")' <<< "$response" >/dev/null \
    || { printf "Registry did not resolve the exact supported .NET 10 execution runtime profile for RID '%s'.\n" "$rid" >&2; exit 1; }

  resolved_version="$(jq -er '.runtimeArtifact.identity.version' <<< "$response")" \
    || { printf "Registry resolution omitted an execution runtime version for RID '%s'.\n" "$rid" >&2; exit 1; }
  resolved_revision="$(jq -er '.runtimeArtifact.identity.packagingRevision | select(type == "number" and floor == . and . > 0)' <<< "$response")" \
    || { printf "Registry resolution omitted an execution runtime revision for RID '%s'.\n" "$rid" >&2; exit 1; }
  if ! details="$(curl --fail-with-body --silent --show-error \
    --proto '=https' --tlsv1.2 --max-redirs 0 \
    "$registry_api/api/v1/execution-runtimes/$runtime_id/releases/$resolved_version/revisions/$resolved_revision")"; then
    printf "Registry execution runtime details request failed for RID '%s'.\n" "$rid" >&2
    exit 1
  fi

  if ! jq -e \
    --arg runtime_id "$runtime_id" \
    --arg module_abi "$module_abi" \
    --arg rid "$rid" \
    --argjson details "$details" \
    --argjson expected_runtime "$expected_runtime" \
    --argjson trust_generation "$trust_generation" '
      type == "object"
      and .success == true
      and (.conflicts | type == "array" and length == 0)
      and (.warnings | type == "array" and length == 0)
      and (.resolutionFingerprint | type == "string" and test("^[0-9a-f]{64}$"))
      and (.runtimePolicyRevision | type == "number" and floor == . and . >= 0)
      and (.expiresAtUtc | type == "string" and length > 0)
      and (.runtimeArtifact | type == "object")
      and .runtimeArtifact.identity.runtimeId == $runtime_id
      and (.runtimeArtifact.identity.version | type == "string" and test("^10\\."))
      and (.runtimeArtifact.identity.packagingRevision | type == "number" and floor == . and . > 0)
      and .runtimeArtifact.identity.rid == $rid
      and .runtimeArtifact.identity.moduleAbi == $module_abi
      and (.runtimeArtifact.manifestSha256 | type == "string" and test("^[0-9a-f]{64}$"))
      and (.runtimeArtifact.artifactSha256 | type == "string" and test("^[0-9a-f]{64}$"))
      and (.runtimeArtifact.artifactSize | type == "number" and floor == . and . > 0)
      and (.runtimeArtifact.signingKeyId | type == "string" and length > 0)
      and (.runtimeArtifact.minimumHostTrustGeneration | type == "number" and floor == . and . > 0 and . <= $trust_generation)
      and (.runtimeArtifact.nativeAttestationSha256 | type == "string" and test("^[0-9a-f]{64}$"))
      and (.runtimeArtifact.nativeAttestationKeyId | type == "string" and length > 0)
      and .runtimeArtifact.lifecycle.state == "supported"
      and (.runtimeArtifact.lifecycle.supportedFromUtc | type == "string" and length > 0)
      and .runtimeArtifact.lifecycle.installBlockedAtUtc == null
      and .runtimeArtifact.lifecycle.activationBlockedAtUtc == null
      and .runtimeArtifact.lifecycle.revokedAtUtc == null
      and (.runtimeArtifact.observedRuntimePolicyRevision | type == "number" and floor == . and . >= 0)
      and (.runtimeArtifact.downloadUrl | type == "string" and length > 0)
      and ($details | type == "object")
      and ($details.release | type == "object")
      and ($details.artifacts | type == "array")
      and $details.release.runtimeId == .runtimeArtifact.identity.runtimeId
      and $details.release.version == .runtimeArtifact.identity.version
      and $details.release.packagingRevision == .runtimeArtifact.identity.packagingRevision
      and $details.release.moduleAbi == .runtimeArtifact.identity.moduleAbi
      and $details.release.workerProtocol == "sunder.worker.v1"
      and $details.release.minimumHostTrustGeneration == .runtimeArtifact.minimumHostTrustGeneration
      and $details.release.lifecycle.state == "supported"
      and $details.release.lifecycle.installBlockedAtUtc == null
      and $details.release.lifecycle.activationBlockedAtUtc == null
      and $details.release.lifecycle.revokedAtUtc == null
      and ($details.release.supportedRids | type == "array" and index($rid) != null)
      and ([$details.artifacts[] | select(.artifact.identity.rid == $rid)] | length) == 1
      and ($details.artifacts[] | select(.artifact.identity.rid == $rid) | .artifact) == .runtimeArtifact
      and ($expected_runtime == null or {
        runtimeId:.runtimeArtifact.identity.runtimeId,
        version:.runtimeArtifact.identity.version,
        packagingRevision:.runtimeArtifact.identity.packagingRevision,
        rid:.runtimeArtifact.identity.rid,
        moduleAbi:.runtimeArtifact.identity.moduleAbi,
        workerProtocol:$details.release.workerProtocol,
        minimumHostTrustGeneration:.runtimeArtifact.minimumHostTrustGeneration,
        releaseSigningKeyId:.runtimeArtifact.signingKeyId,
        nativeAttestationKeyId:.runtimeArtifact.nativeAttestationKeyId
      } == $expected_runtime)
    ' <<< "$response" >/dev/null; then
    printf "Registry did not resolve the exact supported .NET 10 execution runtime profile for RID '%s'.\n" "$rid" >&2
    exit 1
  fi
done

if [[ -n "$production_lock" ]]; then
  printf 'Registry resolved the exact signed and native-attested .NET entries from production.lock.json for all six release RIDs.\n'
else
  printf 'Registry resolved the exact supported .NET 10 execution runtime profile for all six release RIDs.\n'
fi
