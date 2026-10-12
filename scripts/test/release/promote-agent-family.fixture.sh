#!/usr/bin/env bash
set -euo pipefail
export LC_ALL=C

fixture_scripts="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
script_dir="$(cd "$fixture_scripts/../../release" && pwd)"
repository_root="$(cd "$script_dir/../.." && pwd)"
orchestrator="$script_dir/promote-agent-family.sh"
acceptance_verifier="$script_dir/verify-acceptance-artifact.sh"
acceptance_uploader="$script_dir/upload-agent-acceptance.sh"
acceptance_downloader="$script_dir/download-agent-acceptance.sh"
publication_verifier="$script_dir/verify-agent-publication.sh"
publication_handoff_generator="$script_dir/generate-agent-publication-handoff.sh"
native_evidence_verifier="$script_dir/verify-native-family-evidence.sh"
output_auditor="$script_dir/generate-package-output-audit.sh"
[[ -f "$orchestrator" ]] || { printf 'Agent family promotion orchestrator not found.\n' >&2; exit 1; }
[[ -f "$acceptance_verifier" && -f "$acceptance_uploader" && -f "$acceptance_downloader" \
    && -f "$publication_verifier" && -f "$publication_handoff_generator" ]] \
  || { printf 'Acceptance release scripts are missing.\n' >&2; exit 1; }
[[ -f "$native_evidence_verifier" ]] || { printf 'Native family evidence verifier not found.\n' >&2; exit 1; }
[[ -f "$output_auditor" ]] || { printf 'Package output auditor not found.\n' >&2; exit 1; }

for required_command in cp cut grep jq mktemp mv sort tr unzip wc zip; do
  command -v "$required_command" >/dev/null 2>&1 \
    || { printf '%s is required to run the Agent promotion fixture.\n' "$required_command" >&2; exit 127; }
done
if ! command -v sha256sum >/dev/null 2>&1 && ! command -v shasum >/dev/null 2>&1; then
  printf 'sha256sum or shasum is required to run the Agent promotion fixture.\n' >&2
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

temp_root="${TMPDIR:-/tmp}"
fixture_root="$(mktemp -d "${temp_root%/}/sunder-agent-family-promotion.XXXXXX")"
trap 'rm -rf "$fixture_root"' EXIT
fixture_repository="$fixture_root/repository"
release_source="$fixture_root/release-source"
mock_bin="$fixture_root/mock-bin"
asset_source="$fixture_root/release-assets"
mkdir -p "$fixture_repository/scripts/release" "$release_source/scripts/release" "$mock_bin" "$asset_source"
cp "$orchestrator" "$fixture_repository/scripts/release/promote-agent-family.sh"
cp "$acceptance_verifier" "$fixture_repository/scripts/release/verify-acceptance-artifact.sh"
cp "$acceptance_uploader" "$fixture_repository/scripts/release/upload-agent-acceptance.sh"
cp "$acceptance_downloader" "$fixture_repository/scripts/release/download-agent-acceptance.sh"
cp "$publication_verifier" "$fixture_repository/scripts/release/verify-agent-publication.sh"
cp "$script_dir/agent_release.py" "$script_dir/agent_acceptance.py" "$fixture_repository/scripts/release/"
cp "$script_dir/agent_cohort.py" "$fixture_repository/scripts/release/agent_cohort_contract.py"
cp "$native_evidence_verifier" "$script_dir/native-family.py" "$script_dir/native_release_inputs.py" \
  "$fixture_repository/scripts/release/"
cp "$output_auditor" "$fixture_repository/scripts/release/generate-package-output-audit.sh"
cp "$repository_root/packages.json" "$release_source/packages.json"
jq -e '.packages | length == 15 and all(.[]; .artifactType == "sunderpkg" and (has("appProjectPath") | not)) and ([.[].packageId] | unique | length) == 15' \
  "$release_source/packages.json" >/dev/null
cat > "$release_source/scripts/release/promote-agent-family.sh" <<'MALICIOUS_TAG_POLICY'
#!/usr/bin/env bash
printf 'TAG_POLICY_EXECUTED\n' >> "$MOCK_EVENT_LOG"
exit 99
MALICIOUS_TAG_POLICY

export MOCK_ACCEPTANCE_ARTIFACT_DIGEST=sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
export MOCK_ACCEPTANCE_ARTIFACT_ID=7001
export MOCK_ACCEPTANCE_ATTEMPT=2
export MOCK_ACCEPTANCE_RUN_ID=9001
export MOCK_ACCEPTANCE_ARTIFACT="sunder-agent-production-acceptance-$MOCK_ACCEPTANCE_RUN_ID-$MOCK_ACCEPTANCE_ATTEMPT"
export MOCK_ACCEPTANCE_WORKFLOW='Sunder Agent Production Acceptance'
export MOCK_ACCEPTANCE_WORKFLOW_PATH=.github/workflows/sunder-agent-production-acceptance.yml
export MOCK_ACCEPTANCE_WORKFLOW_SHA=4444444444444444444444444444444444444444
export MOCK_DRAFT_STATE="$fixture_root/draft-state"
export MOCK_EVENT_LOG="$fixture_root/events.log"
export MOCK_EXPECTED_CLI_TOKEN=sunder_cli_0123456789012345678901234567890123456789012
export MOCK_GH_REPO=Younics/sunder-agent-package
export MOCK_PROMOTED_STATE="$fixture_root/promoted-state"
export MOCK_RELEASE_CALL_COUNT="$fixture_root/release-call-count"
export MOCK_RELEASE_ASSETS="$asset_source"
export MOCK_RELEASE_ID=424242
export MOCK_RELEASE_SOURCE="$release_source"
export MOCK_POLICY_ROOT="$fixture_repository"
export MOCK_POLICY_SHA=5555555555555555555555555555555555555555
export MOCK_PUBLICATION_ATTEMPT=2
export MOCK_PUBLICATION_RUN_ID=8001
export MOCK_SOURCE_SHA=1111111111111111111111111111111111111111
export MOCK_TAG=agent/v2.0.0
export MOCK_TAG_CALL_COUNT="$fixture_root/tag-call-count"
export MOCK_VERSION=2.0.0
export MOCK_RELEASE_FIXTURE_TOOL="$fixture_scripts/agent-release.fixture.py"
core_sha=2222222222222222222222222222222222222222
acceptance_asset_name="agent-family-acceptance-$MOCK_ACCEPTANCE_RUN_ID-$MOCK_ACCEPTANCE_ATTEMPT.json"
coordinator_sha=cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc
native_driver_sha=dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd

package_ids=()
while IFS= read -r package_id; do package_ids+=("$package_id"); done \
  < <(jq -r '.packages | map(select(.artifactType == "sunderpkg")) | sort_by(.packageId)[] | .packageId' "$release_source/packages.json")
[[ "${#package_ids[@]}" -eq 15 ]]
runtime_names=()
while IFS=$'\t' read -r package_id project_path; do
  name="$package_id.$MOCK_VERSION.sunderpkg"
  runtime_names+=("$name")
  assembly="$(basename "$project_path" .csproj)"
  staging="$fixture_root/archive-$package_id"
  mkdir -p "$staging/manifest"
  runtime_targets="$(printf '%s\n' win-x64 win-arm64 linux-x64 linux-arm64 osx-x64 osx-arm64 | jq -Rsc \
    --arg assembly "$assembly" '
      split("\n")[:-1] | map({
        role: "runtime",
        rid: .,
        kind: "worker",
        entryPoint: ("lib/" + $assembly + ".dll"),
        targetFramework: "net10.0",
        sdkVersion: "1.1.0+fixture.native",
        executionRuntime: {
          id: "sunder.runtime.dotnet",
          versionRange: ">=10.0.0 <11.0.0",
          moduleAbi: "sunder.dotnet-worker-module.v1"
        },
        requiredHostCapabilities: [
          "execution-runtime.v1", "rpc.v1", "sdk-baseline-1-1.v1", "worker-protocol.v1"
        ]
      })
    ')"
  jq -n \
    --arg package_id "$package_id" \
    --arg version "$MOCK_VERSION" \
    --argjson targets "$runtime_targets" '
      {
        archiveFormatVersion: 1,
        manifestVersion: 1,
        id: $package_id,
        version: $version,
        targets: $targets,
        contractBundles: []
      }
    ' > "$staging/manifest/sunder-package.json"
  for rid in win-x64 win-arm64 linux-x64 linux-arm64 osx-x64 osx-arm64; do
    payload="$staging/payload/runtime/$rid/lib"
    mkdir -p "$payload"
    printf 'managed fixture module for %s/%s\n' "$package_id" "$rid" > "$payload/$assembly.dll"
    printf '{"fixture":"dependencies"}\n' > "$payload/$assembly.deps.json"
    jq -n '{runtimeOptions:{tfm:"net10.0",framework:{name:"Microsoft.NETCore.App",version:"10.0.0"}}}' \
      > "$payload/$assembly.runtimeconfig.json"
  done
  indexed_files='[]'
  while IFS= read -r relative_path; do
    indexed_files="$(jq -cn \
      --argjson current "$indexed_files" \
      --arg path "$relative_path" \
      --arg sha "$(hash_file "$staging/$relative_path")" \
      --argjson size "$(file_size "$staging/$relative_path")" \
      '$current + [{path: $path, sha256: $sha, size: $size}]')"
  done < <(find "$staging/manifest" "$staging/payload" -type f ! -name content-index.json -print \
    | sed "s#^$staging/##" | sort)
  jq -n --argjson files "$indexed_files" '{schemaVersion: 1, files: $files}' \
    > "$staging/manifest/content-index.json"
  (
    cd "$staging"
    find manifest payload -type f -print | sort | zip -q -X "$asset_source/$name" -@
  )
