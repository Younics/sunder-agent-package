#!/usr/bin/env bash
set -euo pipefail

[[ $# -eq 3 ]] || {
  printf 'Usage: import-agent-publication-evidence.sh <clean-detached-artifact-source> <retained-release-assets> <registry-origin>\n' >&2
  exit 2
}
script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source_directory="$(cd "$1" && pwd)"
assets="$(cd "$2" && pwd)"
registry="${3%/}"
provenance="$assets/release-provenance.json"
python3 -B "$script_dir/agent_release.py" verify-provenance "$source_directory" "$assets"
[[ "$(git -C "$source_directory" rev-parse HEAD)" == "$(jq -r '.artifactSourceCommit' "$provenance")" \
    && -z "$(git -C "$source_directory" status --porcelain --untracked-files=no)" ]] \
  || { printf 'Publication import requires a clean detached artifact-source checkout.\n' >&2; exit 1; }
if git -C "$source_directory" symbolic-ref -q HEAD >/dev/null; then
  printf 'Use a detached evidence-preparation checkout.\n' >&2
  exit 1
fi
[[ "$registry" == "$(jq -r '.publication.registryOrigin' "$provenance")" ]] \
  || { printf 'Publication import Registry differs from retained provenance.\n' >&2; exit 1; }
lock_set="$(python3 -B "$script_dir/agent_release.py" lock-set "$source_directory")"
work="$(mktemp -d "${RUNNER_TEMP:-${TMPDIR:-/tmp}}/sunder-agent-publication-import.XXXXXX")"
trap 'rm -rf "$work"' EXIT

# Import uses Core's existing online evidence operation. It uploads/builds nothing,
# writes only matching owned-baseline publication fields, and never binds a lock.
while IFS=$'\t' read -r package_id project; do
  version="$(jq -r '.releaseVersion' "$source_directory/packages.json")"
  archive="$assets/$package_id.$version.sunderpkg"
  python3 -B "$script_dir/agent_release.py" expected-publication "$provenance" "$package_id" > "$work/expected.json"
  env -u SUNDER_REGISTRY_PUBLISH_TOKEN -u SUNDER_REGISTRY_CLI_TOKEN \
    bash "$script_dir/publish-registry-package.sh" --verify-only --initial-yanked "$work/expected.json" \
      "$registry" "$archive" "$package_id" "$version"
  dotnet msbuild "$source_directory/$project" -t:ImportSunderContractPublication \
    -p:Configuration=Release -p:SunderUseSourceSdk=false -p:BuildProjectReferences=false \
    -p:SunderPackagePublicationPath="$archive" -p:SunderRegistryOrigin="$registry/"
  [[ "$(python3 -B "$script_dir/agent_release.py" lock-set "$source_directory")" == "$lock_set" ]] \
    || { printf 'Import changed a production lock; stop evidence preparation.\n' >&2; exit 1; }
done < <(jq -r '.packages | sort_by(.packageId)[] | [.packageId,.projectPath] | @tsv' "$source_directory/packages.json")

printf 'Imported all retained Yanked publications. Review and record one evidence-only direct-child commit, then verify it with agent_release.py verify-evidence-commit; keep the artifact tag on its source commit.\n'
