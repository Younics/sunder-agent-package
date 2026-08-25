#!/usr/bin/env bash
set -euo pipefail
export LC_ALL=C

if [[ $# -ne 1 ]]; then
  printf 'Usage: publish-agent-family.sh <release-assets-directory>\n' >&2
  exit 2
fi

asset_directory="$(cd "$1" && pwd)"
: "${CORE_COMMIT:?CORE_COMMIT is required}"
: "${CORE_TAG:?CORE_TAG is required}"
: "${CORE_SDK_VERSION:?CORE_SDK_VERSION is required}"
: "${GH_TOKEN:?GH_TOKEN is required}"
: "${GH_REPO:?GH_REPO is required}"
: "${GITHUB_SHA:?GITHUB_SHA is required}"
: "${IS_PRERELEASE:?IS_PRERELEASE is required}"
: "${NUGET_API_KEY:?NUGET_API_KEY is required}"
: "${NUGET_FLAT_CONTAINER:?NUGET_FLAT_CONTAINER is required}"
: "${NUGET_SOURCE:?NUGET_SOURCE is required}"
: "${REGISTRY_API_URL:?REGISTRY_API_URL is required}"
: "${SUNDER_REGISTRY_PUBLISH_TOKEN:?SUNDER_REGISTRY_PUBLISH_TOKEN is required}"
: "${VERSION:?VERSION is required}"

[[ "$CORE_COMMIT" =~ ^[0-9a-f]{40}$ ]] \
  || { printf 'CORE_COMMIT must be a full lowercase Git SHA.\n' >&2; exit 2; }
[[ "$GITHUB_SHA" =~ ^[0-9a-f]{40}$ ]] \
  || { printf 'GITHUB_SHA must be a full lowercase Git SHA.\n' >&2; exit 2; }
[[ "$CORE_TAG" == "sdk/v$CORE_SDK_VERSION" ]] \
  || { printf 'Core tag and package version do not agree.\n' >&2; exit 2; }
[[ "$IS_PRERELEASE" == true || "$IS_PRERELEASE" == false ]] \
  || { printf 'IS_PRERELEASE must be true or false.\n' >&2; exit 2; }
[[ "$SUNDER_REGISTRY_PUBLISH_TOKEN" =~ ^sunder_pub_v1_[A-Za-z0-9_-]{43}$ ]] \
  || { printf 'SUNDER_REGISTRY_PUBLISH_TOKEN is not a valid scoped publish token.\n' >&2; exit 1; }
[[ "$NUGET_SOURCE" == 'https://api.nuget.org/v3/index.json' \
  && "$NUGET_FLAT_CONTAINER" == 'https://api.nuget.org/v3-flatcontainer' ]] \
  || { printf 'NuGet publication endpoints must be the pinned NuGet.org endpoints.\n' >&2; exit 1; }
registry_api="${REGISTRY_API_URL%/}"
[[ "$registry_api" =~ ^https://[^/@?#]+$ ]] \
  || { printf 'SUNDER_REGISTRY_API_URL must be an absolute HTTPS origin.\n' >&2; exit 1; }

for required_command in curl cut gh grep jq mktemp sort tr wc; do
  command -v "$required_command" >/dev/null 2>&1 \
    || { printf '%s is required to publish the Agent family.\n' "$required_command" >&2; exit 127; }
done
if ! command -v sha256sum >/dev/null 2>&1 && ! command -v shasum >/dev/null 2>&1; then
  printf 'sha256sum or shasum is required to publish the Agent family.\n' >&2
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

runtime_count="$(jq '[.packages[] | select(.artifactType == "sunderpkg")] | length' packages.json)"
protocol_count="$(jq '[.packages[] | select(.artifactType == "nuget" and .packageId == "Sunder.Package.Agent.Protocol")] | length' packages.json)"
[[ "$runtime_count" == 15 && "$protocol_count" == 1 ]] \
  || { printf 'packages.json must contain exactly 15 runtime packages and one Agent Protocol package.\n' >&2; exit 1; }
[[ "$VERSION" == "$(jq -er '.releaseVersion' packages.json)" ]] \
  || { printf 'VERSION does not match packages.json releaseVersion.\n' >&2; exit 1; }

package_ids=()
while IFS= read -r package_id; do package_ids+=("$package_id"); done \
  < <(jq -r '.packages | map(select(.artifactType == "sunderpkg")) | sort_by(.packageId)[] | .packageId' packages.json)
runtime_names=()
for package_id in "${package_ids[@]}"; do
  runtime_names+=("$package_id.$VERSION.sunderpkg")
done
protocol_name="Sunder.Package.Agent.Protocol.$VERSION.nupkg"
native_evidence_name='native-test-evidence.json'
final_artifact_names=("${runtime_names[@]}" "$protocol_name")
primary_names=("${final_artifact_names[@]}" "$native_evidence_name")
evidence_names=(SHA256SUMS release-provenance.json release-sbom.spdx.json toolchain.txt)
expected_release_names=("${primary_names[@]}" "${evidence_names[@]}")
expected_primary_json="$(printf '%s\n' "${primary_names[@]}" | jq -Rsc 'split("\n")[:-1] | sort')"
expected_final_json="$(printf '%s\n' "${final_artifact_names[@]}" | jq -Rsc 'split("\n")[:-1] | sort')"

shopt -s nullglob
initial_files=("$asset_directory"/*)
[[ "${#initial_files[@]}" == "${#primary_names[@]}" ]] \
  || { printf 'The tested family input must contain exactly %s primary files.\n' "${#primary_names[@]}" >&2; exit 1; }
for name in "${primary_names[@]}"; do
  [[ -f "$asset_directory/$name" ]] \
    || { printf 'Missing exact tested release input: %s\n' "$name" >&2; exit 1; }
done

native_evidence="$asset_directory/$native_evidence_name"
jq -e \
  --arg version "$VERSION" \
  --arg agent_commit "$GITHUB_SHA" \
  --arg core_tag "$CORE_TAG" \
  --arg core_commit "$CORE_COMMIT" \
  --arg core_version "$CORE_SDK_VERSION" \
  --argjson expected_final "$expected_final_json" '
    .schemaVersion == 1
    and .version == $version
    and .agentCommit == $agent_commit
    and .core == {tag: $core_tag, commit: $core_commit, packageVersion: $core_version}
    and ([.nativeRuns[].rid] | sort) == ["linux-arm64","linux-x64","osx-arm64","osx-x64","win-arm64","win-x64"]
    and all(.nativeRuns[]; .packages | length == 15)
    and ([.finalArtifacts[].path] | sort) == $expected_final
    and ([.finalArtifacts[].path] | length) == ([.finalArtifacts[].path] | unique | length)
  ' "$native_evidence" >/dev/null || {
    printf 'Native test evidence does not identify this Agent/Core release and six complete RID runs.\n' >&2
    exit 1
  }
while IFS=$'\t' read -r name expected_sha expected_size; do
  path="$asset_directory/$name"
  [[ -f "$path" \
    && "$(hash_file "$path")" == "$expected_sha" \
    && "$(file_size "$path")" == "$expected_size" ]] \
    || { printf 'Tested final artifact differs from native fan-in evidence: %s\n' "$name" >&2; exit 1; }
done < <(jq -r '.finalArtifacts[] | [.path, .sha256, (.size | tostring)] | @tsv' "$native_evidence")

tag="agent/v$VERSION"
release_notes_base="$(printf '%s\n\nAgent source commit: %s\nCore source commit: %s\nCore SDK release: %s\nCore SDK package version: %s\n\n%s' \
  "Coordinated Agent Protocol and 15-package native runtime family release for $VERSION." \
  "$GITHUB_SHA" "$CORE_COMMIT" "$CORE_TAG" "$CORE_SDK_VERSION" \
  'Immutable publication preserves and byte-verifies the native-tested artifacts without moving Registry dist tags or finalizing this draft.')"
created_draft=false
external_publication_started=false
temp_root="${RUNNER_TEMP:-${TMPDIR:-/tmp}}"
verification_root="$(mktemp -d "${temp_root%/}/sunder-agent-publication.XXXXXX")"

cleanup() {
  exit_code=$?
  trap - EXIT
  rm -rf "$verification_root"
  if [[ $exit_code -ne 0 && "$created_draft" == true && "$external_publication_started" != true ]] \
    && gh release view "$tag" --repo "$GH_REPO" >/dev/null 2>&1 \
    && [[ "$(gh release view "$tag" --repo "$GH_REPO" --json isDraft --jq .isDraft)" == true ]]; then
    gh release delete "$tag" --repo "$GH_REPO" --yes || true
  elif [[ $exit_code -ne 0 && "$external_publication_started" == true ]]; then
    printf 'Preserving draft %s because immutable external publication may have started; rerun the same tag to recover.\n' "$tag" >&2
  fi
  exit "$exit_code"
}
trap cleanup EXIT

build_release_notes() {
  local directory="$1"
  local name
  printf '%s\n\nRelease evidence SHA-256:' "$release_notes_base"
  for name in "${evidence_names[@]}"; do
    printf '\n%s SHA-256: %s' "$name" "$(hash_file "$directory/$name")"
  done
}

verify_release_sources() {
  local release_json="$1"
  jq -e \
    --arg tag "$tag" \
    --argjson prerelease "$IS_PRERELEASE" \
    --arg agent_commit "$GITHUB_SHA" \
    --arg core_commit "$CORE_COMMIT" \
    --arg core_tag "$CORE_TAG" \
    --arg core_version "$CORE_SDK_VERSION" '
      .tagName == $tag
      and .isDraft == true
      and .isPrerelease == $prerelease
      and (.body | capture("(?m)^Agent source commit: (?<sha>[0-9a-f]{40})$").sha) == $agent_commit
      and (.body | capture("(?m)^Core source commit: (?<sha>[0-9a-f]{40})$").sha) == $core_commit
      and (.body | capture("(?m)^Core SDK release: (?<tag>sdk/v[^[:space:]]+)$").tag) == $core_tag
      and (.body | capture("(?m)^Core SDK package version: (?<version>[^[:space:]]+)$").version) == $core_version
    ' <<< "$release_json" >/dev/null \
    || { printf 'GitHub release is not the exact expected Agent draft and source provenance.\n' >&2; return 1; }
}

verify_release_inventory() {
  local release_json="$1"
  [[ "$(jq '.assets | length' <<< "$release_json")" == "${#expected_release_names[@]}" ]] \
    || { printf 'Draft asset inventory must contain exactly %s files.\n' "${#expected_release_names[@]}" >&2; return 1; }
  for expected_name in "${expected_release_names[@]}"; do
    [[ "$(jq --arg name "$expected_name" '[.assets[] | select(.name == $name)] | length' <<< "$release_json")" == 1 ]] \
      || { printf 'Draft must contain exactly one %s.\n' "$expected_name" >&2; return 1; }
  done
}

verify_evidence_directory() {
  local directory="$1"
  local provenance="$directory/release-provenance.json"
  local sums="$directory/SHA256SUMS"
  local sbom="$directory/release-sbom.spdx.json"
  local evidence="$directory/$native_evidence_name"

  jq -e \
    --arg agent_commit "$GITHUB_SHA" \
    --arg core_commit "$CORE_COMMIT" \
    --arg core_tag "$CORE_TAG" \
    --arg core_version "$CORE_SDK_VERSION" \
    --argjson expected_names "$expected_primary_json" '
      .schemaVersion == 1
      and .buildType == "https://sunderapp.io/provenance/github-actions/v1"
      and ([.sources[] | {name, sha}] | sort_by(.name)) == ([
        {name: "sunder-agent-package", sha: $agent_commit},
        {name: "sunder-core", sha: $core_commit}
      ] | sort_by(.name))
      and .materials == [{name: "sunder-core-sdk", tag: $core_tag, sha: $core_commit, packageVersion: $core_version}]
      and ([.artifacts[].path] | sort) == $expected_names
      and ([.artifacts[].path] | length) == ([.artifacts[].path] | unique | length)
      and all(.artifacts[];
        (.sha256 | type == "string" and test("^[0-9a-f]{64}$"))
        and (.size | type == "number" and floor == . and . > 0))
    ' "$provenance" >/dev/null \
    || { printf 'Release provenance does not match the exact Agent/Core primary artifact set.\n' >&2; return 1; }

  jq -e \
    --arg version "$VERSION" \
    --arg agent_commit "$GITHUB_SHA" \
    --arg core_tag "$CORE_TAG" \
    --arg core_commit "$CORE_COMMIT" \
    --arg core_version "$CORE_SDK_VERSION" \
    --argjson expected_final "$expected_final_json" '
      .schemaVersion == 1
      and .version == $version
      and .agentCommit == $agent_commit
      and .core == {tag: $core_tag, commit: $core_commit, packageVersion: $core_version}
      and ([.nativeRuns[].rid] | sort) == ["linux-arm64","linux-x64","osx-arm64","osx-x64","win-arm64","win-x64"]
      and all(.nativeRuns[]; .packages | length == 15)
      and ([.finalArtifacts[].path] | sort) == $expected_final
      and ([.finalArtifacts[].path] | length) == ([.finalArtifacts[].path] | unique | length)
    ' "$evidence" >/dev/null \
    || { printf 'Downloaded native evidence is not the accepted six-RID family evidence.\n' >&2; return 1; }

  [[ "$(wc -l < "$sums" | tr -d '[:space:]')" == "${#primary_names[@]}" ]] \
    || { printf 'SHA256SUMS does not contain the exact primary inventory.\n' >&2; return 1; }
  jq -e --argjson expected_names "$expected_primary_json" '
    [.files[].fileName | sub("^\\./"; "")] | sort == $expected_names
  ' "$sbom" >/dev/null \
    || { printf 'Release SBOM does not contain the exact primary inventory.\n' >&2; return 1; }

  for name in "${primary_names[@]}"; do
    local path="$directory/$name"
    local sha
    local size
    sha="$(hash_file "$path")"
    size="$(file_size "$path")"
    jq -e --arg name "$name" --arg sha "$sha" --argjson size "$size" '
      [.artifacts[] | select(.path == $name and .sha256 == $sha and .size == $size)] | length == 1
    ' "$provenance" >/dev/null \
      || { printf 'Provenance hash or size differs for %s.\n' "$name" >&2; return 1; }
    grep -Fqx "$sha  $name" "$sums" \
      || { printf 'SHA256SUMS differs for %s.\n' "$name" >&2; return 1; }
    jq -e --arg name "./$name" --arg sha "$sha" '
      [.files[] | select(.fileName == $name and ([.checksums[] | select(.algorithm == "SHA256" and .checksumValue == $sha)] | length == 1))] | length == 1
    ' "$sbom" >/dev/null \
      || { printf 'Release SBOM checksum differs for %s.\n' "$name" >&2; return 1; }
  done
}

verify_remote_draft() {
  local compare_all_local="$1"
  local directory="$verification_root/assets-$RANDOM-$RANDOM"
  local release_json
  local downloaded_files
  mkdir "$directory"
  release_json="$(gh release view "$tag" --repo "$GH_REPO" --json tagName,isDraft,isPrerelease,body,assets)"
  verify_release_sources "$release_json"
  verify_release_inventory "$release_json"
  gh release download "$tag" --repo "$GH_REPO" --dir "$directory"
  downloaded_files=("$directory"/*)
  [[ "${#downloaded_files[@]}" == "${#expected_release_names[@]}" ]] \
    || { printf 'Downloaded draft contains an unexpected file count.\n' >&2; return 1; }
  for name in "${expected_release_names[@]}"; do
    local remote="$directory/$name"
    [[ -f "$remote" ]] || { printf 'Downloaded draft is missing %s.\n' "$name" >&2; return 1; }
    local remote_size
    remote_size="$(file_size "$remote")"
    [[ "$(jq -r --arg name "$name" '.assets[] | select(.name == $name) | .size' <<< "$release_json")" == "$remote_size" ]] \
      || { printf 'GitHub asset metadata size differs for %s.\n' "$name" >&2; return 1; }
    if [[ "$compare_all_local" == true ]]; then
      [[ "$(hash_file "$asset_directory/$name")" == "$(hash_file "$remote")" \
        && "$(file_size "$asset_directory/$name")" == "$remote_size" ]] \
        || { printf 'Downloaded GitHub draft asset differs byte-for-byte from publication input: %s\n' "$name" >&2; return 1; }
    fi
  done
  for name in "${primary_names[@]}"; do
    [[ "$(hash_file "$asset_directory/$name")" == "$(hash_file "$directory/$name")" ]] \
      || { printf 'Downloaded GitHub draft primary asset differs from tested bytes: %s\n' "$name" >&2; return 1; }
  done
  [[ "$(jq -r '.body' <<< "$release_json")" == "$(build_release_notes "$directory")" ]] \
    || { printf 'GitHub draft notes do not byte-bind all four downloaded release evidence assets.\n' >&2; return 1; }
  verify_evidence_directory "$directory"
}

if gh release view "$tag" --repo "$GH_REPO" >/dev/null 2>&1; then
  verify_remote_draft false
else
  SUNDER_CORE_TAG="$CORE_TAG" \
  SUNDER_CORE_COMMIT="$CORE_COMMIT" \
  SUNDER_CORE_PACKAGE_VERSION="$CORE_SDK_VERSION" \
    bash scripts/generate-release-evidence.sh "$asset_directory" \
      "sunder-agent-package=$GITHUB_SHA" "sunder-core=$CORE_COMMIT"
  verify_evidence_directory "$asset_directory"
  release_notes="$(build_release_notes "$asset_directory")"
  release_assets=("$asset_directory"/*)
  [[ "${#release_assets[@]}" == "${#expected_release_names[@]}" ]] \
    || { printf 'Generated local draft inventory is not exact.\n' >&2; exit 1; }
  create_args=(release create "$tag" --repo "$GH_REPO" --title "Sunder Agent Family $VERSION" --notes "$release_notes" --latest=false --verify-tag --draft)
  [[ "$IS_PRERELEASE" != true ]] || create_args+=(--prerelease)
  gh "${create_args[@]}"
  created_draft=true
  gh release upload "$tag" --repo "$GH_REPO" "${release_assets[@]}"
  verify_remote_draft true
fi

protocol_path="$asset_directory/$protocol_name"
protocol_version_lower="${VERSION,,}"
protocol_url="$NUGET_FLAT_CONTAINER/sunder.package.agent.protocol/$protocol_version_lower/sunder.package.agent.protocol.$protocol_version_lower.nupkg"
verify_protocol() {
  local remote="$verification_root/Sunder.Package.Agent.Protocol.$VERSION.nupkg"
  for _ in {1..30}; do
    rm -f "$remote"
    if curl --fail --silent --show-error --proto '=https' --tlsv1.2 --max-redirs 0 \
      "$protocol_url" --output "$remote" 2>/dev/null; then
      bash scripts/release/verify-nuget-package.sh "$protocol_path" "$remote"
      return
    fi
    sleep 10
  done
  printf 'NuGet.org did not expose Agent Protocol %s.\n' "$VERSION" >&2
  return 1
}

external_publication_started=true
if ! dotnet nuget push "$protocol_path" --source "$NUGET_SOURCE" --api-key "$NUGET_API_KEY" --skip-duplicate; then
  verify_protocol
fi
verify_protocol

for package_id in "${package_ids[@]}"; do
  bash scripts/release/publish-registry-package.sh \
    "$registry_api" "$asset_directory/$package_id.$VERSION.sunderpkg" "$package_id" "$VERSION"
done

verify_remote_draft false
printf 'Published and verified immutable Agent %s artifacts; GitHub release %s remains a draft and no Registry dist tag moved.\n' \
  "$VERSION" "$tag"
trap - EXIT
rm -rf "$verification_root"
