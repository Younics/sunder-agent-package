#!/usr/bin/env bash
set -euo pipefail
export LC_ALL=C

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
verifier="$script_dir/verify-execution-runtime-availability.sh"
[[ -f "$verifier" ]] || { printf 'Execution runtime verifier not found: %s\n' "$verifier" >&2; exit 1; }

for required_command in grep jq mktemp; do
  command -v "$required_command" >/dev/null 2>&1 \
    || { printf '%s is required to run the execution runtime fixture.\n' "$required_command" >&2; exit 127; }
done

temp_root="${TMPDIR:-/tmp}"
fixture_root="$(mktemp -d "${temp_root%/}/sunder-runtime-resolution-fixture.XXXXXX")"
trap 'rm -rf "$fixture_root"' EXIT
mock_bin="$fixture_root/mock-bin"
mkdir -p "$mock_bin"
export MOCK_RUNTIME_CALLS="$fixture_root/runtime-calls.jsonl"
: > "$MOCK_RUNTIME_CALLS"

cat > "$mock_bin/curl" <<'MOCK_CURL'
#!/usr/bin/env bash
set -euo pipefail

request=''
url=''
content_type=''
while [[ $# -gt 0 ]]; do
  case "$1" in
    --fail-with-body|--silent|--show-error|--tlsv1.2)
      shift
      ;;
    --proto|--max-redirs)
      shift 2
      ;;
    -H)
      case "$2" in
        Content-Type:*) content_type="$2" ;;
        Authorization:*) printf 'Runtime resolution unexpectedly received authorization.\n' >&2; exit 2 ;;
      esac
      shift 2
      ;;
    --data-binary)
      request="$2"
      shift 2
      ;;
    https://*)
      url="$1"
      shift
      ;;
    *)
      printf 'Mock curl does not support argument: %s\n' "$1" >&2
      exit 2
      ;;
  esac
done