done < <(jq -r '.packages | map(select(.artifactType == "sunderpkg")) | sort_by(.packageId)[] | [.packageId, .projectPath] | @tsv' "$release_source/packages.json")
final_artifacts="$fixture_root/final-artifacts.json"
printf '[]\n' > "$final_artifacts"
for name in "${runtime_names[@]}"; do
  jq \
    --arg path "$name" \
    --arg sha "$(hash_file "$asset_source/$name")" \
    --argjson size "$(file_size "$asset_source/$name")" \
    '. + [{path: $path, sha256: $sha, size: $size}]' \
    "$final_artifacts" > "$final_artifacts.next"
  mv "$final_artifacts.next" "$final_artifacts"
done
jq -e 'length == 15 and all(.[]; .path | endswith(".sunderpkg"))' "$final_artifacts" >/dev/null
# Construct fresh qualification/metric/leaf bytes, capture all six bundles through
# the frozen native owner implementation, and use its real schema-3 verifier.
python3 -B "$fixture_scripts/native-promotion.fixture.py" "$release_source" "$asset_source" \
  "$fixture_root/native-capture" "$MOCK_SOURCE_SHA" "$core_sha" > "$fixture_root/native-capture.log"
bash "$fixture_repository/scripts/release/generate-package-output-audit.sh" \
  "$asset_source" "$asset_source/native-test-evidence.json" "$release_source" \
  "$asset_source/package-output-audit.json" >/dev/null
