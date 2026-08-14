#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 1 ]]; then
  printf 'Usage: publish-agent-family.sh <release-assets-directory>\n' >&2
  exit 2
fi

asset_directory="$(cd "$1" && pwd)"
: "${CORE_COMMIT:?CORE_COMMIT is required}"
: "${CORE_TAG:?CORE_TAG is required}"
: "${CORE_SDK_VERSION:?CORE_SDK_VERSION is required}"
: "${DIST_TAG:?DIST_TAG is required}"
: "${GH_TOKEN:?GH_TOKEN is required}"
: "${GH_REPO:?GH_REPO is required}"
: "${IS_PRERELEASE:?IS_PRERELEASE is required}"
: "${NUGET_API_KEY:?NUGET_API_KEY is required}"
: "${REGISTRY_API_URL:?REGISTRY_API_URL is required}"
: "${SUNDER_REGISTRY_CLI_TOKEN:?SUNDER_REGISTRY_CLI_TOKEN is required}"
: "${SUNDER_REGISTRY_PUBLISH_TOKEN:?SUNDER_REGISTRY_PUBLISH_TOKEN is required}"
: "${VERSION:?VERSION is required}"
[[ "$CORE_COMMIT" =~ ^[0-9a-f]{40}$ ]] || { printf 'CORE_COMMIT must be a full lowercase Git SHA.\n' >&2; exit 2; }
[[ "$CORE_TAG" == "sdk/v$CORE_SDK_VERSION" ]] || { printf 'Core tag and package version do not agree.\n' >&2; exit 2; }
[[ "$SUNDER_REGISTRY_PUBLISH_TOKEN" =~ ^sunder_pub_v1_[A-Za-z0-9_-]{43}$ ]] \
  || { printf 'SUNDER_REGISTRY_PUBLISH_TOKEN is not a valid scoped publish token.\n' >&2; exit 1; }
[[ "$SUNDER_REGISTRY_CLI_TOKEN" =~ ^sunder_cli_[A-Za-z0-9_-]{43}$ ]] \
  || { printf 'SUNDER_REGISTRY_CLI_TOKEN is not a valid interactive token.\n' >&2; exit 1; }