if [[ "$url" =~ ^https://registry[.]test/api/v1/execution-runtimes/sunder[.]runtime[.]dotnet/releases/10[.]0[.]1/revisions/1$ ]]; then
  rid="$(jq -sr '.[-1].rid' "$MOCK_RUNTIME_CALLS")"
  [[ "$rid" =~ ^(win|linux|osx)-(x64|arm64)$ ]] \
    || { printf 'Execution runtime details request has no preceding RID resolution.\n' >&2; exit 2; }
  jq -cn --arg rid "$rid" '{
    release: {
      runtimeId: "sunder.runtime.dotnet",
      version: "10.0.1",
      packagingRevision: 1,
      moduleAbi: "sunder.dotnet-worker-module.v1",
      workerProtocol: (if env.MOCK_RUNTIME_MODE == "wrong-worker-protocol" then "sunder.worker.wrong" else "sunder.worker.v1" end),
      minimumHostTrustGeneration: 1,
      lifecycle: {
        state: "supported", stagedAtUtc: "2026-08-01T00:00:00Z", supportedFromUtc: "2026-08-02T00:00:00Z",
        deprecatedAtUtc: null, installBlockedAtUtc: null, activationBlockedAtUtc: null, revokedAtUtc: null, message: null
      },
      supportedRids: [$rid],
      publishedAtUtc: "2026-08-01T00:00:00Z"
    },
    artifacts: [{
      artifact: {
        identity: {runtimeId:"sunder.runtime.dotnet",version:"10.0.1",packagingRevision:1,rid:$rid,moduleAbi:"sunder.dotnet-worker-module.v1"},
        manifestSha256:("1" * 64), artifactSha256:("2" * 64), artifactSize:1024,
        signingKeyId:"runtime-signing-v1", minimumHostTrustGeneration:1,
        nativeAttestationSha256:("3" * 64), nativeAttestationKeyId:"runtime-attestation-v1",
        lifecycle: {
          state:"supported", stagedAtUtc:"2026-08-01T00:00:00Z", supportedFromUtc:"2026-08-02T00:00:00Z",
          deprecatedAtUtc:null, installBlockedAtUtc:null, activationBlockedAtUtc:null, revokedAtUtc:null, message:null
        },
        observedRuntimePolicyRevision:7,
        downloadUrl:("/api/v1/execution-runtimes/sunder.runtime.dotnet/releases/10.0.1/revisions/1/artifacts/" + $rid + "/download")
      },
      contentIdentity:("5" * 64),
      publishedAtUtc:"2026-08-01T00:00:00Z"
    }]
  }'
  exit 0
fi

[[ "$url" == 'https://registry.test/api/v1/execution-runtimes/resolve' ]] \
  || { printf 'Unexpected execution runtime URL: %s\n' "$url" >&2; exit 2; }
[[ "$content_type" == 'Content-Type: application/json' ]] \
  || { printf 'Execution runtime request omitted its JSON content type.\n' >&2; exit 2; }
jq -e '
  .requirement == {
    runtimeId: "sunder.runtime.dotnet",
    versionRange: ">=10.0.0 <11.0.0",
    moduleAbi: "sunder.dotnet-worker-module.v1"
  }
  and (.rid | test("^(win|linux|osx)-(x64|arm64)$"))
  and .runtimeTrust == {profileId: "sunder.runtime-trust.v1", generation: 1}
  and .currentRuntime == null
  and .expectedResolutionFingerprint == null
' <<< "$request" >/dev/null || { printf 'Execution runtime request contract is invalid.\n' >&2; exit 2; }
printf '%s\n' "$request" >> "$MOCK_RUNTIME_CALLS"

rid="$(jq -er '.rid' <<< "$request")"
case "${MOCK_RUNTIME_MODE:-supported}" in
  network)
    exit 7
    ;;
  unsupported)
    jq -cn --arg rid "$rid" '{
      success: false,
      runtimeArtifact: null,
      resolutionFingerprint: ("0" * 64),
      runtimePolicyRevision: 7,
      expiresAtUtc: "2026-09-01T01:00:00Z",
      warnings: [],
      conflicts: [{code: "execution-runtime-unsupported", message: "unsupported", requirement: {
        runtimeId: "sunder.runtime.dotnet", versionRange: ">=10.0.0 <11.0.0", moduleAbi: "sunder.dotnet-worker-module.v1"
      }, rid: $rid}]
    }'
    exit 0
    ;;
  deprecated)
    lifecycle=deprecated
    ;;
  supported|wrong-worker-protocol)
    lifecycle=supported
    ;;
  *)
    printf 'Unsupported mock runtime mode.\n' >&2
    exit 2
    ;;
esac

jq -cn --arg rid "$rid" --arg lifecycle "$lifecycle" '{
  success: true,
  runtimeArtifact: {
    identity: {
      runtimeId: "sunder.runtime.dotnet",
      version: "10.0.1",
      packagingRevision: 1,
      rid: $rid,
      moduleAbi: "sunder.dotnet-worker-module.v1"
    },
    manifestSha256: ("1" * 64),
    artifactSha256: ("2" * 64),
    artifactSize: 1024,
    signingKeyId: "runtime-signing-v1",
    minimumHostTrustGeneration: 1,
    nativeAttestationSha256: ("3" * 64),
    nativeAttestationKeyId: "runtime-attestation-v1",
    lifecycle: {
      state: $lifecycle,
      stagedAtUtc: "2026-08-01T00:00:00Z",
      supportedFromUtc: "2026-08-02T00:00:00Z",
      deprecatedAtUtc: null,
      installBlockedAtUtc: null,
      activationBlockedAtUtc: null,
      revokedAtUtc: null,
      message: null
    },
    observedRuntimePolicyRevision: 7,
    downloadUrl: ("/api/v1/execution-runtimes/sunder.runtime.dotnet/releases/10.0.1/revisions/1/artifacts/" + $rid + "/download")
  },
  resolutionFingerprint: ("4" * 64),
  runtimePolicyRevision: 7,
  expiresAtUtc: "2026-09-01T01:00:00Z",
  warnings: (if $lifecycle == "supported" then [] else ["deprecated"] end),
  conflicts: []
}'
MOCK_CURL
chmod +x "$mock_bin/curl"