export MOCK_CANONICAL_OUTPUT_AUDIT="$asset_source/package-output-audit.json"
cat > "$fixture_repository/scripts/release/generate-package-output-audit.sh" <<'MOCK_OUTPUT_AUDITOR'
#!/usr/bin/env bash
set -euo pipefail
[[ $# -eq 4 ]]
cp "$MOCK_CANONICAL_OUTPUT_AUDIT" "$4"
MOCK_OUTPUT_AUDITOR

cat > "$fixture_repository/scripts/release/verify-agent-publication.sh" <<'MOCK_PUBLICATION_VERIFY'
#!/usr/bin/env bash
set -euo pipefail
[[ $# -eq 7 ]]
[[ "$1" == "$MOCK_RELEASE_ID" && "$2" == "$MOCK_TAG" && "$3" == "$MOCK_SOURCE_SHA" ]]
[[ "$4" == "$MOCK_PUBLICATION_RUN_ID" && "$5" == "$MOCK_PUBLICATION_ATTEMPT" ]]
[[ "$6" == "$MOCK_RELEASE_SOURCE" && "$7" == "$MOCK_RELEASE_ASSETS" ]]
[[ "${SUNDER_AGENT_PUBLICATION_EXPECTED_DRAFT:-}" == true \
    || "${SUNDER_AGENT_PUBLICATION_EXPECTED_DRAFT:-}" == false \
    || "${SUNDER_AGENT_PUBLICATION_EXPECTED_DRAFT:-}" == any ]]
printf 'PUBLICATION_VERIFY\n' >> "$MOCK_EVENT_LOG"
MOCK_PUBLICATION_VERIFY

primary_names=("${runtime_names[@]}" native-test-evidence.json package-output-audit.json)
python3 -B "$MOCK_RELEASE_FIXTURE_TOOL" prepare "$release_source" "$asset_source"
[[ "${#primary_names[@]}" -eq 17 ]]
primary_artifacts="$fixture_root/primary-artifacts.json"
printf '[]\n' > "$primary_artifacts"
: > "$asset_source/SHA256SUMS"
sbom_files="$fixture_root/sbom-files.json"
printf '[]\n' > "$sbom_files"
for name in "${primary_names[@]}"; do
  sha="$(hash_file "$asset_source/$name")"
  size="$(file_size "$asset_source/$name")"
  printf '%s  %s\n' "$sha" "$name" >> "$asset_source/SHA256SUMS"
  jq --arg path "$name" --arg sha "$sha" --argjson size "$size" \
    '. + [{path: $path, sha256: $sha, size: $size}]' \
    "$primary_artifacts" > "$primary_artifacts.next"
  mv "$primary_artifacts.next" "$primary_artifacts"
  jq --arg name "./$name" --arg sha "$sha" \
    '. + [{fileName: $name, checksums: [{algorithm: "SHA256", checksumValue: $sha}]}]' \
    "$sbom_files" > "$sbom_files.next"
  mv "$sbom_files.next" "$sbom_files"
done
jq -n \
  --arg agent "$MOCK_SOURCE_SHA" \
  --arg core "$core_sha" \
  --arg repository "$MOCK_GH_REPO" \
  --arg tag "$MOCK_TAG" \
  --argjson artifacts "$(< "$primary_artifacts")" '
    {
      schemaVersion: 1,
      buildType: "https://sunderapp.io/provenance/github-actions/v1",
      invocation: {
        repository:$repository,
        workflow:"Sunder Agent Family Release",
        workflowRef:($repository + "/.github/workflows/sunder-package-release.yml@refs/tags/" + $tag),
        runId:"8001",runAttempt:"1",releaseId:424242,
        ref:("refs/tags/" + $tag),refName:$tag,sha:$agent
      },
      sources: [{name: "sunder-agent-package", sha: $agent}, {name: "sunder-core", sha: $core}],
      materials: [{name: "sunder-core-sdk", tag: "sdk/v1.1.0", sha: $core, packageVersion: "1.1.0"}],
      artifacts: $artifacts
    }
  ' > "$asset_source/release-provenance.json"
python3 -B "$MOCK_RELEASE_FIXTURE_TOOL" complete "$release_source" "$asset_source"
jq -n --argjson files "$(< "$sbom_files")" '{spdxVersion: "SPDX-2.3", files: $files}' \
  > "$asset_source/release-sbom.spdx.json"
printf 'dotnet-version: 10.0.100\nrunner-os: fixture\n' > "$asset_source/toolchain.txt"
handoff_assets='[]'
for asset in "$asset_source"/*; do
  handoff_assets="$(jq -cn \
    --argjson assets "$handoff_assets" \
    --arg path "$(basename "$asset")" \
    --arg sha256 "$(hash_file "$asset")" \
    --argjson size "$(file_size "$asset")" \
    '$assets + [{path:$path,sha256:$sha256,size:$size}]')"
done
verified_release_assets_sha256="$(jq -cS 'sort_by(.path)' <<< "$handoff_assets" | hash_stream)"
export MOCK_HANDOFF_RELEASE="$fixture_root/handoff-release.json"
python3 -B "$MOCK_RELEASE_FIXTURE_TOOL" release "$release_source" "$asset_source" \
  "$MOCK_HANDOFF_RELEASE" "$MOCK_RELEASE_ID" "$MOCK_TAG"
cat > "$mock_bin/gh" <<'MOCK_HANDOFF_GH'
#!/usr/bin/env bash
set -euo pipefail
[[ "$*" == 'api repos/Younics/sunder-agent-package/releases/424242' ]] || exit 99
command cat "$MOCK_HANDOFF_RELEASE"
MOCK_HANDOFF_GH
chmod +x "$mock_bin/gh"
PATH="$mock_bin:$PATH" \
SUNDER_AGENT_RELEASE_SOURCE_DIRECTORY="$release_source" \
GITHUB_REPOSITORY="$MOCK_GH_REPO" \
GITHUB_WORKFLOW='Sunder Agent Family Release' \
GITHUB_WORKFLOW_REF="$MOCK_GH_REPO/.github/workflows/sunder-package-release.yml@refs/tags/$MOCK_TAG" \
GITHUB_RUN_ID="$MOCK_PUBLICATION_RUN_ID" \
GITHUB_RUN_ATTEMPT="$MOCK_PUBLICATION_ATTEMPT" \
GITHUB_REF="refs/tags/$MOCK_TAG" \
GITHUB_REF_NAME="$MOCK_TAG" \
GITHUB_SHA="$MOCK_SOURCE_SHA" \
  bash "$publication_handoff_generator" "$asset_source" "$MOCK_RELEASE_ID" "$MOCK_TAG" \
    "$MOCK_SOURCE_SHA" 1 "$verified_release_assets_sha256" >/dev/null

evidence_names=(SHA256SUMS release-provenance.json release-sbom.spdx.json toolchain.txt)
release_names=("${primary_names[@]}" "${evidence_names[@]}")
[[ "${#release_names[@]}" -eq 21 ]]
accepted_assets="$fixture_root/accepted-assets.json"
printf '[]\n' > "$accepted_assets"
release_assets="$fixture_root/release-assets.json"
printf '[]\n' > "$release_assets"
asset_id=1000
for name in "${release_names[@]}"; do
  sha="$(hash_file "$asset_source/$name")"
  size="$(file_size "$asset_source/$name")"
  asset_id=$((asset_id + 1))
  jq --arg name "$name" --arg sha "$sha" --argjson id "$asset_id" --argjson size "$size" \
    '. + [{id: $id, name: $name, sha256: $sha, size: $size}]' \
    "$accepted_assets" > "$accepted_assets.next"
  mv "$accepted_assets.next" "$accepted_assets"
  jq \
    --arg digest "sha256:$sha" \
    --arg name "$name" \
    --arg url "https://api.github.com/repos/$MOCK_GH_REPO/releases/assets/$asset_id" \
    --argjson id "$asset_id" \
    --argjson size "$size" \
    '. + [{id: $id, name: $name, size: $size, digest: $digest, url: $url}]' \
    "$release_assets" > "$release_assets.next"
  mv "$release_assets.next" "$release_assets"
done

registry_packages="$fixture_root/registry-packages.json"
printf '[]\n' > "$registry_packages"
for package_id in "${package_ids[@]}"; do
  jq \
    --arg package_id "$package_id" \
    --arg version "$MOCK_VERSION" \
    --arg sha "$(hash_file "$asset_source/$package_id.$MOCK_VERSION.sunderpkg")" \
    '. + [{packageId: $package_id, version: $version, canonicalSha256: $sha}]' \
    "$registry_packages" > "$registry_packages.next"
  mv "$registry_packages.next" "$registry_packages"
done

release_body="$(printf '%s\n\nAgent source commit: %s\nCore source commit: %s\nCore SDK release: sdk/v1.1.0\nCore SDK package version: 1.1.0' \
  "Fixture Agent family release" "$MOCK_SOURCE_SHA" "$core_sha")"
release_title="Sunder Agent Family $MOCK_VERSION"
jq -n \
  --arg body "$release_body" \
  --arg name "$release_title" \
  --arg tag "$MOCK_TAG" \
  --argjson assets "$(< "$release_assets")" \
  --argjson id "$MOCK_RELEASE_ID" \
  '{id: $id, tag_name: $tag, name: $name, draft: true, prerelease: false, body: $body, assets: $assets}' \
  > "$fixture_root/release-template.json"
prior_acceptance_name='agent-family-acceptance-8999-1.json'
printf '{"decision":"incomplete prior attempt"}\n' > "$asset_source/$prior_acceptance_name"
jq \
  --arg digest "sha256:$(hash_file "$asset_source/$prior_acceptance_name")" \
  --arg name "$prior_acceptance_name" \
  --arg url "https://api.github.com/repos/$MOCK_GH_REPO/releases/assets/2000" \
  --argjson size "$(file_size "$asset_source/$prior_acceptance_name")" \
  '.assets += [{id:2000,name:$name,size:$size,state:"uploaded",digest:$digest,url:$url}]' \
  "$fixture_root/release-template.json" > "$fixture_root/release-template.next.json"
mv "$fixture_root/release-template.next.json" "$fixture_root/release-template.json"
export MOCK_RELEASE_JSON="$fixture_root/release.json"
export MOCK_RELEASE_TEMPLATE_JSON="$fixture_root/release-template.json"

acceptance="$fixture_root/agent-family-acceptance.json"
native_receipts='[]'
for rid in win-x64 win-arm64 linux-x64 linux-arm64 osx-x64 osx-arm64; do
  native_receipts="$(jq -cn \
    --argjson current "$native_receipts" \
    --arg driver_sha "$native_driver_sha" \
    --arg rid "$rid" \
    --arg sha "$(printf '%s' "$rid-native-receipt" | hash_stream)" \
    '$current + [{rid:$rid,sha256:$sha,driverSha256:$driver_sha}]')"
done
jq -n \
  --arg artifact "$MOCK_ACCEPTANCE_ARTIFACT" \
  --arg attempt "$MOCK_ACCEPTANCE_ATTEMPT" \
  --arg acceptance_workflow_sha "$MOCK_ACCEPTANCE_WORKFLOW_SHA" \
  --arg repository "$MOCK_GH_REPO" \
  --arg run_id "$MOCK_ACCEPTANCE_RUN_ID" \
  --arg source "$MOCK_SOURCE_SHA" \
  --arg tag "$MOCK_TAG" \
  --arg version "$MOCK_VERSION" \
  --arg workflow "$MOCK_ACCEPTANCE_WORKFLOW" \
  --arg workflow_path "$MOCK_ACCEPTANCE_WORKFLOW_PATH" \
  --arg coordinator_sha "$coordinator_sha" \
  --arg provenance_sha "$(hash_file "$asset_source/release-provenance.json")" \
  --arg body "$release_body" \
  --arg body_sha "$(printf '%s' "$release_body" | hash_stream)" \
  --arg title "$release_title" \
  --argjson assets "$(< "$accepted_assets")" \
  --argjson native_receipts "$native_receipts" \
  --argjson packages "$(< "$registry_packages")" \
  --argjson release_id "$MOCK_RELEASE_ID" '
    {
      schemaVersion: 3,
      decision: "accepted",
      repository: $repository,
      tag: $tag,
      version: $version,
      sourceCommit: $source,
      publicationRunId: "8001",
      publicationRunAttempt: "2",
      publicationWorkflow: {
        name: "Sunder Agent Family Release",
        path: ".github/workflows/sunder-package-release.yml",
        event: "push",
        artifactName: "sunder-agent-publication-evidence-424242-2"
      },
      acceptanceRunId: $run_id,
      acceptanceRunAttempt: $attempt,
      acceptanceWorkflow: {
        name: $workflow, path: $workflow_path, event: "workflow_dispatch",
        artifactName: $artifact, coordinatorSha256: $coordinator_sha,
        workflowRef: "refs/heads/main", workflowSha: $acceptance_workflow_sha
      },
      acceptedAtUtc: "2026-08-24T12:00:00Z",
      draftRelease: {
        id: $release_id,
        draft: true,
        title: $title,
        body: $body,
        bodySha256: $body_sha,
        provenanceSha256: $provenance_sha,
        assets: $assets
      },
      nativeReceipts: $native_receipts,
      registryPackages: $packages
    }
  ' > "$acceptance"
python3 -B "$MOCK_RELEASE_FIXTURE_TOOL" acceptance "$release_source" "$asset_source" "$acceptance"
cp "$acceptance" "$fixture_root/post-acceptance-template.json"
export MOCK_COHORT_RESULT="$fixture_root/cohort-result.json"
jq '.cohort.result' "$acceptance" > "$MOCK_COHORT_RESULT"
acceptance_sha="$(hash_file "$acceptance")"
acceptance_asset_id=2001
cp "$acceptance" "$asset_source/agent-family-acceptance.json"
cp "$acceptance" "$asset_source/$acceptance_asset_name"
jq \
  --arg digest "sha256:$acceptance_sha" \
  --arg name "$acceptance_asset_name" \
  --arg url "https://api.github.com/repos/$MOCK_GH_REPO/releases/assets/$acceptance_asset_id" \
  --argjson id "$acceptance_asset_id" \
  --argjson size "$(file_size "$acceptance")" \
  '.assets += [{id:$id,name:$name,size:$size,state:"uploaded",digest:$digest,url:$url}]' \
  "$fixture_root/release-template.json" > "$fixture_root/release-template.next.json"
mv "$fixture_root/release-template.next.json" "$fixture_root/release-template.json"
cp "$fixture_root/release-template.json" "$fixture_root/release.json"
fixture_receipt_json="$(jq -cnS \
  --arg artifact_name "$MOCK_ACCEPTANCE_ARTIFACT" \
  --arg attempt "$MOCK_ACCEPTANCE_ATTEMPT" \
  --arg acceptance_workflow_sha "$MOCK_ACCEPTANCE_WORKFLOW_SHA" \
  --arg body_sha "$(printf '%s' "$release_body" | hash_stream)" \
  --arg dist_tag latest \
  --arg event workflow_dispatch \
  --arg evidence_sha "$acceptance_sha" \
  --arg name "$MOCK_ACCEPTANCE_WORKFLOW" \
  --arg path "$MOCK_ACCEPTANCE_WORKFLOW_PATH" \
  --arg release_asset_name "$acceptance_asset_name" \
  --arg repository "$MOCK_GH_REPO" \
  --arg run_id "$MOCK_ACCEPTANCE_RUN_ID" \
  --arg source "$MOCK_SOURCE_SHA" \
  --arg tag "$MOCK_TAG" \
  --arg version "$MOCK_VERSION" \
  --arg policy_sha "$MOCK_POLICY_SHA" \
  --argjson release_id "$MOCK_RELEASE_ID" '
    {
      schemaVersion: 2,
      releaseId: $release_id,
      tag: $tag,
      sourceCommit: $source,
      version: $version,
      distTag: $dist_tag,
      acceptedBodySha256: $body_sha,
      publication: {
        workflow: {
          repository: $repository,
          name: "Sunder Agent Family Release",
          path: ".github/workflows/sunder-package-release.yml",
          event: "push",
          artifactName: "sunder-agent-publication-evidence-424242-2"
        },
        runId: "8001",
        runAttempt: "2"
      },
      acceptance: {
        workflow: {
          repository: $repository,
          name: $name,
          path: $path,
          event: $event,
          artifactName: $artifact_name,
          workflowRef: "refs/heads/main",
          workflowSha: $acceptance_workflow_sha
        },
        runId: $run_id,
        runAttempt: $attempt,
        releaseAssetName: $release_asset_name,
        evidenceSha256: $evidence_sha
      },
      promotionPolicy: {
        workflowPath: ".github/workflows/sunder-agent-promotion.yml",
        workflowRef: "refs/heads/main",
        workflowSha: $policy_sha
      }
    }
  ')"
export MOCK_EXPECTED_FINALIZED_BODY="$fixture_root/expected-finalized-body.txt"
jq -jr --arg receipt "$fixture_receipt_json" \
  '.draftRelease.body + "\n\n<!-- sunder-agent-promotion-receipt:v2 " + $receipt + " -->"' \
  "$acceptance" > "$MOCK_EXPECTED_FINALIZED_BODY"

cat > "$mock_bin/git" <<'MOCK_GIT'
#!/usr/bin/env bash
set -euo pipefail
if [[ "$*" == "-C $MOCK_POLICY_ROOT rev-parse HEAD" ]]; then
  printf '%s\n' "$MOCK_POLICY_SHA"
  exit 0
fi
if [[ "$*" == "-C $MOCK_RELEASE_SOURCE rev-parse HEAD" ]]; then
  printf '%s\n' "$MOCK_SOURCE_SHA"
  exit 0
fi
if [[ "$*" == "-C $MOCK_RELEASE_SOURCE rev-list --parents -n 1 6666666666666666666666666666666666666666" ]]; then
  printf '6666666666666666666666666666666666666666 %s\n' "$MOCK_SOURCE_SHA"
  exit 0
fi
if [[ "$1" == -C && "$2" == "$MOCK_RELEASE_SOURCE" && "$3" == diff-tree ]]; then
  printf ':100644 100644 aaa bbb M\tsrc/Sunder.Package.Agent/sunder-contracts.owned.json\n'
  exit 0
fi
if [[ "$*" == "-C $MOCK_RELEASE_SOURCE diff --quiet HEAD -- packages.json src" ]]; then exit 0; fi
if [[ "$1" == -C && "$2" == "$MOCK_RELEASE_SOURCE" && "$3" == ls-tree ]]; then
  [[ ! -f "$MOCK_RELEASE_SOURCE/${7}" ]] || printf 'blob 100644\n'
  exit 0
fi
if [[ "$1" == -C && "$2" == "$MOCK_RELEASE_SOURCE" && "$3" == show && "$4" == "$MOCK_SOURCE_SHA":* ]]; then
  command cat "$MOCK_RELEASE_SOURCE/${4#*:}"
  exit 0
fi
if [[ "$1" == -C && "$2" == "$MOCK_RELEASE_SOURCE" && "$3" == show && "$4" == 6666666666666666666666666666666666666666:* ]]; then
  python3 -B "$MOCK_RELEASE_FIXTURE_TOOL" evidence-git "$MOCK_RELEASE_SOURCE" "$MOCK_RELEASE_ASSETS" "${4#*:}"
  exit 0
fi
printf 'Mock git does not support: %s\n' "$*" >&2
exit 2
MOCK_GIT

cat > "$mock_bin/gh" <<'MOCK_GH'
#!/usr/bin/env bash
set -euo pipefail
[[ "${1:-}" == api ]] || { printf 'Only gh api is allowed in promotion.\n' >&2; exit 2; }
shift
method=GET
input=''
endpoint=''
while [[ $# -gt 0 ]]; do
  case "$1" in
    --method)
      method="$2"
      shift 2
      ;;
    --input)
      input="$2"
      shift 2
      ;;
    -H)
      shift 2
      ;;
    --paginate|--slurp)
      shift
      ;;
    *)
      [[ -z "$endpoint" ]] || { printf 'Unexpected gh api argument: %s\n' "$1" >&2; exit 2; }
      endpoint="$1"
      shift
      ;;
  esac
done

case "$endpoint" in
  "repos/$MOCK_GH_REPO/actions/runs/$MOCK_ACCEPTANCE_RUN_ID")
    printf 'ACCEPTANCE_RUN\n' >> "$MOCK_EVENT_LOG"
    jq -n \
      --arg conclusion "${MOCK_RUN_CONCLUSION:-success}" \
      --arg event "${MOCK_RUN_EVENT:-workflow_dispatch}" \
      --arg head "${MOCK_RUN_HEAD_SHA:-$MOCK_ACCEPTANCE_WORKFLOW_SHA}" \
      --arg name "${MOCK_WORKFLOW_NAME:-$MOCK_ACCEPTANCE_WORKFLOW}" \
      --arg path "${MOCK_WORKFLOW_PATH:-$MOCK_ACCEPTANCE_WORKFLOW_PATH}" \
      --arg repository "${MOCK_RUN_REPOSITORY:-$MOCK_GH_REPO}" \
      --arg status "${MOCK_RUN_STATUS:-completed}" \
      --argjson attempt "$MOCK_ACCEPTANCE_ATTEMPT" \
      --argjson id "${MOCK_RUN_ID_RESULT:-$MOCK_ACCEPTANCE_RUN_ID}" \
      '{id:$id,run_attempt:$attempt,name:$name,path:$path,event:$event,head_branch:"main",head_sha:$head,
        repository:{id:1234,full_name:$repository},status:$status,conclusion:$conclusion}'
    ;;
  "repos/$MOCK_GH_REPO/actions/runs/$MOCK_ACCEPTANCE_RUN_ID/attempts/$MOCK_ACCEPTANCE_ATTEMPT")
    printf 'ATTEMPT\n' >> "$MOCK_EVENT_LOG"
    jq -n \
      --arg conclusion "${MOCK_RUN_CONCLUSION:-success}" \
      --arg event "${MOCK_RUN_EVENT:-workflow_dispatch}" \
      --arg head "${MOCK_RUN_HEAD_SHA:-$MOCK_ACCEPTANCE_WORKFLOW_SHA}" \
      --arg name "${MOCK_WORKFLOW_NAME:-$MOCK_ACCEPTANCE_WORKFLOW}" \
      --arg path "${MOCK_WORKFLOW_PATH:-$MOCK_ACCEPTANCE_WORKFLOW_PATH}" \
      --arg repository "${MOCK_RUN_REPOSITORY:-$MOCK_GH_REPO}" \
      --arg status "${MOCK_RUN_STATUS:-completed}" \
      --argjson attempt "${MOCK_RUN_ATTEMPT_RESULT:-$MOCK_ACCEPTANCE_ATTEMPT}" \
      --argjson id "${MOCK_RUN_ID_RESULT:-$MOCK_ACCEPTANCE_RUN_ID}" \
       '{id: $id, run_attempt: $attempt, name: $name, path: $path, event: $event,
         repository: {id: 1234, full_name: $repository}, head_branch:"main",
         head_sha: $head, status: $status, conclusion: $conclusion}'
    ;;
  "repos/$MOCK_GH_REPO/actions/artifacts/"*)
    printf 'ACCEPTANCE_ARTIFACT\n' >> "$MOCK_EVENT_LOG"
    jq -n \
      --arg created_at "${MOCK_ARTIFACT_CREATED_AT:-2026-08-24T12:05:00Z}" \
      --arg digest "${MOCK_ARTIFACT_DIGEST_RESULT:-$MOCK_ACCEPTANCE_ARTIFACT_DIGEST}" \
      --arg head "$MOCK_ACCEPTANCE_WORKFLOW_SHA" \
      --arg name "${MOCK_ARTIFACT_NAME:-$MOCK_ACCEPTANCE_ARTIFACT}" \
      --arg repository "$MOCK_GH_REPO" \
      --argjson artifact_id "${MOCK_ARTIFACT_ID_RESULT:-$MOCK_ACCEPTANCE_ARTIFACT_ID}" \
      --argjson run_id "$MOCK_ACCEPTANCE_RUN_ID" '
      {
        id: $artifact_id,
        name: $name,
        digest: $digest,
        expired: false,
        size_in_bytes: 4096,
        url: ("https://api.github.com/repos/" + $repository + "/actions/artifacts/" + ($artifact_id | tostring)),
        archive_download_url: ("https://api.github.com/repos/" + $repository + "/actions/artifacts/" + ($artifact_id | tostring) + "/zip"),
        created_at: $created_at,
        workflow_run: {id: $run_id, repository_id: 1234, head_sha: $head}
      }'
    ;;
  "repos/$MOCK_GH_REPO/actions/runs/$MOCK_ACCEPTANCE_RUN_ID/attempts/$MOCK_ACCEPTANCE_ATTEMPT/jobs?per_page=100")
    printf 'ATTEMPT_JOBS\n' >> "$MOCK_EVENT_LOG"
    jq -n \
      --argjson attempt "$MOCK_ACCEPTANCE_ATTEMPT" \
      --argjson run_id "$MOCK_ACCEPTANCE_RUN_ID" '
      [{total_count: 1, jobs: [{
        id: 8001,
        run_id: $run_id,
        run_attempt: $attempt,
        started_at: "2026-08-24T12:00:00Z",
        completed_at: "2026-08-24T12:10:00Z"
      }]}]'
    ;;
  "repos/$MOCK_GH_REPO/git/ref/tags/"*)
    printf 'TAG_REF\n' >> "$MOCK_EVENT_LOG"
    tag_call_count=$(( $(< "$MOCK_TAG_CALL_COUNT") + 1 ))
    printf '%s\n' "$tag_call_count" > "$MOCK_TAG_CALL_COUNT"
    tag_sha="${MOCK_TAG_SHA_RESULT:-$MOCK_SOURCE_SHA}"
    if [[ "${MOCK_TAG_DRIFT_ON_CALL:-}" == "$tag_call_count" ]]; then
      tag_sha=3333333333333333333333333333333333333333
    fi
    jq -n \
      --arg ref "refs/tags/$MOCK_TAG" \
      --arg sha "$tag_sha" \
      '{ref: $ref, object: {type: "commit", sha: $sha}}'
    ;;
  "repos/$MOCK_GH_REPO/releases/$MOCK_RELEASE_ID")
    if [[ "$method" == PATCH ]]; then
      [[ "$input" == - ]] || { printf 'Release PATCH must use JSON stdin.\n' >&2; exit 2; }
      payload="$(command cat)"
      jq -e --rawfile expected_body "$MOCK_EXPECTED_FINALIZED_BODY" '
        keys == ["body","draft"]
        and .draft == false
        and .body == $expected_body
      ' <<< "$payload" >/dev/null
      [[ "$(< "$MOCK_PROMOTED_STATE")" == true ]] \
        || { printf 'Release finalized before family promotion.\n' >&2; exit 2; }
      finalized_body="$(jq -er '.body' <<< "$payload")"
      jq --arg body "$finalized_body" '.draft = false | .body = $body' \
        "$MOCK_RELEASE_JSON" > "$MOCK_RELEASE_JSON.next"
      mv "$MOCK_RELEASE_JSON.next" "$MOCK_RELEASE_JSON"
      printf 'FINALIZE\n' >> "$MOCK_EVENT_LOG"
      printf 'false\n' > "$MOCK_DRAFT_STATE"
      if [[ "${MOCK_FAIL_AFTER_FINALIZE:-false}" == true ]]; then
        exit 75
      fi
    else
      printf 'RELEASE_GET\n' >> "$MOCK_EVENT_LOG"
      release_call_count=$(( $(< "$MOCK_RELEASE_CALL_COUNT") + 1 ))
      printf '%s\n' "$release_call_count" > "$MOCK_RELEASE_CALL_COUNT"
    fi
    jq \
      --arg body "${MOCK_RELEASE_BODY_RESULT:-}" \
      --arg title "${MOCK_RELEASE_TITLE_RESULT:-}" \
      --argjson draft "$(< "$MOCK_DRAFT_STATE")" \
      --argjson drift "$([[ "${MOCK_RELEASE_DRIFT_ON_CALL:-}" == "${release_call_count:-0}" ]] && printf true || printf false)" \
      --argjson id "${MOCK_RELEASE_ID_RESULT:-$MOCK_RELEASE_ID}" \
      '.draft = $draft
        | .id = $id
        | if $title != "" then .name = $title else . end
        | if $body != "" then .body = $body else . end
        | if $drift then .assets[0].digest = "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" else . end' \
      "$MOCK_RELEASE_JSON"
    ;;
  https://api.github.com/repos/*/releases/assets/*|"repos/$MOCK_GH_REPO/releases/assets/"*)
    asset_id="${endpoint##*/}"
    asset_name="$(jq -er --argjson id "$asset_id" '.assets[] | select(.id == $id) | .name' "$MOCK_RELEASE_JSON")"
    printf 'ASSET %s\n' "$asset_name" >> "$MOCK_EVENT_LOG"
    if [[ "${MOCK_TAMPER_ASSET:-}" == "$asset_name" ]]; then
      printf 'tampered asset bytes\n'
    else
      command cat "$MOCK_RELEASE_ASSETS/$asset_name"
    fi
    ;;
  *)
    printf 'Unsupported mock gh endpoint: %s %s\n' "$method" "$endpoint" >&2
    exit 2
    ;;
esac
MOCK_GH

cat > "$mock_bin/curl" <<'MOCK_CURL'
#!/usr/bin/env bash
set -euo pipefail
data_path=''
url=''
while [[ $# -gt 0 ]]; do
  case "$1" in
    --fail|--silent|--show-error|--tlsv1.2)
      shift
      ;;
    --proto|--max-redirs|--request|-H)
      shift 2
      ;;
    --data-binary)
      data_path="${2#@}"
      shift 2
      ;;
    https://*)
      url="$1"
      shift
      ;;
    *)
      printf 'Unsupported mock curl argument: %s\n' "$1" >&2
      exit 2
      ;;
  esac