registry_api="${REGISTRY_API_URL%/}"
[[ "$registry_api" =~ ^https://[^/@?#]+$ ]] \
  || { printf 'SUNDER_REGISTRY_API_URL must be an absolute HTTPS origin.\n' >&2; exit 1; }

shopt -s nullglob
runtime_packages=("$asset_directory"/*.sunderpkg)
nuget_packages=("$asset_directory"/*.nupkg)
native_evidence="$asset_directory/native-test-evidence.json"
runtime_count="$(jq '[.packages[] | select(.artifactType == "sunderpkg")] | length' packages.json)"
[[ "$runtime_count" == 15 && "${#runtime_packages[@]}" == 15 ]] \
  || { printf 'Expected exactly 15 tested runtime archives.\n' >&2; exit 1; }
[[ "${#nuget_packages[@]}" == 1 && -f "$asset_directory/Sunder.Package.Agent.Protocol.$VERSION.nupkg" ]] \
  || { printf 'Expected one exact tested Agent Protocol archive.\n' >&2; exit 1; }
[[ -f "$native_evidence" ]] || { printf 'Native test evidence is missing.\n' >&2; exit 1; }

jq -e \
  --arg version "$VERSION" \
  --arg agent_commit "$GITHUB_SHA" \
  --arg core_tag "$CORE_TAG" \
  --arg core_commit "$CORE_COMMIT" \
  --arg core_version "$CORE_SDK_VERSION" '
    .schemaVersion == 1
    and .version == $version
    and .agentCommit == $agent_commit
    and .core == {tag: $core_tag, commit: $core_commit, packageVersion: $core_version}
    and ([.nativeRuns[].rid] | sort) == ["linux-arm64","linux-x64","osx-arm64","osx-x64","win-arm64","win-x64"]
    and all(.nativeRuns[]; .packages | length == 15)
    and (.finalArtifacts | length == 16)
  ' "$native_evidence" >/dev/null || {
    printf 'Native test evidence does not identify this Agent/Core release and six complete RID runs.\n' >&2
    exit 1
  }
while IFS=$'\t' read -r name expected_sha expected_size; do
  path="$asset_directory/$name"
  [[ -f "$path" \
     && "$(sha256sum "$path" | cut -d ' ' -f 1)" == "$expected_sha" \
     && "$(wc -c < "$path" | tr -d '[:space:]')" == "$expected_size" ]] \
    || { printf 'Tested final artifact differs from native fan-in evidence: %s\n' "$name" >&2; exit 1; }
done < <(jq -r '.finalArtifacts[] | [.path, .sha256, (.size | tostring)] | @tsv' "$native_evidence")
while IFS= read -r package_id; do
  [[ -f "$asset_directory/$package_id.$VERSION.sunderpkg" ]] \
    || { printf 'Missing runtime archive for %s.\n' "$package_id" >&2; exit 1; }
done < <(jq -r '.packages[] | select(.artifactType == "sunderpkg") | .packageId' packages.json)

primary_assets=("${runtime_packages[@]}" "${nuget_packages[@]}" "$native_evidence")
evidence_names=(SHA256SUMS release-provenance.json release-sbom.spdx.json toolchain.txt)
expected_release_names=()
for asset in "${primary_assets[@]}"; do expected_release_names+=("$(basename "$asset")"); done
expected_release_names+=("${evidence_names[@]}")
tag="agent/v$VERSION"
release_notes="$(printf '%s\n\nAgent source commit: %s\nCore source commit: %s\nCore SDK release: %s\nCore SDK package version: %s\n\n%s' \
  "Coordinated Agent Protocol and 15-package native runtime family release for $VERSION." \
  "$GITHUB_SHA" "$CORE_COMMIT" "$CORE_TAG" "$CORE_SDK_VERSION" \
  'Recovery preserves and byte-verifies the same native-tested artifacts and source/package provenance.')"
created_draft=false
external_publication_started=false
existing_published_release=false

cleanup_failed_draft() {
  exit_code=$?
  if [[ $exit_code -ne 0 ]]; then
    if [[ "$created_draft" == true && "$external_publication_started" != true ]] \
       && gh release view "$tag" >/dev/null 2>&1 \
       && [[ "$(gh release view "$tag" --json isDraft --jq .isDraft)" == true ]]; then
      gh release delete "$tag" --yes || true
    elif gh release view "$tag" >/dev/null 2>&1; then
      printf 'Preserving release %s because external publication may have started; rerun the same tag to recover.\n' "$tag" >&2
    fi
  fi
  exit "$exit_code"
}
trap cleanup_failed_draft EXIT

verify_release_sources() {
  local release_json="$1"
  jq -e \
    --arg agent_commit "$GITHUB_SHA" \
    --arg core_commit "$CORE_COMMIT" \
    --arg core_tag "$CORE_TAG" \
    --arg core_version "$CORE_SDK_VERSION" '
      (.body | capture("(?m)^Agent source commit: (?<sha>[0-9a-f]{40})$").sha) == $agent_commit
      and (.body | capture("(?m)^Core source commit: (?<sha>[0-9a-f]{40})$").sha) == $core_commit
      and (.body | capture("(?m)^Core SDK release: (?<tag>sdk/v[^[:space:]]+)$").tag) == $core_tag
      and (.body | capture("(?m)^Core SDK package version: (?<version>[^[:space:]]+)$").version) == $core_version
    ' <<< "$release_json" >/dev/null \
    || { printf 'Release does not record exact Agent/Core/tag/package provenance.\n' >&2; exit 1; }
}

verify_release_inventory() {
  local release_json="$1"
  [[ "$(jq '.assets | length' <<< "$release_json")" == "${#expected_release_names[@]}" ]] \
    || { printf 'Release asset inventory must contain exactly %s files.\n' "${#expected_release_names[@]}" >&2; exit 1; }
  for expected_name in "${expected_release_names[@]}"; do
    [[ "$(jq --arg name "$expected_name" '[.assets[] | select(.name == $name)] | length' <<< "$release_json")" == 1 ]] \
      || { printf 'Release must contain exactly one %s.\n' "$expected_name" >&2; exit 1; }
  done
}

verify_provenance() {
  local provenance="$1"
  jq -e \
    --arg agent_commit "$GITHUB_SHA" \
    --arg core_commit "$CORE_COMMIT" \
    --arg core_tag "$CORE_TAG" \
    --arg core_version "$CORE_SDK_VERSION" \
    --argjson expected_artifacts "$((runtime_count + 2))" '
      .schemaVersion == 1
      and ([.sources[] | select(.name == "sunder-agent-package" and .sha == $agent_commit)] | length == 1)
      and ([.sources[] | select(.name == "sunder-core" and .sha == $core_commit)] | length == 1)
      and .materials == [{name: "sunder-core-sdk", tag: $core_tag, sha: $core_commit, packageVersion: $core_version}]
      and (.artifacts | length == $expected_artifacts)
    ' "$provenance" >/dev/null \
    || { printf 'Release provenance does not match exact Agent/Core native-tested inputs.\n' >&2; exit 1; }
}

if gh release view "$tag" >/dev/null 2>&1; then
  release_json="$(gh release view "$tag" --json isDraft,body,assets)"
  verify_release_sources "$release_json"
  verify_release_inventory "$release_json"
  [[ "$(jq -r '.isDraft' <<< "$release_json")" == true ]] || existing_published_release=true
  existing_assets="$RUNNER_TEMP/existing-release-assets"
  mkdir -p "$existing_assets"
  gh release download "$tag" --dir "$existing_assets"
  verify_provenance "$existing_assets/release-provenance.json"
  for asset in "${primary_assets[@]}"; do
    name="$(basename "$asset")"
    [[ -f "$existing_assets/$name" ]] || { printf 'Published release is missing %s.\n' "$name" >&2; exit 1; }
    expected_sha="$(sha256sum "$asset" | cut -d ' ' -f 1)"
    expected_size="$(wc -c < "$asset" | tr -d '[:space:]')"
    actual_sha="$(sha256sum "$existing_assets/$name" | cut -d ' ' -f 1)"
    evidence_sha="$(jq -er --arg name "$name" '[.artifacts[] | select(.path == $name)] | if length == 1 then .[0].sha256 else error("artifact evidence must be unique") end' "$existing_assets/release-provenance.json")"
    evidence_size="$(jq -er --arg name "$name" '[.artifacts[] | select(.path == $name)] | if length == 1 then .[0].size else error("artifact evidence must be unique") end' "$existing_assets/release-provenance.json")"
    [[ "$actual_sha" == "$expected_sha" && "$evidence_sha" == "$expected_sha" && "$evidence_size" == "$expected_size" ]] \
      || { printf "Published release asset '%s' differs from this verified build.\n" "$name" >&2; exit 1; }
  done
else
  SUNDER_CORE_TAG="$CORE_TAG" \
  SUNDER_CORE_COMMIT="$CORE_COMMIT" \
  SUNDER_CORE_PACKAGE_VERSION="$CORE_SDK_VERSION" \
    bash scripts/generate-release-evidence.sh "$asset_directory" \
      "sunder-agent-package=$GITHUB_SHA" "sunder-core=$CORE_COMMIT"
  verify_provenance "$asset_directory/release-provenance.json"
  release_assets=("$asset_directory"/*)
  [[ "${#release_assets[@]}" == "${#expected_release_names[@]}" ]] \
    || { printf 'Local release inventory is not exact.\n' >&2; exit 1; }
  create_args=(release create "$tag" --title "Sunder Agent Family $VERSION" --notes "$release_notes" --latest=false --draft)
  [[ "$IS_PRERELEASE" != true ]] || create_args+=(--prerelease)
  gh "${create_args[@]}"
  created_draft=true
  gh release upload "$tag" "${release_assets[@]}"
  release_json="$(gh release view "$tag" --json isDraft,body,assets)"
  verify_release_sources "$release_json"
  verify_release_inventory "$release_json"
fi

protocol_path="$asset_directory/Sunder.Package.Agent.Protocol.$VERSION.nupkg"
protocol_version_lower="${VERSION,,}"
protocol_url="$NUGET_FLAT_CONTAINER/sunder.package.agent.protocol/$protocol_version_lower/sunder.package.agent.protocol.$protocol_version_lower.nupkg"
verify_protocol() {
  local remote="$RUNNER_TEMP/Sunder.Package.Agent.Protocol.$VERSION.nupkg"
  for _ in {1..30}; do
    if curl -fsSL "$protocol_url" -o "$remote" 2>/dev/null; then
      bash scripts/release/verify-nuget-package.sh "$protocol_path" "$remote"
      return
    fi
    sleep 10
  done
  printf 'NuGet.org did not expose Agent Protocol %s.\n' "$VERSION" >&2
  exit 1
}

external_publication_started=true
if ! dotnet nuget push "$protocol_path" --source "$NUGET_SOURCE" --api-key "$NUGET_API_KEY" --skip-duplicate; then
  verify_protocol
fi
verify_protocol

while IFS= read -r package_id; do
  env -u SUNDER_REGISTRY_CLI_TOKEN bash scripts/release/publish-registry-package.sh \
    "$registry_api" "$asset_directory/$package_id.$VERSION.sunderpkg" "$package_id" "$VERSION"
done < <(jq -r '.packages[] | select(.artifactType == "sunderpkg") | .packageId' packages.json)

package_ids=()
while IFS= read -r package_id; do package_ids+=("$package_id"); done \
  < <(jq -r '.packages[] | select(.artifactType == "sunderpkg") | .packageId' packages.json)
env -u SUNDER_REGISTRY_PUBLISH_TOKEN bash scripts/release/promote-family-dist-tag.sh \
  "$registry_api" "$DIST_TAG" "$VERSION" "${package_ids[@]}"

release_json="$(gh release view "$tag" --json isDraft,body,assets)"
verify_release_sources "$release_json"
verify_release_inventory "$release_json"
if [[ "$existing_published_release" == true ]]; then
  [[ "$(jq -r '.isDraft' <<< "$release_json")" == false ]] \
    || { printf 'Existing published Agent release unexpectedly became a draft.\n' >&2; exit 1; }
else
  [[ "$(jq -r '.isDraft' <<< "$release_json")" == true ]] \
    || { printf 'Agent release must remain draft through external publication.\n' >&2; exit 1; }
  gh release edit "$tag" --draft=false
fi
trap - EXIT