PATH="$mock_bin:$PATH" bash "$verifier" https://registry.test > "$fixture_root/supported.log"
jq -s -e '
  length == 6
  and ([.[].rid] | sort) == ["linux-arm64","linux-x64","osx-arm64","osx-x64","win-arm64","win-x64"]
  and ([.[].rid] | length) == ([.[].rid] | unique | length)
' "$MOCK_RUNTIME_CALLS" >/dev/null

production_lock="$fixture_root/production.lock.json"
jq -n '
  def entry($id; $rid): {
    id: $id,
    version: (if $id == "sunder.runtime.dotnet" then "10.0.1" else "24.0.1" end),
    packagingRevision: 1,
    rid: $rid,
    minimumHostTrustGeneration: 1,
    profile: {
      moduleAbi: (if $id == "sunder.runtime.dotnet" then "sunder.dotnet-worker-module.v1" else "sunder.node-worker-module.v1" end),
      workerProtocol: "sunder.worker.v1"
    },
    trust: {releaseSigningKeyId: "runtime-signing-v1", attestationSigningKeyId: "runtime-attestation-v1"}
  };
  ["sunder.runtime.dotnet", "sunder.runtime.node"] as $ids
  | ["win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64"] as $rids
  | {
    schemaVersion: 1,
    lockKind: "production",
    entries: [$ids[] as $id | $rids[] as $rid | entry($id; $rid)]
  }
' > "$production_lock"
: > "$MOCK_RUNTIME_CALLS"
PATH="$mock_bin:$PATH" bash "$verifier" https://registry.test "$production_lock" > "$fixture_root/lock-supported.log"
[[ "$(wc -l < "$MOCK_RUNTIME_CALLS" | tr -d '[:space:]')" == 6 ]]

jq '(.entries[] | select(.id == "sunder.runtime.dotnet" and .rid == "linux-x64") | .trust.releaseSigningKeyId) = "wrong-key"' \
  "$production_lock" > "$production_lock.next"
mv "$production_lock.next" "$production_lock"
if PATH="$mock_bin:$PATH" bash "$verifier" https://registry.test "$production_lock" \
  > "$fixture_root/lock-mismatch.log" 2>&1; then
  printf 'Execution runtime verifier accepted a Registry result with the wrong locked signer identity.\n' >&2
  exit 1
fi
grep -Fq 'did not resolve the exact supported' "$fixture_root/lock-mismatch.log"

jq '(.entries[] | select(.id == "sunder.runtime.dotnet" and .rid == "linux-x64") | .trust.releaseSigningKeyId) = "runtime-signing-v1"' \
  "$production_lock" > "$production_lock.next"
mv "$production_lock.next" "$production_lock"
if MOCK_RUNTIME_MODE=wrong-worker-protocol PATH="$mock_bin:$PATH" \
  bash "$verifier" https://registry.test "$production_lock" > "$fixture_root/lock-worker-protocol.log" 2>&1; then
  printf 'Execution runtime verifier accepted details with the wrong locked Worker protocol.\n' >&2
  exit 1
fi
grep -Fq 'did not resolve the exact supported' "$fixture_root/lock-worker-protocol.log"

for failure_mode in unsupported deprecated network; do
  : > "$MOCK_RUNTIME_CALLS"
  if MOCK_RUNTIME_MODE="$failure_mode" PATH="$mock_bin:$PATH" \
    bash "$verifier" https://registry.test > "$fixture_root/$failure_mode.log" 2>&1; then
    printf 'Execution runtime verifier accepted failure mode %s.\n' "$failure_mode" >&2
    exit 1
  fi
done
grep -Fq 'did not resolve the exact supported' "$fixture_root/unsupported.log"
grep -Fq 'did not resolve the exact supported' "$fixture_root/deprecated.log"
grep -Fq 'resolution request failed' "$fixture_root/network.log"

if PATH="$mock_bin:$PATH" bash "$verifier" http://registry.test > "$fixture_root/insecure.log" 2>&1; then
  printf 'Execution runtime verifier accepted an insecure Registry origin.\n' >&2
  exit 1
fi
grep -Fq 'absolute HTTPS origin' "$fixture_root/insecure.log"

printf 'Execution runtime availability fixture passed: anonymous six-RID resolution is lock- and signer-bound, and every unavailable state fails closed.\n'