done
[[ "$url" == "https://uploads.github.com/repos/$MOCK_GH_REPO/releases/$MOCK_RELEASE_ID/assets?name=$MOCK_ACCEPTANCE_RELEASE_ASSET" \
    && -f "$data_path" ]] \
  || { printf 'Mock curl received invalid acceptance upload inputs.\n' >&2; exit 2; }
if command -v sha256sum >/dev/null 2>&1; then
  digest="$(sha256sum "$data_path" | cut -d ' ' -f 1)"
else
  digest="$(shasum -a 256 "$data_path" | cut -d ' ' -f 1)"
fi
size="$(wc -c < "$data_path" | tr -d '[:space:]')"
asset_id=2001
jq \
  --arg digest "sha256:$digest" \
  --arg name "$MOCK_ACCEPTANCE_RELEASE_ASSET" \
  --arg url "https://api.github.com/repos/$MOCK_GH_REPO/releases/assets/$asset_id" \
  --argjson id "$asset_id" \
  --argjson size "$size" \
  '.assets += [{id:$id,name:$name,size:$size,state:"uploaded",digest:$digest,url:$url}]' \
  "$MOCK_RELEASE_JSON" > "$MOCK_RELEASE_JSON.next"
mv "$MOCK_RELEASE_JSON.next" "$MOCK_RELEASE_JSON"
printf 'UPLOAD\n' >> "$MOCK_EVENT_LOG"
# GitHub may expose the digest on the release before adding it to the upload response.
jq -n --arg name "$MOCK_ACCEPTANCE_RELEASE_ASSET" --argjson id "$asset_id" --argjson size "$size" \
  '{id:$id,name:$name,size:$size,state:"uploaded",digest:null}'
MOCK_CURL

cat > "$fixture_repository/scripts/release/publish-registry-package.sh" <<'MOCK_REGISTRY_VERIFY'
#!/usr/bin/env bash
set -euo pipefail
[[ "${1:-}" == --verify-only && ( $# -eq 9 || $# -eq 11 ) && "$2" == --initial-yanked && "$4" == --current-availability ]] \
  || { printf 'IMMUTABLE_WRITE\n' >> "$MOCK_EVENT_LOG"; exit 2; }
if [[ $# -eq 11 ]]; then
  [[ "$6" == --published-at-utc && "$7" == 2026-10-05T00:00:00Z ]]
  set -- "${@:1:5}" "${@:8}"
fi
package_id="$8"
version="$9"
[[ "$version" == "$MOCK_VERSION" ]]
printf 'REGISTRY_VERIFY %s\n' "$package_id" >> "$MOCK_EVENT_LOG"
[[ "${MOCK_REGISTRY_MISMATCH_PACKAGE:-}" != "$package_id" ]] \
  || { printf 'Fixture Registry canonical identity mismatch.\n' >&2; exit 1; }
MOCK_REGISTRY_VERIFY

cat > "$fixture_repository/scripts/release/agent_cohort.py" <<'MOCK_FAMILY_PROMOTE'
from agent_cohort_contract import *
if __name__ == '__main__':
    command, registry, req, res = sys.argv[1:5]
    proposed = request(load(req))
    def event(text):
        with open(os.environ['MOCK_EVENT_LOG'], 'a') as stream: stream.write(text + '\n')
    if command in ('verify', 'confirm'):
        if command == 'confirm':
            require(os.environ['SUNDER_REGISTRY_CLI_TOKEN'] == os.environ['MOCK_EXPECTED_CLI_TOKEN'], 'Owner confirmation token differs')
            event('COHORT_CONFIRM')
        result(load(res), proposed)
        counter = Path(os.environ['MOCK_FAMILY_VERIFY_CALL_COUNT'])
        count = int(counter.read_text()) + 1
        counter.write_text(str(count))
        event('FAMILY_VERIFY')
        require(Path(os.environ['MOCK_PROMOTED_STATE']).read_text().strip() == 'true', 'Family is not fully promoted')
        if os.environ.get('MOCK_FAMILY_DRIFT_ON_VERIFY_CALL') == str(count):
            Path(os.environ['MOCK_PROMOTED_STATE']).write_text('false')
    elif command == 'apply':
        require(os.environ['SUNDER_REGISTRY_CLI_TOKEN'] == os.environ['MOCK_EXPECTED_CLI_TOKEN'], 'Owner token differs')
        event('FAMILY_SNAPSHOT_DONE')
        guard()
        committed = result(load(os.environ['MOCK_COHORT_RESULT']), proposed)
        store(res, committed)
        event('PROMOTE')
        Path(os.environ['MOCK_PROMOTED_STATE']).write_text('true')
    else:
        raise RuntimeError('No sequential fallback is supported')
MOCK_FAMILY_PROMOTE
cat > "$fixture_repository/scripts/release/agent_cohort_receipt.py" <<'MOCK_COHORT_RECEIPT'
import os
import sys
from agent_cohort_contract import result, load
assert sys.argv[1] == 'upload'
result(load(sys.argv[-1]), load(sys.argv[-2])['cohort']['request'])
with open(os.environ['MOCK_EVENT_LOG'], 'a') as stream: stream.write('COHORT_RECEIPT\n')
MOCK_COHORT_RECEIPT

chmod +x "$mock_bin/git" "$mock_bin/gh" "$mock_bin/curl"

export MOCK_FAMILY_VERIFY_CALL_COUNT="$fixture_root/family-verify-call-count"
export MOCK_ACCEPTANCE_RELEASE_ASSET="$acceptance_asset_name"

reset_fixture() {
  : > "$MOCK_EVENT_LOG"
  cp "$fixture_root/post-acceptance-template.json" "$acceptance"
  cp "$acceptance" "$asset_source/agent-family-acceptance.json"
  cp "$acceptance" "$asset_source/$acceptance_asset_name"
  cp "$MOCK_RELEASE_TEMPLATE_JSON" "$MOCK_RELEASE_JSON"
  printf 'true\n' > "$MOCK_DRAFT_STATE"
  printf 'true\n' > "$MOCK_PROMOTED_STATE"
  printf '0\n' > "$MOCK_FAMILY_VERIFY_CALL_COUNT"
  printf '0\n' > "$MOCK_RELEASE_CALL_COUNT"
  printf '0\n' > "$MOCK_TAG_CALL_COUNT"
  unset \
    MOCK_ARTIFACT_CREATED_AT \
    MOCK_ARTIFACT_DIGEST_RESULT \
    MOCK_ARTIFACT_ID_RESULT \
    MOCK_ARTIFACT_NAME \
    MOCK_FAIL_AFTER_FINALIZE \
    MOCK_FAMILY_DRIFT_ON_VERIFY_CALL \
    MOCK_INPUT_ARTIFACT_DIGEST \
    MOCK_INPUT_ARTIFACT_ID \
    MOCK_INPUT_ACCEPTANCE_WORKFLOW_SHA \
    MOCK_INPUT_POLICY_SHA \
    MOCK_INPUT_PUBLICATION_ATTEMPT \
    MOCK_INPUT_PUBLICATION_RUN_ID \
    MOCK_REGISTRY_MISMATCH_PACKAGE \
    MOCK_RELEASE_BODY_RESULT \
    MOCK_RELEASE_DRIFT_ON_CALL \
    MOCK_RELEASE_ID_RESULT \
    MOCK_RELEASE_TITLE_RESULT \
    MOCK_RUN_ATTEMPT_RESULT \
    MOCK_RUN_CONCLUSION \
    MOCK_RUN_EVENT \
    MOCK_RUN_HEAD_SHA \
    MOCK_RUN_ID_RESULT \
    MOCK_RUN_REPOSITORY \
    MOCK_RUN_STATUS \
    MOCK_TAG_DRIFT_ON_CALL \
    MOCK_TAG_SHA_RESULT \
    MOCK_TAMPER_ASSET \
    MOCK_WORKFLOW_NAME \
    MOCK_WORKFLOW_PATH
}

run_orchestrator() {
  local case_name="$1"
  local expected_hash="${2:-$acceptance_sha}"
  (
    cd "$fixture_repository"
    PATH="$mock_bin:$PATH" \
    ACCEPTANCE_EVIDENCE_SHA256="$expected_hash" \
    ACCEPTANCE_RUN_ATTEMPT="$MOCK_ACCEPTANCE_ATTEMPT" \
    ACCEPTANCE_RUN_ID="$MOCK_ACCEPTANCE_RUN_ID" \
    ACCEPTANCE_WORKFLOW_SHA="${MOCK_INPUT_ACCEPTANCE_WORKFLOW_SHA:-$MOCK_ACCEPTANCE_WORKFLOW_SHA}" \
    GH_REPO="$MOCK_GH_REPO" \
    GH_TOKEN=fixture-token \
    PUBLICATION_EVIDENCE_DIRECTORY="$MOCK_RELEASE_ASSETS" \
    PUBLICATION_RUN_ATTEMPT="${MOCK_INPUT_PUBLICATION_ATTEMPT:-$MOCK_PUBLICATION_ATTEMPT}" \
    PUBLICATION_RUN_ID="${MOCK_INPUT_PUBLICATION_RUN_ID:-$MOCK_PUBLICATION_RUN_ID}" \
    SUNDER_AGENT_POLICY_SHA="${MOCK_INPUT_POLICY_SHA:-$MOCK_POLICY_SHA}" \
    SUNDER_AGENT_POLICY_WORKFLOW_PATH=.github/workflows/sunder-agent-promotion.yml \
    SUNDER_AGENT_RELEASE_SOURCE_DIRECTORY="$MOCK_RELEASE_SOURCE" \
    SUNDER_REGISTRY_CLI_TOKEN="$MOCK_EXPECTED_CLI_TOKEN" \
      bash scripts/release/promote-agent-family.sh \
        "$MOCK_TAG" "$MOCK_SOURCE_SHA" https://registry.test "$acceptance" \
        "$fixture_root/verification-$case_name"
  )
}

run_candidate() {
  (
    cd "$fixture_repository"
    PATH="$mock_bin:$PATH" \
    ACCEPTANCE_RUN_ATTEMPT="$MOCK_ACCEPTANCE_ATTEMPT" \
    ACCEPTANCE_RUN_ID="$MOCK_ACCEPTANCE_RUN_ID" \
    ACCEPTANCE_WORKFLOW_SHA="$MOCK_ACCEPTANCE_WORKFLOW_SHA" \
    GH_REPO="$MOCK_GH_REPO" \
    GH_TOKEN=fixture-token \
    PUBLICATION_EVIDENCE_DIRECTORY="$MOCK_RELEASE_ASSETS" \
    PUBLICATION_RUN_ATTEMPT="$MOCK_PUBLICATION_ATTEMPT" \
    PUBLICATION_RUN_ID="$MOCK_PUBLICATION_RUN_ID" \
    SUNDER_AGENT_ACCEPTANCE_CANDIDATE_ONLY=true \
    SUNDER_AGENT_POLICY_SHA="$MOCK_POLICY_SHA" \
    SUNDER_AGENT_POLICY_WORKFLOW_PATH=.github/workflows/sunder-agent-production-acceptance.yml \
    SUNDER_AGENT_RELEASE_SOURCE_DIRECTORY="$MOCK_RELEASE_SOURCE" \
      bash scripts/release/promote-agent-family.sh \
        "$MOCK_TAG" "$MOCK_SOURCE_SHA" https://registry.test "$acceptance" \
        "$fixture_root/verification-candidate"
  )
}

run_previsibility() {
  local case_name="$1"
  jq '.phase = "pre-visibility" | .cohort.result = null' \
    "$fixture_root/post-acceptance-template.json" > "$acceptance"
  cp "$acceptance" "$asset_source/$acceptance_asset_name"
  local pre_sha
  pre_sha="$(hash_file "$acceptance")"
  jq --arg name "$acceptance_asset_name" --arg digest "sha256:$pre_sha" \
    --argjson size "$(file_size "$acceptance")" \
    '(.assets[] | select(.name == $name)) |= (.digest = $digest | .size = $size)' \
    "$MOCK_RELEASE_JSON" > "$MOCK_RELEASE_JSON.next"
  mv "$MOCK_RELEASE_JSON.next" "$MOCK_RELEASE_JSON"
  printf 'false\n' > "$MOCK_PROMOTED_STATE"
  run_orchestrator "$case_name" "$pre_sha"
}

expect_rejected_before_cohort() {
  local case_name="$1"
  if run_previsibility "$case_name" > "$fixture_root/$case_name.log" 2>&1; then
    printf 'Pre-visibility fixture unexpectedly accepted %s.\n' "$case_name" >&2
    exit 1
  fi
  ! grep -Eq '^(PROMOTE|FINALIZE|COHORT_RECEIPT|IMMUTABLE_WRITE)$' "$MOCK_EVENT_LOG"
}

expect_rejected_without_mutation() {
  local case_name="$1"
  local expected_hash="${2:-$acceptance_sha}"
  if run_orchestrator "$case_name" "$expected_hash" > "$fixture_root/$case_name.log" 2>&1; then
    printf 'Promotion fixture unexpectedly accepted case: %s\n' "$case_name" >&2
    exit 1
  fi
  if grep -Eq '^(PROMOTE|FINALIZE|IMMUTABLE_WRITE)$' "$MOCK_EVENT_LOG"; then
    printf 'Rejected promotion case mutated state: %s\n' "$case_name" >&2
    exit 1
  fi
}

expect_rejected_after_promotion_without_finalization() {
  local case_name="$1"
  if run_orchestrator "$case_name" > "$fixture_root/$case_name.log" 2>&1; then
    printf 'Promotion fixture unexpectedly accepted post-promotion drift: %s\n' "$case_name" >&2
    exit 1
  fi
  [[ "$(grep -c '^PROMOTE$' "$MOCK_EVENT_LOG" || true)" -eq 0 ]]
  if grep -Eq '^(FINALIZE|IMMUTABLE_WRITE)$' "$MOCK_EVENT_LOG"; then
    printf 'Post-promotion drift reached finalization or immutable publication: %s\n' "$case_name" >&2
    exit 1
  fi
}

reset_fixture
(
  cd "$fixture_repository"
  PATH="$mock_bin:$PATH" \
  GH_REPO="$MOCK_GH_REPO" \
  GH_TOKEN=fixture-token \
    bash scripts/release/download-agent-acceptance.sh \
      "$MOCK_RELEASE_ID" "$MOCK_TAG" "$MOCK_SOURCE_SHA" \
      "$MOCK_PUBLICATION_RUN_ID" "$MOCK_PUBLICATION_ATTEMPT" \
      "$MOCK_ACCEPTANCE_RUN_ID" "$MOCK_ACCEPTANCE_ATTEMPT" "$MOCK_ACCEPTANCE_WORKFLOW_SHA" "$acceptance_sha" \
      "$fixture_root/downloaded-agent-family-acceptance.json"
  PATH="$mock_bin:$PATH" \
  GH_REPO="$MOCK_GH_REPO" \
  GH_TOKEN=fixture-token \
    bash scripts/release/upload-agent-acceptance.sh \
      "$MOCK_RELEASE_ID" "$MOCK_TAG" "$MOCK_PUBLICATION_RUN_ID" "$MOCK_PUBLICATION_ATTEMPT" \
      "$MOCK_ACCEPTANCE_RUN_ID" "$MOCK_ACCEPTANCE_ATTEMPT" "$MOCK_ACCEPTANCE_WORKFLOW_SHA" \
      "$asset_source/agent-family-acceptance.json"
)
cmp "$acceptance" "$fixture_root/downloaded-agent-family-acceptance.json"

reset_fixture
jq --arg name "$acceptance_asset_name" '.assets |= map(select(.name != $name))' \
  "$MOCK_RELEASE_TEMPLATE_JSON" > "$MOCK_RELEASE_JSON"
(
  cd "$fixture_repository"
  PATH="$mock_bin:$PATH" \
  GH_REPO="$MOCK_GH_REPO" \
  GH_TOKEN=fixture-token \
    bash scripts/release/upload-agent-acceptance.sh \
      "$MOCK_RELEASE_ID" "$MOCK_TAG" "$MOCK_PUBLICATION_RUN_ID" "$MOCK_PUBLICATION_ATTEMPT" \
      "$MOCK_ACCEPTANCE_RUN_ID" "$MOCK_ACCEPTANCE_ATTEMPT" "$MOCK_ACCEPTANCE_WORKFLOW_SHA" \
      "$asset_source/agent-family-acceptance.json"
  PATH="$mock_bin:$PATH" \
  GH_REPO="$MOCK_GH_REPO" \
  GH_TOKEN=fixture-token \
    bash scripts/release/download-agent-acceptance.sh \
      "$MOCK_RELEASE_ID" "$MOCK_TAG" "$MOCK_SOURCE_SHA" \
      "$MOCK_PUBLICATION_RUN_ID" "$MOCK_PUBLICATION_ATTEMPT" \
      "$MOCK_ACCEPTANCE_RUN_ID" "$MOCK_ACCEPTANCE_ATTEMPT" "$MOCK_ACCEPTANCE_WORKFLOW_SHA" "$acceptance_sha" \
      "$fixture_root/uploaded-agent-family-acceptance.json"
)
grep -Fxq UPLOAD "$MOCK_EVENT_LOG"
cmp "$acceptance" "$fixture_root/uploaded-agent-family-acceptance.json"

reset_fixture
jq --arg name "$acceptance_asset_name" '.assets |= map(select(.name != $name))' \
  "$MOCK_RELEASE_TEMPLATE_JSON" > "$MOCK_RELEASE_JSON"
run_candidate > "$fixture_root/candidate.log"
! grep -Eq '^(ACCEPTANCE_ARTIFACT|ATTEMPT|ATTEMPT_JOBS|PROMOTE|FINALIZE|IMMUTABLE_WRITE)$' "$MOCK_EVENT_LOG"
[[ "$(grep -c '^REGISTRY_VERIFY ' "$MOCK_EVENT_LOG")" -eq 15 ]]

reset_fixture
run_previsibility previsibility > "$fixture_root/previsibility.log"
[[ "$(grep -c '^PROMOTE$' "$MOCK_EVENT_LOG")" -eq 1 ]]
[[ "$(grep -c '^COHORT_RECEIPT$' "$MOCK_EVENT_LOG")" -eq 1 ]]
! grep -Eq '^(FINALIZE|IMMUTABLE_WRITE)$' "$MOCK_EVENT_LOG"
test "$(< "$MOCK_DRAFT_STATE")" = true

reset_fixture
run_orchestrator valid > "$fixture_root/valid.log"
valid_events=()
while IFS= read -r event; do valid_events+=("$event"); done < "$MOCK_EVENT_LOG"
promote_index=-1
finalize_index=-1
snapshot_index=-1
for ((index = 0; index < ${#valid_events[@]}; index++)); do
  [[ "${valid_events[$index]}" != PROMOTE ]] || promote_index=$index
  [[ "${valid_events[$index]}" != FINALIZE ]] || finalize_index=$index
  [[ "${valid_events[$index]}" != FAMILY_SNAPSHOT_DONE ]] || snapshot_index=$index
done
(( snapshot_index == -1 && promote_index == -1 && finalize_index >= 0 ))
[[ "${valid_events[$((finalize_index - 1))]}" == FAMILY_VERIFY ]]
[[ "$(grep -c '^ATTEMPT$' "$MOCK_EVENT_LOG")" -eq 4 ]]
[[ "$(grep -c '^ATTEMPT_JOBS$' "$MOCK_EVENT_LOG")" -eq 4 ]]
[[ "$(grep -c '^RELEASE_GET$' "$MOCK_EVENT_LOG")" -eq 4 ]]
# Three complete 21-asset downloads plus four selected post-visibility acceptance reads.
[[ "$(grep -c '^ASSET ' "$MOCK_EVENT_LOG")" -eq 67 ]]
[[ "$(grep -c '^REGISTRY_VERIFY ' "$MOCK_EVENT_LOG")" -eq 45 ]]
[[ "$(grep -c '^TAG_REF$' "$MOCK_EVENT_LOG")" -eq 4 ]]
[[ "$(grep -c '^FAMILY_VERIFY$' "$MOCK_EVENT_LOG")" -eq 4 ]]
[[ "$(grep -c '^FINALIZE$' "$MOCK_EVENT_LOG")" -eq 1 ]]
! grep -Eq '^(IMMUTABLE_WRITE|BUILD|NUGET|PACK|PUBLISH|SIGN)' "$MOCK_EVENT_LOG"
! grep -Fxq TAG_POLICY_EXECUTED "$MOCK_EVENT_LOG"
jq -e --slurpfile template "$MOCK_RELEASE_TEMPLATE_JSON" '.assets == $template[0].assets' \
  "$MOCK_RELEASE_JSON" >/dev/null
jq -e --arg name "$prior_acceptance_name" \
  '([.assets[] | select(.name == $name)] | length) == 1' "$MOCK_RELEASE_JSON" >/dev/null

reset_fixture
export MOCK_WORKFLOW_PATH=.github/workflows/untrusted.yml
expect_rejected_without_mutation wrong-workflow

reset_fixture
export MOCK_INPUT_ACCEPTANCE_WORKFLOW_SHA=6666666666666666666666666666666666666666
expect_rejected_without_mutation wrong-acceptance-policy-sha

reset_fixture
export MOCK_INPUT_PUBLICATION_RUN_ID=8002
expect_rejected_without_mutation wrong-publication-run

reset_fixture
export MOCK_INPUT_PUBLICATION_ATTEMPT=1
expect_rejected_without_mutation wrong-publication-attempt

reset_fixture
export MOCK_INPUT_POLICY_SHA=7777777777777777777777777777777777777777
expect_rejected_without_mutation stale-promotion-policy

reset_fixture
export MOCK_RUN_ID_RESULT=9002
expect_rejected_without_mutation wrong-run

reset_fixture
export MOCK_RUN_ATTEMPT_RESULT=3
expect_rejected_without_mutation wrong-attempt

reset_fixture
expect_rejected_without_mutation wrong-evidence "$(printf '%064d' 0)"

reset_fixture
export MOCK_RUN_EVENT=push
expect_rejected_without_mutation wrong-event

reset_fixture
export MOCK_RELEASE_ID_RESULT=424243
expect_rejected_without_mutation draft-replacement

reset_fixture
export MOCK_RELEASE_TITLE_RESULT='Replacement title'
expect_rejected_without_mutation release-title-mismatch

reset_fixture
export MOCK_RELEASE_BODY_RESULT='Replacement body'
expect_rejected_without_mutation release-body-mismatch

reset_fixture
export MOCK_TAG_SHA_RESULT=3333333333333333333333333333333333333333
expect_rejected_without_mutation tag-drift

reset_fixture
export MOCK_TAG_DRIFT_ON_CALL=2
expect_rejected_before_cohort tag-drift-before-mutation
grep -Fqx 'FAMILY_SNAPSHOT_DONE' "$MOCK_EVENT_LOG"

reset_fixture
export MOCK_RELEASE_DRIFT_ON_CALL=2
expect_rejected_before_cohort release-drift-before-mutation
grep -Fqx 'FAMILY_SNAPSHOT_DONE' "$MOCK_EVENT_LOG"

reset_fixture
export MOCK_TAMPER_ASSET=toolchain.txt
expect_rejected_without_mutation asset-tamper

reset_fixture
export MOCK_TAMPER_ASSET="$acceptance_asset_name"
expect_rejected_without_mutation durable-acceptance-tamper

reset_fixture
export MOCK_TAMPER_ASSET=package-output-audit.json
expect_rejected_without_mutation output-audit-tamper

reset_fixture
export MOCK_REGISTRY_MISMATCH_PACKAGE="${package_ids[7]}"
expect_rejected_without_mutation registry-mismatch

reset_fixture
export MOCK_RELEASE_DRIFT_ON_CALL=3
expect_rejected_after_promotion_without_finalization release-drift-before-finalization

reset_fixture
export MOCK_TAG_DRIFT_ON_CALL=3
expect_rejected_after_promotion_without_finalization tag-drift-before-finalization

reset_fixture
export MOCK_FAMILY_DRIFT_ON_VERIFY_CALL=1
expect_rejected_after_promotion_without_finalization family-drift-before-finalization
[[ "$(grep -c '^FAMILY_VERIFY$' "$MOCK_EVENT_LOG")" -eq 2 ]]

# Simulate runner loss after GitHub atomically persists draft=false and the exact receipt.
reset_fixture
export MOCK_FAIL_AFTER_FINALIZE=true
if run_orchestrator runner-loss-after-finalize > "$fixture_root/runner-loss-after-finalize.log" 2>&1; then
  printf 'Promotion unexpectedly reported success after simulated runner loss.\n' >&2
  exit 1
fi
[[ "$(< "$MOCK_DRAFT_STATE")" == false ]]
[[ "$(< "$MOCK_PROMOTED_STATE")" == true ]]
grep -Fqx 'FINALIZE' "$MOCK_EVENT_LOG"
unset MOCK_FAIL_AFTER_FINALIZE
: > "$MOCK_EVENT_LOG"
printf '0\n' > "$MOCK_RELEASE_CALL_COUNT"
printf '0\n' > "$MOCK_TAG_CALL_COUNT"
printf '0\n' > "$MOCK_FAMILY_VERIFY_CALL_COUNT"
run_orchestrator runner-loss-retry > "$fixture_root/runner-loss-retry.log"
grep -Fq 'Verified durable promotion receipt' "$fixture_root/runner-loss-retry.log"
! grep -Eq '^(PROMOTE|FINALIZE)$' "$MOCK_EVENT_LOG"
[[ "$(grep -c '^FAMILY_VERIFY$' "$MOCK_EVENT_LOG")" -eq 2 ]]

reset_fixture
printf 'false\n' > "$MOCK_DRAFT_STATE"
printf 'true\n' > "$MOCK_PROMOTED_STATE"
expect_rejected_without_mutation finalized-retry-without-receipt

reset_fixture
printf 'false\n' > "$MOCK_DRAFT_STATE"
printf 'true\n' > "$MOCK_PROMOTED_STATE"
forged_body="$release_body"$'\n\n<!-- sunder-agent-promotion-receipt:v2 {"schemaVersion":2,"sourceCommit":"3333333333333333333333333333333333333333"} -->'
jq --arg body "$forged_body" '.draft = false | .body = $body' \
  "$MOCK_RELEASE_JSON" > "$MOCK_RELEASE_JSON.next"
mv "$MOCK_RELEASE_JSON.next" "$MOCK_RELEASE_JSON"
expect_rejected_without_mutation forged-finalized-receipt

printf 'Agent promotion orchestration fixture passed: trusted-policy isolation, exact publication and durable acceptance binding, refreshed final family guard, atomic receipt finalization, runner-loss retry, and forged-state rejection verified.\n'
